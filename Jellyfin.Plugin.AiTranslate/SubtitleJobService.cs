using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Billing;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Media;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>What the user picked in the subtitle search.</summary>
public sealed record SubtitleJob(
    string MediaPath,
    string Title,
    string TargetLanguage,
    string TranscriberId,
    string TranslatorId,
    string SourceKind = Subtitles.SourceKind.Audio,
    string? SourceRef = null,
    string? SourceLanguage = null)
{
    /// <summary>Gets the cache key of the source: the transcriber for audio, "sub-en" for an English subtitle.</summary>
    public string SourceKey => SourceKind == Subtitles.SourceKind.Audio ? TranscriberId : "sub-" + SourceLanguage;
}

/// <summary>
/// Status of a job for the settings page. <paramref name="Id"/> stops a running job (or hides a finished one's note);
/// <paramref name="Running"/> is false for a job that ended with an error that is kept on show.
/// </summary>
public sealed record JobStatus(string Title, string TargetLanguage, string Combination, string Stage, DateTime StartedUtc, string Id = "", bool Running = true);

/// <summary>Runs transcription (or reads existing subtitles) + translation, caches every step and records the cost.</summary>
public sealed class SubtitleJobService
{
    // Measured with Gemini 3.8 Flash, thinking "low": ~21 input and ~19 output tokens per subtitle line (+ batch overhead).
    private const double TokensInPerCue = 25;
    private const double TokensOutPerCue = 25;

    // Dialogue density when the line count is unknown (measured 8–10 lines per minute; rounded up).
    private const double CuesPerMinute = 12;

    private readonly IReadOnlyList<ITranscriber> _transcribers;
    private readonly IReadOnlyList<ITranslator> _translators;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly IServiceProvider _services;
    private readonly ILogger<SubtitleJobService> _logger;
    private readonly SingleFlight<string, string> _flights = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobStatus> _status = new(StringComparer.Ordinal);

    public SubtitleJobService(
        OpenAiProvider openAi,
        GeminiProvider gemini,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        IServiceProvider services,
        ILogger<SubtitleJobService> logger)
    {
        _transcribers = [openAi, gemini];
        _translators = [openAi, gemini];
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _services = services;
        _logger = logger;
        Ledger = new Ledger(DataFolder);
    }

    public Ledger Ledger { get; }

    public IReadOnlyCollection<JobStatus> Running => _status.Values.ToList();

    private static PluginConfiguration Config => ConfigOverride ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets or sets a configuration used instead of the plugin's (tests only).</summary>
    internal static PluginConfiguration? ConfigOverride { get; set; }

    private string DataFolder => PluginData.Folder(_appPaths);

    /// <summary>The active translator (or, when comparing, every enabled one) among those with a key.</summary>
    public IEnumerable<ITranslator> AvailableTranslators()
    {
        var c = Config;
        var keyed = _translators.Where(x => x.IsAvailable(c)).ToList();
        return c.CompareProviders
            ? keyed.Where(x => IsEnabled(x, c, transcription: false))
            : Active(keyed, c.ActiveTranslator);
    }

    /// <summary>Transcriber × translator pairs: the active ones, or every enabled pair when comparing.</summary>
    public IEnumerable<(ITranscriber Transcriber, ITranslator Translator)> AvailableCombinations()
    {
        var c = Config;
        var keyed = _transcribers.Where(t => t.IsAvailable(c)).ToList();
        var transcribers = c.CompareProviders ? keyed.Where(t => IsEnabled(t, c, transcription: true)) : Active(keyed, c.ActiveTranscriber);
        foreach (var t in transcribers)
        {
            foreach (var x in AvailableTranslators())
            {
                yield return (t, x);
            }
        }
    }

    /// <summary>Providers with a key, for the settings page: (id, name, has key).</summary>
    public IEnumerable<(string Id, string Name, bool HasKey)> ProviderKeys()
    {
        var c = Config;
        return _translators.Select(x => (x.Id, x.DisplayName(c), x.IsAvailable(c)));
    }

