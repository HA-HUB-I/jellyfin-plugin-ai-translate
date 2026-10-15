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

/// <summary>OpenAI: whisper-1 transcription (segment timestamps) and the Responses API for translation.</summary>
public sealed class OpenAiProvider : ITranscriber, ITranslator
{
    private const string BaseUrl = "https://api.openai.com/v1/";
    private readonly IHttpClientFactory _httpClientFactory;

    public OpenAiProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Id => "openai";

    // whisper-1 accepts 25 MB; 20 minutes of 32 kbit/s MP3 is about 4.6 MB.
    public TimeSpan ChunkLength => TimeSpan.FromMinutes(20);

    public string DisplayName(PluginConfiguration config) => "OpenAI";

    public bool IsAvailable(PluginConfiguration config) => !string.IsNullOrWhiteSpace(config.OpenAiApiKey);

    public double PricePerMinute(PluginConfiguration config) => config.PriceOpenAiTranscribePerMinute;

    public (double Input, double Output) PricePerMTok(PluginConfiguration config) => (config.PriceOpenAiTextInputPerMTok, config.PriceOpenAiTextOutputPerMTok);

    public async Task<Transcription> TranscribeAsync(AudioChunk chunk, PluginConfiguration config, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(chunk.Path, cancellationToken).ConfigureAwait(false));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(file, "file", Path.GetFileName(chunk.Path));
        form.Add(new StringContent(config.OpenAiTranscribeModel), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "audio/transcriptions") { Content = form };
        var body = await SendAsync(request, config, cancellationToken).ConfigureAwait(false);
        var (cues, language) = ParseVerboseJson(body);
        double minutes = chunk.Duration.TotalMinutes;
        return new Transcription(cues, language, new Usage(Id, config.OpenAiTranscribeModel, minutes, 0, 0, minutes * config.PriceOpenAiTranscribePerMinute));
    }

    public async Task<(string Json, Usage Usage)> CompleteJsonAsync(string systemPrompt, string userJson, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["model"] = config.OpenAiTextModel,
            ["instructions"] = systemPrompt,
            ["input"] = userJson,
            ["text"] = new JsonObject { ["format"] = Translation.OpenAiJsonSchemaFormat() },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "responses")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var body = await SendAsync(request, config, cancellationToken).ConfigureAwait(false);
        var (text, input, output) = ParseResponses(body);
        var (pi, po) = PricePerMTok(config);
        return (text, new Usage(Id, config.OpenAiTextModel, 0, input, output, ((input * pi) + (output * po)) / 1_000_000));
    }

    internal static (List<Cue> Cues, string? Language) ParseVerboseJson(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var segments = new List<Cue>();
        if (root.TryGetProperty("segments", out var segs))
        {
            foreach (var s in segs.EnumerateArray())
            {
                segments.Add(new Cue(
                    TimeSpan.FromSeconds(s.GetProperty("start").GetDouble()),
                    TimeSpan.FromSeconds(s.GetProperty("end").GetDouble()),
                    s.GetProperty("text").GetString() ?? string.Empty));
            }
        }

        string? language = root.TryGetProperty("language", out var l) ? Languages.FromEnglishName(l.GetString()) : null;
        return (CueTools.SplitLongSegments(segments), language);
    }

    internal static (string Text, long InputTokens, long OutputTokens) ParseResponses(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var sb = new StringBuilder();
        if (root.TryGetProperty("output", out var output))
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var c in content.EnumerateArray())
                {
                    if (c.TryGetProperty("type", out var t) && t.GetString() == "output_text")
                    {
                        sb.Append(c.GetProperty("text").GetString());
                    }
                }
            }
        }

        long input = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            input = usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
            outputTokens = usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
        }

        return (sb.ToString(), input, outputTokens);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, PluginConfiguration config, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAiApiKey.Trim());
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"OpenAI {(int)response.StatusCode}: {Trim(body)}"));
        }

        return body;
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] : s;
}
