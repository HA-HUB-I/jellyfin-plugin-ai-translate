using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Dubbing;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>OpenAI text-to-speech (/v1/audio/speech, WAV). Bulgarian works; the voices are tuned for English.</summary>
public sealed class OpenAiTts : ITtsProvider
{
    private const string Url = "https://api.openai.com/v1/audio/speech";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Pacer _pacer = new();

    public OpenAiTts(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Id => "openai";

    public IReadOnlyList<(string Id, string Description)> Voices { get; } =
    [
        ("onyx", "мъжки, дълбок"),
        ("ash", "мъжки, ясен"),
        ("echo", "мъжки, мек"),
        ("cedar", "мъжки, естествен (препоръчан)"),
        ("verse", "мъжки, изразителен"),
        ("ballad", "мъжки, мелодичен"),
        ("marin", "женски, естествен (препоръчан)"),
        ("coral", "женски, топъл"),
        ("nova", "женски, ярък"),
        ("shimmer", "женски, лек"),
        ("sage", "женски, спокоен"),
        ("alloy", "неутрален"),
        ("fable", "неутрален, разказвач"),
    ];

    public string Model(PluginConfiguration config) => config.OpenAiTtsModel;

    public string DisplayName(PluginConfiguration config) => "OpenAI " + config.OpenAiTtsModel;

    public bool IsAvailable(PluginConfiguration config) => !string.IsNullOrWhiteSpace(config.OpenAiApiKey);

    // No pause tags: a paragraph break plus the instruction below gives a clear pause; the splitter falls back to text share.
    public string LineSeparator(PluginConfiguration config) => "\n\n";

    public int RequestsPerMinute(PluginConfiguration config) => config.OpenAiTtsRequestsPerMinute;

    public int RequestsPerDay(PluginConfiguration config) => 0;

    public double Estimate(int characters, double seconds, PluginConfiguration config) =>
        ((characters / 3.0 * config.PriceOpenAiTtsInputPerMTok) / 1_000_000) + (seconds / 60 * config.PriceOpenAiTtsPerMinute);

    public Task<SpeechClip> SynthesizeAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken) =>
        RateLimit.RetryAsync(
            async () =>
            {
                await _pacer.WaitAsync(config.OpenAiTtsRequestsPerMinute, cancellationToken).ConfigureAwait(false);
                return await SynthesizeOnceAsync(text, voice, style, config, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    private async Task<SpeechClip> SynthesizeOnceAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = config.OpenAiTtsModel,
            ["voice"] = voice,
            ["input"] = text,
            ["instructions"] = $"Speak Bulgarian. {style}. Pause for about one second at every paragraph break.",
            ["response_format"] = "wav",
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAiApiKey.Trim());
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"OpenAI TTS {(int)response.StatusCode}: {(body.Length > 400 ? body[..400] : body)}"));
        }

        var (samples, rate) = Wav.ReadPcm16Mono(bytes);
        double seconds = samples.Length / (double)rate;

        // The speech endpoint returns no usage; cost is estimated from the audio length.
        return new SpeechClip(samples, rate, new Usage("openai", config.OpenAiTtsModel, seconds / 60, text.Length / 3, 0, Estimate(text.Length, seconds, config)));
    }
}
