using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AiTranslate.Subtitles;

/// <summary>One subtitle cue.</summary>
public sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

/// <summary>A timed word from a speech recognizer.</summary>
public sealed record TimedWord(TimeSpan Start, TimeSpan End, string Text);

/// <summary>Builds readable cues and reads/writes SRT.</summary>
public static partial class CueTools
{
    /// <summary>Netflix-style limits: 42 characters per line, 2 lines, at most 7 seconds.</summary>
    public const int MaxLineLength = 42;

    public const int MaxCueLength = MaxLineLength * 2;

    public static readonly TimeSpan MaxCueDuration = TimeSpan.FromSeconds(7);

    private static readonly TimeSpan PauseBreak = TimeSpan.FromMilliseconds(700);

    /// <summary>Splits recognizer segments that are too long, distributing time by character count.</summary>
    public static List<Cue> SplitLongSegments(IEnumerable<Cue> segments)
    {
        var result = new List<Cue>();
        foreach (var seg in segments)
        {
            var text = Normalize(seg.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var pieces = SplitText(text);
            double total = pieces.Sum(p => p.Length);
            var start = seg.Start;
            var span = seg.End - seg.Start;
            for (int p = 0; p < pieces.Count; p++)
            {
                var piece = pieces[p];
                var end = p == pieces.Count - 1 ? seg.End : start + TimeSpan.FromTicks((long)(span.Ticks * (piece.Length / total)));
                result.Add(new Cue(start, end, Wrap(piece)));
                start = end;
            }
        }

        return result;
    }

    /// <summary>
    /// Fixes impossible word timings before grouping: words longer than 3 s are shortened, and words whose time jumps
    /// ahead of the following words are re-timed in spoken order (see <see cref="RetimeInOrder"/>).
    /// </summary>
    public static List<TimedWord> SanitizeWords(IReadOnlyList<TimedWord> words)
    {
        var times = RetimeInOrder(
            words.Select(w => (w.Start, w.End, w.Text.Length)).ToList(),
            _ => TimeSpan.FromSeconds(3),
            len => TimeSpan.FromSeconds(Math.Clamp((0.08 * len) + 0.12, 0.2, 1.5)));
        return words.Select((w, i) => new TimedWord(times[i].Start, times[i].End, w.Text)).ToList();
    }

    /// <summary>
    /// Keeps the items in the order they were recognised (the spoken order) and repairs their times:
    /// an item that starts more than 1 s after one of the next three is "displaced"; every run of displaced items is
    /// spread, by text length, over the gap between the surrounding good items. Good items longer than allowed are
    /// shortened to their natural length.
    /// </summary>
    internal static List<(TimeSpan Start, TimeSpan End)> RetimeInOrder(
        IReadOnlyList<(TimeSpan Start, TimeSpan End, int Length)> items,
        Func<int, TimeSpan> maxDuration,
        Func<int, TimeSpan> natural)
    {
        int n = items.Count;
        var result = items.Select(x => (x.Start, x.End)).ToList();
        var displaced = new bool[n];
        for (int i = 0; i < n; i++)
        {
            displaced[i] = Enumerable.Range(i + 1, Math.Min(3, n - i - 1)).Any(j => items[j].Start + TimeSpan.FromSeconds(1) < items[i].Start);
            if (!displaced[i] && (items[i].End < items[i].Start || items[i].End - items[i].Start > maxDuration(items[i].Length)))
            {
                result[i] = (items[i].Start, items[i].Start + natural(items[i].Length));
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!displaced[i])
            {
                continue;
            }

            int runEnd = i;
            while (runEnd + 1 < n && displaced[runEnd + 1])
            {
                runEnd++;
            }

            var run = Enumerable.Range(i, runEnd - i + 1).ToList();
            var naturalTotal = TimeSpan.FromTicks(run.Sum(k => natural(items[k].Length).Ticks));
            var windowStart = i > 0 ? result[i - 1].End : (runEnd + 1 < n ? Max(TimeSpan.Zero, result[runEnd + 1].Start - naturalTotal) : items[i].Start);
            var windowEnd = runEnd + 1 < n ? result[runEnd + 1].Start : windowStart + naturalTotal;
            var minimum = TimeSpan.FromMilliseconds(300 * run.Count);
            if (windowEnd - windowStart < minimum)
            {
                windowEnd = windowStart + minimum;
            }

            if (windowEnd - windowStart > naturalTotal * 1.5)
            {
                windowEnd = windowStart + (naturalTotal * 1.5);
            }

            double totalLength = Math.Max(1, run.Sum(k => items[k].Length));
            var cursor = windowStart;
            foreach (int k in run)
            {
                var span = TimeSpan.FromTicks((long)((windowEnd - windowStart).Ticks * (Math.Max(1, items[k].Length) / totalLength)));
                result[k] = (cursor, cursor + span);
                cursor += span;
            }

            i = runEnd;
        }

        return result;
    }

    /// <summary>Groups a word stream into cues, breaking at pauses, sentence ends and length limits.</summary>
    public static List<Cue> GroupWords(IReadOnlyList<TimedWord> rawWords)
    {
        var words = SanitizeWords(rawWords);
        var result = new List<Cue>();
        var current = new List<TimedWord>();

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            var text = Normalize(string.Join(' ', current.Select(w => w.Text)));
            if (text.Length > 0)
            {
                result.Add(new Cue(current[0].Start, current[^1].End, Wrap(text)));
            }

            current.Clear();
        }

        foreach (var word in words)
        {
            if (current.Count > 0)
            {
                var last = current[^1];
                int length = current.Sum(w => w.Text.Length + 1) + word.Text.Length;
                bool sentenceEnd = SentenceEndRegex().IsMatch(last.Text);
                if (word.Start - last.End >= PauseBreak
                    || length > MaxCueLength
                    || word.End - current[0].Start > MaxCueDuration
                    || (sentenceEnd && length > MaxLineLength))
                {
                    Flush();
                }
            }

            current.Add(word);
        }

        Flush();
        return Repair(result);
    }

