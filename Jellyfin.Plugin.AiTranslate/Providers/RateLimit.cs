using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Spaces requests evenly to stay under a requests-per-minute limit, shared by all callers.</summary>
public sealed class Pacer
{
    private readonly object _lock = new();
    private DateTime _next = DateTime.MinValue;

    public Task WaitAsync(int requestsPerMinute, CancellationToken cancellationToken)
    {
        if (requestsPerMinute <= 0)
        {
            return Task.CompletedTask;
        }

        var interval = TimeSpan.FromMinutes(1.0 / requestsPerMinute);
        TimeSpan wait;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var slot = _next > now ? _next : now;
            _next = slot + interval;
            wait = slot - now;
        }

        return wait > TimeSpan.Zero ? Task.Delay(wait, cancellationToken) : Task.CompletedTask;
    }
}

/// <summary>Retries HTTP 429/503 answers, honouring "retry in Ns" / "Please try again in 1.5s" hints.</summary>
public static partial class RateLimit
{
    public const int MaxAttempts = 6;

    public static async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && IsRetryable(ex.Message))
            {
                await Task.Delay(RetryDelay(ex.Message, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // A daily quota ("100 requests per day … retry in 14h") or an empty prepaid balance (OpenAI answers 429
    // "insufficient_quota") is not worth waiting for inside a job.
    internal static bool IsRetryable(string message) =>
        (message.Contains(" 429:", StringComparison.Ordinal) || message.Contains(" 503:", StringComparison.Ordinal))
        && !message.Contains("per day", StringComparison.OrdinalIgnoreCase)
        && !message.Contains("insufficient_quota", StringComparison.Ordinal)
        && !message.Contains("credit_balance", StringComparison.Ordinal);

    internal static TimeSpan RetryDelay(string message, int attempt)
    {
        var m = RetryHintRegex().Match(message);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Min(seconds + 1, 120));
        }

        return TimeSpan.FromSeconds(Math.Min(10 * attempt * attempt, 120));
    }

    [GeneratedRegex(@"(?:retry|try again) in ([0-9.]+)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex RetryHintRegex();
}
