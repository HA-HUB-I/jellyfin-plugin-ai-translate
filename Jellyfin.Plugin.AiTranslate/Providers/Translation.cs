using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Subtitles;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Translates cues in numbered batches, keeping timing; validates and retries.</summary>
public sealed class Translation
{
    public const int BatchSize = 50;
    private const int ContextLines = 4;
    private const int MaxAttempts = 3;

    private readonly ILogger _logger;

    public Translation(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<(List<Cue> Cues, List<Usage> Usage)> TranslateAsync(
        ITranslator translator,
        IReadOnlyList<Cue> source,
        string? sourceLanguage,
        string targetLanguage,
        string filmTitle,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var system = SystemPrompt(sourceLanguage, targetLanguage, config.TranslationInstructions);
        var batches = Enumerable.Range(0, (source.Count + BatchSize - 1) / BatchSize).ToList();
        var results = new string[source.Count];
        var usage = new List<Usage>();
        var usageLock = new Lock();

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (b, ct) =>
            {
                int from = b * BatchSize;
                int count = Math.Min(BatchSize, source.Count - from);
                var lines = Enumerable.Range(from, count).Select(i => Flatten(source[i].Text)).ToList();
                var context = Enumerable.Range(Math.Max(0, from - ContextLines), Math.Min(ContextLines, from)).Select(i => Flatten(source[i].Text)).ToList();
                var user = UserJson(filmTitle, context, lines);

                List<string>? translated = null;
                for (int attempt = 1; attempt <= MaxAttempts && translated is null; attempt++)
                {
                    try
                    {
                        var (json, u) = await translator.CompleteJsonAsync(system, user, config, ct).ConfigureAwait(false);
                        lock (usageLock)
                        {
                            usage.Add(u);
                        }

                        translated = ParseLines(json, count);
                        if (translated is null)
                        {
                            _logger.LogWarning("AI Translate: batch {Batch} returned an invalid answer (attempt {Attempt})", b, attempt);
                        }
                    }
                    catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or JsonException)
                    {
                        _logger.LogWarning(ex, "AI Translate: batch {Batch} failed (attempt {Attempt} of {Max})", b, attempt, MaxAttempts);
                        if (attempt < MaxAttempts)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct).ConfigureAwait(false);
                        }
                    }
                }

                for (int k = 0; k < count; k++)
                {
                    // Keep the original line if a batch could not be translated at all.
                    results[from + k] = translated?[k] ?? lines[k];
                }
            }).ConfigureAwait(false);

        var cues = source.Select((c, i) => c with { Text = CueTools.Wrap(results[i]) }).ToList();
        return (cues, usage);
    }

    internal static string SystemPrompt(string? sourceLanguage, string targetLanguage, string extra)
    {
        string target = Languages.EnglishName(targetLanguage);
        string from = sourceLanguage is null ? "the film's original language" : Languages.EnglishName(sourceLanguage);
        return $$"""
            You are a professional film subtitler. Translate subtitle lines from {{from}} into {{target}}.
            Rules:
            - Return JSON {"lines":[{"i":<same i>,"t":"<translation>"}]} with exactly one item for every input line, same i values, same order.
            - Never merge, split, drop or reorder lines: each line is shown on screen at its own time.
            - Write natural, idiomatic spoken {{target}} as in a cinema release, not a word-for-word translation. Match the register (formal/informal address) to who is speaking to whom.
            - Keep it short enough to read: aim for at most 84 characters per line.
            - Use "context_before" and the neighbouring lines only to understand meaning; do not translate them separately.
            - Keep names, places and brands; transliterate names into the {{target}} alphabet when that is the convention.
            - Keep song markers ♪ and dialogue dashes "-". If a line is already in {{target}}, return it unchanged.
            - Output only the JSON.
            {{extra}}
            """;
    }

    internal static string UserJson(string filmTitle, IReadOnlyList<string> context, IReadOnlyList<string> lines)
    {
        var obj = new JsonObject
        {
            ["film"] = filmTitle,
            ["context_before"] = new JsonArray(context.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["lines"] = new JsonArray(lines.Select((t, i) => (JsonNode)new JsonObject { ["i"] = i, ["t"] = t }).ToArray()),
        };
        return obj.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>Returns the translated lines in input order, or null when the answer does not match the batch.</summary>
    internal static List<string>? ParseLines(string json, int expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(StripFence(json));
            var lines = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("lines");
            var map = new Dictionary<int, string>();
            foreach (var item in lines.EnumerateArray())
            {
                map[item.GetProperty("i").GetInt32()] = item.GetProperty("t").GetString() ?? string.Empty;
            }

            if (Enumerable.Range(0, expected).Any(i => !map.ContainsKey(i) || string.IsNullOrWhiteSpace(map[i])))
            {
                return null;
            }

            return Enumerable.Range(0, expected).Select(i => map[i].Trim()).ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static JsonObject OpenAiJsonSchemaFormat() => new()
    {
        ["type"] = "json_schema",
        ["name"] = "subtitle_lines",
        ["strict"] = true,
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("lines"),
            ["properties"] = new JsonObject
            {
                ["lines"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("i", "t"),
                        ["properties"] = new JsonObject
                        {
                            ["i"] = new JsonObject { ["type"] = "integer" },
                            ["t"] = new JsonObject { ["type"] = "string" },
                        },
                    },
                },
            },
        },
    };

    internal static JsonObject GeminiResponseSchema() => new()
    {
        ["type"] = "OBJECT",
        ["required"] = new JsonArray("lines"),
        ["properties"] = new JsonObject
        {
            ["lines"] = new JsonObject
            {
                ["type"] = "ARRAY",
                ["items"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["required"] = new JsonArray("i", "t"),
                    ["properties"] = new JsonObject
                    {
                        ["i"] = new JsonObject { ["type"] = "INTEGER" },
                        ["t"] = new JsonObject { ["type"] = "STRING" },
                    },
                },
            },
        },
    };

    private static string Flatten(string text) => text.Replace('\n', ' ');

    private static string StripFence(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int first = s.IndexOf('\n', StringComparison.Ordinal);
            int last = s.LastIndexOf("```", StringComparison.Ordinal);
            if (first > 0 && last > first)
            {
                s = s[(first + 1)..last];
            }
        }

        return s;
    }
}
