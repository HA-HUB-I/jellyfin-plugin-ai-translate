using System;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.AiTranslate.Tests;

public class SourceTests
{
    private static MediaStream Sub(int index, string lang, string codec, bool external = false, bool sdh = false, bool forced = false) => new()
    {
        Type = MediaStreamType.Subtitle,
        Index = index,
        Language = lang,
        Codec = codec,
        IsExternal = external,
        Path = external ? $"/m/film.{lang}.srt" : null,
        IsHearingImpaired = sdh,
        IsForced = forced,
    };

    [Fact]
    public void PicksPreferredLanguageTextStreamAndSkipsImagesForcedAndTarget()
    {
        var streams = new[]
        {
            new MediaStream { Type = MediaStreamType.Audio, Index = 1, Language = "eng" },
            Sub(2, "eng", "subrip", sdh: true),
            Sub(3, "eng", "subrip"),
            Sub(4, "spa", "subrip"),
            Sub(5, "eng", "hdmv_pgs_subtitle"),
            Sub(6, "eng", "subrip", forced: true),
            Sub(7, "bul", "subrip"),
        };

        var sources = SubtitleSources.FromStreams(streams, "bg", ["en"]);

        Assert.Equal(["3", "2", "4"], sources.Select(s => s.Ref));
        Assert.Equal(SourceKind.Embedded, sources[0].Kind);
        Assert.Equal("en", sources[0].Language);
        Assert.Contains("English", sources[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefersExternalFileOverEmbeddedOfSameLanguage()
    {
        var sources = SubtitleSources.FromStreams([Sub(3, "eng", "subrip"), Sub(9, "eng", "subrip", external: true)], "bg", ["en"]);
        Assert.Equal(SourceKind.File, sources[0].Kind);
        Assert.Equal("/m/film.eng.srt", sources[0].Ref);
    }

    [Fact]
    public void CleansMarkupAndHearingImpairedNotes()
    {
        var cues = new[]
        {
            new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(1), "<i>JOHN: Get down!</i>\n[gunshots]"),
            new Cue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "[door creaks]"),
            new Cue(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "{\\an8}- MAN 2: Who's there?\n- (sighs) Me."),
            new Cue(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), "♪ Fly me to the moon ♪"),
        };

        var clean = SubtitleSources.Clean(cues, stripHearingImpaired: true);

        Assert.Equal(["Get down!", "- Who's there?\n- Me.", "♪ Fly me to the moon ♪"], clean.Select(c => c.Text));
        Assert.Equal(4, SubtitleSources.Clean(cues, stripHearingImpaired: false).Count);
    }