    /// <summary>
    /// Repairs broken timings (seen with Gemini word timestamps: single words lasting 270 s or jumping minutes ahead):
    /// a cue that starts later than one of the next three is moved right after its predecessor, cues are sorted,
    /// durations are capped (7 s or reading time), overlaps removed, and fragments of one sentence re-joined.
    /// </summary>
    public static List<Cue> Repair(IReadOnlyList<Cue> input, bool joinFragments = true)
    {
        var source = input.Where(c => !string.IsNullOrWhiteSpace(c.Text)).ToList();
        var times = RetimeInOrder(
            source.Select(c => (c.Start, c.End, c.Text.Replace("\n", string.Empty, StringComparison.Ordinal).Length)).ToList(),
            len => Max(MaxCueDuration, TimeSpan.FromSeconds(len / 15.0)),
            len => Max(TimeSpan.FromSeconds(1), Min(MaxCueDuration, TimeSpan.FromSeconds(len / 15.0))));
        var cues = source.Select((c, i) => c with { Start = times[i].Start, End = times[i].End }).ToList();

        // Re-join fragments of one sentence ("Ако пак" + "се навърташ" + "около" …).
        var joined = new List<Cue>();
        foreach (var c in cues)
        {
            if (joined.Count > 0)
            {
                var last = joined[^1];
                string lastFlat = last.Text.Replace('\n', ' ');
                if (joinFragments
                    && !SentenceEndRegex().IsMatch(lastFlat.TrimEnd('"', '“', '”', ' '))
                    && !c.Text.TrimStart().StartsWith('-')
                    && c.Start - last.End < TimeSpan.FromMilliseconds(600)
                    && lastFlat.Length + c.Text.Length + 1 <= MaxCueLength
                    && c.End - last.Start <= MaxCueDuration)
                {
                    joined[^1] = new Cue(last.Start, Max(last.End, c.End), Wrap(lastFlat + " " + c.Text.Replace('\n', ' ')));
                    continue;
                }
            }

            joined.Add(c);
        }

        // No overlaps: end each cue just before the next one starts.
        for (int i = 0; i + 1 < joined.Count; i++)
        {
            var limit = joined[i + 1].Start - TimeSpan.FromMilliseconds(50);
            if (joined[i].End > limit)
            {
                joined[i] = joined[i] with { End = Max(joined[i].Start + TimeSpan.FromMilliseconds(300), limit) };
            }
        }

        return joined;
    }

    /// <summary>Reading time at ~15 characters per second, at least one second.</summary>
    public static TimeSpan ReadingTime(string text) => TimeSpan.FromSeconds(Math.Max(1.0, text.Replace("\n", string.Empty, StringComparison.Ordinal).Length / 15.0));

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// Final pass: re-wraps every cue and stretches cues that are too short to read (20 characters per second, at least
    /// one second) into the gap before the next cue, never overlapping it.
    /// </summary>
    public static List<Cue> Polish(IReadOnlyList<Cue> cues)
    {
        var result = new List<Cue>(cues.Count);
        for (int i = 0; i < cues.Count; i++)
        {
            var text = WrapKeepingSpeakers(cues[i].Text);
            var start = cues[i].Start;
            var end = cues[i].End;
            var wanted = TimeSpan.FromSeconds(Math.Clamp(text.Length / 20.0, 1.0, MaxCueDuration.TotalSeconds));
            if (end - start < wanted)
            {
                var limit = i + 1 < cues.Count ? cues[i + 1].Start - TimeSpan.FromMilliseconds(50) : start + wanted;
                end = new TimeSpan(Math.Max(end.Ticks, Math.Min((start + wanted).Ticks, limit.Ticks)));
            }

            result.Add(new Cue(start, end, text));
        }

        return result;
    }

    /// <summary>
    /// Dialogue ("A\n- B", "- A\n- B", or a model that put both on one line: "- A - B") keeps one speaker per line;
    /// anything else is wrapped into two balanced lines.
    /// </summary>
    public static string WrapKeepingSpeakers(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > 1 && lines.Skip(1).All(l => l.StartsWith('-')))
        {
            return string.Join('\n', lines.Select(l => Normalize(l)));
        }

