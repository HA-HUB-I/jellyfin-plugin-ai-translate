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
    int RequestsPerDay = 0,
    int UsedToday = 0,
    DateTime? QuotaResetUtc = null,
    string Provider = "",
    string Voice = "");

/// <summary>Voice-over: Bulgarian (target-language) subtitles → TTS → ducked mix with the original audio → "Film.bg.mka".</summary>
public sealed class DubService
{
    public const string TrackTitle = "Български (AI войсоувър)";
    private const int OutputRate = 48000;

    private readonly IReadOnlyList<ITtsProvider> _providers;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly SubtitleJobService _jobs;
    private readonly ILogger<DubService> _logger;
    private readonly SingleFlight<Guid, bool> _flights = new();
    private readonly ConcurrentDictionary<Guid, JobStatus> _status = new();

    public DubService(
        GeminiTts gemini,
        OpenAiTts openAi,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        SubtitleJobService jobs,
        ILogger<DubService> logger)
    {
        _providers = [gemini, openAi];
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _jobs = jobs;
        _logger = logger;
        Quota = new QuotaTracker(DataFolder);
    }

    public IReadOnlyCollection<JobStatus> Running => _status.Values.ToList();

    public QuotaTracker Quota { get; }

    /// <summary>Voice providers that have a key.</summary>
    public IEnumerable<ITtsProvider> Providers => _providers.Where(p => p.IsAvailable(Config));

    /// <summary>The requested provider, else the configured one, else the first with a key.</summary>
    public ITtsProvider GetProvider(string? id) =>
        Providers.FirstOrDefault(p => p.Id == id)
        ?? Providers.FirstOrDefault(p => p.Id == Config.DubProvider)
        ?? Providers.FirstOrDefault()
        ?? throw new InvalidOperationException("Няма въведен API ключ за доставчик на глас (Gemini или OpenAI).");

    /// <summary>The requested voice if the provider has it, else the configured one, else the provider's first.</summary>
    public static string GetVoice(ITtsProvider tts, string? voice)
    {
        foreach (var v in new[] { voice, Config.DubVoice })
        {
            if (!string.IsNullOrWhiteSpace(v) && tts.Voices.Any(x => string.Equals(x.Id, v, StringComparison.OrdinalIgnoreCase)))
            {
                return tts.Voices.First(x => string.Equals(x.Id, v, StringComparison.OrdinalIgnoreCase)).Id;
            }
        }

        return tts.Voices[0].Id;
    }

    /// <summary>The configured female voice if the provider has it, otherwise the provider's first female voice.</summary>
    public static string GetFemaleVoice(ITtsProvider tts, string? voice)
    {
        var match = tts.Voices.FirstOrDefault(x => string.Equals(x.Id, voice, StringComparison.OrdinalIgnoreCase));
        if (match.Id is not null)
        {
            return match.Id;
        }

        var female = tts.Voices.FirstOrDefault(x => x.Description.StartsWith("женски", StringComparison.Ordinal));
        return female.Id ?? tts.Voices[0].Id;
    }

    private static string QuotaKey(ITtsProvider tts, PluginConfiguration config) => tts.Id + ":" + tts.Model(config);

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private string DataFolder => PluginData.Folder(_appPaths);

    /// <summary>Stops a running voice-over (what is spoken so far stays cached), or hides the note of one that ended.</summary>
    public bool Cancel(string id)
    {
        var entry = _status.FirstOrDefault(s => s.Value.Id == id);
        if (entry.Value is null)
        {
            return false;
        }

        return _flights.Cancel(entry.Key) || _status.TryRemove(entry.Key, out _);
    }

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

