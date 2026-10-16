using System;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiTranslate.Providers;

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

    internal static bool IsRetryable(string message) => message.Contains(" 429:", StringComparison.Ordinal) || message.Contains(" 503:", StringComparison.Ordinal);

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