        var flat = Normalize(string.Join(' ', lines));
        var m = InlineDialogueRegex().Match(flat);
        if (m.Success)
        {
            return m.Groups[1].Value.Trim() + "\n- " + m.Groups[2].Value.Trim();
        }

        return Wrap(flat);
    }

    /// <summary>Wraps text into at most two balanced lines.</summary>
    public static string Wrap(string text)
    {
        text = Normalize(text);
        if (text.Length <= MaxLineLength)
        {
            return text;
        }

        int middle = text.Length / 2;
        int best = -1;
        for (int d = 0; d < middle; d++)
        {
            if (middle + d < text.Length && text[middle + d] == ' ')
            {
                best = middle + d;
                break;
            }

            if (middle - d > 0 && text[middle - d] == ' ')
            {
                best = middle - d;
                break;
            }
        }

        return best < 0 ? text : text[..best] + "\n" + text[(best + 1)..];
    }

    public static string ToSrt(IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < cues.Count; i++)
        {
            sb.Append(i + 1).Append('\n')
              .Append(SrtTime(cues[i].Start)).Append(" --> ").Append(SrtTime(cues[i].End)).Append('\n')
              .Append(cues[i].Text.Trim()).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads SRT as it is found in the wild: the counter may be missing or followed by spaces, cues may follow each other
    /// without a blank line (or with one holding only spaces), hours may have one digit, milliseconds may be written
    /// with a dot or fewer digits, and a position after the end time ("X1:… Y2:…") is ignored.
    /// </summary>
    public static List<Cue> ParseSrt(string srt)
    {
        var cues = new List<Cue>();
        var lines = srt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = SrtTimingRegex().Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            var text = new List<string>();
            int next = i + 1;
            while (next < lines.Length && !string.IsNullOrWhiteSpace(lines[next]) && !SrtTimingRegex().IsMatch(lines[next]))
            {
                text.Add(lines[next].Trim());
                next++;
            }

            // A cue that runs straight into the next one: its last line is the next cue's counter.
            if (next < lines.Length && text.Count > 0 && SrtTimingRegex().IsMatch(lines[next]) && CounterRegex().IsMatch(text[^1]))
            {
                text.RemoveAt(text.Count - 1);
            }

            cues.Add(new Cue(Time(m, 1), Time(m, 5), string.Join('\n', text)));
            i = next - 1;
        }

        return cues;
    }

    public static void WriteSrt(string path, IReadOnlyList<Cue> cues)
    {
        File.WriteAllText(path, ToSrt(cues), new UTF8Encoding(false));
    }

    internal static List<string> SplitText(string text)
    {
        var pieces = new List<string>();
        if (text.Length <= MaxCueLength)
        {
            pieces.Add(text);
            return pieces;
        }

        // Prefer sentence ends, then commas, then any space, keeping pieces under the cue length.
        var rest = text;
        while (rest.Length > MaxCueLength)
        {
            int cut = LastBreak(rest, MaxCueLength, ". ", "? ", "! ", "… ");
            if (cut < MaxCueLength / 3)
            {
                cut = LastBreak(rest, MaxCueLength, ", ", "; ", ": ", " - ");
            }

            if (cut < MaxCueLength / 3)
            {
                cut = rest.LastIndexOf(' ', MaxCueLength);
            }

            if (cut <= 0)
            {
                cut = MaxCueLength;
            }

            pieces.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }

        if (rest.Length > 0)
        {
            pieces.Add(rest);
        }

        return pieces;
    }

    private static int LastBreak(string text, int limit, params string[] marks)
    {
        int best = -1;
        foreach (var mark in marks)
        {
            int i = text.LastIndexOf(mark, Math.Min(limit, text.Length - 1), StringComparison.Ordinal);
            if (i >= 0)
            {
                best = Math.Max(best, i + mark.Length - 1);
            }
        }

        return best;
    }

    private static string Normalize(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    private static string SrtTime(TimeSpan t)
    {
        t = Max(t, TimeSpan.Zero);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");
    }

    // Groups first..first+3 of the timing regex: hours, minutes, seconds, fraction ("5" = 500 ms, "05" = 50 ms).
    private static TimeSpan Time(Match m, int first)
    {
        int Part(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups[first + 3];
        int ms = fraction.Success ? int.Parse(fraction.Value.PadRight(3, '0'), CultureInfo.InvariantCulture) : 0;
        return new TimeSpan(0, Part(first), Part(first + 1), Part(first + 2), ms);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[.!?…]$")]
    private static partial Regex SentenceEndRegex();

    // "- Who's there? - Me." / "♪ Hee! ♪ - ♪ Hee! ♪": a dash after sentence-ending punctuation starts the second speaker.
    [GeneratedRegex(@"^(.+?[.!?…♪""])\s+-\s+(.+)$")]
    private static partial Regex InlineDialogueRegex();

    [GeneratedRegex(@"^\s*(\d{1,3}):(\d{1,2}):(\d{1,2})(?:[,.:](\d{1,3}))?\s*-->\s*(\d{1,3}):(\d{1,2}):(\d{1,2})(?:[,.:](\d{1,3}))?")]
    private static partial Regex SrtTimingRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex CounterRegex();
}
