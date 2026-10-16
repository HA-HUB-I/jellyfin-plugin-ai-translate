using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.AiTranslate.Billing;

/// <summary>
/// Counts requests per provider/model per quota day, so jobs can warn before they hit a daily limit.
/// The day is UTC: Gemini's "100 requests per day" answer at 09:37 UTC said "retry in 14h22m", i.e. at 00:00 UTC.
/// </summary>
public sealed class QuotaTracker
{
    private readonly string _path;
    private readonly Lock _lock = new();

    public QuotaTracker(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "quota.json");
    }

    public static string QuotaDay(DateTime utc) => DateOnly.FromDateTime(utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>When the daily quota resets next (00:00 UTC).</summary>
    public static DateTime NextResetUtc(DateTime utc) => DateTime.SpecifyKind(utc.Date.AddDays(1), DateTimeKind.Utc);

    public int UsedToday(string key, DateTime utcNow)
    {
        lock (_lock)
        {
            var data = Load();
            return data.Day == QuotaDay(utcNow) && data.Counts.TryGetValue(key, out var n) ? n : 0;
        }
    }

    public void Add(string key, int requests, DateTime utcNow)
    {
        lock (_lock)
        {
            var data = Load();
            string day = QuotaDay(utcNow);
            if (data.Day != day)
            {
                data = new State { Day = day };
            }

            data.Counts[key] = (data.Counts.TryGetValue(key, out var n) ? n : 0) + requests;
            File.WriteAllText(_path, JsonSerializer.Serialize(data));
        }
    }

    /// <summary>The provider said the daily quota is used up: remember it until the reset.</summary>
    public void MarkExhausted(string key, int limit, DateTime utcNow)
    {
        int used = UsedToday(key, utcNow);
        if (limit > used)
        {
            Add(key, limit - used, utcNow);
        }
    }

    private State Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State() : new State();
        }
        catch (JsonException)
        {
            return new State();
        }
    }

    private sealed class State
    {
        public string Day { get; set; } = string.Empty;

        public Dictionary<string, int> Counts { get; set; } = new(StringComparer.Ordinal);
    }
}