    [Fact]
    public void DecodesUtf8BomAndWindows1252()
    {
        Assert.Equal("Здравей", SubtitleSources.DecodeText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Здравей")]));
        Assert.Equal("café", SubtitleSources.DecodeText([0x63, 0x61, 0x66, 0xE9]));
    }

    [Fact]
    public void PolishStretchesTooShortCuesWithoutOverlap()
    {
        var cues = new[]
        {
            new Cue(TimeSpan.FromSeconds(122.3), TimeSpan.FromSeconds(122.7), "на щатската полиция на Арканзас."),
            new Cue(TimeSpan.FromSeconds(124.5), TimeSpan.FromSeconds(127.3), "Здравейте, майоре."),
            new Cue(TimeSpan.FromSeconds(127.4), TimeSpan.FromSeconds(127.6), "Фанг."),
            new Cue(TimeSpan.FromSeconds(127.9), TimeSpan.FromSeconds(129.0), "Да?"),
        };

        var polished = CueTools.Polish(cues);

        Assert.Equal(TimeSpan.FromSeconds(123.9), polished[0].End);               // 32 chars / 20 cps = 1.6 s
        Assert.Equal(TimeSpan.FromSeconds(127.85), polished[2].End);              // stopped 50 ms before the next cue
        Assert.Equal(TimeSpan.FromSeconds(127.3), polished[1].End);               // long enough already
        for (int i = 1; i < polished.Count; i++)
        {
            Assert.True(polished[i - 1].End <= polished[i].Start);
        }
    }

    [Theory]
    [InlineData("♪ Хи-хи! ♪\n- ♪ Хи-хи! ♪", "♪ Хи-хи! ♪\n- ♪ Хи-хи! ♪")]
    [InlineData("♪ Хи-хи! ♪ - ♪ Хи-хи! ♪", "♪ Хи-хи! ♪\n- ♪ Хи-хи! ♪")]
    [InlineData("- Кой е там? - Аз.", "- Кой е там?\n- Аз.")]
    [InlineData("Хайде отново\nот самото начало.", "Хайде отново от самото начало.")]
    [InlineData("Почакай - какво каза той", "Почакай - какво каза той")]
    public void KeepsOneSpeakerPerLine(string input, string expected)
    {
        Assert.Equal(expected, CueTools.WrapKeepingSpeakers(input));
    }

    [Fact]
    public void FlattenKeepsSecondSpeakerBreak()
    {
        Assert.Equal("I said you wanna be startin' somethin'", Providers.Translation.Flatten("I said you wanna be\nstartin' somethin'"));
        Assert.Equal("♪ Hee-hee! ♪\n- ♪ Hee-hee! ♪", Providers.Translation.Flatten("♪ Hee-hee! ♪\n- ♪ Hee-hee! ♪"));
    }

    [Fact]
    public void TranslationEstimatesFollowMeasurements()
    {
        // 1125 lines (Michael 2026) at Gemini 3.8 Flash prices: 28 125 in × $0.75 + 28 125 out × $3.75 per 1M ≈ $0.13.
        var jobs = (SubtitleJobService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SubtitleJobService));
        var gemini = new Providers.GeminiProvider(null!);
        Assert.Equal(0.127, jobs.EstimateTranslation(gemini, TimeSpan.FromMinutes(127), 1125), 3);
        Assert.Equal(TimeSpan.FromMinutes(0.5 + (8 * 0.12)), SubtitleJobService.TranslationTime(TimeSpan.Zero, 1125));

        // Measured: 2250 lines took 88 s; the estimate must be in the same range.
        Assert.InRange(SubtitleJobService.TranslationTime(TimeSpan.Zero, 2250).TotalSeconds, 60, 180);
    }

    [Fact]
    public void OnlyTheActiveProviderWithAKeyIsOfferedUnlessComparing()
    {
        var config = new Configuration.PluginConfiguration { GeminiApiKey = "g", OpenAiApiKey = "o", ActiveTranslator = "openai", ActiveTranscriber = "gemini" };
        var jobs = NewJobs(config);

        Assert.Equal(["openai"], jobs.AvailableTranslators().Select(x => x.Id));
        Assert.Equal([("gemini", "openai")], jobs.AvailableCombinations().Select(c => (c.Transcriber.Id, c.Translator.Id)));

        config.OpenAiApiKey = string.Empty;                       // active one loses its key → falls back to one that has
        Assert.Equal(["gemini"], jobs.AvailableTranslators().Select(x => x.Id));

        config.OpenAiApiKey = "o";
        config.CompareProviders = true;                           // compare → every provider with a key
        Assert.Equal(4, jobs.AvailableCombinations().Count());
    }

    private static SubtitleJobService NewJobs(Configuration.PluginConfiguration config)
    {
        var jobs = (SubtitleJobService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SubtitleJobService));
        var providers = new object[] { new Providers.OpenAiProvider(null!), new Providers.GeminiProvider(null!) };
        typeof(SubtitleJobService).GetField("_transcribers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(jobs, providers.Cast<Providers.ITranscriber>().ToList());
        typeof(SubtitleJobService).GetField("_translators", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(jobs, providers.Cast<Providers.ITranslator>().ToList());
        SubtitleJobService.ConfigOverride = config;
        return jobs;
    }

    [Fact]
    public void JobSourceKeys()
    {
        Assert.Equal("gemini", new SubtitleJob("/m", "T", "bg", "gemini", "gemini").SourceKey);
        Assert.Equal("sub-en", new SubtitleJob("/m", "T", "bg", "-", "gemini", SourceKind.Embedded, "3", "en").SourceKey);
        var job = new SubtitleJob("/m", "T", "bg", "-", "openai", SourceKind.Remote, "abc_123", "en");
        Assert.Equal(job, AiSubtitleProvider.Decode(AiSubtitleProvider.Encode(job)));
    }
}
