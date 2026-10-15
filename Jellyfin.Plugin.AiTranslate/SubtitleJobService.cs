using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>What the user picked in the subtitle search.</summary>
public sealed record SubtitleJob(string MediaPath, string Title, string TargetLanguage, string TranscriberId, string TranslatorId);

/// <summary>Status of a job for the settings page.</summary>
public sealed record JobStatus(string Title, string TargetLanguage, string Combination, string Stage, DateTime StartedUtc);

/// <summary>Runs transcription + translation, caches every step and records the cost.</summary>
public sealed class SubtitleJobService
{
    // Rough token use per minute of film for estimates (prompt + context in, target language out).
    private const double InputTokensPerMinute = 400;
    private const double OutputTokensPerMinute = 500;

    private readonly IReadOnlyList<ITranscriber> _transcribers;
    private readonly IReadOnlyList<ITranslator> _translators;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<SubtitleJobService> _logger;
    private readonly ConcurrentDictionary<string, Task<string>> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, JobStatus> _status = new(StringComparer.Ordinal);

    public SubtitleJobService(OpenAiProvider openAi, GeminiProvider gemini, IMediaEncoder mediaEncoder, IApplicationPaths appPaths, ILogger<SubtitleJobService> logger)
    {
        _transcribers = [openAi, gemini];
        _translators = [openAi, gemini];
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
        Ledger = new Ledger(DataFolder);
    }

    public Ledger Ledger { get; }

    public IReadOnlyCollection<JobStatus> Running => _status.Values.ToList();

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string DataFolder => Plugin.Instance?.DataFolderPath ?? Path.Combine(_appPaths.DataPath, "ai-translate");

    /// <summary>Transcriber × translator pairs that are enabled and have a key.</summary>
    public IEnumerable<(ITranscriber Transcriber, ITranslator Translator)> AvailableCombinations()
    {
        var c = Config;
        foreach (var t in _transcribers.Where(t => t.IsAvailable(c) && IsEnabled(t, c, transcription: true)))
        {
            foreach (var x in _translators.Where(x => x.IsAvailable(c) && IsEnabled(x, c, transcription: false)))
            {
                yield return (t, x);
            }
        }
    }

    public string ModelLabel(ITranscriber t, ITranslator x)
    {
        var c = Config;
        string tm = t.Id == "openai" ? c.OpenAiTranscribeModel : c.GeminiTranscribeModel;
        string xm = x.Id == "openai" ? c.OpenAiTextModel : c.GeminiTextModel;
        return $"{t.DisplayName(c)} {tm} + {x.DisplayName(c)} {xm}";
    }

    /// <summary>Estimated USD for a job; a cached transcript is free.</summary>
    public double Estimate(ITranscriber t, ITranslator x, string mediaPath, TimeSpan duration)
    {
        var c = Config;
        double minutes = duration.TotalMinutes;
        double transcribe = File.Exists(TranscriptPath(mediaPath, t.Id)) ? 0 : minutes * t.PricePerMinute(c);
        var (pi, po) = x.PricePerMTok(c);
        double translate = ((minutes * InputTokensPerMinute * pi) + (minutes * OutputTokensPerMinute * po)) / 1_000_000;
        return transcribe + translate;
    }

    /// <summary>Finished subtitle files for this media and language.</summary>
    public IEnumerable<(string Path, string TranscriberId, string TranslatorId, DateTime Created)> Cached(string mediaPath, string targetLanguage)
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

    /// <summary>Starts (or joins) the job. It keeps running if the caller gives up, so a slow client can fetch the result later.</summary>
    public Task<string> RunAsync(SubtitleJob job)
    {
        string key = $"{job.MediaPath}|{job.TargetLanguage}|{job.TranscriberId}|{job.TranslatorId}";
        return _running.GetOrAdd(key, k => Task.Run(async () =>
        {
            try
            {
                return await ExecuteAsync(job, key).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(key, out _);
                _status.TryRemove(key, out _);
            }
        }));
    }

    internal static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();

