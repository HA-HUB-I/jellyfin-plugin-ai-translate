using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>
/// Appears in Jellyfin's "Search subtitles" dialog. Offers, with an estimated price: translating an existing subtitle in
/// another language (embedded, external or from another provider), or generating one from the audio track.
/// Automated (scheduled) searches only ever get finished results.
/// </summary>
public sealed partial class AiSubtitleProvider : ISubtitleProvider
{
    /// <summary>Marks the temporary "being generated" subtitle so it can be found and replaced.</summary>
    public const string PendingMarker = "⏳ AI Translate:";

    /// <summary>Marks the one-line note left when a job failed; a later run replaces it like a placeholder.</summary>
    public const string FailedMarker = "⚠ AI Translate:";

    private static readonly TimeSpan QuickWait = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OtherProvidersTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OtherProvidersCacheTime = TimeSpan.FromMinutes(10);

    // Set while this provider asks the other providers, so the nested search never comes back here.
    private static readonly AsyncLocal<bool> InNestedSearch = new();

    private readonly SubtitleJobService _jobs;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _services;
    private readonly ILogger<AiSubtitleProvider> _logger;
    private readonly Notifier _notifier;
    private readonly ConcurrentDictionary<string, (RemoteSubtitleInfo[] Results, DateTime Utc)> _othersCache = new(StringComparer.Ordinal);

    public AiSubtitleProvider(SubtitleJobService jobs, ILibraryManager libraryManager, IServiceProvider services, Notifier notifier, ILogger<AiSubtitleProvider> logger)
    {
        _jobs = jobs;
        _libraryManager = libraryManager;
        _services = services;
        _notifier = notifier;
        _logger = logger;
    }

    public string Name => "AI Translate";

    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string lang = Languages.ToTwoLetter(request.TwoLetterISOLanguageName ?? request.Language);
        if (InNestedSearch.Value || string.IsNullOrEmpty(request.MediaPath) || !File.Exists(request.MediaPath) || !IsTargetLanguage(config, lang))
        {
            return [];
        }

        string title = Title(request);
        var results = new List<RemoteSubtitleInfo>();
        foreach (var (_, sourceKey, x, created) in _jobs.Cached(request.MediaPath, lang))
        {
            var job = sourceKey.StartsWith("sub-", StringComparison.Ordinal)
                ? new SubtitleJob(request.MediaPath, title, lang, "-", x, SourceKind.File, null, sourceKey[4..])
                : new SubtitleJob(request.MediaPath, title, lang, sourceKey, x);
            results.Add(Info(request, job, $"✓ Готови · {sourceKey} + {x} · {created.ToLocalTime():dd.MM.yyyy HH:mm}", "Вече генерирани, без нов разход."));
        }

        // Never spend money on automatic searches (library scans, "download missing subtitles").
        if (request.IsAutomated || !_jobs.AvailableTranslators().Any())
        {
            return results;
        }

        var video = _libraryManager.FindByPath(request.MediaPath, false) as Video;
        var streams = video?.GetMediaStreams() ?? [];
        if (config.OnlyWhenNoOtherSubtitles)
        {
            // This plugin's own "being generated" / "failed" notes do not count, or a failed job could never be retried.
            if (streams.Any(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language) && Languages.ToTwoLetter(s.Language) == lang
                && !(s.IsExternal && IsPlaceholder(s.Path))))
            {
                _logger.LogDebug("AI Translate: {Title} already has {Lang} subtitles, not offering generation", title, lang);
                return results;
            }

