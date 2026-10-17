using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiTranslate.Media;

/// <summary>A piece of the audio track and where it starts in the film.</summary>
public sealed record AudioChunk(string Path, TimeSpan Offset, TimeSpan Duration);

/// <summary>An audio stream of the media: its position among the audio streams, language tag and flags.</summary>
public sealed record AudioTrack(int Position, string? Language, bool IsDefault, bool Commentary);

/// <summary>Extracts a speech-friendly audio track (mono, 16 kHz, low bitrate MP3) in chunks with Jellyfin's ffmpeg.</summary>
public sealed class AudioExtractor
{
    private static readonly string[] CommentaryWords = ["comment", "коментар", "description", "описание"];

    private readonly string _ffmpeg;
    private readonly string _ffprobe;

    public AudioExtractor(string ffmpegPath, string ffprobePath)
    {
        _ffmpeg = ffmpegPath;
        _ffprobe = ffprobePath;
    }

    public async Task<TimeSpan> GetDurationAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var r = await RunAsync(_ffprobe, ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", mediaPath], cancellationToken).ConfigureAwait(false);
        return double.TryParse(r.StdOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;
    }

    /// <summary>
    /// Returns the index (among audio streams) of the track to use: the first one in a preferred language (usually the
    /// film's original — releases often make a dub the default, e.g. Russian before English), else the default track,
    /// else the first. Commentary and audio-description tracks are skipped.
    /// </summary>
    public async Task<int> GetAudioIndexAsync(string mediaPath, IReadOnlyList<string> preferredLanguages, CancellationToken cancellationToken)
    {
        var r = await RunAsync(
            _ffprobe,
            ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_disposition=default,comment,visual_impaired:stream_tags=language,title", "-of", "json", mediaPath],
            cancellationToken).ConfigureAwait(false);
        var tracks = new List<AudioTrack>();
        try
        {
            using var json = JsonDocument.Parse(r.StdOut);
            foreach (var s in json.RootElement.GetProperty("streams").EnumerateArray())
            {
                bool Flag(string name) => s.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var v) && v.GetInt32() == 1;
                string? Tag(string name) => s.TryGetProperty("tags", out var t) && t.TryGetProperty(name, out var v) ? v.GetString() : null;
                string title = Tag("title") ?? string.Empty;
                bool commentary = Flag("comment") || Flag("visual_impaired")
                    || CommentaryWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
                tracks.Add(new AudioTrack(tracks.Count, Tag("language"), Flag("default"), commentary));
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        return ChooseAudio(tracks, preferredLanguages);
    }

    internal static int ChooseAudio(IReadOnlyList<AudioTrack> tracks, IReadOnlyList<string> preferredLanguages)
    {
        foreach (var language in preferredLanguages)
        {
            var match = tracks
                .Where(t => !t.Commentary && !string.IsNullOrEmpty(t.Language) && Providers.Languages.ToTwoLetter(t.Language) == language)
                .OrderByDescending(t => t.IsDefault)
                .FirstOrDefault();
            if (match is not null)
            {
                return match.Position;
            }
        }

        return (tracks.FirstOrDefault(t => t.IsDefault) ?? tracks.FirstOrDefault())?.Position ?? 0;
    }

    /// <summary>Cuts [start, start + length) of a piece into a new MP3 (re-encoded, so the cut is exact).</summary>
    public async Task<AudioChunk> CutAsync(AudioChunk source, TimeSpan start, TimeSpan length, string output, CancellationToken cancellationToken)
    {
        string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var r = await RunAsync(
            _ffmpeg,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y", "-ss", Seconds(start), "-t", Seconds(length), "-i", source.Path,
             "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", "32k", output],
            cancellationToken).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not cut the audio: " + Tail(r.StdErr));
        }

        return new AudioChunk(output, source.Offset + start, length);
    }

    /// <summary>Writes the chosen audio track as MP3 chunks of at most <paramref name="chunkLength"/> into <paramref name="workDir"/>.</summary>
    public async Task<IReadOnlyList<AudioChunk>> ExtractAsync(string mediaPath, int audioIndex, TimeSpan chunkLength, string workDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        string pattern = Path.Combine(workDir, "chunk%03d.mp3");
        var r = await RunAsync(
            _ffmpeg,
            ["-hide_banner", "-nostdin", "-nostats", "-v", "error", "-y", "-i", mediaPath, "-map", "0:a:" + audioIndex.ToString(CultureInfo.InvariantCulture),
             "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "libmp3lame", "-b:a", "32k",
             "-f", "segment", "-segment_time", ((int)chunkLength.TotalSeconds).ToString(CultureInfo.InvariantCulture), "-reset_timestamps", "1", pattern],
            cancellationToken).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not extract audio: " + Tail(r.StdErr));
        }

        var chunks = new List<AudioChunk>();
        var offset = TimeSpan.Zero;
        foreach (var file in Directory.GetFiles(workDir, "chunk*.mp3").Order(StringComparer.Ordinal))
        {
            var duration = await GetDurationAsync(file, cancellationToken).ConfigureAwait(false);
            chunks.Add(new AudioChunk(file, offset, duration));
            offset += duration;
        }

        return chunks;
    }

    internal static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = p.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await p.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw;
        }

        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string Tail(string s) => s.Length > 600 ? s[^600..] : s;
}
