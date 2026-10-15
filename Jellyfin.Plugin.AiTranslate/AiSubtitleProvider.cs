using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Providers;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>
/// Appears in Jellyfin's "Search subtitles" dialog. Each provider combination is listed with its estimated price;
/// picking one runs speech recognition + translation. Automated (scheduled) searches only ever get finished results.
/// </summary>
public sealed class AiSubtitleProvider : ISubtitleProvider
{
    private readonly SubtitleJobService _jobs;
    private readonly ILogger<AiSubtitleProvider> _logger;

    public AiSubtitleProvider(SubtitleJobService jobs, ILogger<AiSubtitleProvider> logger)
    {
        _jobs = jobs;
        _logger = logger;
    }

    public string Name => "AI Translate";

    public IEnumerable<VideoContentType> SupportedMediaTypes => [VideoContentType.Movie, VideoContentType.Episode];

    public Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        string lang = Languages.ToTwoLetter(request.TwoLetterISOLanguageName ?? request.Language);
        if (string.IsNullOrEmpty(request.MediaPath) || !File.Exists(request.MediaPath) || !IsTargetLanguage(config, lang))
        {
            return Task.FromResult(Enumerable.Empty<RemoteSubtitleInfo>());
        }

        string title = Title(request);
        var results = new List<RemoteSubtitleInfo>();
        foreach (var (path, t, x, created) in _jobs.Cached(request.MediaPath, lang))
        {
            results.Add(Info(request, new SubtitleJob(request.MediaPath, title, lang, t, x), $"✓ Готови · {t} + {x} · {created.ToLocalTime():dd.MM.yyyy HH:mm}", "Вече генерирани, без нов разход."));
        }

        // Never spend money on automatic searches (library scans, "download missing subtitles").
        if (!request.IsAutomated)
        {
            var duration = request.RuntimeTicks is > 0 ? TimeSpan.FromTicks(request.RuntimeTicks.Value) : TimeSpan.FromMinutes(100);
            foreach (var (t, x) in _jobs.AvailableCombinations())
            {
                if (results.Any(r => r.Id == Encode(new SubtitleJob(request.MediaPath, title, lang, t.Id, x.Id))))
                {
                    continue;
                }

                double cost = _jobs.Estimate(t, x, request.MediaPath, duration);
                string name = string.Create(CultureInfo.InvariantCulture, $"AI · {_jobs.ModelLabel(t, x)} · ≈ ${cost:0.00}");
                string comment = string.Create(CultureInfo.InvariantCulture, $"Генериране от аудиото ({duration.TotalMinutes:0} мин): ≈ ${cost:0.00}, обикновено 3–10 минути. Таксува се при избор.");
                results.Add(Info(request, new SubtitleJob(request.MediaPath, title, lang, t.Id, x.Id), name, comment));
            }
        }

        return Task.FromResult<IEnumerable<RemoteSubtitleInfo>>(results);
    }

    public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
    {
        var job = Decode(id);
        _logger.LogInformation("AI Translate: {Title} → {Lang} with {Transcriber}+{Translator}", job.Title, job.TargetLanguage, job.TranscriberId, job.TranslatorId);

        // The job is not tied to this request: if the client times out, it finishes and shows up as "✓ Готови".
        string path = await _jobs.RunAsync(job).WaitAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new SubtitleResponse
        {
            Format = "srt",
            Language = job.TargetLanguage,
            IsForced = false,
            Stream = new MemoryStream(bytes),
        };
    }

    internal static string Encode(SubtitleJob job) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static SubtitleJob Decode(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        return JsonSerializer.Deserialize<SubtitleJob>(Convert.FromBase64String(b64)) ?? throw new ArgumentException("Bad subtitle id", nameof(id));
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
        var allowed = config.TargetLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return allowed.Length == 0 || allowed.Contains(lang, StringComparer.OrdinalIgnoreCase);
    }

    private static string Title(SubtitleSearchRequest r)
    {
        if (!string.IsNullOrEmpty(r.SeriesName))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{r.SeriesName} S{r.ParentIndexNumber:00}E{r.IndexNumber:00} {r.Name}");
        }

        return r.ProductionYear is { } y ? $"{r.Name} ({y})" : r.Name ?? Path.GetFileNameWithoutExtension(r.MediaPath);
    }
}
