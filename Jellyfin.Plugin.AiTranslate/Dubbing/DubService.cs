using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Billing;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Media;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate.Dubbing;

/// <summary>What a voice-over of one item would cost.</summary>
public sealed record DubEstimate(
    Guid ItemId,
    string Title,
    string SubtitleLabel,
    int Segments,
    int Characters,
    double SpeechMinutes,
    double CostUsd,
    double Minutes,
    bool Exists,
    int Requests = 0,
    int RequestsPerDay = 0);

/// <summary>Voice-over: Bulgarian (target-language) subtitles → TTS → ducked mix with the original audio → "Film.bg.mka".</summary>
public sealed class DubService
{
    public const string TrackTitle = "Български (AI войсоувър)";
    private const int OutputRate = 48000;

    private readonly ITtsProvider _tts;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly SubtitleJobService _jobs;
    private readonly ILogger<DubService> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly ConcurrentDictionary<Guid, JobStatus> _status = new();

    public DubService(
        GeminiTts tts,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        SubtitleJobService jobs,
        ILogger<DubService> logger)
    {
        _tts = tts;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _jobs = jobs;
        _logger = logger;
    }

    public IReadOnlyCollection<JobStatus> Running => _status.Values.ToList();

    public ITtsProvider Tts => _tts;

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string DataFolder => Plugin.Instance?.DataFolderPath ?? Path.Combine(_appPaths.DataPath, "ai-translate");

