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
        Assert.Equal(TimeSpan.FromMinutes(63.8), GeminiTts.PacedTime(638, new Configuration.PluginConfiguration()));
    }

    [Fact]
    public void MixDucksOriginalUnderVoiceAndTagsTrack()
    {
        var args = DubService.MixArguments("/m/film.mp4", 0, "/t/n.raw", 24000, "bg", "/m/.film.bg.mka.part");
        var line = string.Join(' ', args);
        Assert.Contains("sidechaincompress", line, StringComparison.Ordinal);
        Assert.Contains("[0:a:0]", line, StringComparison.Ordinal);
        Assert.Contains("language=bul", line, StringComparison.Ordinal);
        Assert.Contains("title=" + DubService.TrackTitle, line, StringComparison.Ordinal);
        Assert.Equal("matroska", args[^2]);
    }
}
