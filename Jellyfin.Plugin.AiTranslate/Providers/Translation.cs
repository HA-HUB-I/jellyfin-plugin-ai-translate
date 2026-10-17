using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

    private static readonly JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ILogger _logger;

    public Translation(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the pause before a batch is sent again (5 s, 10 s, …; tests use zero).</summary>
    internal TimeSpan RetryPause { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Translates all lines. Every finished batch is saved in <paramref name="cacheDir"/> (keyed by
    /// <paramref name="cacheKey"/> and the exact request) until the whole film is done, so a run that stops half way
    /// continues there and does not pay again. A provider error that waiting cannot fix (key, balance, daily quota,
    /// bad request) stops at once, and so does a batch whose requests keep failing: the original text must never be
    /// handed out as the translation. Only a batch whose answers stay unusable (broken JSON, merged lines, blocked
    /// content) keeps its original lines — unless that happens to more than a tenth of the film.
    /// Every request is added to <paramref name="usage"/>, also when this throws.
    /// </summary>
    public async Task<List<Cue>> TranslateAsync(
        ITranslator translator,
        IReadOnlyList<Cue> source,
        string? sourceLanguage,
        string targetLanguage,
        string filmTitle,
        PluginConfiguration config,
        string? cacheDir,
        string cacheKey,
        ICollection<Usage> usage,
        CancellationToken cancellationToken)
    {
        var system = SystemPrompt(sourceLanguage, targetLanguage, config.TranslationInstructions);
        int batchCount = (source.Count + BatchSize - 1) / BatchSize;
        var results = new string[source.Count];
        var cacheFiles = new string?[batchCount];
        int failedBatches = 0, untranslated = 0;
        Exception? lastError = null;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, batchCount),
            new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cancellationToken },
            async (b, ct) =>
            {
                int from = b * BatchSize;
                int count = Math.Min(BatchSize, source.Count - from);
                var lines = Enumerable.Range(from, count).Select(i => Flatten(source[i].Text)).ToList();
                var context = Enumerable.Range(Math.Max(0, from - ContextLines), Math.Min(ContextLines, from)).Select(i => Flatten(source[i].Text)).ToList();
                var user = UserJson(filmTitle, context, lines);
                string? cache = cacheDir is null ? null : Path.Combine(cacheDir, SubtitleJobService.Hash(cacheKey + "\n" + system + "\n" + user) + ".json");
                cacheFiles[b] = cache;

                var translated = await ReadCacheAsync(cache, count, ct).ConfigureAwait(false);
                bool requestFailed = false;
                for (int attempt = 1; attempt <= MaxAttempts && translated is null; attempt++)
                {
                    try
                    {
                        var (json, u) = await translator.CompleteJsonAsync(system, user, config, ct).ConfigureAwait(false);
                        lock (usage)
                        {
                            usage.Add(u);
                        }

                        requestFailed = false;
                        translated = ParseLines(json, count);
                        if (translated is null)
                        {
                            _logger.LogWarning("AI Translate: batch {Batch} returned an invalid answer (attempt {Attempt})", b, attempt);
                        }
                    }
                    catch (Exception ex) when ((ex is System.Net.Http.HttpRequestException http && RateLimit.IsRetryable(http))
                        || ex is JsonException or System.Net.Http.HttpIOException)
                    {
                        // Rate limits and server errors were already waited for; give the batch a few more chances.
                        // Anything else (key, balance, quota, bad request) is not caught: it stops the whole job.
                        requestFailed = true;
                        lastError = ex;
                        _logger.LogWarning(ex, "AI Translate: batch {Batch} failed (attempt {Attempt} of {Max})", b, attempt, MaxAttempts);
                        if (attempt < MaxAttempts)
                        {
                            await Task.Delay(RetryPause * attempt, ct).ConfigureAwait(false);
                        }
                    }
                }

                if (translated is null)
                {
                    if (requestFailed)
                    {
                        Interlocked.Increment(ref failedBatches);
                        return;
                    }

                    Interlocked.Add(ref untranslated, count);
                    translated = lines;
                }
                else
                {
                    // Paid for: saved even if the job is being stopped.
                    await WriteCacheAsync(cache, translated, CancellationToken.None).ConfigureAwait(false);
                }

                for (int k = 0; k < count; k++)
                {
                    results[from + k] = translated[k];
                }
            }).ConfigureAwait(false);

        if (failedBatches > 0)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"{failedBatches} of {batchCount} translation requests failed ({lastError?.Message}). The finished part is saved: run it again to continue."),
                lastError);
        }

        if (untranslated * 10 > source.Count)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"The model gave no usable translation for {untranslated} of {source.Count} lines."));
        }

        if (untranslated > 0)
        {
            _logger.LogWarning("AI Translate: {Count} of {Total} lines of {Title} kept their original text (no usable translation)", untranslated, source.Count, filmTitle);
        }

        // Done: the saved batches were only needed to continue a run that stopped.
        foreach (var file in cacheFiles)
        {
            TryDelete(file);
        }

        return source.Select((c, i) => c with { Text = results[i] }).ToList();
    }

    private static async Task<List<string>?> ReadCacheAsync(string? path, int count, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var lines = JsonSerializer.Deserialize<List<string>>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            return lines?.Count == count ? lines : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static async Task WriteCacheAsync(string? path, List<string> lines, CancellationToken cancellationToken)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(lines, RelaxedJson), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a cache: the translation itself is fine.
        }
    }

    private static void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
            - Keep song markers ♪ and dialogue dashes "-". When a line has two speakers on separate lines ("...\n- ..."), keep that line break ("\n") so each speaker stays on its own line.
            - If a line is already in {{target}}, return it unchanged.
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
        return obj.ToJsonString(RelaxedJson);
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

    /// <summary>Joins wrapped lines but keeps the break before a second speaker ("\n- ").</summary>
    internal static string Flatten(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(lines[i].StartsWith('-') ? '\n' : ' ');
            }

            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

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