    // The configured provider if it has a key, otherwise the first one that has.
    private static IEnumerable<T> Active<T>(IReadOnlyList<T> keyed, string activeId)
        where T : class
    {
        var chosen = keyed.FirstOrDefault(p => p is ITranslator x ? x.Id == activeId : p is ITranscriber t && t.Id == activeId) ?? keyed.FirstOrDefault();
        return chosen is null ? [] : [chosen];
    }

    public string ModelLabel(ITranscriber t, ITranslator x)
    {
        var c = Config;
        return $"{t.DisplayName(c)} {TranscribeModel(t, c)} + {TranslatorLabel(x)}";
    }

    public string TranslatorLabel(ITranslator x)
    {
        var c = Config;
        return $"{x.DisplayName(c)} {(x.Id == "openai" ? c.OpenAiTextModel : c.GeminiTextModel)}";
    }

    private static string TranscribeModel(ITranscriber t, PluginConfiguration c) => t.Id == "openai" ? c.OpenAiTranscribeModel : c.GeminiTranscribeModel;

    // Everything besides the request text that changes a translation: provider, model and thinking level.
    private static string TranslationCacheKey(ITranslator x, PluginConfiguration c) =>
        x.Id == "openai" ? $"openai|{c.OpenAiTextModel}|{c.OpenAiReasoningEffort}" : $"gemini|{c.GeminiTextModel}|{c.GeminiThinkingLevel}";

    /// <summary>Estimated USD for an audio job; a cached transcript is free.</summary>
    public double Estimate(ITranscriber t, ITranslator x, string mediaPath, TimeSpan duration)
    {
        double transcribe = File.Exists(TranscriptPath(mediaPath, t.Id)) ? 0 : duration.TotalMinutes * t.PricePerMinute(Config);
        return transcribe + EstimateTranslation(x, duration);
    }

    /// <summary>Estimated USD for translating <paramref name="cueCount"/> lines (or a film of this length when unknown).</summary>
    public double EstimateTranslation(ITranslator x, TimeSpan duration, int? cueCount = null)
    {
        var (pi, po) = x.PricePerMTok(Config);
        double cues = cueCount ?? (duration.TotalMinutes * CuesPerMinute);
        return ((cues * TokensInPerCue * pi) + (cues * TokensOutPerCue * po)) / 1_000_000;
    }

    /// <summary>Translation wall-clock time: batches of 50 lines, 3 at a time; measured ~6 s per round (2250 lines in 88 s).</summary>
    public static TimeSpan TranslationTime(TimeSpan duration, int? cueCount = null)
    {
        double cues = cueCount ?? (duration.TotalMinutes * CuesPerMinute);
        double rounds = Math.Ceiling(Math.Ceiling(cues / Translation.BatchSize) / 3);
        return TimeSpan.FromMinutes(0.5 + (rounds * 0.12));
    }

    /// <summary>Rough wall-clock time; a cached transcript only needs the translation.</summary>
    public TimeSpan EstimatedTime(ITranscriber t, string mediaPath, TimeSpan duration) =>
        File.Exists(TranscriptPath(mediaPath, t.Id)) ? TimeSpan.FromMinutes(2) : t.EstimatedTime(duration, Config);

    /// <summary>Finished subtitle files for this media and language: (path, source key, translator id, created).</summary>
    public IEnumerable<(string Path, string SourceKey, string TranslatorId, DateTime Created)> Cached(string mediaPath, string targetLanguage)
    {
        var dir = MediaFolder(mediaPath);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(dir, targetLanguage + ".*.srt"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('.');
            if (parts.Length == 3)
            {
                yield return (file, parts[1], parts[2], File.GetLastWriteTimeUtc(file));
            }
        }
    }

