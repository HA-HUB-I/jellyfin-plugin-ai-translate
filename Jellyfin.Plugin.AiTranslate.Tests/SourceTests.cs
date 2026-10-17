using System;
using System.Collections.Generic;
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
    public void DecodesTheLegacyCodePageOfTheSubtitleLanguage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Old Bulgarian subtitles are Windows-1251; read as 1252 they became "Çäðàâåé, ìàéîðå." — and that was spoken in the voice-over.
        var bulgarian = Encoding.GetEncoding(1251).GetBytes("Здравей, майоре.");
        Assert.Equal("Здравей, майоре.", SubtitleSources.DecodeText(bulgarian, "bul"));
        Assert.Equal("Здравей, майоре.", SubtitleSources.DecodeText(bulgarian, "bg"));
        Assert.Equal("Zażółć gęślą jaźń", SubtitleSources.DecodeText(Encoding.GetEncoding(1250).GetBytes("Zażółć gęślą jaźń"), "pol"));
        Assert.Equal("café", SubtitleSources.DecodeText([0x63, 0x61, 0x66, 0xE9], "eng"));

        // UTF-16 big endian used to be read as little endian.
        Assert.Equal("Hello", SubtitleSources.DecodeText([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("Hello")]));
        Assert.Equal("Hello", SubtitleSources.DecodeText([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Hello")]));
    }

    [Fact]
    public void HumanSubtitlesKeepTheirTimingsRecognisedSpeechIsRepaired()
    {
        // Measured before: the 11 s cue of a translated human subtitle was cut to 1.2 s.
        var cues = new[]
        {
            new Cue(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(21), "♪ Long song line ♪"),
            new Cue(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(35), "Normal line here."),
        };

        Assert.Equal(TimeSpan.FromSeconds(21), SubtitleJobService.FinalCues(cues, fromAudio: false)[0].End);
        Assert.True(SubtitleJobService.FinalCues(cues, fromAudio: true)[0].End < TimeSpan.FromSeconds(12));
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
    public void PlaceholderIsReplacedOnlyWhereItWasSaved()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("ai-placeholder-").FullName;
        try
        {
            string pending = System.IO.Path.Combine(dir, "Film.bg.srt");
            string human = System.IO.Path.Combine(dir, "Film.en.srt");
            System.IO.File.WriteAllText(pending, AiSubtitleProvider.Placeholder("15–25 мин"));
            System.IO.File.WriteAllText(human, "1\n00:00:01,000 --> 00:00:02,000\nHello\n");

            Assert.Equal(1, AiSubtitleProvider.ReplacePlaceholders([pending, human, pending, System.IO.Path.Combine(dir, "missing.srt")], "1\n00:00:01,000 --> 00:00:02,000\nЗдравей\n"));
            Assert.Contains("Здравей", System.IO.File.ReadAllText(pending), StringComparison.Ordinal);
            Assert.Contains("Hello", System.IO.File.ReadAllText(human), StringComparison.Ordinal);
            Assert.Single(CueTools.ParseSrt(AiSubtitleProvider.Placeholder("1–5 мин")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FinishedJobOnlyReplacesThePlaceholdersOfItsOwnFilm()
    {
        // A season folder: two episodes are being generated at the same time.
        var dir = System.IO.Directory.CreateTempSubdirectory("ai-season-").FullName;
        try
        {
            string F(string name) => System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllText(F("Show S01E01.bg.srt"), AiSubtitleProvider.Placeholder("15–25 мин"));
            System.IO.File.WriteAllText(F("Show S01E01.bg.0.srt"), "1\n00:00:00,000 --> 09:59:59,000\n" + AiSubtitleProvider.FailedMarker + " субтитрите не успяха.\nGemini 503\n");
            System.IO.File.WriteAllText(F("Show S01E01.bul.srt"), AiSubtitleProvider.Placeholder("15–25 мин"));
            System.IO.File.WriteAllText(F("Show S01E02.bg.srt"), AiSubtitleProvider.Placeholder("15–25 мин"));
            System.IO.File.WriteAllText(F("Show S01E01 Extended.bg.srt"), AiSubtitleProvider.Placeholder("15–25 мин"));
            System.IO.File.WriteAllText(F("Show S01E01.en.srt"), AiSubtitleProvider.Placeholder("15–25 мин"));

            var candidates = AiSubtitleProvider.PlaceholderCandidates(F("Show S01E01.mkv"), "bg", [], [dir, dir]);
            Assert.Equal(["Show S01E01.bg.0.srt", "Show S01E01.bg.srt", "Show S01E01.bul.srt"], candidates.Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal));

            // The finished episode 1 (also over its earlier "failed" note); episode 2 keeps waiting for its own job.
            Assert.Equal(3, AiSubtitleProvider.ReplacePlaceholders(candidates, "1\n00:00:01,000 --> 00:00:02,000\nЗдравей\n"));
            Assert.True(AiSubtitleProvider.IsPlaceholder(F("Show S01E02.bg.srt")));
            Assert.False(AiSubtitleProvider.IsPlaceholder(F("Show S01E01.bg.0.srt")));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StoppedJobRemovesItsPlaceholdersButNotRealSubtitles()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("ai-stop-").FullName;
        try
        {
            string F(string name) => System.IO.Path.Combine(dir, name);
            System.IO.File.WriteAllText(F("Film.bg.srt"), AiSubtitleProvider.Placeholder("1–5 мин"));
            System.IO.File.WriteAllText(F("Film.bg.0.srt"), "1\n00:00:00,000 --> 09:59:59,000\n" + AiSubtitleProvider.FailedMarker + " субтитрите не успяха.\nGemini 503\n");
            System.IO.File.WriteAllText(F("Film.bg.1.srt"), "1\n00:00:01,000 --> 00:00:02,000\nЗдравей\n");

            var candidates = AiSubtitleProvider.PlaceholderCandidates(F("Film.mkv"), "bg", [], [dir]);
            Assert.Equal(2, AiSubtitleProvider.RemovePlaceholders(candidates));
            Assert.Equal(["Film.bg.1.srt"], System.IO.Directory.GetFiles(dir).Select(System.IO.Path.GetFileName));
        }
        finally
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task RecognisedChunksAreKeptWhenTheJobStops()
    {
        var cache = System.IO.Path.Combine(System.IO.Directory.CreateTempSubdirectory("ai-chunks-").FullName, "chunks");
        try
        {
            var chunks = Enumerable.Range(0, 4).Select(i => new Media.AudioChunk($"chunk{i:000}.mp3", TimeSpan.FromMinutes(6 * i), TimeSpan.FromMinutes(6))).ToList();
            var config = new Configuration.PluginConfiguration();

            // Chunk 2 hits an error: 0 and 1 are paid for, recorded and kept.
            var first = new FakeTranscriber(failAt: 2);
            var usage = new List<Providers.Usage>();
            await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => SubtitleJobService.TranscribeChunksAsync(first, chunks, config, cache, usage, () => { }));
            Assert.Equal([0, 1, 2], first.Requested);
            Assert.Equal(2, usage.Count);

            // The next run only pays for chunks 2 and 3.
            var second = new FakeTranscriber(failAt: -1);
            var again = new List<Providers.Usage>();
            var parts = await SubtitleJobService.TranscribeChunksAsync(second, chunks, config, cache, again, () => { });
            Assert.Equal([2, 3], second.Requested);
            Assert.Equal(2, again.Count);
            Assert.Equal(["chunk000.mp3 says hi", "chunk001.mp3 says hi", "chunk002.mp3 says hi", "chunk003.mp3 says hi"], parts.Select(p => p.Cues[0].Text));
            Assert.Equal(TimeSpan.FromSeconds(1.5), parts[0].Cues[0].Start);
        }
        finally
        {
            System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(cache)!, recursive: true);
        }
    }

    private sealed class FakeTranscriber : Providers.ITranscriber
    {
        private readonly int _failAt;

        public FakeTranscriber(int failAt) => _failAt = failAt;

        public List<int> Requested { get; } = [];

        public string Id => "gemini";

        public int Parallelism => 1;

        public string DisplayName(Configuration.PluginConfiguration config) => "Fake";

        public bool IsAvailable(Configuration.PluginConfiguration config) => true;

        public TimeSpan ChunkLength(Configuration.PluginConfiguration config) => TimeSpan.FromMinutes(6);

        public TimeSpan EstimatedTime(TimeSpan duration, Configuration.PluginConfiguration config) => duration;

        public double PricePerMinute(Configuration.PluginConfiguration config) => 0.005;

        public System.Threading.Tasks.Task<Providers.Transcription> TranscribeAsync(Media.AudioChunk chunk, Configuration.PluginConfiguration config, System.Threading.CancellationToken cancellationToken)
        {
            int index = int.Parse(chunk.Path.AsSpan(5, 3), System.Globalization.CultureInfo.InvariantCulture);
            Requested.Add(index);
            if (index == _failAt)
            {
                throw new System.Net.Http.HttpRequestException("Gemini 400: bad request");
            }

            var cue = new Cue(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), chunk.Path + " says hi");
            return System.Threading.Tasks.Task.FromResult(new Providers.Transcription([cue], null, new Providers.Usage("gemini", "fake", 6, 0, 0, 0.03)));
        }
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
