using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Models an account can use, grouped by what the plugin needs them for.</summary>
public sealed record ModelLists(IReadOnlyList<string> Transcribe, IReadOnlyList<string> Text, IReadOnlyList<string> Tts, string? Error);

/// <summary>Lists the current models from the providers' model endpoints (cached for an hour).</summary>
public sealed class ModelCatalog
{
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(1);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, (ModelLists Lists, DateTime Utc, string KeyHash)> _cache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public ModelCatalog(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<ModelLists> GetAsync(string provider, PluginConfiguration config, CancellationToken cancellationToken)
    {
        string key = provider == "openai" ? config.OpenAiApiKey.Trim() : config.GeminiApiKey.Trim();
        if (key.Length == 0)
        {
            return new ModelLists([], [], [], "Няма въведен ключ.");
        }

        string keyHash = SubtitleJobService.Hash(key);
        lock (_lock)
        {
            if (_cache.TryGetValue(provider, out var c) && c.KeyHash == keyHash && DateTime.UtcNow - c.Utc < CacheTime)
            {
                return c.Lists;
            }
        }

        ModelLists lists;
        try
        {
            lists = provider == "openai" ? ClassifyOpenAi(await GetOpenAiAsync(key, cancellationToken).ConfigureAwait(false))
                : ClassifyGemini(await GetGeminiAsync(key, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Shown next to the key on the settings page (an unexpected answer or a timeout must not break the page).
            return new ModelLists([], [], [], ex.Message);
        }

        lock (_lock)
        {
            _cache[provider] = (lists, DateTime.UtcNow, keyHash);
        }

        return lists;
    }

    /// <summary>Sorts Gemini models (name, supported methods) into the plugin's three uses.</summary>
    internal static ModelLists ClassifyGemini(IEnumerable<(string Name, IReadOnlyList<string> Methods)> models)
    {
        var all = models.Select(m => (Id: m.Name.StartsWith("models/", StringComparison.Ordinal) ? m.Name[7..] : m.Name, m.Methods)).ToList();
        static bool Has(string id, params string[] parts) => parts.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase));
        var tts = all.Where(m => Has(m.Id, "tts")).Select(m => m.Id);
        var transcribe = all.Where(m => Has(m.Id, "transcribe") && !Has(m.Id, "live")).Select(m => m.Id);
        var text = all.Where(m => m.Id.StartsWith("gemini", StringComparison.Ordinal)
                && m.Methods.Contains("generateContent")
                && !Has(m.Id, "tts", "transcribe", "image", "banana", "embedding", "live", "audio", "robotics", "computer-use"))
            .Select(m => m.Id);
        return new ModelLists(Sort(transcribe), Sort(text), Sort(tts), null);
    }

    /// <summary>Sorts OpenAI model ids into the plugin's three uses.</summary>
    internal static ModelLists ClassifyOpenAi(IEnumerable<string> ids)
    {
        var all = ids.ToList();
        static bool Has(string id, params string[] parts) => parts.Any(p => id.Contains(p, StringComparison.OrdinalIgnoreCase));
        var tts = all.Where(id => Has(id, "tts"));
        var transcribe = all.Where(id => Has(id, "transcribe", "whisper") && !Has(id, "realtime", "live"));
        var text = all.Where(id => (id.StartsWith("gpt-", StringComparison.Ordinal) || (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1])))
            && !Has(id, "tts", "transcribe", "audio", "realtime", "image", "search", "instruct", "embedding", "codex", "computer"));
        return new ModelLists(Sort(transcribe), Sort(text), Sort(tts), null);
    }

    // Newest-looking first: dated snapshots after their alias, otherwise descending by name.
    private static List<string> Sort(IEnumerable<string> ids) =>
        ids.Distinct(StringComparer.Ordinal).OrderByDescending(i => i, StringComparer.Ordinal).ToList();

    private async Task<List<(string, IReadOnlyList<string>)>> GetGeminiAsync(string key, CancellationToken cancellationToken)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        string? pageToken = null;
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        do
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000" + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken)));
            request.Headers.Add("x-goog-api-key", key);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(ErrorText("Gemini", (int)response.StatusCode, body));
            }

            using var json = JsonDocument.Parse(body);
            foreach (var m in json.RootElement.GetProperty("models").EnumerateArray())
            {
                var methods = m.TryGetProperty("supportedGenerationMethods", out var sm) ? sm.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList() : [];
                result.Add((m.GetProperty("name").GetString() ?? string.Empty, methods));
            }

            pageToken = json.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    private async Task<List<string>> GetOpenAiAsync(string key, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(PluginServiceRegistrator.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(ErrorText("OpenAI", (int)response.StatusCode, body));
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString() ?? string.Empty).ToList();
    }

    /// <summary>"Gemini 400: API key not valid…": the provider's own message tells a wrong key from a missing balance.</summary>
    internal static string ErrorText(string provider, int status, string body)
    {
        string? message = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m))
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
        }

        message ??= body;
        message = message.Trim();
        return string.Create(CultureInfo.InvariantCulture, $"{provider} {status}{(message.Length > 0 ? ": " + (message.Length > 200 ? message[..200] : message) : string.Empty)}");
    }
}
