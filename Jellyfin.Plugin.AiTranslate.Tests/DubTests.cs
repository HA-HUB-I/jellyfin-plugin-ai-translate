using System;
using System.Linq;
using Jellyfin.Plugin.AiTranslate.Dubbing;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.AiTranslate.Tests;

public class DubTests
{
    private static Cue C(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);

    [Fact]
    public void PlanSkipsSongsCleansDialogueAndMergesShortNeighbours()
    {
        var cues = new[]
        {
            C(5, 11, "♪ Хи-хи! ♪\n- ♪ Хи-хи! ♪"),
            C(20, 21, "- Кой е там?\n- Аз."),
            C(21.2, 22.5, "<i>Влизай.</i>"),          // 0.2 s gap → merged
            C(30, 33, "Здравейте, майоре."),
        };

        var plan = DubPlanner.Plan(cues, skipSongs: true);

        Assert.Equal(["Кой е там? Аз. Влизай.", "Здравейте, майоре."], plan.Select(p => p.Text));
        Assert.Equal(TimeSpan.FromSeconds(20), plan[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(22.5), plan[0].End);
        Assert.Equal(3, DubPlanner.Plan(cues, skipSongs: false).Count);
    }

    [Fact]
    public void PlaceSpeedsUpToFitThenShiftsWhenStillTooLong()
    {
        var segments = new[]
        {
            new DubSegment(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(14), "a"),
            new DubSegment(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(16), "b"),
            new DubSegment(TimeSpan.FromSeconds(16.5), TimeSpan.FromSeconds(18), "c"),
            new DubSegment(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(31), "d"),
        };
        var clips = new[] { TimeSpan.FromSeconds(5.7), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1) };

        var placed = DubPlanner.Place(segments, clips, maxTempo: 1.3);

        // a: slot 10 → 14.92 s, 5.7 s needs tempo 1.159 and ends just before b.
        Assert.Equal(TimeSpan.FromSeconds(10), placed[0].Start);
        Assert.InRange(placed[0].Tempo, 1.15, 1.17);
        Assert.True(placed[0].Start + placed[0].Duration <= segments[1].Start);

        // b: 3 s in a 1.42 s slot → capped at 1.3×, runs long; c starts after it.
        Assert.Equal(1.3, placed[1].Tempo);
        Assert.True(placed[2].Start >= placed[1].Start + placed[1].Duration);

        // d: plenty of room → normal speed at its own time.
        Assert.Equal(1.0, placed[3].Tempo);
        Assert.Equal(TimeSpan.FromSeconds(30), placed[3].Start);
    }

    [Fact]
    public void WavRoundTripAndStereoDownmix()
    {
        var samples = new short[] { 0, 1000, -1000, short.MaxValue, short.MinValue };
        var (back, rate) = Wav.ReadPcm16Mono(Wav.WritePcm16Mono(samples, 24000));
        Assert.Equal(24000, rate);
        Assert.Equal(samples, back);
    }

    [Fact]
    public void ParsesGeminiTtsInteraction()
    {
        var wav = Convert.ToBase64String(Wav.WritePcm16Mono(new short[] { 1, 2, 3 }, 24000));
        var body = "{\"status\":\"completed\",\"usage\":{\"total_input_tokens\":36,\"total_output_tokens\":143}," +
                   "\"steps\":[{\"type\":\"model_output\",\"content\":[{\"type\":\"audio\",\"mime_type\":\"audio/wav\",\"data\":\"" + wav + "\"}]}]}";
        var (bytes, input, output) = GeminiTts.ParseInteraction(body);
        Assert.Equal(36, input);
        Assert.Equal(143, output);
        Assert.Equal(new short[] { 1, 2, 3 }, Wav.ReadPcm16Mono(bytes).Samples);
    }

    [Fact]
    public void TtsEstimateMatchesMeasuredLine()
    {
        // Measured: 95 characters → 5.72 s, 36 input + 143 output tokens = $0.00131.
        var tts = new GeminiTts(null!);
        Assert.Equal(0.0013, tts.Estimate(95, 5.72, new Configuration.PluginConfiguration()), 4);
    }

    [Fact]
    public async System.Threading.Tasks.Task PacerSpacesRequestsEvenly()
    {
        var pacer = new Pacer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
        {
            await pacer.WaitAsync(600, System.Threading.CancellationToken.None); // 600/min = one every 100 ms
        }

        Assert.InRange(sw.ElapsedMilliseconds, 280, 1000);
    }

