using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AiTranslate.Billing;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using Xunit;

namespace Jellyfin.Plugin.AiTranslate.Tests;

public class CueTests
{
    [Fact]
    public void ShortSegmentStaysOneCue()
    {
        var cues = CueTools.SplitLongSegments([new Cue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "  Hello   there. ")]);
        Assert.Single(cues);
        Assert.Equal("Hello there.", cues[0].Text);
    }

    [Fact]
    public void LongSegmentIsSplitAtSentencesWithProportionalTime()
    {
        var text = "We have to leave the island before the storm comes in tonight. " +
                   "Nobody else knows the way through the reef, so you are coming with us whether you like it or not.";
        var cues = CueTools.SplitLongSegments([new Cue(TimeSpan.Zero, TimeSpan.FromSeconds(12), text)]);

        Assert.True(cues.Count >= 2);
        Assert.All(cues, c => Assert.True(c.Text.Replace("\n", " ", StringComparison.Ordinal).Length <= CueTools.MaxCueLength));
        Assert.All(cues, c => Assert.All(c.Text.Split('\n'), l => Assert.True(l.Length <= CueTools.MaxLineLength + 10)));
        Assert.Equal(TimeSpan.Zero, cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(12), cues[^1].End);
        Assert.StartsWith("We have to leave the island", cues[0].Text, StringComparison.Ordinal);
        for (int i = 1; i < cues.Count; i++)
        {
            Assert.Equal(cues[i - 1].End, cues[i].Start);
        }
    }

    [Fact]
    public void WordsAreGroupedAtPausesAndSentenceEnds()
    {
        TimedWord W(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);
        var words = new[]
        {
            W(0.0, 0.3, "Are"), W(0.3, 0.5, "you"), W(0.5, 0.9, "okay?"),
            W(2.5, 2.8, "Yes."), // long pause → new cue
            W(2.9, 3.1, "Let's"), W(3.1, 3.4, "go."),
        };
        var cues = CueTools.GroupWords(words);
        Assert.Equal(["Are you okay?", "Yes. Let's go."], cues.Select(c => c.Text));
        Assert.Equal(TimeSpan.FromSeconds(2.5), cues[1].Start);
    }

    [Fact]
    public void WrapMakesTwoBalancedLines()
    {
        var wrapped = CueTools.Wrap("Ако не беше онзи коралов риф, сега щяхме да отваряме шампанското.");
        var lines = wrapped.Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.True(Math.Abs(lines[0].Length - lines[1].Length) < 12);
    }

    [Fact]
    public void SrtRoundTrip()
    {
        var cues = new[] { new Cue(TimeSpan.FromMilliseconds(1500), TimeSpan.FromSeconds(3.25), "Здравей.\nКак си?"), new Cue(TimeSpan.FromHours(1.5), TimeSpan.FromHours(1.5) + TimeSpan.FromSeconds(2), "♪ Song ♪") };
        var srt = CueTools.ToSrt(cues);
        Assert.Contains("00:00:01,500 --> 00:00:03,250", srt, StringComparison.Ordinal);
        Assert.Contains("01:30:00,000 --> 01:30:02,000", srt, StringComparison.Ordinal);
        Assert.Equal(cues, CueTools.ParseSrt(srt));
    }
}

public class ProviderParsingTests
{
    [Fact]
    public void ParsesWhisperVerboseJson()
    {
        const string body = """
            {"task":"transcribe","language":"english","duration":12.5,"text":"...",
             "segments":[{"id":0,"start":0.0,"end":2.4,"text":" Hello, Captain."},{"id":1,"start":3.0,"end":5.5,"text":" Beautiful voice."}]}
            """;
        var (cues, language) = OpenAiProvider.ParseVerboseJson(body);
        Assert.Equal("en", language);
        Assert.Equal(["Hello, Captain.", "Beautiful voice."], cues.Select(c => c.Text));
        Assert.Equal(TimeSpan.FromSeconds(3), cues[1].Start);
    }

    [Fact]
    public void ParsesResponsesApiOutput()
    {
        const string body = """
            {"id":"resp_1","output":[{"type":"reasoning","summary":[]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{\"lines\":[{\"i\":0,\"t\":\"Здравей\"}]}"}]}],
             "usage":{"input_tokens":120,"output_tokens":45,"total_tokens":165}}
            """;
        var (text, input, output) = OpenAiProvider.ParseResponses(body);
        Assert.Equal("{\"lines\":[{\"i\":0,\"t\":\"Здравей\"}]}", text);
        Assert.Equal(120, input);
        Assert.Equal(45, output);
    }

