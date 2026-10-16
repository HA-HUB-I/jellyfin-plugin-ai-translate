using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AiTranslate.Subtitles;

namespace Jellyfin.Plugin.AiTranslate.Dubbing;

/// <summary>A piece of narration: spoken text, the time slot it belongs to and the speaker's voice class.</summary>
public sealed record DubSegment(TimeSpan Start, TimeSpan End, string Text, VoiceKind Kind = VoiceKind.Unknown);

/// <summary>A segment placed on the timeline after synthesis (Tempo &gt; 1 = sped up).</summary>
public sealed record PlacedClip(TimeSpan Start, TimeSpan Duration, double Tempo);

/// <summary>Turns subtitle cues into narration segments and places synthesized clips on the timeline.</summary>
public static partial class DubPlanner
{
    /// <summary>
    /// Billed speech per character for Bulgarian, measured on a whole film (25 311 characters → 34.9 min of audio,
    /// pauses between batched lines included).
    /// </summary>
    public const double CharactersPerSecond = 12;

    private static readonly TimeSpan MergeGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MaxSegment = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan MinGapAfter = TimeSpan.FromMilliseconds(80);
    private const int MaxSegmentChars = 170;

    /// <summary>Cleans cue text for speaking and joins neighbouring short cues into one request.</summary>
    public static List<DubSegment> Plan(IReadOnlyList<Cue> cues, bool skipSongs) => Plan(cues, skipSongs, null);

    /// <summary>
    /// Cleans cue text for speaking. With <paramref name="classify"/>, two-speaker cues ("- A\n- B") are split into one
    /// line per speaker (time shared by text length), every line gets a voice class, and only neighbours with the same
    /// class are joined.
    /// </summary>
    public static List<DubSegment> Plan(IReadOnlyList<Cue> cues, bool skipSongs, Func<TimeSpan, TimeSpan, VoiceKind>? classify)
    {
        var lines = new List<DubSegment>();
        foreach (var cue in cues)
        {
            if (skipSongs && IsSongOnly(cue.Text))
            {
                continue;
            }

            var parts = classify is null ? [cue.Text] : SpeakerParts(cue.Text);
            var spoken = parts.Select(Speakable).Where(t => t.Length > 0).ToList();
            double total = spoken.Sum(t => t.Length);
            var start = cue.Start;
            for (int i = 0; i < spoken.Count; i++)
            {
                var end = i == spoken.Count - 1 ? cue.End : start + TimeSpan.FromTicks((long)((cue.End - cue.Start).Ticks * (spoken[i].Length / total)));
                lines.Add(new DubSegment(start, end, spoken[i]));
                start = end;
            }
        }

        if (classify is not null)
        {
            var kinds = VoicePitch.FillUnknown(lines.Select(l => classify(l.Start, l.End)).ToList());
            lines = lines.Select((l, i) => l with { Kind = kinds[i] }).ToList();
        }

        var segments = new List<DubSegment>();
        foreach (var line in lines)
        {
            if (segments.Count > 0)
            {
                var last = segments[^1];
                if (last.Kind == line.Kind && line.Start - last.End <= MergeGap && line.End - last.Start <= MaxSegment && last.Text.Length + line.Text.Length < MaxSegmentChars)
                {
                    segments[^1] = last with { End = line.End, Text = last.Text + " " + line.Text };
                    continue;
                }
            }

            segments.Add(line);
        }

        return segments;
    }

    /// <summary>Splits "A\n- B", "- A\n- B" and "- A - B" into one string per speaker.</summary>
    internal static List<string> SpeakerParts(string text)
    {
        var normalized = CueTools.WrapKeepingSpeakers(TagRegex().Replace(text, string.Empty));
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 1 && lines.Skip(1).All(l => l.StartsWith('-')) ? lines.ToList() : [text];
    }

    /// <summary>
    /// Places clips in order: each starts at its cue (or right after the previous clip if that one ran long), is sped up
    /// to fit before the next cue (at most <paramref name="maxTempo"/>), and may run on into the next gap.
    /// </summary>
    public static List<PlacedClip> Place(IReadOnlyList<DubSegment> segments, IReadOnlyList<TimeSpan> clipDurations, double maxTempo)
    {
        var placed = new List<PlacedClip>(segments.Count);
        var cursor = TimeSpan.Zero;
        for (int i = 0; i < segments.Count; i++)
        {
            var start = segments[i].Start > cursor ? segments[i].Start : cursor;
            var slotEnd = i + 1 < segments.Count ? segments[i + 1].Start - MinGapAfter : segments[i].End + TimeSpan.FromSeconds(2);
            var slot = slotEnd - start;
            double tempo = 1;
            if (slot > TimeSpan.Zero && clipDurations[i] > slot)
            {
                tempo = Math.Min(maxTempo, clipDurations[i] / slot);
            }
            else if (slot <= TimeSpan.Zero)
            {
                tempo = maxTempo;
            }

            tempo = Math.Round(tempo, 3);
            var duration = TimeSpan.FromTicks((long)(clipDurations[i].Ticks / tempo));
            placed.Add(new PlacedClip(start, duration, tempo));
            cursor = start + duration + MinGapAfter;
        }

        return placed;
    }

    public static bool IsSongOnly(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 && lines.All(l => l.TrimStart('-', ' ').StartsWith('♪') || l.TrimStart('-', ' ').StartsWith('♫'));
    }

    /// <summary>Removes markup, dialogue dashes and song markers; one line of plain text.</summary>
    public static string Speakable(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => TagRegex().Replace(l, string.Empty))
            .Select(l => l.TrimStart('-').Trim())
            .Select(l => l.Replace("♪", string.Empty, StringComparison.Ordinal).Replace("♫", string.Empty, StringComparison.Ordinal).Trim())
            .Where(l => l.Length > 0 && l.Any(char.IsLetterOrDigit));
        return SpacesRegex().Replace(string.Join(' ', lines), " ").Trim();
    }

    [GeneratedRegex(@"<[^>]{1,40}>|\{\\[^}]*\}")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRegex();
}
