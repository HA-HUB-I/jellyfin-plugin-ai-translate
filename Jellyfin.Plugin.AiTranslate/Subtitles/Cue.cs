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

    /// <summary>Groups a word stream into cues, breaking at pauses, sentence ends and length limits.</summary>
    public static List<Cue> GroupWords(IReadOnlyList<TimedWord> words)
    {
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
        return result;
    }

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

    public static List<Cue> ParseSrt(string srt)
    {
        var cues = new List<Cue>();
        foreach (Match m in SrtBlockRegex().Matches(srt.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            cues.Add(new Cue(ParseTime(m.Groups[1].Value), ParseTime(m.Groups[2].Value), m.Groups[3].Value.Trim()));
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

    private static string SrtTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}");

    private static TimeSpan ParseTime(string s) =>
        TimeSpan.ParseExact(s.Replace('.', ','), @"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[.!?…]$")]
    private static partial Regex SentenceEndRegex();

    // "- Who's there? - Me." / "♪ Hee! ♪ - ♪ Hee! ♪": a dash after sentence-ending punctuation starts the second speaker.
    [GeneratedRegex(@"^(.+?[.!?…♪""])\s+-\s+(.+)$")]
    private static partial Regex InlineDialogueRegex();

    [GeneratedRegex(@"\d+\n(\d\d:\d\d:\d\d[,.]\d\d\d) --> (\d\d:\d\d:\d\d[,.]\d\d\d)\n(.*?)(?:\n\n|\n*$)", RegexOptions.Singleline)]
    private static partial Regex SrtBlockRegex();
}
