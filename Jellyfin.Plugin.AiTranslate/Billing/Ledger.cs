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

/// <summary>
/// Append-only cost log (JSON lines) in the plugin's data folder, and the monthly budget: a job reserves its estimate
/// before it starts, so jobs started at the same time cannot overrun the budget together.
/// </summary>
public sealed class Ledger
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, double> _reserved = [];
    private long _lastReservation;

    public Ledger(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "ledger.jsonl");
    }

    /// <summary>Gets the estimates held by running jobs (USD).</summary>
    public double Reserved
    {
        get
        {
            lock (_lock)
            {
                return _reserved.Values.Sum();
            }
        }
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
            return ReadAllLocked();
        }
    }

    public double MonthTotal(DateTime utcNow)
    {
        lock (_lock)
        {
            return MonthTotalLocked(utcNow);
        }
    }

    /// <summary>
    /// Holds <paramref name="estimateUsd"/> for a job until the returned handle is disposed. Throws
    /// <see cref="BudgetExceededException"/> when this month's spending, the other running jobs and this estimate together
    /// would exceed <paramref name="budgetUsd"/> (0 = no limit).
    /// </summary>
    public IDisposable Reserve(double estimateUsd, double budgetUsd, string title, DateTime utcNow)
    {
        lock (_lock)
        {
            if (budgetUsd > 0)
            {
                double spent = MonthTotalLocked(utcNow);
                double held = _reserved.Values.Sum();
                if (spent + held + estimateUsd > budgetUsd)
                {
                    string running = held > 0 ? $" + ${held:0.00} за текущи задачи" : string.Empty;
                    throw new BudgetExceededException($"Месечният лимит ${budgetUsd:0.00} би бил надхвърлен: похарчени ${spent:0.00}{running} + ≈${estimateUsd:0.00} за „{title}“.");
                }
            }

            long id = ++_lastReservation;
            _reserved[id] = Math.Max(0, estimateUsd);
            return new Reservation(this, id);
        }
    }

    private void Release(long id)
    {
        lock (_lock)
        {
            _reserved.Remove(id);
        }
    }

    private double MonthTotalLocked(DateTime utcNow) =>
        ReadAllLocked().Where(e => e.Utc.Year == utcNow.Year && e.Utc.Month == utcNow.Month).Sum(e => e.CostUsd);

    private List<UsageEntry> ReadAllLocked()
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

    private sealed class Reservation : IDisposable
    {
        private readonly Ledger _ledger;
        private readonly long _id;
        private int _released;

        public Reservation(Ledger ledger, long id)
        {
            _ledger = ledger;
            _id = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ledger.Release(_id);
            }
        }
    }
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
