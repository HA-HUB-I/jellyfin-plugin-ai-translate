using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Dubbing;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Gemini TTS through the Interactions API (24 kHz mono WAV; 25 audio tokens per second).</summary>
public sealed class GeminiTts : ITtsProvider
{
    private const string Url = "https://generativelanguage.googleapis.com/v1beta/interactions";
    private const double AudioTokensPerSecond = 25;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Pacer _pacer = new();

    public GeminiTts(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Id => "gemini";

    public IReadOnlyList<(string Id, string Description)> Voices { get; } =
    [
        ("Charon", "мъжки, спокоен, информативен"),
        ("Orus", "мъжки, твърд"),
        ("Fenrir", "мъжки, енергичен"),
        ("Puck", "мъжки, жизнерадостен"),
        ("Iapetus", "мъжки, ясен"),
        ("Algieba", "мъжки, мек"),
        ("Kore", "женски, твърд"),
        ("Aoede", "женски, лек"),
        ("Leda", "женски, младежки"),
        ("Zephyr", "женски, ярък"),
        ("Despina", "женски, мек"),
        ("Sulafat", "женски, топъл"),
    ];

    public string DisplayName(PluginConfiguration config) => "Google Gemini " + config.GeminiTtsModel;

    public bool IsAvailable(PluginConfiguration config) => !string.IsNullOrWhiteSpace(config.GeminiApiKey);

    public double Estimate(int characters, double seconds, PluginConfiguration config) =>
        ((characters / 3.0 * config.PriceGeminiTtsInputPerMTok) + (seconds * AudioTokensPerSecond * config.PriceGeminiTtsAudioPerMTok)) / 1_000_000;

    public Task<SpeechClip> SynthesizeAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken) =>
        RateLimit.RetryAsync(
            async () =>
            {
                await _pacer.WaitAsync(config.GeminiTtsRequestsPerMinute, cancellationToken).ConfigureAwait(false);
                return await SynthesizeOnceAsync(text, voice, style, config, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Wall-clock time for <paramref name="requests"/> requests at the tier's pace.</summary>
    public static TimeSpan PacedTime(int requests, PluginConfiguration config) =>
        TimeSpan.FromMinutes(requests / (double)Math.Max(1, config.GeminiTtsRequestsPerMinute));

    internal static (byte[] Wav, long InputTokens, long OutputTokens) ParseInteraction(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        string? data = null;
        if (root.TryGetProperty("steps", out var steps))
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (step.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in content.EnumerateArray())
                    {
                        if (c.TryGetProperty("type", out var t) && t.GetString() == "audio" && c.TryGetProperty("data", out var d))
                        {
                            data = d.GetString();
                        }
                    }
                }
            }
        }

        if (data is null)
        {
            throw new HttpRequestException("Gemini TTS: no audio in the answer");
        }

        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            input = usage.TryGetProperty("total_input_tokens", out var i) ? i.GetInt64() : 0;
            output = usage.TryGetProperty("total_output_tokens", out var o) ? o.GetInt64() : 0;
        }

        return (Convert.FromBase64String(data), input, output);
    }

    private async Task<SpeechClip> SynthesizeOnceAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var content = new JsonObject { ["type"] = "text", ["text"] = text };
        if (!string.IsNullOrWhiteSpace(style))
        {
            content["annotations"] = new JsonArray(new JsonObject { ["type"] = "speech_metadata", ["style"] = style });
        }

        var payload = new JsonObject
        {
            ["model"] = config.GeminiTtsModel,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "user_input", ["content"] = new JsonArray(content) }),
            ["response_format"] = new JsonObject { ["type"] = "audio" },
            ["generation_config"] = new JsonObject { ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = voice }) },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-goog-api-key", config.GeminiApiKey.Trim());
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"Gemini TTS {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}"));
        }

        var (wav, input, output) = ParseInteraction(body);
        var (samples, rate) = Wav.ReadPcm16Mono(wav);
        double cost = ((input * config.PriceGeminiTtsInputPerMTok) + (output * config.PriceGeminiTtsAudioPerMTok)) / 1_000_000;
        return new SpeechClip(samples, rate, new Usage("gemini", config.GeminiTtsModel, samples.Length / (double)rate / 60, input, output, cost));
    }
}