    [Fact]
    public void ParsesGeminiWordInfo()
    {
        const string body = """
            {"id":"interactions/abc","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"Hi there.",
              "annotations":[{"type":"word_info","text":"Hi","start_offset":"0.100s","end_offset":"0.450s"},{"type":"word_info","text":"there.","start_offset":"0.500s","end_offset":"0.900s"}]}]}]}
            """;
        var words = GeminiProvider.ParseWordInfo(body);
        Assert.Equal(2, words.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.1), words[0].Start);
        Assert.Equal("there.", words[1].Text);
    }

    [Fact]
    public void ParsesGeminiGenerateContentAndCountsThinkingTokens()
    {
        const string body = """
            {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"text":"{\"lines\":[]}"}],"role":"model"}}],
             "usageMetadata":{"promptTokenCount":300,"candidatesTokenCount":80,"thoughtsTokenCount":20}}
            """;
        var (text, input, output) = GeminiProvider.ParseGenerateContent(body);
        Assert.Equal("{\"lines\":[]}", text);
        Assert.Equal(300, input);
        Assert.Equal(100, output);
    }
}

public class TranslationTests
{
    [Fact]
    public void AcceptsCompleteAnswerInAnyOrder()
    {
        var lines = Translation.ParseLines("""{"lines":[{"i":1,"t":"Две"},{"i":0,"t":"Едно"}]}""", 2);
        Assert.Equal(["Едно", "Две"], lines);
    }

    [Fact]
    public void AcceptsCodeFenceAndBareArray()
    {
        Assert.NotNull(Translation.ParseLines("```json\n[{\"i\":0,\"t\":\"Да\"}]\n```", 1));
    }

    [Theory]
    [InlineData("""{"lines":[{"i":0,"t":"Едно"}]}""")]
    [InlineData("""{"lines":[{"i":0,"t":"Едно"},{"i":1,"t":"  "}]}""")]
    [InlineData("not json")]
    public void RejectsIncompleteAnswers(string json)
    {
        Assert.Null(Translation.ParseLines(json, 2));
    }