    /// <summary>
    /// Starts (or joins) the job; <paramref name="started"/> is true for the one caller that started it. It keeps running if
    /// the caller gives up, so a slow client can fetch the result later.
    /// </summary>
    public Task<string> RunAsync(SubtitleJob job, out bool started)
    {
        string key = JobKey(job);
        return _flights.Run(
            key,
            async cancellationToken =>
            {
                try
                {
                    return await ExecuteAsync(job, key, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _status.TryRemove(key, out _);
                }
            },
            out started);
    }

    public bool IsRunning(SubtitleJob job) => _flights.IsRunning(JobKey(job));

    /// <summary>Stops the running job with this status id; what is finished stays cached, so it can continue later.</summary>
    public bool Cancel(string id)
    {
        var key = _status.FirstOrDefault(s => s.Value.Id == id).Key;
        return key is not null && _flights.Cancel(key);
    }

    private static string JobKey(SubtitleJob job) => $"{job.MediaPath}|{job.TargetLanguage}|{job.SourceKey}|{job.TranslatorId}";

    /// <summary>Returns the finished file for a cached job id, if any.</summary>
    public string? CachedPath(SubtitleJob job)
    {
        string path = OutputPath(job);
        return File.Exists(path) ? path : null;
    }

    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    private async Task<string> ExecuteAsync(SubtitleJob job, string key, CancellationToken cancellationToken)
    {
        var config = Config;
        var translator = _translators.First(t => t.Id == job.TranslatorId);
        string output = OutputPath(job);
        if (File.Exists(output))
        {
            return output;
        }

        var extractor = new AudioExtractor(_mediaEncoder.EncoderPath, _mediaEncoder.ProbePath);
        var duration = await extractor.GetDurationAsync(job.MediaPath, cancellationToken).ConfigureAwait(false);
        bool fromAudio = job.SourceKind == SourceKind.Audio;
        ITranscriber? transcriber = fromAudio ? _transcribers.First(t => t.Id == job.TranscriberId) : null;
        string label = transcriber is not null ? ModelLabel(transcriber, translator) : $"{job.SourceKey} + {TranslatorLabel(translator)}";
        double estimate = transcriber is not null ? Estimate(transcriber, translator, job.MediaPath, duration) : EstimateTranslation(translator, duration);
        using var budget = Ledger.Reserve(estimate, config.MonthlyBudgetUsd, job.Title, DateTime.UtcNow);
        string id = "sub-" + Hash(key);
        void Stage(string stage) => _status[key] = new JobStatus(job.Title, job.TargetLanguage, label, stage, _status.TryGetValue(key, out var s) ? s.StartedUtc : DateTime.UtcNow, id);

        // 1) Source text with timings: speech recognition (cached per transcriber) or an existing subtitle.
        List<Cue> cues;
        string? sourceLanguage;
        if (transcriber is not null)
        {
            (cues, sourceLanguage) = await GetTranscriptAsync(job, transcriber, extractor, config, Stage, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            Stage("четене на субтитрите");
            cues = await ReadSourceSubtitlesAsync(job, config, cancellationToken).ConfigureAwait(false);
            sourceLanguage = job.SourceLanguage;
        }

        if (cues.Count == 0)
        {
            throw new InvalidOperationException(fromAudio ? "No speech was recognized in the audio track." : "The source subtitle has no text.");
        }

        // 2) Translation (skipped when the source is already in the target language).
        List<Cue> result;
        if (string.Equals(sourceLanguage, job.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            result = cues;
        }
        else
        {
            Stage($"превод ({cues.Count} реплики)");
            var usage = new List<Usage>();
            try
            {
                result = await new Translation(_logger)
                    .TranslateAsync(translator, cues, sourceLanguage, job.TargetLanguage, job.Title, config, Path.Combine(MediaFolder(job.MediaPath), "batches"), TranslationCacheKey(translator, config), usage, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                // What was paid for counts against the budget even when the job stops.
                Record(job, "translate", usage);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var final = FinalCues(result, fromAudio);
        CueTools.WriteSrt(output, final);
        _logger.LogInformation("AI Translate: {Title} → {Lang}: {Count} subtitles ({Combination})", job.Title, job.TargetLanguage, final.Count, label);
        return output;
    }

    /// <summary>
    /// Recognised speech gets its timings repaired (see <see cref="CueTools.Repair"/>); subtitles made by people keep
    /// theirs — a cue shown for 10 s is meant to be. Both are re-wrapped and too-short cues are stretched.
    /// </summary>
    internal static List<Cue> FinalCues(IReadOnlyList<Cue> cues, bool fromAudio) => CueTools.Polish(fromAudio ? CueTools.Repair(cues) : cues);

    private async Task<List<Cue>> ReadSourceSubtitlesAsync(SubtitleJob job, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        try
        {
            // A "finished" entry from the search carries no source; it can only be served from the cache.
            string source = job.SourceRef ?? throw new InvalidOperationException("These subtitles are no longer in the cache; search again.");
            string path;
            switch (job.SourceKind)
            {
                case SourceKind.File:
                    path = source;
                    break;
                case SourceKind.Embedded:
                    path = await SubtitleSources.ExtractEmbeddedAsync(job.MediaPath, int.Parse(source, CultureInfo.InvariantCulture), _mediaEncoder.EncoderPath, work, cancellationToken).ConfigureAwait(false);
                    break;
                case SourceKind.Remote:
                    var manager = _services.GetRequiredService<ISubtitleManager>();
                    var response = await manager.GetRemoteSubtitles(source, cancellationToken).ConfigureAwait(false);
                    Directory.CreateDirectory(work);
                    path = Path.Combine(work, "remote." + (string.IsNullOrEmpty(response.Format) ? "srt" : response.Format.ToLowerInvariant()));
                    await using (var remote = response.Stream)
                    await using (var file = File.Create(path))
                    {
                        await remote.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new InvalidOperationException("Unknown subtitle source " + job.SourceKind);
            }

            return await SubtitleSources.ReadAsync(path, _mediaEncoder.EncoderPath, work, config.StripHearingImpaired, job.SourceLanguage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    private async Task<(List<Cue> Cues, string? Language)> GetTranscriptAsync(
        SubtitleJob job,
        ITranscriber transcriber,
        AudioExtractor extractor,
        PluginConfiguration config,
        Action<string> stage,
        CancellationToken cancellationToken)
    {
        string path = TranscriptPath(job.MediaPath, transcriber.Id);
        if (File.Exists(path))
        {
            var cached = JsonSerializer.Deserialize<CachedTranscript>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))!;
            return (CueTools.Repair(CueTools.ParseSrt(cached.Srt)), cached.Language);
        }

        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        var usage = new List<Usage>();
        try
        {
            stage("извличане на аудио");
            var chunkLength = transcriber.ChunkLength(config);
            int audioIndex = await extractor.GetDefaultAudioIndexAsync(job.MediaPath, cancellationToken).ConfigureAwait(false);
            var chunks = await extractor.ExtractAsync(job.MediaPath, audioIndex, chunkLength, work, cancellationToken).ConfigureAwait(false);

            // The same file, track, model and chunk length give the same chunks, so recognised ones can be reused.
            string chunkCache = Path.Combine(
                MediaFolder(job.MediaPath),
                "chunks",
                Hash(string.Create(CultureInfo.InvariantCulture, $"{transcriber.Id}|{TranscribeModel(transcriber, config)}|{(int)chunkLength.TotalSeconds}|{audioIndex}|{MediaStamp(job.MediaPath)}")));
            int done = 0;
            stage($"разпознаване на говор 0/{chunks.Count}");
            var parts = await TranscribeChunksAsync(transcriber, chunks, config, chunkCache, usage, () => stage($"разпознаване на говор {Interlocked.Increment(ref done)}/{chunks.Count}"), cancellationToken).ConfigureAwait(false);

            var cues = parts
                .SelectMany((p, i) => p.Cues.Select(c => c with { Start = c.Start + chunks[i].Offset, End = c.End + chunks[i].Offset }))
                .ToList();
            string? language = parts.Select(p => p.Language).FirstOrDefault(l => l is not null);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Paid for: saved even if the job is being stopped.
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CachedTranscript(language, CueTools.ToSrt(cues), job.MediaPath)), CancellationToken.None).ConfigureAwait(false);
            TryDeleteDirectory(chunkCache);
            return (cues, language);
        }
        finally
        {
            // Chunks that were paid for count against the budget even when another chunk failed.
            Record(job, "transcribe", usage);
            TryDeleteDirectory(work);
        }
    }

    /// <summary>
    /// Recognises the chunks (<see cref="ITranscriber.Parallelism"/> at a time). Each finished chunk is saved in
    /// <paramref name="cacheDir"/> at once, so a run that stops half way (rate limit, server error, restart, Stop)
    /// continues from there and does not pay for those chunks again. Paid requests are added to <paramref name="usage"/>.
    /// </summary>
    internal static async Task<Transcription[]> TranscribeChunksAsync(
        ITranscriber transcriber,
        IReadOnlyList<AudioChunk> chunks,
        PluginConfiguration config,
        string cacheDir,
        ICollection<Usage> usage,
        Action progress,
        CancellationToken cancellationToken = default)
    {
        var parts = new Transcription[chunks.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunks.Count),
            new ParallelOptions { MaxDegreeOfParallelism = transcriber.Parallelism, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                string cache = Path.Combine(cacheDir, i.ToString("000", CultureInfo.InvariantCulture) + ".json");
                var part = await ReadChunkAsync(cache, ct).ConfigureAwait(false);
                if (part is null)
                {
                    part = await transcriber.TranscribeAsync(chunks[i], config, ct).ConfigureAwait(false);
                    lock (usage)
                    {
                        usage.Add(part.Usage);
                    }

                    // Paid for: saved even if the job is being stopped.
                    Directory.CreateDirectory(cacheDir);
                    await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(part), CancellationToken.None).ConfigureAwait(false);
                }

                parts[i] = part;
                progress();
            }).ConfigureAwait(false);
        return parts;
    }

    private static async Task<Transcription?> ReadChunkAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Transcription>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Size and modification time: a replaced file at the same path must not reuse old chunks.
    private static string MediaStamp(string mediaPath)
    {
        var info = new FileInfo(mediaPath);
        return info.Exists ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}|{info.LastWriteTimeUtc.Ticks}") : string.Empty;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Record(SubtitleJob job, string kind, IEnumerable<Usage> usage)
    {
        foreach (var group in usage.GroupBy(u => (u.Provider, u.Model)))
        {
            Ledger.Add(new UsageEntry(
                DateTime.UtcNow,
                job.Title,
                kind,
                group.Key.Provider,
                group.Key.Model,
                group.Sum(u => u.AudioMinutes),
                group.Sum(u => u.InputTokens),
                group.Sum(u => u.OutputTokens),
                group.Sum(u => u.CostUsd)));
        }
    }

    private static bool IsEnabled(object provider, PluginConfiguration c, bool transcription) => (provider, transcription) switch
    {
        (OpenAiProvider, true) => c.EnableOpenAiTranscription,
        (OpenAiProvider, false) => c.EnableOpenAiTranslation,
        (GeminiProvider, true) => c.EnableGeminiTranscription,
        (GeminiProvider, false) => c.EnableGeminiTranslation,
        _ => false,
    };

    private string OutputPath(SubtitleJob job) => Path.Combine(MediaFolder(job.MediaPath), $"{job.TargetLanguage}.{job.SourceKey}.{job.TranslatorId}.srt");

    private string MediaFolder(string mediaPath) => Path.Combine(DataFolder, "cache", Hash(mediaPath));

    private string TranscriptPath(string mediaPath, string transcriberId) => Path.Combine(MediaFolder(mediaPath), $"transcript.{transcriberId}.json");

    private sealed record CachedTranscript(string? Language, string Srt, string MediaPath);
}
