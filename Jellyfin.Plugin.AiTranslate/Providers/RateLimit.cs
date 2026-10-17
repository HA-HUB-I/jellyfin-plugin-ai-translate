using System;
using System.Globalization;
using System.Linq;
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

/// <summary>
/// Retries rate limits (429), server errors (500/502/503/504) and dropped connections, honouring
/// "retry in Ns" / "Please try again in 1.5s" hints.
/// </summary>
public static partial class RateLimit
{
    public const int MaxAttempts = 6;

    private static readonly string[] RetryableStatuses = [" 429:", " 500:", " 502:", " 503:", " 504:"];

    public static async Task<T> RetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await call().ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && IsRetryable(ex))
            {
                await Task.Delay(RetryDelay(ex.Message, attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpIOException) when (attempt < MaxAttempts)
            {
                // The answer was cut off while it was being read.
                await Task.Delay(RetryDelay(string.Empty, attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether waiting and sending again can help: an answer above, or no answer at all (network error).</summary>
    internal static bool IsRetryable(HttpRequestException ex) =>
        ex.HttpRequestError != HttpRequestError.Unknown || IsRetryable(ex.Message);

    // A daily quota ("100 requests per day … retry in 14h") or an empty prepaid balance (OpenAI answers 429
    // "insufficient_quota") is not worth waiting for inside a job.
    internal static bool IsRetryable(string message) =>
        RetryableStatuses.Any(s => message.Contains(s, StringComparison.Ordinal))
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