    [Fact]
    public void QuotaCountsPerUtcDayAndResetsAtMidnightUtc()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("ai-quota-").FullName;
        try
        {
            var q = new Billing.QuotaTracker(dir);
            var at1237Sofia = new DateTime(2026, 10, 16, 9, 37, 0, DateTimeKind.Utc);
            q.Add("gemini:gemini-3.8-flash-tts", 64, at1237Sofia);
            q.MarkExhausted("gemini:gemini-3.8-flash-tts", 100, at1237Sofia);

            Assert.Equal(100, q.UsedToday("gemini:gemini-3.8-flash-tts", at1237Sofia));
            Assert.Equal(0, q.UsedToday("gemini:gemini-3.8-flash-lite-tts", at1237Sofia));

            // Google: "retry in 14h22m" at 09:37 UTC → 23:59 UTC; the next day starts clean.
            Assert.Equal(new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc), Billing.QuotaTracker.NextResetUtc(at1237Sofia));
            Assert.Equal(0, q.UsedToday("gemini:gemini-3.8-flash-tts", at1237Sofia.AddHours(14.5)));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void VoiceFallsBackToProviderDefault()
    {
        var openAi = new OpenAiTts(null!);
        Assert.Equal("onyx", DubService.GetVoice(openAi, "Charon"));   // Gemini voice name → OpenAI default
        Assert.Equal("marin", DubService.GetVoice(openAi, "Marin"));
        Assert.Equal("Kore", DubService.GetVoice(new GeminiTts(null!), "kore"));
    }

    private static short[] Tone(double seconds, int rate = 24000) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (short)(8000 * Math.Sin(i * 2 * Math.PI * 220 / rate))).ToArray();

    private static short[] Silence(double seconds, int rate = 24000) => new short[(int)(seconds * rate)];

    [Fact]
    public void SplitterCutsAtThePausesBetweenLines()
    {
        // Three "lines" of 1.0 s, 2.0 s and 0.6 s with 0.6 s pauses, plus a short 0.1 s breath inside line 2.
        var clip = Tone(1.0).Concat(Silence(0.6)).Concat(Tone(0.9)).Concat(Silence(0.1)).Concat(Tone(1.0))
            .Concat(Silence(0.6)).Concat(Tone(0.6)).ToArray();

        var pieces = ClipSplitter.Split(clip, 24000, [16, 33, 10]);

        Assert.Equal(3, pieces.Count);
        Assert.InRange(pieces[0].Length / 24000.0, 0.95, 1.2);
        Assert.InRange(pieces[1].Length / 24000.0, 1.9, 2.2);
        Assert.InRange(pieces[2].Length / 24000.0, 0.55, 0.8);
    }

    [Fact]
    public void SplitterFallsBackToTextShareWithoutPauses()
    {
        var pieces = ClipSplitter.Split(Tone(3.0), 24000, [10, 20]);
        Assert.Equal(2, pieces.Count);
        Assert.InRange(pieces[0].Length / 24000.0, 0.9, 1.15);
    }

    [Fact]
    public void BatchesAreConsecutiveAndBounded()
    {
        var segments = Enumerable.Range(0, 30).Select(i => new DubSegment(TimeSpan.FromSeconds(i * 5), TimeSpan.FromSeconds((i * 5) + 2), new string('а', 40))).ToList();
        var missing = Enumerable.Range(0, 30).Where(i => i != 12).ToList();
        var batches = DubService.Batches(missing, segments, new Configuration.PluginConfiguration { DubLinesPerRequest = 10 });

        Assert.Equal(missing, batches.SelectMany(b => b));
        Assert.All(batches, b => Assert.True(b.Count <= 10));
        Assert.All(batches, b => Assert.Equal(Enumerable.Range(b[0], b.Count), b));  // no gaps inside a request
        Assert.Equal(4, batches.Count);                                                // 0–9, 10–11, 13–22, 23–29
    }

    [Fact]
    public void DailyQuotaIsNotRetried()
    {
        Assert.False(RateLimit.IsRetryable("Gemini TTS 429: limit: 100 requests per day on Tier 1. Please retry in 14h22m27s"));
        Assert.True(RateLimit.IsRetryable("Gemini TTS 429: limit: 10 requests per minute on Tier 1. Please retry in 41s"));

        // Measured answer for an OpenAI key without prepaid credits.
        Assert.False(RateLimit.IsRetryable("OpenAI TTS 429: {\"error\":{\"message\":\"You have no credits remaining.\",\"type\":\"insufficient_quota\",\"code\":\"credit_balance_exhausted\"}}"));
    }

    [Fact]
    public void MixDucksOriginalUnderVoiceAndTagsTrack()
    {
        var args = DubService.MixArguments("/m/film.mp4", 0, "/t/n.raw", 24000, "bg", "/m/.film.bg.mka.part");
        var line = string.Join(' ', args);
        Assert.Contains("sidechaincompress", line, StringComparison.Ordinal);
        Assert.Contains("apad,asplit", line, StringComparison.Ordinal);       // track lasts as long as the film
        Assert.Contains("duration=first", line, StringComparison.Ordinal);
        Assert.Contains("[0:a:0]", line, StringComparison.Ordinal);
        Assert.Contains("language=bul", line, StringComparison.Ordinal);
        Assert.Contains("title=" + DubService.TrackTitle, line, StringComparison.Ordinal);
        Assert.Equal("matroska", args[^2]);
    }
}