    public async Task<DubEstimate> EstimateAsync(Guid itemId, string language, string? providerId, string? voice, CancellationToken cancellationToken)
    {
        var config = Config;
        var tts = GetProvider(providerId);
        voice = GetVoice(tts, voice);
        var video = GetVideo(itemId);
        var stream = FindSubtitle(video, language) ?? throw new InvalidOperationException("Няма текстови субтитри на " + language);
        // Without the (slow) voice analysis: every line counted once with the main voice — close enough for a price.
        var segments = DubPlanner.Plan(await ReadSubtitleAsync(video, stream, cancellationToken).ConfigureAwait(false), config.DubSkipSongs);
        string femaleVoice = config.DubSplitByGender ? GetFemaleVoice(tts, config.DubVoiceFemale) : voice;
        var missing = Enumerable.Range(0, segments.Count)
            .Where(i => !File.Exists(ClipPath(video, tts, voice, config, segments[i].Text)) && !File.Exists(ClipPath(video, tts, femaleVoice, config, segments[i].Text)))
            .ToList();
        int chars = missing.Sum(i => segments[i].Text.Length);
        double seconds = segments.Sum(s => s.Text.Length) / DubPlanner.CharactersPerSecond;
        double cost = tts.Estimate(chars, chars / DubPlanner.CharactersPerSecond, config);
        int requests = Batches(missing, segments, config).Count;
        double minutes = 3 + Math.Max(
            requests / (double)Math.Max(1, tts.RequestsPerMinute(config)),
            requests * 20.0 / 60 / Math.Max(1, config.DubParallelism));
        int perDay = tts.RequestsPerDay(config);
        return new DubEstimate(
            itemId,
            video.Name,
            Label(stream),
            segments.Count,
            segments.Sum(s => s.Text.Length),
            seconds / 60,
            cost,
            minutes,
            File.Exists(OutputPath(video, language)),
            requests,
            perDay,
            perDay > 0 ? Quota.UsedToday(QuotaKey(tts, config), DateTime.UtcNow) : 0,
            perDay > 0 ? QuotaTracker.NextResetUtc(DateTime.UtcNow) : null,
            tts.DisplayName(config),
            voice);
    }

    /// <summary>Starts a voice-over in the background (one per item). Returns null when started, else why not.</summary>
    public string? Start(Guid itemId, string language, string? providerId, string? voice)
    {
        var config = Config;
        var tts = GetProvider(providerId);
        voice = GetVoice(tts, voice);
        var video = GetVideo(itemId);
        const string AlreadyRunning = "Вече тече озвучаване на този филм.";
        if (_flights.IsRunning(itemId))
        {
            return AlreadyRunning;
        }

        int perDay = tts.RequestsPerDay(config);
        if (perDay > 0 && Quota.UsedToday(QuotaKey(tts, config), DateTime.UtcNow) >= perDay)
        {
            return $"Дневният лимит от {perDay} заявки за {tts.DisplayName(config)} е изчерпан. Нулира се в {QuotaTracker.NextResetUtc(DateTime.UtcNow).ToLocalTime():HH:mm}.";
        }

        // Reserved before it starts: a double click cannot start (and pay for) a second run.
        string id = "dub-" + itemId.ToString("N", CultureInfo.InvariantCulture);
        _flights.Run(
            itemId,
            async cancellationToken =>
            {
                _status[itemId] = new JobStatus(video.Name, language, "voice-over", "стартиране", DateTime.UtcNow, id);
                try
                {
                    await RunAsync(video, language, tts, voice, id, cancellationToken).ConfigureAwait(false);
                    _status.TryRemove(itemId, out _);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("AI Translate: voice-over of {Title} stopped", video.Name);
                    _status[itemId] = new JobStatus(video.Name, language, "voice-over", "спряно — готовото е запазено; „Озвучи“ продължава оттам", DateTime.UtcNow, id, Running: false);
                }
                catch (Exception ex)
                {
                    // Keep the error visible on the settings page; a new run may start right away.
                    _logger.LogError(ex, "AI Translate: voice-over of {Title} failed", video.Name);
                    _status[itemId] = new JobStatus(video.Name, language, "voice-over", "грешка: " + ex.Message, DateTime.UtcNow, id, Running: false);
                }

                return true;
            },
            out bool started);
        return started ? null : AlreadyRunning;
    }

    internal static string OutputPath(Video video, string language) =>
        Path.Combine(Path.GetDirectoryName(video.Path)!, Path.GetFileNameWithoutExtension(video.Path) + "." + language + ".mka");