            if ((await OtherProvidersAsync(request, request.Language, request.TwoLetterISOLanguageName, cancellationToken).ConfigureAwait(false)).Length > 0)
            {
                _logger.LogDebug("AI Translate: other providers found {Lang} subtitles for {Title}, not offering generation", lang, title);
                return results;
            }
        }

        var duration = request.RuntimeTicks is > 0 ? TimeSpan.FromTicks(request.RuntimeTicks.Value) : TimeSpan.FromMinutes(100);
        var preferred = LanguageList(config.SourceLanguages);

        // 1) Translate an existing subtitle: embedded/external first, then another provider's result.
        var source = SubtitleSources.FromStreams(streams, lang, preferred).FirstOrDefault();
        if (source is null && config.UseOtherProvidersAsSource)
        {
            source = await FindRemoteSourceAsync(request, preferred, cancellationToken).ConfigureAwait(false);
        }

        if (source is not null)
        {
            foreach (var x in _jobs.AvailableTranslators())
            {
                var job = new SubtitleJob(request.MediaPath, title, lang, "-", x.Id, source.Kind, source.Ref, source.Language);
                if (results.Any(r => Decode(r.Id) is var j && j.SourceKey == job.SourceKey && j.TranslatorId == job.TranslatorId))
                {
                    continue;
                }

                int? cueCount = source.Kind == SourceKind.File ? CountCues(source.Ref) : null;
                double cost = _jobs.EstimateTranslation(x, duration, cueCount);
                double minutes = Math.Ceiling(SubtitleJobService.TranslationTime(duration, cueCount).TotalMinutes + (source.Kind == SourceKind.Embedded ? 2 : 0));
                results.Add(Running(job, Info(
                    request,
                    job,
                    string.Create(CultureInfo.InvariantCulture, $"AI превод от {source.Label} · {_jobs.TranslatorLabel(x)} · ≈ ${cost:0.00} · ~{minutes:0} мин"),
                    string.Create(CultureInfo.InvariantCulture, $"Превежда съществуващите субтитри ({source.Label}{(cueCount is { } n ? $", {n} реплики" : string.Empty)}) с техните времена: ≈ ${cost:0.00}, около {minutes:0} мин. Таксува се при избор."))));
            }
        }

        // 2) Generate from the audio track (slow) — hidden when a subtitle can be translated, unless configured otherwise.
        if (source is null || !config.PreferExistingSubtitles)
        {
            foreach (var (t, x) in _jobs.AvailableCombinations())
            {
                var job = new SubtitleJob(request.MediaPath, title, lang, t.Id, x.Id);
                if (results.Any(r => Decode(r.Id) is var j && j.SourceKey == job.SourceKey && j.TranslatorId == job.TranslatorId))
                {
                    continue;
                }

                double cost = _jobs.Estimate(t, x, request.MediaPath, duration);
                double minutes = Math.Ceiling(_jobs.EstimatedTime(t, request.MediaPath, duration).TotalMinutes);
                results.Add(Running(job, Info(
                    request,
                    job,
                    string.Create(CultureInfo.InvariantCulture, $"AI от аудиото · {_jobs.ModelLabel(t, x)} · ≈ ${cost:0.00} · ~{minutes:0} мин"),
                    string.Create(CultureInfo.InvariantCulture, $"Разпознаване на говора ({duration.TotalMinutes:0} мин филм) и превод: ≈ ${cost:0.00}, около {minutes:0} мин. Таксува се при избор. Субтитрите се записват и ако прозорецът спре да чака."))));
            }
        }

        return results;
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var job = Decode(id);

        // The id comes back from the client: only library items, and only their own subtitle files, are accepted.
        var video = _libraryManager.FindByPath(job.MediaPath, false) as Video ?? throw new ArgumentException("Not a library item", nameof(id));
        if (job.SourceKind == SourceKind.File && job.SourceRef is not null
            && !video.GetMediaStreams().Any(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && string.Equals(s.Path, job.SourceRef, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Not a subtitle file of this item", nameof(id));
        }

        _logger.LogInformation("AI Translate: {Title} → {Lang} from {Source} with {Translator}", job.Title, job.TargetLanguage, job.SourceKey, job.TranslatorId);

        string? cached = _jobs.CachedPath(job);
        if (cached is not null)
        {
            return Response(job, await File.ReadAllBytesAsync(cached, cancellationToken).ConfigureAwait(false));
        }

        // Quick jobs (e.g. translating a cached transcript) are returned directly. Longer ones run detached: Jellyfin gets
        // a one-line "being generated" subtitle right away (so the user sees that it started), and the file is replaced
        // with the real subtitles when the job finishes.
        var task = _jobs.RunAsync(job, out bool started);
        if (await Task.WhenAny(task, Task.Delay(QuickWait, cancellationToken)).ConfigureAwait(false) == task)
        {
            return Response(job, await File.ReadAllBytesAsync(await task.ConfigureAwait(false), cancellationToken).ConfigureAwait(false));
        }

        string eta = job.SourceKind == SourceKind.Audio ? "15–25 мин" : "1–5 мин";
        if (started)
        {
            await _notifier.SendAsync($"Генерирането на субтитри ({job.TargetLanguage}) за „{job.Title}“ започна — около {eta}. Субтитрите ще се появят сами.").ConfigureAwait(false);
            _ = task.ContinueWith(t => OnJobFinishedAsync(job, t), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }

        return Response(job, System.Text.Encoding.UTF8.GetBytes(Placeholder(eta)));
    }

    /// <summary>Text of the temporary subtitle; also the marker used to find and replace it later.</summary>
    internal static string Placeholder(string eta) =>
        $"1\n00:00:00,000 --> 09:59:59,000\n{PendingMarker} субтитрите се генерират (~{eta}).\nЩе се сменят с истинските автоматично.\n";

    /// <summary>Writes the finished subtitles (or the error) over every placeholder (or earlier error note) among the files.</summary>
    internal static int ReplacePlaceholders(IEnumerable<string> candidateFiles, string content)
    {
        int replaced = 0;
        foreach (var file in candidateFiles.Distinct(StringComparer.Ordinal))
        {
            if (!IsPlaceholder(file))
            {
                continue;
            }

            try
            {
                File.WriteAllText(file, content, new UTF8Encoding(false));
                replaced++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return replaced;
    }

    /// <summary>Deletes the placeholders (and earlier error notes) among the files.</summary>
    internal static int RemovePlaceholders(IEnumerable<string> candidateFiles)
    {
        int removed = 0;
        foreach (var file in candidateFiles.Distinct(StringComparer.Ordinal).Where(IsPlaceholder))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>Whether the file is a "being generated" placeholder or a "failed" note of this plugin, not real subtitles.</summary>
    internal static bool IsPlaceholder(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length >= 4096)
            {
                return false;
            }

            string text = File.ReadAllText(path);
            return text.Contains(PendingMarker, StringComparison.Ordinal) || text.Contains(FailedMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Subtitle files that Jellyfin may have saved for this film in this language ("Film.bg.srt", "Film.bg.0.srt",
    /// "Film.bul.srt", …): from the item's known streams and the given folders. Files of other films in the same folder
    /// (a season, a flat movie folder) are left out — their own jobs replace them.
    /// </summary>
    internal static List<string> PlaceholderCandidates(string mediaPath, string language, IEnumerable<string> knownFiles, IEnumerable<string> folders)
    {
        string film = Path.GetFileNameWithoutExtension(mediaPath) + ".";
        bool OfThisFilm(string file)
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith(film, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string token = name[film.Length..].Split('.')[0];
            return token.Length > 0 && Languages.ToTwoLetter(token) == language;
        }

        var files = knownFiles.ToList();
        foreach (var folder in folders.Distinct(StringComparer.Ordinal))
        {
            if (Directory.Exists(folder))
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.srt"));
            }
        }

        return files.Where(OfThisFilm).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task OnJobFinishedAsync(SubtitleJob job, Task<string> finished)
    {
        try
        {
            // Jellyfin saves the downloaded subtitle next to the film or in its metadata folder; look at both.
            var video = _libraryManager.FindByPath(job.MediaPath, false) as Video;
            var known = video?.GetMediaStreams().Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal && !string.IsNullOrEmpty(s.Path)).Select(s => s.Path) ?? [];
            var folders = new List<string>();
            if (Path.GetDirectoryName(job.MediaPath) is { Length: > 0 } folder)
            {
                folders.Add(folder);
            }

            if (video is not null)
            {
                folders.Add(video.GetInternalMetadataPath());
            }

            var candidates = PlaceholderCandidates(job.MediaPath, job.TargetLanguage, known, folders);
            int replaced;
            string message;
            if (finished.IsCanceled)
            {
                // Stopped on purpose: no note in the player, just gone (what was done stays cached for a new pick).
                replaced = RemovePlaceholders(candidates);
                message = $"Генерирането на субтитри за „{job.Title}“ е спряно. Готовото е запазено — нов избор продължава оттам.";
            }
            else
            {
                string content;
                if (finished.IsCompletedSuccessfully)
                {
                    content = await File.ReadAllTextAsync(await finished.ConfigureAwait(false)).ConfigureAwait(false);
                    message = $"Субтитрите ({job.TargetLanguage}) за „{job.Title}“ са готови.";
                }
                else
                {
                    _logger.LogWarning(finished.Exception?.GetBaseException(), "AI Translate: {Title} → {Lang} failed", job.Title, job.TargetLanguage);
                    string reason = finished.Exception?.GetBaseException().Message ?? "прекъснато";
                    content = $"1\n00:00:00,000 --> 09:59:59,000\n{FailedMarker} субтитрите не успяха.\n{(reason.Length > 120 ? reason[..120] : reason)}\n";
                    message = $"Субтитрите за „{job.Title}“ не успяха: {reason}";
                }

                replaced = ReplacePlaceholders(candidates, content);
            }

            _logger.LogInformation("AI Translate: {Title}: updated {Count} placeholder subtitle file(s)", job.Title, replaced);
            if (video is not null && replaced > 0)
            {
                var providers = _services.GetService<IProviderManager>();
                var fileSystem = _services.GetService<MediaBrowser.Model.IO.IFileSystem>();
                if (providers is not null && fileSystem is not null)
                {
                    providers.QueueRefresh(video.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)) { MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ImageRefreshMode = MetadataRefreshMode.None }, RefreshPriority.High);
                }
            }

            await _notifier.SendAsync(message, finished.IsCompletedSuccessfully ? 10000 : 30000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Runs detached: nobody else would see this.
            _logger.LogError(ex, "AI Translate: could not put the finished subtitles of {Title} in place", job.Title);
        }
    }

    private static SubtitleResponse Response(SubtitleJob job, byte[] bytes) => new()
    {
        Format = "srt",
        Language = job.TargetLanguage,
        IsForced = false,
        Stream = new MemoryStream(bytes),
    };

    internal static string Encode(SubtitleJob job) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Reads a subtitle id back. It comes from the client, so every field that ends up in a file name is checked.</summary>
    internal static SubtitleJob Decode(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        var job = JsonSerializer.Deserialize<SubtitleJob>(Convert.FromBase64String(b64)) ?? throw new ArgumentException("Bad subtitle id", nameof(id));
        bool valid = !string.IsNullOrEmpty(job.MediaPath)
            && LanguageCodeRegex().IsMatch(job.TargetLanguage)
            && (job.SourceLanguage is null || LanguageCodeRegex().IsMatch(job.SourceLanguage))
            && job.TranslatorId is "openai" or "gemini"
            && job.SourceKind switch
            {
                SourceKind.Audio => job.TranscriberId is "openai" or "gemini",
                SourceKind.Embedded => job.SourceLanguage is not null && int.TryParse(job.SourceRef, NumberStyles.None, CultureInfo.InvariantCulture, out _),
                SourceKind.File or SourceKind.Remote => job.SourceLanguage is not null,
                _ => false,
            };
        return valid ? job : throw new ArgumentException("Bad subtitle id", nameof(id));
    }

    /// <summary>Number of cues in a subtitle file (SRT/VTT arrows, ASS dialogue lines), or null if unreadable.</summary>
    private static int? CountCues(string path)
    {
        try
        {
            int n = File.ReadLines(path).Count(l => l.Contains("-->", StringComparison.Ordinal) || l.StartsWith("Dialogue:", StringComparison.Ordinal));
            return n > 0 ? n : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Best result of the other providers in the first preferred source language that has one.</summary>
    private async Task<SubtitleSource?> FindRemoteSourceAsync(SubtitleSearchRequest request, IReadOnlyList<string> preferred, CancellationToken cancellationToken)
    {
        foreach (var two in preferred)
        {
            string three = CultureInfo.GetCultureInfo(two).ThreeLetterISOLanguageName;
            var found = await OtherProvidersAsync(request, three, two, cancellationToken).ConfigureAwait(false);
            var best = found
                .Where(r => string.Equals(r.Format, "srt", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(r.Format) || r.Format is "ass" or "ssa" or "vtt")
                .OrderBy(r => r.Forced == true)
                .ThenBy(r => r.HearingImpaired == true)
                .ThenByDescending(r => r.IsHashMatch == true)
                .ThenByDescending(r => r.DownloadCount ?? 0)
                .FirstOrDefault();
            if (best is not null)
            {
                return new SubtitleSource(SourceKind.Remote, best.Id, two, $"{best.ProviderName} {Languages.EnglishName(two)}", best.HearingImpaired == true);
            }
        }

        return null;
    }

    /// <summary>Runs the other enabled subtitle providers (this one disabled) for a language; results cached for a few minutes.</summary>
    private async Task<RemoteSubtitleInfo[]> OtherProvidersAsync(SubtitleSearchRequest request, string language, string? twoLetter, CancellationToken cancellationToken)
    {
        string key = request.MediaPath + "|" + language;
        if (_othersCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Utc < OtherProvidersCacheTime)
        {
            return cached.Results;
        }

        // Resolved lazily: the subtitle manager itself depends on all subtitle providers, including this one.
        var manager = _services.GetService<ISubtitleManager>();
        if (manager is null)
        {
            return [];
        }

        var nested = new SubtitleSearchRequest
        {
            Language = language,
            TwoLetterISOLanguageName = twoLetter ?? Languages.ToTwoLetter(language),
            ContentType = request.ContentType,
            MediaPath = request.MediaPath,
            SeriesName = request.SeriesName,
            Name = request.Name,
            IndexNumber = request.IndexNumber,
            IndexNumberEnd = request.IndexNumberEnd,
            ParentIndexNumber = request.ParentIndexNumber,
            ProductionYear = request.ProductionYear,
            RuntimeTicks = request.RuntimeTicks,
            IsPerfectMatch = request.IsPerfectMatch,
            ProviderIds = request.ProviderIds,
            SearchAllProviders = true,
            DisabledSubtitleFetchers = [.. request.DisabledSubtitleFetchers ?? [], Name],
            SubtitleFetcherOrder = request.SubtitleFetcherOrder,
            IsAutomated = request.IsAutomated,
        };

        RemoteSubtitleInfo[] found;
        InNestedSearch.Value = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OtherProvidersTimeout);
            found = (await manager.SearchSubtitles(nested, timeout.Token).ConfigureAwait(false)).Where(r => r.ProviderName != Name).ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            found = [];
        }
        finally
        {
            InNestedSearch.Value = false;
        }

        _othersCache[key] = (found, DateTime.UtcNow);
        return found;
    }

    // A job that is already running is joined when picked, not paid again: say so instead of showing it as new.
    private RemoteSubtitleInfo Running(SubtitleJob job, RemoteSubtitleInfo info)
    {
        if (_jobs.IsRunning(job))
        {
            info.Name = "⏳ Тече · " + info.Name;
            info.Comment = "Вече се генерира — изборът не плаща отново, а изчаква същата задача. " + info.Comment;
        }

        return info;
    }

    private RemoteSubtitleInfo Info(SubtitleSearchRequest request, SubtitleJob job, string name, string comment) => new()
    {
        Id = Encode(job),
        ProviderName = Name,
        Name = name,
        Comment = comment,
        Format = "srt",
        ThreeLetterISOLanguageName = request.Language,
        Author = "AI",
        AiTranslated = true,
        MachineTranslated = true,
    };

    private static bool IsTargetLanguage(PluginConfiguration config, string lang)
    {
        var allowed = LanguageList(config.TargetLanguages);
        return allowed.Count == 0 || allowed.Contains(lang, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A comma-separated language setting as ISO 639-1 codes ("bul, EN" → bg, en).</summary>
    private static List<string> LanguageList(string setting) =>
        setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Languages.ToTwoLetter).Distinct(StringComparer.Ordinal).ToList();

    private static string Title(SubtitleSearchRequest r)
    {
        if (!string.IsNullOrEmpty(r.SeriesName))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{r.SeriesName} S{r.ParentIndexNumber:00}E{r.IndexNumber:00} {r.Name}");
        }

        return r.ProductionYear is { } y ? $"{r.Name} ({y})" : r.Name ?? Path.GetFileNameWithoutExtension(r.MediaPath);
    }

    // "bg", "bul", "pt-br", "zh-tw": letters only, so it is safe inside a cache file name.
    [GeneratedRegex("^[a-z]{2,3}(?:-[a-z0-9]{2,8})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageCodeRegex();
}
