using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Billing;
using Jellyfin.Plugin.AiTranslate.Providers;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Theory]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000 X1:100 X2:200 Y1:10 Y2:20\nPosition after the time\n")]
    [InlineData("1 \r\n00:00:01,000 --> 00:00:02,000\r\nCounter with a space\r\n")]
    [InlineData("1\n0:00:01,000 --> 0:00:02,000\nOne-digit hours\n")]
    [InlineData("00:00:01,000 --> 00:00:02,000\nNo counter\n")]
    [InlineData("1\n00:00:01.000 --> 00:00:02.000\nDots\n")]
    [InlineData("1\n00:00:01,0 --> 00:00:02,00\nShort milliseconds\n")]
    public void ParsesSrtAsFoundInTheWild(string srt)
    {
        // Measured before: each of these lost the cue.
        var cue = Assert.Single(CueTools.ParseSrt(srt));
        Assert.Equal(TimeSpan.FromSeconds(1), cue.Start);
        Assert.Equal(TimeSpan.FromSeconds(2), cue.End);
        Assert.DoesNotContain("-->", cue.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SeparatesCuesWithoutAProperBlankLine()
    {
        // Measured before: both were read as one cue with the second timing line inside its text.
        Assert.Equal(["First", "Second\nline"], CueTools.ParseSrt("1\n00:00:01,000 --> 00:00:02,000\nFirst\n2\n00:00:03,000 --> 00:00:04,500\nSecond\nline\n").Select(c => c.Text));
        var cues = CueTools.ParseSrt("1\n00:00:01,000 --> 00:00:02,000\nFirst\n \t\n2\n00:00:03,000 --> 00:00:04,000\nSecond\n");
        Assert.Equal(["First", "Second"], cues.Select(c => c.Text));
        Assert.Equal(TimeSpan.FromSeconds(3), cues[1].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), CueTools.ParseSrt("1\n00:00:01,5 --> 00:00:02,000\nx\n")[0].Start);
    }

    [Fact]
    public void NeverWritesNegativeTimes()
    {
        // A displaced first cue was spread back before 00:00:00 ("00:00:00,-933 --> 00:00:00,350").
        TimedWord W(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);
        var repaired = CueTools.Repair([C(30, 31, "Hello there, friend."), C(0.4, 1.2, "A b c."), C(1.3, 2, "D e f."), C(2.1, 3, "G h i.")]);
        Assert.All(repaired, c => Assert.True(c.Start >= TimeSpan.Zero, c.Start.ToString()));
        Assert.All(CueTools.SanitizeWords([W(9, 9.5, "Late"), W(0.1, 0.3, "a"), W(0.3, 0.5, "b"), W(0.5, 0.7, "c")]), w => Assert.True(w.Start >= TimeSpan.Zero));
        Assert.StartsWith("1\n00:00:00,000 --> 00:00:01,000", CueTools.ToSrt([new Cue(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1), "x")]), StringComparison.Ordinal);
    }

    private static Cue C(double s, double e, string t) => new(TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e), t);
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

    [Theory]
    [InlineData("OpenAI 429: {\"error\":{\"type\":\"insufficient_quota\",\"code\":\"credit_balance_exhausted\"}}")]
    [InlineData("OpenAI 401: {\"error\":{\"message\":\"Incorrect API key provided\"}}")]
    [InlineData("Gemini 400: {\"error\":{\"message\":\"API key not valid\"}}")]
    public async Task ProviderErrorStopsAtOnceInsteadOfReturningTheOriginal(string error)
    {
        // Measured before: no exception, 3 attempts, and the English lines came back as the "translation" (and were cached).
        var fake = new FakeTranslator((_, _) => throw new HttpRequestException(error));
        var usage = new List<Usage>();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Translate(fake, Lines(120), null, usage));
        Assert.Equal(error, ex.Message);
        Assert.InRange(fake.Calls, 1, 3);   // at most one try per batch (3 run at the same time), no retries
    }

    [Fact]
    public async Task FailedRequestsStopTheJobAndTheNextRunOnlyPaysForTheRest()
    {
        var cache = Directory.CreateTempSubdirectory("ai-batches-").FullName;
        try
        {
            // Batch 2 of 3 (lines 50–99) keeps failing with a server error.
            var failing = new FakeTranslator((_, user) => user.Contains("\"line 50\"", StringComparison.Ordinal) ? throw new HttpRequestException("Gemini 503: overloaded") : FakeTranslator.Answer(user));
            var usage = new List<Usage>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => Translate(failing, Lines(120), cache, usage));
            Assert.Equal(2, usage.Count);                                   // the two good batches were paid and are recorded
            Assert.Equal(2, Directory.GetFiles(cache).Length);              // … and kept

            var working = new FakeTranslator((_, user) => FakeTranslator.Answer(user));
            var cues = await Translate(working, Lines(120), cache, new List<Usage>());
            Assert.Equal(1, working.Calls);                                 // only the failed batch is sent again
            Assert.All(cues, c => Assert.StartsWith("BG:", c.Text, StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(cache));                        // done: nothing left to resume
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task UnusableAnswersKeepTheOriginalLinesOnlyWhenTheyAreFew()
    {
        // 600 lines = 12 batches; one batch always answers without lines (e.g. blocked content).
        string Answer(string user, params string[] badFirstLines) =>
            badFirstLines.Any(b => user.Contains($"\"{b}\"", StringComparison.Ordinal)) ? "{\"lines\":[]}" : FakeTranslator.Answer(user);

        var oneBad = await Translate(new FakeTranslator((_, u) => Answer(u, "line 100")), Lines(600), null, new List<Usage>());
        Assert.Equal("line 120", oneBad[120].Text);                       // kept in the original
        Assert.Equal("BG:line 0", oneBad[0].Text);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Translate(new FakeTranslator((_, u) => Answer(u, "line 100", "line 300")), Lines(600), null, new List<Usage>()));
    }

    private static List<Cue> Lines(int n) => Enumerable.Range(0, n).Select(i => new Cue(TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds((i * 3) + 2), $"line {i}")).ToList();

    private static Task<List<Cue>> Translate(ITranslator translator, List<Cue> source, string? cache, List<Usage> usage) =>
        new Translation(NullLogger.Instance) { RetryPause = TimeSpan.Zero }
            .TranslateAsync(translator, source, "en", "bg", "Film (2026)", new Configuration.PluginConfiguration(), cache, "fake|model|low", usage, CancellationToken.None);
}