    private async Task RunAsync(Video video, string language, ITtsProvider tts, string voice, string id, CancellationToken cancellationToken)
    {
        var config = Config;
        var started = DateTime.UtcNow;
        bool byGender = config.DubSplitByGender;
        string femaleVoice = byGender ? GetFemaleVoice(tts, config.DubVoiceFemale) : voice;
        string label = byGender ? $"{tts.DisplayName(config)} · {voice} / {femaleVoice}" : $"{tts.DisplayName(config)} · {voice}";
        void Stage(string s) => _status[video.Id] = new JobStatus(video.Name, language, label, s, started, id);
        string VoiceFor(DubSegment s) => byGender && s.Kind == VoiceKind.Female ? femaleVoice : voice;

        var stream = FindSubtitle(video, language) ?? throw new InvalidOperationException("Няма текстови субтитри на " + language);
        Stage("четене на субтитрите");
        var cues = await ReadSubtitleAsync(video, stream, cancellationToken).ConfigureAwait(false);
        var extractor = new AudioExtractor(_mediaEncoder.EncoderPath, _mediaEncoder.ProbePath);
        int audioIndex = await extractor.GetDefaultAudioIndexAsync(video.Path, cancellationToken).ConfigureAwait(false);
        var segments = byGender
            ? await PlanByGenderAsync(video, cues, audioIndex, config, Stage, cancellationToken).ConfigureAwait(false)
            : DubPlanner.Plan(cues, config.DubSkipSongs);

        // 1) Speech for every segment (cached on disk per line, so an interrupted run resumes for free).
        //    Several lines go into one request (TTS quotas count requests) and are split again at the pauses.
        var clips = new short[segments.Count][];
        var usage = new ConcurrentBag<Usage>();
        int rate = 24000;
        var missing = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            string cache = ClipPath(video, tts, VoiceFor(segments[i]), config, segments[i].Text);
            if (File.Exists(cache))
            {
                (clips[i], rate) = Wav.ReadPcm16Mono(await File.ReadAllBytesAsync(cache, cancellationToken).ConfigureAwait(false));
                CacheCleaner.MarkUsed(cache);
            }
            else
            {
                missing.Add(i);
            }
        }

        var batches = Batches(missing, segments, config);
        int missingChars = missing.Sum(i => segments[i].Text.Length);
        using var budget = _jobs.Ledger.Reserve(tts.Estimate(missingChars, missingChars / DubPlanner.CharactersPerSecond, config), config.MonthlyBudgetUsd, video.Name, DateTime.UtcNow);

        int perDay = tts.RequestsPerDay(config);
        string quotaKey = QuotaKey(tts, config);
        int remaining = perDay > 0 ? perDay - Quota.UsedToday(quotaKey, DateTime.UtcNow) : int.MaxValue;
        if (batches.Count > remaining)
        {
            _logger.LogWarning(
                "AI Translate: voice-over {Title} needs {Requests} requests, {Remaining} left today for {Model}; it will stop there and continue on the next run",
                video.Name,
                batches.Count,
                remaining,
                quotaKey);
        }

