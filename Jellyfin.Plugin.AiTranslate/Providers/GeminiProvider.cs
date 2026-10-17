using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Media;
using Jellyfin.Plugin.AiTranslate.Subtitles;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Google Gemini API: Gemini Transcribe (word timestamps, via the Files API) and generateContent for translation.</summary>
public sealed class GeminiProvider : ITranscriber, ITranslator, IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/";
    private const int MaxTranscribeAttempts = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly IHttpClientFactory _httpClientFactory;

    public GeminiProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Gets the pause before a transcription that "failed" on Google's side is asked for again (tests use zero).</summary>
    internal TimeSpan RetryPause { get; init; } = TimeSpan.FromSeconds(10);

    // Gemini bills audio input at 25 tokens per second.
    private const double AudioTokensPerSecond = 25;

    // One transcription at a time, paced to the tier's tokens-per-minute limit.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _thinkingUnsupported = new(StringComparer.Ordinal);
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    public string Id => "gemini";

    public int Parallelism => 1;

    public void Dispose() => _gate.Dispose();

    /// <summary>Word timestamps allow 30 minutes per request; the tier's token limit usually allows much less.</summary>
    public TimeSpan ChunkLength(PluginConfiguration config)
    {
        double byRateLimit = 0.9 * Math.Max(1000, config.GeminiAudioTokensPerMinute) / AudioTokensPerSecond;
        return TimeSpan.FromSeconds(Math.Clamp(byRateLimit, 60, 25 * 60));
    }

    public TimeSpan EstimatedTime(TimeSpan duration, PluginConfiguration config)
    {
        // Paced: each chunk needs (its tokens / limit) minutes before the next one may start.
        double tokens = duration.TotalSeconds * AudioTokensPerSecond;
        double minutes = tokens / Math.Max(1000, config.GeminiAudioTokensPerMinute);
        return TimeSpan.FromMinutes(minutes + 2);
    }

    public string DisplayName(PluginConfiguration config) => "Google Gemini";

    public bool IsAvailable(PluginConfiguration config) => !string.IsNullOrWhiteSpace(config.GeminiApiKey);

    public double PricePerMinute(PluginConfiguration config) => config.PriceGeminiTranscribePerMinute;

    public (double Input, double Output) PricePerMTok(PluginConfiguration config) => (config.PriceGeminiTextInputPerMTok, config.PriceGeminiTextOutputPerMTok);

    public async Task<Transcription> TranscribeAsync(AudioChunk chunk, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var (fileName, fileUri) = await RateLimit.RetryAsync(() => UploadAsync(chunk.Path, "audio/mpeg", config, cancellationToken), cancellationToken).ConfigureAwait(false);
        try
        {
            var payload = new JsonObject
            {
                ["model"] = config.GeminiTranscribeModel,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "audio", ["uri"] = fileUri, ["mime_type"] = "audio/mpeg" }),
                ["generation_config"] = new JsonObject
                {
                    ["transcription_config"] = new JsonObject
                    {
                        ["mode"] = new JsonObject { ["type"] = "verbatim", ["timestamp_granularities"] = new JsonArray("word") },
                    },
                },
            };
            // A running or failed interaction has no words (or only some): never pass it on as a silent chunk — the
            // transcript is cached, so the gap would stay in every subtitle made from it. "failed"/"expired"/"cancelled"
            // are faults on Google's side: ask again. "incomplete" (or still running after 30 min) means the model got
            // stuck on this audio and would do so again: the caller cuts the chunk into shorter pieces.
            string body;
            string? status;
            int attempts = 0;
            while (true)
            {
                attempts++;
                body = await StartInteractionAsync(payload, chunk, config, cancellationToken).ConfigureAwait(false);

                // Long audio may come back as a running interaction; poll until it completes.
                for (int i = 0; i < 360 && IsUnfinished(Status(body)); i++)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    string interaction = "v1beta/" + InteractionId(body);
                    body = await RateLimit.RetryAsync(() => SendJsonAsync(HttpMethod.Get, interaction, null, config, cancellationToken), cancellationToken).ConfigureAwait(false);
                }

                status = Status(body);
                if (!IsUnfinished(status) && !IsFailed(status))
                {
                    break;
                }

                if (status == "incomplete" || IsUnfinished(status) || attempts >= MaxTranscribeAttempts)
                {
                    double tried = chunk.Duration.TotalMinutes * attempts;
                    throw new TranscriptionIncompleteException(
                        string.Create(CultureInfo.InvariantCulture, $"Gemini transcription of {chunk.Duration.TotalSeconds:0} s at {chunk.Offset:hh\\:mm\\:ss} ended as \"{status}\": {(body.Length > 300 ? body[..300] : body)}"),
                        new Usage(Id, config.GeminiTranscribeModel, tried, 0, 0, tried * config.PriceGeminiTranscribePerMinute));
                }

                await Task.Delay(RetryPause, cancellationToken).ConfigureAwait(false);
            }

            var words = ParseWordInfo(body);

            // Every attempt sent the whole chunk: all of them count.
            double minutes = chunk.Duration.TotalMinutes * attempts;
            return new Transcription(CueTools.GroupWords(words), null, new Usage(Id, config.GeminiTranscribeModel, minutes, 0, 0, minutes * config.PriceGeminiTranscribePerMinute));
        }
        finally
        {
            try
            {
                await SendJsonAsync(HttpMethod.Delete, "v1beta/" + fileName, null, config, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Files expire on their own after 48 hours; a failed clean-up must not hide the result.
            }
        }
    }

    /// <summary>Sends the transcription request, one at a time and paced to the tier's audio tokens per minute.</summary>
    private async Task<string> StartInteractionAsync(JsonObject payload, AudioChunk chunk, PluginConfiguration config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextAllowedUtc - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            string body = await RateLimit.RetryAsync(() => SendJsonAsync(HttpMethod.Post, "v1beta/interactions", payload, config, cancellationToken), cancellationToken).ConfigureAwait(false);
            double tokens = chunk.Duration.TotalSeconds * AudioTokensPerSecond;
            _nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromMinutes(tokens / Math.Max(1000, config.GeminiAudioTokensPerMinute));
            return body;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(string Json, Usage Usage)> CompleteJsonAsync(string systemPrompt, string userJson, PluginConfiguration config, CancellationToken cancellationToken)
    {
        JsonObject Payload(bool withThinking)
        {
            var generationConfig = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = Translation.GeminiResponseSchema(),
            };
            if (withThinking)
            {
                generationConfig["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = config.GeminiThinkingLevel.Trim() };
            }

            return new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = systemPrompt }) },
                ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = userJson }) }),
                ["generationConfig"] = generationConfig,
            };
        }

        string url = $"v1beta/models/{Uri.EscapeDataString(config.GeminiTextModel)}:generateContent";
        bool thinking = !string.IsNullOrWhiteSpace(config.GeminiThinkingLevel) && !_thinkingUnsupported.ContainsKey(config.GeminiTextModel);
        string body;
        try
        {
            body = await RateLimit.RetryAsync(() => SendJsonAsync(HttpMethod.Post, url, Payload(thinking), config, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (thinking && ex.Message.Contains(" 400:", StringComparison.Ordinal) && ex.Message.Contains("hinking", StringComparison.Ordinal))
        {
            // This model does not take that thinking setting: remember and use the model default.
            _thinkingUnsupported[config.GeminiTextModel] = true;
            body = await RateLimit.RetryAsync(() => SendJsonAsync(HttpMethod.Post, url, Payload(false), config, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        var (text, input, output) = ParseGenerateContent(body);
        var (pi, po) = PricePerMTok(config);
        return (text, new Usage(Id, config.GeminiTextModel, 0, input, output, ((input * pi) + (output * po)) / 1_000_000));
    }

    internal static List<TimedWord> ParseWordInfo(string body)
    {
        var words = new List<TimedWord>();
        using var json = JsonDocument.Parse(body);
        if (!json.RootElement.TryGetProperty("steps", out var steps))
        {
            return words;
        }

        foreach (var step in steps.EnumerateArray())
        {
            if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var c in content.EnumerateArray())
            {
                if (!c.TryGetProperty("annotations", out var annotations))
                {
                    continue;
                }

                foreach (var a in annotations.EnumerateArray())
                {
                    if (a.TryGetProperty("type", out var t) && t.GetString() == "word_info"
                        && a.TryGetProperty("start_offset", out var so) && a.TryGetProperty("end_offset", out var eo))
                    {
                        words.Add(new TimedWord(Offset(so.GetString()), Offset(eo.GetString()), a.GetProperty("text").GetString() ?? string.Empty));
                    }
                }
            }
        }

        return words;
    }

    internal static (string Text, long InputTokens, long OutputTokens) ParseGenerateContent(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
        {
            foreach (var p in parts.EnumerateArray())
            {
                if (p.TryGetProperty("text", out var text) && !(p.TryGetProperty("thought", out var thought) && thought.GetBoolean()))
                {
                    sb.Append(text.GetString());
                }
            }
        }

        long input = 0, output = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            input = usage.TryGetProperty("promptTokenCount", out var i) ? i.GetInt64() : 0;
            output = (usage.TryGetProperty("candidatesTokenCount", out var o) ? o.GetInt64() : 0)
                + (usage.TryGetProperty("thoughtsTokenCount", out var th) ? th.GetInt64() : 0);
        }

        return (sb.ToString(), input, output);
    }

    private static TimeSpan Offset(string? value) =>
        TimeSpan.FromSeconds(double.Parse((value ?? "0").TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture));

    private static bool IsUnfinished(string? status) => status is "in_progress" or "queued" or "running";

    private static bool IsFailed(string? status) => status is "failed" or "cancelled" or "canceled" or "incomplete" or "expired";

    private static string? Status(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
    }

    private static string InteractionId(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Resumable upload to the Files API; waits until the file is ACTIVE.</summary>
    private async Task<(string Name, string Uri)> UploadAsync(string path, string mimeType, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);

        using var start = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "upload/v1beta/files")
        {
            Content = new StringContent(new JsonObject { ["file"] = new JsonObject { ["display_name"] = Path.GetFileName(path) } }.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        start.Headers.Add("x-goog-api-key", config.GeminiApiKey.Trim());
        start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        start.Headers.Add("X-Goog-Upload-Command", "start");
        start.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
        start.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        using var startResponse = await client.SendAsync(start, cancellationToken).ConfigureAwait(false);
        await EnsureOk(startResponse, cancellationToken).ConfigureAwait(false);
        var uploadUrl = startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var urls) ? urls.First() : throw new HttpRequestException("Gemini Files API: no upload URL");

        using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = new ByteArrayContent(bytes) };
        upload.Headers.Add("X-Goog-Upload-Offset", "0");
        upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        using var uploadResponse = await client.SendAsync(upload, cancellationToken).ConfigureAwait(false);
        var body = await EnsureOk(uploadResponse, cancellationToken).ConfigureAwait(false);

        using var json = JsonDocument.Parse(body);
        var file = json.RootElement.GetProperty("file");
        string name = file.GetProperty("name").GetString()!;
        string uri = file.GetProperty("uri").GetString()!;
        string? state = file.TryGetProperty("state", out var s) ? s.GetString() : "ACTIVE";
        for (int i = 0; i < 60 && state == "PROCESSING"; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            using var info = JsonDocument.Parse(await SendJsonAsync(HttpMethod.Get, "v1beta/" + name, null, config, cancellationToken).ConfigureAwait(false));
            state = info.RootElement.TryGetProperty("state", out var st) ? st.GetString() : "ACTIVE";
        }

        return (name, uri);
    }

    private async Task<string> SendJsonAsync(HttpMethod method, string relative, JsonNode? payload, PluginConfiguration config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BaseUrl + relative);
        if (payload is not null)
        {
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }

        request.Headers.Add("x-goog-api-key", config.GeminiApiKey.Trim());
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await EnsureOk(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> EnsureOk(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"Gemini {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}"));
        }

        return body;
    }
}
