using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.AiTranslate.Billing;

/// <summary>What one API call consumed and its estimated price.</summary>
public sealed record UsageEntry(
    DateTime Utc,
    string Item,
    string Kind,
    string Provider,
    string Model,
    double AudioMinutes,
    long InputTokens,
    long OutputTokens,
    double CostUsd);

/// <summary>Append-only cost log (JSON lines) in the plugin's data folder.</summary>
public sealed class Ledger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Lock _lock = new();

    public Ledger(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "ledger.jsonl");
    }

    public void Add(UsageEntry entry)
    {
        lock (_lock)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
        }
    }

    public IReadOnlyList<UsageEntry> ReadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            return File.ReadAllLines(_path)
                .Where(l => l.Length > 0)
                .Select(l =>
                {
                    try
                    {
                        return JsonSerializer.Deserialize<UsageEntry>(l, Json);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                })
                .Where(e => e is not null)
                .Select(e => e!)
                .ToList();
        }
    }

    public double MonthTotal(DateTime utcNow) =>
        ReadAll().Where(e => e.Utc.Year == utcNow.Year && e.Utc.Month == utcNow.Month).Sum(e => e.CostUsd);
}

/// <summary>Thrown when a job would exceed the monthly budget.</summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string message)
        : base(message)
    {
    }

    public BudgetExceededException()
    {
    }

    public BudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
