using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>A synthesized line: 16-bit mono PCM samples and what it cost.</summary>
public sealed record SpeechClip(short[] Samples, int SampleRate, Usage Usage);

/// <summary>Text-to-speech provider for voice-over.</summary>
public interface ITtsProvider
{
    string Id { get; }

    string Model(PluginConfiguration config);

    string DisplayName(PluginConfiguration config);

    bool IsAvailable(PluginConfiguration config);

    /// <summary>Gets the voices the provider offers: (id, description).</summary>
    IReadOnlyList<(string Id, string Description)> Voices { get; }

    /// <summary>Gets the text placed between lines of one request so the voice pauses there.</summary>
    string LineSeparator(PluginConfiguration config);

    int RequestsPerMinute(PluginConfiguration config);

    /// <summary>Requests per day the account may make (0 = no daily limit).</summary>
    int RequestsPerDay(PluginConfiguration config);

    /// <summary>Estimated USD for <paramref name="characters"/> of text that will become <paramref name="seconds"/> of speech.</summary>
    double Estimate(int characters, double seconds, PluginConfiguration config);

    Task<SpeechClip> SynthesizeAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken);
}

/// <summary>Thrown when the provider's daily request quota is used up.</summary>
public sealed class DailyQuotaException : Exception
{
    public DailyQuotaException(string message)
        : base(message)
    {
    }

    public DailyQuotaException()
    {
    }

    public DailyQuotaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
