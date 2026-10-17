using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiTranslate.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>Space one kind of cached data takes.</summary>
public sealed record CacheUsage(string Kind, int Files, long Bytes);

/// <summary>What a clean-up removed.</summary>
public sealed record CleanResult(int Files, long Bytes);

/// <summary>
/// Keeps the plugin's disk use in check. Finished subtitles and transcripts are small and save money, so they stay while
/// their film is in the library. Spoken lines (WAV, ~100 MB per dubbed film) go when unused for a while and, over the
/// size limit, least recently used first. Resume data of jobs that never finished and leftovers of crashed runs go too.
/// </summary>
public sealed class CacheCleaner
{
    /// <summary>Finished subtitles and transcripts (cache/&lt;media&gt;/*).</summary>
    public const string Subtitles = "subtitles";

    /// <summary>Spoken lines of voice-overs (dub/…).</summary>
    public const string Voice = "voice";

    /// <summary>Recognised chunks and translated batches of jobs that have not finished (cache/&lt;media&gt;/batches, chunks).</summary>
    public const string Resume = "resume";

    /// <summary>Work folders of running jobs, or leftovers of crashed ones.</summary>
    public const string Temp = "temp";

    private static readonly TimeSpan ResumeKeep = TimeSpan.FromDays(14);
    private static readonly TimeSpan TempKeep = TimeSpan.FromDays(1);

    // A film must be gone from the library (and its cache untouched) this long before its cache goes: a library that
    // is briefly unavailable must not cost the transcripts that were paid for.
    private static readonly TimeSpan OrphanKeep = TimeSpan.FromDays(30);

    private readonly IApplicationPaths _paths;
    private readonly ILibraryManager _library;
    private readonly ILogger<CacheCleaner> _logger;

    public CacheCleaner(IApplicationPaths paths, ILibraryManager library, ILogger<CacheCleaner> logger)
    {
        _paths = paths;
        _library = library;
        _logger = logger;
    }

    private string Data => PluginData.Folder(_paths);

    private string TempRoot => Path.Combine(_paths.TempDirectory, "ai-translate");