    private async Task<string> ExecuteAsync(SubtitleJob job, string key)
    {
        var config = Config;
        var transcriber = _transcribers.First(t => t.Id == job.TranscriberId);
        var translator = _translators.First(t => t.Id == job.TranslatorId);
        string output = Path.Combine(MediaFolder(job.MediaPath), $"{job.TargetLanguage}.{transcriber.Id}.{translator.Id}.srt");
        if (File.Exists(output))
        {
            return output;
        }

        var extractor = new AudioExtractor(_mediaEncoder.EncoderPath, _mediaEncoder.ProbePath);
        var duration = await extractor.GetDurationAsync(job.MediaPath, CancellationToken.None).ConfigureAwait(false);
        CheckBudget(config, Estimate(transcriber, translator, job.MediaPath, duration), job.Title);
        void Stage(string stage) => _status[key] = new JobStatus(job.Title, job.TargetLanguage, ModelLabel(transcriber, translator), stage, _status.TryGetValue(key, out var s) ? s.StartedUtc : DateTime.UtcNow);

        // 1) Transcript (cached per transcriber, reused for other languages and translators).
        var (cues, sourceLanguage) = await GetTranscriptAsync(job, transcriber, extractor, config, Stage).ConfigureAwait(false);
        if (cues.Count == 0)
        {
            throw new InvalidOperationException("No speech was recognized in the audio track.");
        }

        // 2) Translation (skipped when the film is already in the target language).
        List<Cue> result;
        if (string.Equals(sourceLanguage, job.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            result = cues;
        }
        else
        {
            Stage($"превод ({cues.Count} реплики)");
            var (translated, usage) = await new Translation(_logger)
                .TranslateAsync(translator, cues, sourceLanguage, job.TargetLanguage, job.Title, config, CancellationToken.None)
                .ConfigureAwait(false);
            Record(job, "translate", usage);
            result = translated;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        CueTools.WriteSrt(output, result);
        _logger.LogInformation("AI Translate: {Title} → {Lang}: {Count} subtitles ({Combination})", job.Title, job.TargetLanguage, result.Count, ModelLabel(transcriber, translator));
        return output;
    }

    private async Task<(List<Cue> Cues, string? Language)> GetTranscriptAsync(
        SubtitleJob job,
        ITranscriber transcriber,
        AudioExtractor extractor,
        PluginConfiguration config,
        Action<string> stage)
    {
        string path = TranscriptPath(job.MediaPath, transcriber.Id);
        if (File.Exists(path))
        {
            var cached = JsonSerializer.Deserialize<CachedTranscript>(await File.ReadAllTextAsync(path).ConfigureAwait(false))!;
            return (CueTools.ParseSrt(cached.Srt), cached.Language);
        }

        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        try
        {
            stage("извличане на аудио");
            int audioIndex = await extractor.GetDefaultAudioIndexAsync(job.MediaPath, CancellationToken.None).ConfigureAwait(false);
            var chunks = await extractor.ExtractAsync(job.MediaPath, audioIndex, transcriber.ChunkLength, work, CancellationToken.None).ConfigureAwait(false);

            var parts = new Transcription[chunks.Count];
            int done = 0;
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunks.Count),
                new ParallelOptions { MaxDegreeOfParallelism = 2 },
                async (i, ct) =>
                {
                    parts[i] = await transcriber.TranscribeAsync(chunks[i], config, ct).ConfigureAwait(false);
                    stage($"разпознаване на говор {Interlocked.Increment(ref done)}/{chunks.Count}");
                }).ConfigureAwait(false);
            Record(job, "transcribe", parts.Select(p => p.Usage));

            var cues = parts
                .SelectMany((p, i) => p.Cues.Select(c => c with { Start = c.Start + chunks[i].Offset, End = c.End + chunks[i].Offset }))
                .ToList();
            string? language = parts.Select(p => p.Language).FirstOrDefault(l => l is not null);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CachedTranscript(language, CueTools.ToSrt(cues), job.MediaPath))).ConfigureAwait(false);
            return (cues, language);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private void CheckBudget(PluginConfiguration config, double estimate, string title)
    {
        if (config.MonthlyBudgetUsd <= 0)
        {
            return;
        }

        double spent = Ledger.MonthTotal(DateTime.UtcNow);
        if (spent + estimate > config.MonthlyBudgetUsd)
        {
            throw new BudgetExceededException($"Месечният лимит ${config.MonthlyBudgetUsd:0.00} би бил надхвърлен: похарчени ${spent:0.00} + ≈${estimate:0.00} за „{title}“.");
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

    private string MediaFolder(string mediaPath) => Path.Combine(DataFolder, "cache", Hash(mediaPath));

    private string TranscriptPath(string mediaPath, string transcriberId) => Path.Combine(MediaFolder(mediaPath), $"transcript.{transcriberId}.json");

    private sealed record CachedTranscript(string? Language, string Srt, string MediaPath);
}
