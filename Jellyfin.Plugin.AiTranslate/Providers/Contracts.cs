using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using Jellyfin.Plugin.AiTranslate.Media;
using Jellyfin.Plugin.AiTranslate.Subtitles;

namespace Jellyfin.Plugin.AiTranslate.Providers;

/// <summary>Tokens or audio minutes one call used.</summary>
public sealed record Usage(string Provider, string Model, double AudioMinutes, long InputTokens, long OutputTokens, double CostUsd);

/// <summary>Speech recognition result for one audio chunk; cue times are relative to the chunk.</summary>
public sealed record Transcription(IReadOnlyList<Cue> Cues, string? Language, Usage Usage);

/// <summary>Speech-to-text with timestamps.</summary>
public interface ITranscriber
{
    string Id { get; }

    string DisplayName(PluginConfiguration config);

    bool IsAvailable(PluginConfiguration config);

    /// <summary>Gets the longest chunk the provider accepts (timestamps, size and rate limits).</summary>
    TimeSpan ChunkLength(PluginConfiguration config);

    /// <summary>Gets how many chunks may be sent at the same time.</summary>
    int Parallelism { get; }

    /// <summary>Rough wall-clock time for a film of this length.</summary>
    TimeSpan EstimatedTime(TimeSpan duration, PluginConfiguration config);

    double PricePerMinute(PluginConfiguration config);

    Task<Transcription> TranscribeAsync(AudioChunk chunk, PluginConfiguration config, CancellationToken cancellationToken);
}

/// <summary>Batch translation of subtitle lines with an LLM.</summary>
public interface ITranslator
{
    string Id { get; }

    string DisplayName(PluginConfiguration config);

    bool IsAvailable(PluginConfiguration config);

    (double Input, double Output) PricePerMTok(PluginConfiguration config);

    /// <summary>Sends one request; returns the raw JSON text the model produced and the usage.</summary>
    Task<(string Json, Usage Usage)> CompleteJsonAsync(string systemPrompt, string userJson, PluginConfiguration config, CancellationToken cancellationToken);
}
