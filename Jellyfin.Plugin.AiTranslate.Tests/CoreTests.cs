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