    [Fact]
    public void PromptNamesLanguagesAndKeepsJsonExample()
    {
        var prompt = Translation.SystemPrompt("en", "bg", "Use informal address.");
        Assert.Contains("from English into Bulgarian", prompt, StringComparison.Ordinal);
        Assert.Contains("{\"lines\":[{\"i\":", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Use informal address.", prompt.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void UserJsonKeepsCyrillicReadable()
    {
        var json = Translation.UserJson("Deep Water (2026)", ["Здравей"], ["Hi"]);
        Assert.Contains("Здравей", json, StringComparison.Ordinal);
        Assert.Contains("\"i\":0", json, StringComparison.Ordinal);
    }
}

public class RateLimitTests
{
    private const string Gemini429 = "Gemini 429: {\"error\":{\"message\":\"Rate limit exceeded for model gemini-3.5-transcribe (limit: 10000 input tokens per minute on Tier 1). Please retry in 19s or upgrade your tier\"}}";

    [Fact]
    public void UsesRetryHint()
    {
        Assert.True(RateLimit.IsRetryable(Gemini429));
        Assert.Equal(TimeSpan.FromSeconds(20), RateLimit.RetryDelay(Gemini429, 1));
        Assert.Equal(TimeSpan.FromSeconds(2.5), RateLimit.RetryDelay("OpenAI 429: Please try again in 1.5s.", 1));
    }

    [Fact]
    public void BacksOffWithoutHintAndIgnoresOtherErrors()
    {
        Assert.Equal(TimeSpan.FromSeconds(40), RateLimit.RetryDelay("Gemini 503: overloaded", 2));
        Assert.False(RateLimit.IsRetryable("Gemini 400: bad request"));
    }

    [Fact]
    public void GeminiChunksFollowTheTierLimit()
    {
        var gemini = new GeminiProvider(null!);
        Assert.Equal(TimeSpan.FromSeconds(360), gemini.ChunkLength(new Configuration.PluginConfiguration { GeminiAudioTokensPerMinute = 10000 }));
        Assert.Equal(TimeSpan.FromMinutes(25), gemini.ChunkLength(new Configuration.PluginConfiguration { GeminiAudioTokensPerMinute = 1_000_000 }));

        // 100 minutes = 150 000 tokens = 15 minutes at Tier 1, plus overhead.
        Assert.Equal(TimeSpan.FromMinutes(17), gemini.EstimatedTime(TimeSpan.FromMinutes(100), new Configuration.PluginConfiguration()));
    }
}

public class MiscTests
{
    [Theory]
    [InlineData("english", "en")]
    [InlineData("Bulgarian", "bg")]
    [InlineData("de", "de")]
    [InlineData("", null)]
    public void LanguageNames(string name, string? code) => Assert.Equal(code, Languages.FromEnglishName(name));

    [Theory]
    [InlineData("bul", "bg")]
    [InlineData("eng", "en")]
    [InlineData("ger", "de")]
    [InlineData("fre", "fr")]
    [InlineData("BG", "bg")]
    public void TwoLetterCodes(string code, string expected) => Assert.Equal(expected, Languages.ToTwoLetter(code));

    [Fact]
    public void SubtitleIdRoundTrip()
    {
        var job = new SubtitleJob("/mnt/media/Deep Water (2026)/Deep Water (2026).mkv", "Дълбока вода (2026)", "bg", "openai", "gemini");
        var id = AiSubtitleProvider.Encode(job);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('+', id);
        Assert.Equal(job, AiSubtitleProvider.Decode(id));
    }

    [Fact]
    public void LedgerSumsTheCurrentMonth()
    {
        var dir = Directory.CreateTempSubdirectory("ai-ledger-").FullName;
        try
        {
            var ledger = new Ledger(dir);
            var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            ledger.Add(new UsageEntry(now, "A", "transcribe", "openai", "whisper-1", 100, 0, 0, 0.6));
            ledger.Add(new UsageEntry(now, "A", "translate", "gemini", "gemini-3.8-flash", 0, 40000, 50000, 0.2175));
            ledger.Add(new UsageEntry(now.AddMonths(-1), "B", "transcribe", "openai", "whisper-1", 90, 0, 0, 0.54));

            Assert.Equal(0.8175, ledger.MonthTotal(now), 6);
            Assert.Equal(3, new Ledger(dir).ReadAll().Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class RepairTests
{
    private static Cue C(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);

    [Fact]
    public void FixesThePinocchioChunkBoundaryCase()
    {
        // Real output (Gemini word timestamps went wrong after 1440 s): one-word cues lasting minutes, out of order.
        var broken = new[]
        {
            C(1437.2, 1438.2, "Махай се от мен."),
            C(1442.3, 1442.7, "Ако пак"),
            C(1442.7, 1712.8, "се навърташ"),
            C(1712.8, 1732.9, "около"),
            C(1732.9, 1743.2, "този"),
            C(1743.2, 1743.7, "храм,"),
            C(1444.3, 1445.3, "ще те убия."),
            C(1447.0, 2147.1, "Какво,"),
            C(2147.1, 2157.2, "по дяволите,"),
            C(1451.2, 1452.2, "Всичките."),
        };

        var fixedCues = CueTools.Repair(broken);

        Assert.All(fixedCues, c => Assert.InRange((c.End - c.Start).TotalSeconds, 0.29, 7.0));
        for (int i = 1; i < fixedCues.Count; i++)
        {
            Assert.True(fixedCues[i - 1].End <= fixedCues[i].Start, $"overlap at {i}");
        }

        Assert.All(fixedCues, c => Assert.InRange(c.Start.TotalSeconds, 1437, 1460));

        // Spoken order is kept: the sentence comes back whole, "ще те убия." after "този храм,".
        Assert.Contains(fixedCues, c => c.Text.Replace('\n', ' ') == "Ако пак се навърташ около този храм, ще те убия.");
        Assert.Equal("Всичките.", fixedCues[^1].Text);
    }

    [Fact]
    public void LeavesGoodHumanSubtitlesAlone()
    {
        var good = new[] { C(1, 3, "Обичам те със всеки дъх,"), C(3.1, 5, "усмивки, сълзи на целия ми живот."), C(6, 8, "Добре.") };
        Assert.Equal(good, CueTools.Repair(good, joinFragments: false));
    }

    [Fact]
    public void SanitizesWordsBeforeGrouping()
    {
        TimedWord W(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);
        var cues = CueTools.GroupWords([W(10, 10.3, "Ако"), W(10.3, 10.5, "пак"), W(10.6, 280, "се"), W(280, 300, "навърташ"), W(11.5, 11.9, "тук."), W(12.5, 13, "Махай"), W(13, 13.4, "се!")]);
        Assert.All(cues, c => Assert.True((c.End - c.Start).TotalSeconds <= 7));
        Assert.StartsWith("Ако пак се навърташ тук.", cues[0].Text.Replace('\n', ' '), StringComparison.Ordinal);
        Assert.InRange(cues[^1].End.TotalSeconds, 12, 15);
    }
}