        int done = segments.Count - missing.Count;
        try
        {
            Stage($"синтез на глас {done}/{segments.Count} ({batches.Count} заявки)");
            await Parallel.ForEachAsync(
                batches,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(config.DubParallelism, 1, 8), CancellationToken = cancellationToken },
                async (batch, ct) =>
                {
                    string text = string.Join(tts.LineSeparator(config), batch.Select(i => segments[i].Text));
                    string batchVoice = VoiceFor(segments[batch[0]]);
                    var clip = await tts.SynthesizeAsync(text, batchVoice, config.DubStyle, config, ct).ConfigureAwait(false);
                    Quota.Add(quotaKey, 1, DateTime.UtcNow);
                    usage.Add(clip.Usage);
                    rate = clip.SampleRate;
                    var pieces = ClipSplitter.Split(clip.Samples, clip.SampleRate, batch.Select(i => segments[i].Text.Length).ToList());
                    for (int k = 0; k < batch.Count; k++)
                    {
                        clips[batch[k]] = pieces[k];
                        string cache = ClipPath(video, tts, batchVoice, config, segments[batch[k]].Text);
                        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);

                        // Paid for: saved even if the run is being stopped.
                        await File.WriteAllBytesAsync(cache, Wav.WritePcm16Mono(pieces[k], clip.SampleRate), CancellationToken.None).ConfigureAwait(false);
                    }

                    Stage($"синтез на глас {Interlocked.Add(ref done, batch.Count)}/{segments.Count}");
                }).ConfigureAwait(false);
        }
        catch (DailyQuotaException)
        {
            Quota.MarkExhausted(quotaKey, perDay, DateTime.UtcNow);
            throw new InvalidOperationException(
                $"Дневният лимит на {tts.DisplayName(config)} е изчерпан при {done}/{segments.Count} реплики. " +
                $"Готовото е запазено — пусни „Озвучи“ отново след {QuotaTracker.NextResetUtc(DateTime.UtcNow).ToLocalTime():HH:mm} и ще продължи оттук (или избери друг доставчик/модел).");
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
            await WriteNarrationAsync(narration, clips, placed, rate, work, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "AI Translate: voice-over {Title}: {Count} segments, {Fast} sped up, {Late} shifted",
                video.Name,
                segments.Count,
                placed.Count(p => p.Tempo > 1.01),
                placed.Where((p, i) => p.Start > segments[i].Start + TimeSpan.FromMilliseconds(200)).Count());

            // 3) Mix with the original audio, ducked under the voice.
            Stage("миксиране със звука на филма");
            string output = OutputPath(video, language);
            string part = Path.Combine(Path.GetDirectoryName(output)!, "." + Path.GetFileName(output) + ".part");
            try
            {
                var r = await AudioExtractor.RunAsync(_mediaEncoder.EncoderPath, MixArguments(video.Path, audioIndex, narration, rate, language, part), cancellationToken).ConfigureAwait(false);
                if (r.ExitCode != 0)
                {
                    throw new InvalidOperationException("ffmpeg mix failed: " + (r.StdErr.Length > 600 ? r.StdErr[^600..] : r.StdErr));
                }

                File.Move(part, output, overwrite: true);
            }
            catch
            {
                // A half-written track next to the film must not stay (stopped, or ffmpeg failed).
                File.Delete(part);
                throw;
            }

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

    /// <summary>Speaker classes from the film's dialogue channel, then the line plan (male/female voices).</summary>
    private async Task<List<DubSegment>> PlanByGenderAsync(Video video, IReadOnlyList<Cue> cues, int audioIndex, PluginConfiguration config, Action<string> stage, CancellationToken cancellationToken)
    {
        stage("анализ на гласовете (мъжки/женски)");
        var raw = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N") + ".raw");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        try
        {
            await VoicePitch.ExtractDialogueAsync(_mediaEncoder.EncoderPath, _mediaEncoder.ProbePath, video.Path, audioIndex, raw, cancellationToken).ConfigureAwait(false);
            await using var dialogue = File.OpenRead(raw);
            var segments = DubPlanner.Plan(
                cues,
                config.DubSkipSongs,
                (start, end) => VoicePitch.Classify(VoicePitch.MedianPitch(VoicePitch.Read(dialogue, start, end)), config.DubMaleBelowHz, config.DubFemaleAboveHz));
            _logger.LogInformation(
                "AI Translate: voice-over {Title}: {Male} male / {Female} female segments",
                video.Name,
                segments.Count(s => s.Kind != VoiceKind.Female),
                segments.Count(s => s.Kind == VoiceKind.Female));
            return segments;
        }
        finally
        {
            File.Delete(raw);
        }
    }

    /// <summary>Groups consecutive missing lines with the same voice into requests of at most N lines / 700 characters.</summary>
    internal static List<List<int>> Batches(IReadOnlyList<int> missing, IReadOnlyList<DubSegment> segments, PluginConfiguration config)
    {
        int perRequest = Math.Clamp(config.DubLinesPerRequest, 1, 30);
        var batches = new List<List<int>>();
        foreach (int i in missing)
        {
            var last = batches.Count > 0 ? batches[^1] : null;
            if (last is null || last.Count >= perRequest || last[^1] != i - 1 || segments[last[0]].Kind != segments[i].Kind
                || last.Sum(j => segments[j].Text.Length) + segments[i].Text.Length > 700)
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

    private async Task WriteNarrationAsync(string path, short[][] clips, IReadOnlyList<PlacedClip> placed, int rate, string work, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        long written = 0;
        var zeros = new byte[1 << 16];
        for (int i = 0; i < clips.Length; i++)
        {
            var samples = placed[i].Tempo > 1.01 ? await StretchAsync(clips[i], rate, placed[i].Tempo, work, cancellationToken).ConfigureAwait(false) : clips[i];
            long startSample = (long)(placed[i].Start.TotalSeconds * rate);
            for (long gap = (startSample - written) * 2; gap > 0; gap -= zeros.Length)
            {
                await file.WriteAsync(zeros.AsMemory(0, (int)Math.Min(gap, zeros.Length)), cancellationToken).ConfigureAwait(false);
            }

            written = Math.Max(written, startSample);
            var bytes = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            written += samples.Length;
        }
    }

    private async Task<short[]> StretchAsync(short[] samples, int rate, double tempo, string work, CancellationToken cancellationToken)
    {
        string input = Path.Combine(work, "clip-in.wav");
        string output = Path.Combine(work, "clip-out.wav");
        await File.WriteAllBytesAsync(input, Wav.WritePcm16Mono(samples, rate), cancellationToken).ConfigureAwait(false);
        var r = await AudioExtractor.RunAsync(
            _mediaEncoder.EncoderPath,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-filter:a", "atempo=" + tempo.ToString("0.000", CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-ac", "1", output],
            cancellationToken).ConfigureAwait(false);
        return r.ExitCode == 0 ? Wav.ReadPcm16Mono(await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false)).Samples : samples;
    }

    private async Task<List<Cue>> ReadSubtitleAsync(Video video, MediaStream stream, CancellationToken cancellationToken)
    {
        var work = Path.Combine(_appPaths.TempDirectory, "ai-translate", Guid.NewGuid().ToString("N"));
        try
        {
            string path = stream.IsExternal
                ? stream.Path
                : await SubtitleSources.ExtractEmbeddedAsync(video.Path, stream.Index, _mediaEncoder.EncoderPath, work, cancellationToken).ConfigureAwait(false);
            return await SubtitleSources.ReadAsync(path, _mediaEncoder.EncoderPath, work, stripHearingImpaired: true, stream.Language, cancellationToken).ConfigureAwait(false);
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

    // Text subtitles in the language, external first; this plugin's "being generated" / "failed" notes are not subtitles.
    private static MediaStream? FindSubtitle(Video video, string language) =>
        video.GetMediaStreams()
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && !string.IsNullOrEmpty(s.Language)
                && Languages.ToTwoLetter(s.Language!) == language
                && SubtitleSources.IsTextCodec(s.Codec)
                && !(s.IsExternal && AiSubtitleProvider.IsPlaceholder(s.Path)))
            .OrderBy(s => s.IsExternal ? 0 : 1)
            .FirstOrDefault();

    private static string Label(MediaStream s) => (s.IsExternal ? Path.GetFileName(s.Path) : "вградени") + " (" + s.Language + ")";

    private Video GetVideo(Guid itemId) =>
        _libraryManager.GetItemById(itemId) as Video ?? throw new InvalidOperationException("Item not found: " + itemId);

    /// <summary>
    /// Where a spoken line is cached. The audio depends only on voice, model, style and text, so the cache is shared by
    /// all films: a new release of the same film (same subtitles) or a re-run never pays for a line twice.
    /// Clips from the earlier per-file cache (dub/&lt;media hash&gt;/) are still found.
    /// </summary>
    private string ClipPath(Video video, ITtsProvider tts, string voice, PluginConfiguration config, string text)
    {
        string name = $"{voice}-{SubtitleJobService.Hash(tts.Model(config) + "|" + config.DubStyle + "|" + text)}.wav";
        string shared = Path.Combine(DataFolder, "dub", "clips", name);
        if (!File.Exists(shared))
        {
            string legacy = Path.Combine(DataFolder, "dub", SubtitleJobService.Hash(video.Path), name);
            if (File.Exists(legacy))
            {
                return legacy;
            }
        }

        return shared;
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
