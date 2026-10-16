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

    string DisplayName(PluginConfiguration config);

    bool IsAvailable(PluginConfiguration config);

    /// <summary>Gets the voices the provider offers: (id, description).</summary>
    IReadOnlyList<(string Id, string Description)> Voices { get; }

    /// <summary>Estimated USD for <paramref name="characters"/> of text that will become <paramref name="seconds"/> of speech.</summary>
    double Estimate(int characters, double seconds, PluginConfiguration config);

    Task<SpeechClip> SynthesizeAsync(string text, string voice, string style, PluginConfiguration config, CancellationToken cancellationToken);
}