    /// <summary>Movies and episodes matching <paramref name="search"/> that have a text subtitle in the target language.</summary>
    public IEnumerable<Video> Candidates(string search, string language)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Episode],
            SearchTerm = search,
            Recursive = true,
            Limit = 30,
        });
        return items.OfType<Video>().Where(v => FindSubtitle(v, language) is not null);
    }

    public async Task<DubEstimate> EstimateAsync(Guid itemId, string language, CancellationToken cancellationToken)
    {
        var video = GetVideo(itemId);
        var stream = FindSubtitle(video, language) ?? throw new InvalidOperationException("Няма текстови субтитри на " + language);
        var segments = DubPlanner.Plan(await ReadSubtitleAsync(video, stream, cancellationToken).ConfigureAwait(false), Config.DubSkipSongs);
        int chars = segments.Sum(s => s.Text.Length);
        double seconds = chars / DubPlanner.CharactersPerSecond;
        double cost = _tts.Estimate(chars, seconds, Config) * (1 - CachedShare(video, segments));
        var config = Config;
        var missing = Enumerable.Range(0, segments.Count).Where(i => !File.Exists(ClipPath(video, config.DubVoice, config, segments[i].Text))).ToList();
        int requests = Batches(missing, segments, config).Count;
        double minutes = 3 + Math.Max(
            GeminiTts.PacedTime(requests, config).TotalMinutes,
            requests * 20.0 / 60 / Math.Max(1, config.DubParallelism));
        return new DubEstimate(itemId, video.Name, Label(stream), segments.Count, chars, seconds / 60, cost, minutes, File.Exists(OutputPath(video, language)), requests, config.GeminiTtsRequestsPerDay);
    }

    /// <summary>Starts a voice-over in the background (one per item).</summary>
    public bool Start(Guid itemId, string language, string? voice)
    {
        var video = GetVideo(itemId);
        if (_running.ContainsKey(itemId))
        {
            return false;
        }

        _status[itemId] = new JobStatus(video.Name, language, "voice-over", "стартиране", DateTime.UtcNow);

        _running[itemId] = Task.Run(async () =>
        {
            try
            {
                await RunAsync(video, language, string.IsNullOrWhiteSpace(voice) ? Config.DubVoice : voice!).ConfigureAwait(false);
                _status.TryRemove(itemId, out _);
            }
            catch (Exception ex)
            {
                // Keep the error visible on the settings page; a new run may start right away.
                _logger.LogError(ex, "AI Translate: voice-over of {Title} failed", video.Name);
                _status[itemId] = new JobStatus(video.Name, language, "voice-over", "грешка: " + ex.Message, DateTime.UtcNow);
            }
            finally
            {
                _running.TryRemove(itemId, out _);
            }
        });
        return true;
    }

    internal static string OutputPath(Video video, string language) =>
        Path.Combine(Path.GetDirectoryName(video.Path)!, Path.GetFileNameWithoutExtension(video.Path) + "." + language + ".mka");

    private async Task RunAsync(Video video, string language, string voice)
    {
        var config = Config;
        var started = DateTime.UtcNow;
        string label = $"{_tts.DisplayName(config)} · {voice}";
        void Stage(string s) => _status[video.Id] = new JobStatus(video.Name, language, label, s, started);

        var stream = FindSubtitle(video, language) ?? throw new InvalidOperationException("Няма текстови субтитри на " + language);
        Stage("четене на субтитрите");
        var segments = DubPlanner.Plan(await ReadSubtitleAsync(video, stream, CancellationToken.None).ConfigureAwait(false), config.DubSkipSongs);
        int chars = segments.Sum(s => s.Text.Length);
        CheckBudget(config, _tts.Estimate(chars, chars / DubPlanner.CharactersPerSecond, config) * (1 - CachedShare(video, segments)), video.Name);

        // 1) Speech for every segment (cached on disk per line, so an interrupted run resumes for free).
        //    Several lines go into one request (TTS quotas count requests) and are split again at the pauses.
        var clips = new short[segments.Count][];
        var usage = new ConcurrentBag<Usage>();
        int rate = 24000;
        var missing = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            string cache = ClipPath(video, voice, config, segments[i].Text);
            if (File.Exists(cache))
            {
                (clips[i], rate) = Wav.ReadPcm16Mono(await File.ReadAllBytesAsync(cache).ConfigureAwait(false));
            }
            else
            {
                missing.Add(i);
            }
        }

        var batches = Batches(missing, segments, config);
        int done = segments.Count - missing.Count;
        try
        {
            Stage($"синтез на глас {done}/{segments.Count} ({batches.Count} заявки)");
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.DubParallelism, 1, 8) },
                async (batch, ct) =>
                {
                    string text = string.Join(config.DubLineSeparator, batch.Select(i => segments[i].Text));
                    var clip = await _tts.SynthesizeAsync(text, voice, config.DubStyle, config, ct).ConfigureAwait(false);
                    usage.Add(clip.Usage);
                    rate = clip.SampleRate;
                    var pieces = ClipSplitter.Split(clip.Samples, clip.SampleRate, batch.Select(i => segments[i].Text.Length).ToList());
                    for (int k = 0; k < batch.Count; k++)
                    {
                        clips[batch[k]] = pieces[k];
                        string cache = ClipPath(video, voice, config, segments[batch[k]].Text);
                        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                        await File.WriteAllBytesAsync(cache, Wav.WritePcm16Mono(pieces[k], clip.SampleRate), ct).ConfigureAwait(false);
                    }

                    Stage($"синтез на глас {Interlocked.Add(ref done, batch.Count)}/{segments.Count}");
                }).ConfigureAwait(false);
        }
        finally
        {
            // Record what was spent even if the run stops half way.
            RecordUsage(video.Name, usage);
        }

        // 2) Timeline: fit every clip into its slot, speed up where needed.
        Stage("подреждане на репликите");
        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var durations = clips.Select(c => TimeSpan.FromSeconds(c.Length / (double)rate)).ToList();
            var placed = DubPlanner.Place(segments, durations, config.DubMaxTempo);
            string narration = Path.Combine(work, "narration.raw");
            await WriteNarrationAsync(narration, clips, placed, rate, work).ConfigureAwait(false);
            _logger.LogInformation(
                "AI Translate: voice-over {Title}: {Count} segments, {Fast} sped up, {Late} shifted",
                video.Name,
                segments.Count,
                placed.Count(p => p.Tempo > 1.01),
                placed.Where((p, i) => p.Start > segments[i].Start + TimeSpan.FromMilliseconds(200)).Count());

            // 3) Mix with the original audio, ducked under the voice.
            Stage("миксиране със звука на филма");
            var extractor = new AudioExtractor(_mediaEncoder.EncoderPath, _mediaEncoder.ProbePath);
            int audioIndex = await extractor.GetDefaultAudioIndexAsync(video.Path, CancellationToken.None).ConfigureAwait(false);
            string output = OutputPath(video, language);
            string part = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileName(output) + ".part");
            var r = await AudioExtractor.RunAsync(_mediaEncoder.EncoderPath, MixArguments(video.Path, audioIndex, narration, rate, language, part), CancellationToken.None).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                File.Delete(part);
                throw new InvalidOperationException("ffmpeg mix failed: " + (r.StdErr.Length > 600 ? r.StdErr[^600..] : r.StdErr));
            }

            File.Move(part, output, overwrite: true);
            _logger.LogInformation("AI Translate: voice-over ready: {Output}", output);
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

        // 4) Let Jellyfin pick up the new external audio track.
        _providerManager.QueueRefresh(
            video.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem)) { MetadataRefreshMode = MetadataRefreshMode.FullRefresh, ImageRefreshMode = MetadataRefreshMode.None },
            RefreshPriority.High);
    }

    /// <summary>Groups consecutive missing lines into requests of at most N lines / 700 characters.</summary>
    internal static List<List<int>> Batches(IReadOnlyList<int> missing, IReadOnlyList<DubSegment> segments, PluginConfiguration config)
    {
        int perRequest = Math.Clamp(config.DubLinesPerRequest, 1, 30);
        var batches = new List<List<int>>();
        foreach (int i in missing)
        {
            var last = batches.Count > 0 ? batches[^1] : null;
            if (last is null || last.Count >= perRequest || last[^1] != i - 1 || last.Sum(j => segments[j].Text.Length) + segments[i].Text.Length > 700)
            {
                batches.Add([i]);
            }
            else
            {
                last.Add(i);
            }
        }

        return batches;
    }

    internal static List<string> MixArguments(string mediaPath, int audioIndex, string narrationRaw, int narrationRate, string language, string output)
    {
        // Voice → stereo, louder; it also drives the compressor that lowers the film's audio while someone narrates.
        // apad: the narration ends with the last line, but the track must last as long as the film (credits).
        string graph = string.Create(
            CultureInfo.InvariantCulture,
            $"[1:a]aresample={OutputRate},pan=stereo|c0=c0|c1=c0,volume=1.3,apad,asplit=2[voice][sc];[0:a:{audioIndex}]aresample={OutputRate},aformat=channel_layouts=stereo[orig];[orig][sc]sidechaincompress=threshold=0.012:ratio=12:attack=20:release=450[duck];[duck][voice]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[out]");
        return
        [
            "-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y",
            "-i", mediaPath,
            "-f", "s16le", "-ar", narrationRate.ToString(CultureInfo.InvariantCulture), "-ac", "1", "-i", narrationRaw,
            "-filter_complex", graph, "-map", "[out]",
            "-c:a", "aac", "-b:a", "192k",
            "-metadata:s:a:0", "language=" + CultureInfo.GetCultureInfo(language).ThreeLetterISOLanguageName,
            "-metadata:s:a:0", "title=" + TrackTitle,
            "-f", "matroska", output,
        ];
    }

    private async Task WriteNarrationAsync(string path, short[][] clips, IReadOnlyList<PlacedClip> placed, int rate, string work)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        long written = 0;
        var zeros = new byte[1 << 16];
        for (int i = 0; i < clips.Length; i++)
        {
            var samples = placed[i].Tempo > 1.01 ? await StretchAsync(clips[i], rate, placed[i].Tempo, work).ConfigureAwait(false) : clips[i];
            long startSample = (long)(placed[i].Start.TotalSeconds * rate);
            for (long gap = (startSample - written) * 2; gap > 0; gap -= zeros.Length)
            {
                await file.WriteAsync(zeros.AsMemory(0, (int)Math.Min(gap, zeros.Length))).ConfigureAwait(false);
            }

            written = Math.Max(written, startSample);
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await file.WriteAsync(bytes).ConfigureAwait(false);
            written += samples.Length;
        }
    }

    private async Task<short[]> StretchAsync(short[] samples, int rate, double tempo, string work)
    {
        string input = Path.Combine(work, "clip-in.wav");
        string output = Path.Combine(work, "clip-out.wav");
        await File.WriteAllBytesAsync(input, Wav.WritePcm16Mono(samples, rate)).ConfigureAwait(false);
        var r = await AudioExtractor.RunAsync(
            _mediaEncoder.EncoderPath,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-filter:a", "atempo=" + tempo.ToString("0.000", CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-ac", "1", output],
            CancellationToken.None).ConfigureAwait(false);
        return r.ExitCode == 0 ? Wav.ReadPcm16Mono(await File.ReadAllBytesAsync(output).ConfigureAwait(false)).Samples : samples;
    }

    private async Task<List<Cue>> ReadSubtitleAsync(Video video, MediaStream stream, CancellationToken cancellationToken)
    {
        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        try
        {
            string path = stream.IsExternal
                ? stream.Path
                : await SubtitleSources.ExtractEmbeddedAsync(video.Path, stream.Index, _mediaEncoder.EncoderPath, work, cancellationToken).ConfigureAwait(false);
            return await SubtitleSources.ReadAsync(path, _mediaEncoder.EncoderPath, work, stripHearingImpaired: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                {
                    Directory.Delete(work, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static MediaStream? FindSubtitle(Video video, string language) =>
        video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && !string.IsNullOrEmpty(s.Language)
                && Languages.ToTwoLetter(s.Language!) == language
                && s.Codec is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "vtt")
            .OrderBy(s => s.IsExternal ? 0 : 1)
            .FirstOrDefault();

    private static string Label(MediaStream s) => (s.IsExternal ? Path.GetFileName(s.Path) : "вградени") + " (" + s.Language + ")";

    private Video GetVideo(Guid itemId) =>
        _libraryManager.GetItemById(itemId) as Video ?? throw new InvalidOperationException("Item not found: " + itemId);

    private double CachedShare(Video video, IReadOnlyList<DubSegment> segments)
    {
        if (segments.Count == 0)
        {
            return 0;
        }

        var config = Config;
        return segments.Count(s => File.Exists(ClipPath(video, config.DubVoice, config, s.Text))) / (double)segments.Count;
    }

    private string ClipPath(Video video, string voice, PluginConfiguration config, string text) =>
        Path.Combine(DataFolder, "dub", SubtitleJobService.Hash(video.Path), $"{voice}-{SubtitleJobService.Hash(config.GeminiTtsModel + "|" + config.DubStyle + "|" + text)}.wav");

    private void CheckBudget(PluginConfiguration config, double estimate, string title)
    {
        double spent = _jobs.Ledger.MonthTotal(DateTime.UtcNow);
        if (config.MonthlyBudgetUsd > 0 && spent + estimate > config.MonthlyBudgetUsd)
        {
            throw new BudgetExceededException($"Месечният лимит ${config.MonthlyBudgetUsd:0.00} би бил надхвърлен: похарчени ${spent:0.00} + ≈${estimate:0.00} за „{title}“.");
        }
    }

    private void RecordUsage(string title, IEnumerable<Usage> usage)
    {
        var list = usage.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _jobs.Ledger.Add(new UsageEntry(
            DateTime.UtcNow,
            title,
            "tts",
            list[0].Provider,
            list[0].Model,
            list.Sum(u => u.AudioMinutes),
            list.Sum(u => u.InputTokens),
            list.Sum(u => u.OutputTokens),
            list.Sum(u => u.CostUsd)));
    }
}