/// <summary>A translator whose answers the test decides: (call number, request JSON) → answer JSON, or an exception.</summary>
internal sealed class FakeTranslator : ITranslator
{
    private readonly Func<int, string, string> _answer;
    private int _calls;

    public FakeTranslator(Func<int, string, string> answer) => _answer = answer;

    public int Calls => _calls;

    public string Id => "gemini";

    public string DisplayName(Configuration.PluginConfiguration config) => "Fake";

    public bool IsAvailable(Configuration.PluginConfiguration config) => true;

    public (double Input, double Output) PricePerMTok(Configuration.PluginConfiguration config) => (1, 1);

    public Task<(string Json, Usage Usage)> CompleteJsonAsync(string systemPrompt, string userJson, Configuration.PluginConfiguration config, CancellationToken cancellationToken)
    {
        string json = _answer(Interlocked.Increment(ref _calls), userJson);
        return Task.FromResult((json, new Usage("gemini", "fake", 0, 100, 100, 0.001)));
    }

    /// <summary>Every line of the request as "BG:&lt;line&gt;".</summary>
    public static string Answer(string userJson)
    {
        using var doc = JsonDocument.Parse(userJson);
        var lines = doc.RootElement.GetProperty("lines").EnumerateArray()
            .Select(l => (JsonNode)new JsonObject { ["i"] = l.GetProperty("i").GetInt32(), ["t"] = "BG:" + l.GetProperty("t").GetString() })
            .ToArray();
        return new JsonObject { ["lines"] = new JsonArray(lines) }.ToJsonString();
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

    [Theory]
    [InlineData("Gemini 500: {\"error\":{\"status\":\"INTERNAL\"}}", true)]
    [InlineData("Gemini 502: Bad Gateway", true)]
    [InlineData("Gemini 504: {\"error\":{\"status\":\"DEADLINE_EXCEEDED\"}}", true)]
    [InlineData("OpenAI 500: server_error", true)]
    [InlineData("OpenAI 401: invalid_api_key", false)]
    [InlineData("Gemini 404: model not found", false)]
    public void ServerErrorsAreRetried(string message, bool retryable)
    {
        // A single 500 used to end a 20-minute transcription or a film's voice-over.
        Assert.Equal(retryable, RateLimit.IsRetryable(new HttpRequestException(message)));
    }

    [Fact]
    public void DroppedConnectionsAreRetried()
    {
        Assert.True(RateLimit.IsRetryable(new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset by peer")));
        Assert.True(RateLimit.IsRetryable(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely")));
        Assert.False(RateLimit.IsRetryable(new HttpRequestException("OpenAI 400: bad request")));
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

        var brazil = new SubtitleJob("/m/f.mkv", "F", "pt-br", "-", "openai", SourceKind.Embedded, "3", "en");
        Assert.Equal(brazil, AiSubtitleProvider.Decode(AiSubtitleProvider.Encode(brazil)));
    }

    [Fact]
    public void SubtitleIdsThatCouldLeaveTheCacheAreRejected()
    {
        // The id comes back from the client and its fields end up in cache file names.
        SubtitleJob[] evil =
        [
            new("/m/f.mkv", "F", "../../../../tmp/x", "openai", "gemini"),
            new("/m/f.mkv", "F", "bg", "-", "gemini", SourceKind.File, "/m/f.en.srt", "en/../../../x"),
            new("/m/f.mkv", "F", "bg", "openai", "../../x"),
            new("/m/f.mkv", "F", "bg", "../x", "gemini"),
            new("/m/f.mkv", "F", "bg", "-", "gemini", "elsewhere", "/etc/passwd", "en"),
            new("/m/f.mkv", "F", "bg", "-", "gemini", SourceKind.Embedded, "0:s:0", "en"),
            new(string.Empty, "F", "bg", "openai", "gemini"),
        ];
        Assert.All(evil, job => Assert.Throws<ArgumentException>(() => AiSubtitleProvider.Decode(AiSubtitleProvider.Encode(job))));
    }

    [Fact]
    public async Task SingleFlightStartsOneJobPerKey()
    {
        var flights = new SingleFlight<string, int>(StringComparer.Ordinal);
        int starts = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // 50 requests at the same moment from several threads (a double click, two clients): one paid job.
        var all = new Task<int>[50];
        Parallel.For(0, all.Length, i => all[i] = flights.Run(
            "film|bg",
            async _ =>
            {
                Interlocked.Increment(ref starts);
                await gate.Task;
                return 7;
            },
            out _));
        Assert.True(flights.IsRunning("film|bg"));
        gate.SetResult();
        Assert.All(await Task.WhenAll(all), r => Assert.Equal(7, r));
        Assert.Equal(1, starts);
        Assert.False(flights.IsRunning("film|bg"));

        // A failure is not remembered: the next request starts again.
        var failed = flights.Run("film|bg", _ => Task.FromException<int>(new InvalidOperationException("quota")), out bool first);
        Assert.True(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failed);
        Assert.Equal(8, await flights.Run("film|bg", _ => Task.FromResult(8), out bool again));
        Assert.True(again);
    }

    [Fact]
    public async Task SingleFlightStopsAJobOnRequestOnly()
    {
        var flights = new SingleFlight<string, int>(StringComparer.Ordinal);
        var running = flights.Run("dub", async ct => { await Task.Delay(Timeout.Infinite, ct); return 1; }, out _);
        Assert.False(flights.Cancel("other"));
        Assert.True(flights.Cancel("dub"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(running.IsCanceled);
        Assert.False(flights.IsRunning("dub"));
        Assert.False(flights.Cancel("dub"));

        // A job's own timeout (not a Stop) is a failure, not a stop.
        var timedOut = flights.Run("tts", async _ => { using var t = new CancellationTokenSource(10); await Task.Delay(Timeout.Infinite, t.Token); return 1; }, out _);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timedOut);
        Assert.True(timedOut.IsFaulted);
    }

    [Fact]
    public void BudgetHoldsTheEstimatesOfRunningJobs()
    {
        var dir = Directory.CreateTempSubdirectory("ai-budget-").FullName;
        try
        {
            var ledger = new Ledger(dir);
            var now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            ledger.Add(new UsageEntry(now, "A", "translate", "gemini", "m", 0, 0, 0, 6.0));

            // $6 spent of $10: a $3 job fits, a second $3 job started at the same time does not.
            using (ledger.Reserve(3.0, 10, "Film 1", now))
            {
                Assert.Equal(3.0, ledger.Reserved);
                var ex = Assert.Throws<BudgetExceededException>(() => ledger.Reserve(3.0, 10, "Film 2", now));
                Assert.Contains("„Film 2“", ex.Message, StringComparison.Ordinal);
                ledger.Reserve(0.5, 0, "No limit", now).Dispose();
            }

            Assert.Equal(0, ledger.Reserved);
            ledger.Reserve(3.0, 10, "Film 2", now).Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CostLogMovesOutOfTheOldFallbackFolder()
    {
        // Services created before the plugin wrote the cost log to {data}/ai-translate: its spending must not vanish.
        var root = Directory.CreateTempSubdirectory("ai-data-").FullName;
        try
        {
            string legacy = Path.Combine(root, "data", "ai-translate");
            string folder = Path.Combine(root, "plugins", "Jellyfin.Plugin.AiTranslate");
            Directory.CreateDirectory(legacy);
            var now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            new Ledger(legacy).Add(new UsageEntry(now, "Old", "tts", "openai", "m", 1, 0, 0, 0.46));
            new Ledger(folder).Add(new UsageEntry(now.AddDays(1), "New", "translate", "gemini", "m", 0, 1, 1, 0.07));
            File.WriteAllText(Path.Combine(legacy, "quota.json"), "{\"Day\":\"2026-10-16\",\"Counts\":{}}");

            PluginData.MergeLegacy(legacy, folder);

            Assert.Equal(["Old", "New"], new Ledger(folder).ReadAll().Select(e => e.Item));
            Assert.Equal(0.53, new Ledger(folder).MonthTotal(now), 6);
            Assert.True(File.Exists(Path.Combine(folder, "quota.json")));
            Assert.True(File.Exists(Path.Combine(legacy, "ledger.jsonl.moved")));
            Assert.False(File.Exists(Path.Combine(legacy, "ledger.jsonl")));

            PluginData.MergeLegacy(legacy, folder);                 // nothing left to move: no double counting
            Assert.Equal(2, new Ledger(folder).ReadAll().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