    /// <summary>Marks a cached spoken line as just used (it is kept longest).</summary>
    public static void MarkUsed(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IReadOnlyList<CacheUsage> Usage() => Measure(Data, TempRoot);

    /// <summary>The daily clean-up (also the "clean by the rules" button).</summary>
    public CleanResult CleanByRules(PluginConfiguration config)
    {
        var known = LibraryHashes();
        var result = CleanByRules(Data, TempRoot, known.Count > 0 ? known : null, config.DubCacheDays, config.DubCacheMaxMb * 1024L * 1024L, DateTime.UtcNow);
        _logger.LogInformation("AI Translate: cache clean-up removed {Files} files ({Megabytes:0.0} MB)", result.Files, result.Bytes / 1048576.0);
        return result;
    }

    /// <summary>Empties one kind at once (the buttons on the settings page).</summary>
    public CleanResult Clear(string kind)
    {
        var result = Clear(Data, TempRoot, kind, DateTime.UtcNow);
        _logger.LogInformation("AI Translate: cleared {Kind}: {Files} files ({Megabytes:0.0} MB)", kind, result.Files, result.Bytes / 1048576.0);
        return result;
    }

    internal static IReadOnlyList<CacheUsage> Measure(string data, string temp)
    {
        CacheUsage Of(string kind, IEnumerable<FileInfo> files)
        {
            var list = files.ToList();
            return new CacheUsage(kind, list.Count, list.Sum(f => f.Length));
        }

        return
        [
            Of(Subtitles, MediaDirs(data).SelectMany(d => d.EnumerateFiles())),
            Of(Voice, Files(Path.Combine(data, "dub"))),
            Of(Resume, MediaDirs(data).SelectMany(d => d.EnumerateDirectories()).SelectMany(d => Files(d.FullName))),
            Of(Temp, Files(temp)),
        ];
    }

    /// <summary>
    /// The rules: leftovers older than a day, resume data older than 14 days, the cache of films that left the library
    /// (<paramref name="libraryHashes"/>; null = unknown, keep) a month ago, spoken lines unused for
    /// <paramref name="voiceDays"/> days (0 = keep) and then the least recently used over <paramref name="voiceMaxBytes"/>
    /// (0 = no limit).
    /// </summary>
    internal static CleanResult CleanByRules(string data, string temp, IReadOnlySet<string>? libraryHashes, int voiceDays, long voiceMaxBytes, DateTime utcNow)
    {
        var freed = new Freed();
        foreach (var file in Files(temp).Where(f => f.LastWriteTimeUtc < utcNow - TempKeep))
        {
            freed.Delete(file);
        }

        foreach (var file in MediaDirs(data).SelectMany(d => d.EnumerateDirectories()).SelectMany(d => Files(d.FullName)).Where(f => f.LastWriteTimeUtc < utcNow - ResumeKeep))
        {
            freed.Delete(file);
        }

        if (libraryHashes is not null)
        {
            var orphans = MediaDirs(data).Concat(Dirs(Path.Combine(data, "dub")).Where(d => d.Name != "clips"))
                .Where(d => !libraryHashes.Contains(d.Name) && Files(d.FullName).All(f => f.LastWriteTimeUtc < utcNow - OrphanKeep));
            foreach (var file in orphans.SelectMany(d => Files(d.FullName)))
            {
                freed.Delete(file);
            }
        }

        var voice = Files(Path.Combine(data, "dub")).OrderBy(f => f.LastWriteTimeUtc).ToList();
        if (voiceDays > 0)
        {
            foreach (var file in voice.Where(f => f.LastWriteTimeUtc < utcNow - TimeSpan.FromDays(voiceDays)).ToList())
            {
                freed.Delete(file);
                voice.Remove(file);
            }
        }

        long total = voice.Sum(f => f.Length);
        for (int i = 0; voiceMaxBytes > 0 && total > voiceMaxBytes && i < voice.Count; i++)
        {
            total -= voice[i].Length;
            freed.Delete(voice[i]);
        }

        RemoveEmptyDirs(data);
        RemoveEmptyDirs(temp);
        return freed.Result;
    }

    internal static CleanResult Clear(string data, string temp, string kind, DateTime utcNow)
    {
        var freed = new Freed();
        IEnumerable<FileInfo> files = kind switch
        {
            Subtitles => MediaDirs(data).SelectMany(d => d.EnumerateFiles()),
            Voice => Files(Path.Combine(data, "dub")),
            Resume => MediaDirs(data).SelectMany(d => d.EnumerateDirectories()).SelectMany(d => Files(d.FullName)),

            // Not the work folders of jobs that are running now.
            Temp => Files(temp).Where(f => f.LastWriteTimeUtc < utcNow - TimeSpan.FromHours(6)),
            _ => throw new ArgumentException("Unknown cache kind: " + kind, nameof(kind)),
        };
        foreach (var file in files.ToList())
        {
            freed.Delete(file);
        }

        RemoveEmptyDirs(data);
        RemoveEmptyDirs(temp);
        return freed.Result;
    }

    private static IEnumerable<DirectoryInfo> MediaDirs(string data) => Dirs(Path.Combine(data, "cache"));

    private static IEnumerable<DirectoryInfo> Dirs(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).EnumerateDirectories() : [];

    private static IEnumerable<FileInfo> Files(string path) =>
        Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories) : [];

    private static void RemoveEmptyDirs(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Deepest first, so a folder emptied by its children goes too; the root itself stays.
        foreach (var dir in new DirectoryInfo(root).EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(d => d.FullName.Length).ToList())
        {
            try
            {
                if (!dir.EnumerateFileSystemInfos().Any())
                {
                    dir.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private HashSet<string> LibraryHashes()
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo],
            Recursive = true,
        });
        return items.Where(i => !string.IsNullOrEmpty(i.Path)).Select(i => SubtitleJobService.Hash(i.Path)).ToHashSet(StringComparer.Ordinal);
    }

    private sealed class Freed
    {
        private int _files;
        private long _bytes;

        public CleanResult Result => new(_files, _bytes);

        public void Delete(FileInfo file)
        {
            try
            {
                long length = file.Length;
                file.Delete();
                _files++;
                _bytes += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
