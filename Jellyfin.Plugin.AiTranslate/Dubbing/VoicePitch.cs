using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Media;

namespace Jellyfin.Plugin.AiTranslate.Dubbing;

/// <summary>Speaker voice class from the original audio.</summary>
public enum VoiceKind
{
    /// <summary>No clear voice (whisper, shout, music).</summary>
    Unknown,

    Male,

    Female,
}

/// <summary>
/// Estimates the speaker's pitch for a time window of the film's dialogue: the centre channel of a 5.1/7.1 mix
/// (where dialogue is mixed) or the mono downmix of stereo, 8 kHz, autocorrelation over 40 ms frames.
/// Measured on a film: narrator 81 Hz, women 190–230 Hz, men 100–160 Hz.
/// </summary>
public static class VoicePitch
{
    public const int Rate = 8000;
    private const int Frame = 320;          // 40 ms
    private const int Hop = 160;            // 20 ms
    private const int MinLag = Rate / 400;  // 400 Hz
    private const int MaxLag = Rate / 70;   // 70 Hz

    /// <summary>Writes the dialogue signal (s16le, 8 kHz mono) of the whole film to <paramref name="output"/>.</summary>
    public static async Task ExtractDialogueAsync(string ffmpeg, string ffprobe, string mediaPath, int audioIndex, string output, CancellationToken cancellationToken)
    {
        var probe = await AudioExtractor.RunAsync(
            ffprobe,
            ["-v", "error", "-select_streams", "a:" + audioIndex.ToString(CultureInfo.InvariantCulture), "-show_entries", "stream=channels", "-of", "csv=p=0", mediaPath],
            cancellationToken).ConfigureAwait(false);
        int channels = int.TryParse(probe.StdOut.Trim().Split('\n')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 2;
        string filter = (channels >= 3 ? "pan=mono|c0=FC," : string.Empty) + "highpass=f=70,lowpass=f=900";
        var r = await AudioExtractor.RunAsync(
            ffmpeg,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", mediaPath, "-map", "0:a:" + audioIndex.ToString(CultureInfo.InvariantCulture),
             "-af", filter, "-ac", "1", "-ar", Rate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", output],
            cancellationToken).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not extract the dialogue channel: " + r.StdErr);
        }
    }

    /// <summary>Reads [start, end) (at most 3 s) from the extracted dialogue file.</summary>
    public static short[] Read(FileStream dialogue, TimeSpan start, TimeSpan end)
    {
        long from = (long)(start.TotalSeconds * Rate) * 2;
        long length = (long)(Math.Min((end - start).TotalSeconds, 3) * Rate) * 2;
        if (from >= dialogue.Length || length <= 0)
        {
            return [];
        }

        length = Math.Min(length, dialogue.Length - from);
        var bytes = new byte[length];
        dialogue.Seek(from, SeekOrigin.Begin);
        dialogue.ReadExactly(bytes);
        var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>Median pitch of confidently voiced frames, or null if fewer than 4 such frames.</summary>
    public static double? MedianPitch(ReadOnlySpan<short> s)
    {
        var found = new List<double>();
        for (int start = 0; start + Frame + MaxLag < s.Length; start += Hop)
        {
            double e0 = 0;
            for (int i = 0; i < Frame; i++)
            {
                e0 += (double)s[start + i] * s[start + i];
            }

            if (e0 < Frame * 300.0 * 300.0)
            {
                continue; // too quiet
            }

            var corr = new double[MaxLag];
            double best = 0;
            for (int lag = MinLag; lag < MaxLag; lag++)
            {
                double sum = 0;
                for (int i = 0; i < Frame; i += 2)
                {
                    sum += (double)s[start + i] * s[start + i + lag];
                }

                corr[lag] = sum;
                best = Math.Max(best, sum);
            }

            // Multiples of the period correlate almost as well; take the shortest lag that is a local peak
            // within 90 % of the best (avoids reading a voice an octave or more too low).
            int bestLag = 0;
            for (int lag = MinLag + 1; lag < MaxLag - 1 && bestLag == 0; lag++)
            {
                if (corr[lag] >= 0.9 * best && corr[lag] >= corr[lag - 1] && corr[lag] >= corr[lag + 1])
                {
                    bestLag = lag;
                }
            }

            if (bestLag > 0 && best / (e0 / 2) > 0.55)
            {
                found.Add(Rate / (double)bestLag);
            }
        }

        if (found.Count < 4)
        {
            return null;
        }

        found.Sort();
        return found[found.Count / 2];
    }

    public static VoiceKind Classify(double? hz, double maleBelow, double femaleAbove) => hz switch
    {
        null => VoiceKind.Unknown,
        < 60 or > 350 => VoiceKind.Unknown,           // shouts, screams, noise
        var v when v < maleBelow => VoiceKind.Male,
        var v when v > femaleAbove => VoiceKind.Female,
        _ => VoiceKind.Unknown,
    };

    /// <summary>Unknown lines take the class of the nearest known neighbour (previous first); none at all → male.</summary>
    public static VoiceKind[] FillUnknown(IReadOnlyList<VoiceKind> kinds)
    {
        var result = kinds.ToArray();
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i] != VoiceKind.Unknown)
            {
                continue;
            }

            VoiceKind prev = VoiceKind.Unknown, next = VoiceKind.Unknown;
            for (int j = i - 1; j >= 0 && prev == VoiceKind.Unknown; j--)
            {
                prev = kinds[j];
            }

            for (int j = i + 1; j < kinds.Count && next == VoiceKind.Unknown; j++)
            {
                next = kinds[j];
            }

            result[i] = prev != VoiceKind.Unknown ? prev : next != VoiceKind.Unknown ? next : VoiceKind.Male;
        }

        return result;
    }
}
