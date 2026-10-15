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

/// <summary>Extracts a speech-friendly audio track (mono, 16 kHz, low bitrate MP3) in chunks with Jellyfin's ffmpeg.</summary>
public sealed class AudioExtractor
{
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

    /// <summary>Returns the index (among audio streams) of the default audio track, or 0.</summary>
    public async Task<int> GetDefaultAudioIndexAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var r = await RunAsync(_ffprobe, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_disposition=default", "-of", "json", mediaPath], cancellationToken).ConfigureAwait(false);
        try
        {
            using var json = JsonDocument.Parse(r.StdOut);
            int i = 0;
            foreach (var s in json.RootElement.GetProperty("streams").EnumerateArray())
            {
                if (s.TryGetProperty("disposition", out var d) && d.TryGetProperty("default", out var def) && def.GetInt32() == 1)
                {
                    return i;
                }

                i++;
            }
        }
        catch (JsonException)
        {
        }

        return 0;
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
