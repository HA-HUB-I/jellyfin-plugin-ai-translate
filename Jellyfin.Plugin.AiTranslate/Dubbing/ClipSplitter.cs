using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AiTranslate.Dubbing;

/// <summary>
/// Splits one synthesized clip that contains several lines (separated by pauses) back into one clip per line:
/// finds the silent gaps, keeps the longest ones near where each line is expected to end (by text length), and cuts in
/// their middle. Falls back to cutting at the expected positions when the pauses are not clear.
/// </summary>
public static class ClipSplitter
{
    private const double WindowSeconds = 0.02;
    private const double MinSilenceSeconds = 0.18;

    public static List<short[]> Split(short[] samples, int rate, IReadOnlyList<int> lineLengths)
    {
        int n = lineLengths.Count;
        if (n <= 1)
        {
            return [samples];
        }

        var silences = FindSilences(samples, rate);

        // Expected cut points from the share of characters, then the best silence near each.
        double total = lineLengths.Sum();
        var cuts = new List<int>();
        double acc = 0;
        int lastCut = 0;
        for (int i = 0; i < n - 1; i++)
        {
            acc += lineLengths[i];
            int expected = (int)(samples.Length * (acc / total));
            int tolerance = (int)(samples.Length * 0.6 * Math.Min(lineLengths[i], lineLengths[i + 1]) / total) + (rate / 2);
            var best = silences
                .Where(s => s.Mid > lastCut + (rate / 10) && Math.Abs(s.Mid - expected) <= tolerance)
                .OrderByDescending(s => s.Length - (Math.Abs(s.Mid - expected) / 4.0))
                .Select(s => (int?)s.Mid)
                .FirstOrDefault();
            int cut = best ?? Math.Max(expected, lastCut + 1);
            cuts.Add(cut);
            lastCut = cut;
        }

        var pieces = new List<short[]>(n);
        int from = 0;
        foreach (int cut in cuts.Append(samples.Length))
        {
            int to = Math.Clamp(cut, from, samples.Length);
            pieces.Add(Trim(samples.AsSpan(from, to - from), rate));
            from = to;
        }

        return pieces;
    }

    /// <summary>Silent stretches (20 ms RMS windows under ~-40 dBFS, at least 180 ms): (start, length, middle) in samples.</summary>
    internal static List<(int Start, int Length, int Mid)> FindSilences(short[] samples, int rate)
    {
        int window = Math.Max(1, (int)(rate * WindowSeconds));
        int minWindows = (int)Math.Ceiling(MinSilenceSeconds / WindowSeconds);
        const double threshold = 330; // ≈ -40 dBFS RMS
        var result = new List<(int, int, int)>();
        int runStart = -1;
        for (int w = 0; w * window < samples.Length; w++)
        {
            int start = w * window;
            int end = Math.Min(samples.Length, start + window);
            double sum = 0;
            for (int i = start; i < end; i++)
            {
                sum += (double)samples[i] * samples[i];
            }

            bool silent = Math.Sqrt(sum / (end - start)) < threshold;
            if (silent && runStart < 0)
            {
                runStart = start;
            }
            else if (!silent && runStart >= 0)
            {
                Add(runStart, start);
                runStart = -1;
            }
        }

        if (runStart >= 0)
        {
            Add(runStart, samples.Length);
        }

        return result;

        void Add(int s, int e)
        {
            if (e - s >= minWindows * window)
            {
                result.Add((s, e - s, s + ((e - s) / 2)));
            }
        }
    }

    /// <summary>Removes leading/trailing silence, keeping 50 ms.</summary>
    internal static short[] Trim(ReadOnlySpan<short> clip, int rate)
    {
        int pad = rate / 20;
        int first = 0, last = clip.Length - 1;
        while (first < clip.Length && Math.Abs((int)clip[first]) < 500)
        {
            first++;
        }

        while (last > first && Math.Abs((int)clip[last]) < 500)
        {
            last--;
        }

        if (first >= clip.Length)
        {
            return [];
        }

        first = Math.Max(0, first - pad);
        last = Math.Min(clip.Length - 1, last + pad);
        return clip[first..(last + 1)].ToArray();
    }
}
