using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Media;
using Jellyfin.Plugin.AiTranslate.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AiTranslate.Subtitles;

/// <summary>Where the text to translate comes from.</summary>
public static class SourceKind
{
    /// <summary>Speech recognition of the audio track.</summary>
    public const string Audio = "audio";

    /// <summary>A text subtitle stream inside the media file (Ref = absolute stream index).</summary>
    public const string Embedded = "stream";

    /// <summary>A subtitle file next to the media or in Jellyfin's metadata folder (Ref = path).</summary>
    public const string File = "file";

    /// <summary>A result of another Jellyfin subtitle provider (Ref = its subtitle id).</summary>
    public const string Remote = "remote";
}

/// <summary>A subtitle track in another language that can be translated instead of transcribing the audio.</summary>
public sealed record SubtitleSource(string Kind, string Ref, string Language, string Label, bool HearingImpaired);

/// <summary>Finds and reads text subtitles to translate.</summary>
public static partial class SubtitleSources
{
    private static readonly string[] TextCodecs = ["subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "vtt", "text", "tx3g"];

    static SubtitleSources()
    {
        // Older subtitle files are often Windows-125x encoded.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Text subtitle streams of the item in other languages, best first (preferred languages, not forced, not SDH).</summary>
    public static List<SubtitleSource> FromStreams(IEnumerable<MediaStream> streams, string targetLanguage, IReadOnlyList<string> preferredLanguages)
    {
        return streams
            .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsForced && !string.IsNullOrEmpty(s.Language)
                && TextCodecs.Contains(s.Codec ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .Select(s => (Stream: s, Lang: Languages.ToTwoLetter(s.Language!)))
            .Where(x => x.Lang != targetLanguage && x.Lang.Length == 2)
            .Select(x => new SubtitleSource(
                x.Stream.IsExternal ? SourceKind.File : SourceKind.Embedded,
                x.Stream.IsExternal ? x.Stream.Path : x.Stream.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                x.Lang,
                (x.Stream.IsExternal ? "външни " : "вградени ") + Languages.EnglishName(x.Lang) + (x.Stream.IsHearingImpaired ? " SDH" : string.Empty),
                x.Stream.IsHearingImpaired))
            .OrderBy(s => Rank(s, preferredLanguages))
            .ToList();
    }

    public static int Rank(SubtitleSource s, IReadOnlyList<string> preferredLanguages)
    {
        int lang = preferredLanguages.ToList().FindIndex(l => string.Equals(l, s.Language, StringComparison.OrdinalIgnoreCase));
        return ((lang < 0 ? preferredLanguages.Count : lang) * 10) + (s.HearingImpaired ? 1 : 0) + (s.Kind == SourceKind.Embedded ? 2 : 0);
    }

    /// <summary>Reads subtitle text in any common format/encoding and returns clean cues.</summary>
    public static async Task<List<Cue>> ReadAsync(string path, string ffmpegPath, string workDir, bool stripHearingImpaired, CancellationToken cancellationToken)
    {
        string srtPath = path;
        if (!path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
        {
            // ASS/SSA/VTT → SRT.
            Directory.CreateDirectory(workDir);
            srtPath = Path.Combine(workDir, "converted.srt");
            var r = await AudioExtractor.RunAsync(ffmpegPath, ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", path, "-c:s", "srt", srtPath], cancellationToken).ConfigureAwait(false);
            if (r.ExitCode != 0)
            {
                throw new InvalidOperationException("ffmpeg could not convert the subtitle file: " + r.StdErr);
            }
        }

        return Clean(CueTools.ParseSrt(DecodeText(await System.IO.File.ReadAllBytesAsync(srtPath, cancellationToken).ConfigureAwait(false))), stripHearingImpaired);
    }

    /// <summary>Extracts an embedded text subtitle stream to SRT (reads the file, does not decode video).</summary>
    public static async Task<string> ExtractEmbeddedAsync(string mediaPath, int streamIndex, string ffmpegPath, string workDir, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(workDir);
        string output = Path.Combine(workDir, "embedded.srt");
        var r = await AudioExtractor.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", mediaPath, "-map", "0:" + streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:s", "srt", output],
            cancellationToken).ConfigureAwait(false);
        if (r.ExitCode != 0)
        {
            throw new InvalidOperationException("ffmpeg could not extract the subtitle stream: " + r.StdErr);
        }

        return output;
    }

    /// <summary>UTF-8 (with or without BOM), UTF-16, else Windows-1252.</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return Encoding.Unicode.GetString(bytes).TrimStart('﻿');
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>Removes markup and, optionally, hearing-impaired annotations; drops cues that become empty.</summary>
    public static List<Cue> Clean(IEnumerable<Cue> cues, bool stripHearingImpaired)
    {
        var result = new List<Cue>();
        foreach (var cue in cues)
        {
            var lines = new List<string>();
            foreach (var raw in cue.Text.Split('\n'))
            {
                var line = TagRegex().Replace(raw, string.Empty);
                if (stripHearingImpaired)
                {
                    line = SoundRegex().Replace(line, string.Empty);
                    line = SpeakerRegex().Replace(line, "$1");
                }

                line = SpacesRegex().Replace(line, " ").Trim();
                if (line.Length > 0 && line != "-")
                {
                    lines.Add(line);
                }
            }

            if (lines.Count > 0)
            {
                result.Add(cue with { Text = string.Join('\n', lines) });
            }
        }

        return result;
    }

    // <i>, </font>, {\an8}
    [GeneratedRegex(@"<[^>]{1,40}>|\{\\[^}]*\}")]
    private static partial Regex TagRegex();

    // [door creaks], (sighs)
    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex SoundRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpacesRegex();

    // "JOHN: Hi" / "- MAN 2: Hi" → "Hi" / "- Hi"
    [GeneratedRegex(@"^(-\s*)?[A-Z][A-Z0-9 .'-]{1,30}:\s+")]
    private static partial Regex SpeakerRegex();
}
