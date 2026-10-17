using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Jellyfin.Plugin.AiTranslate.Tests;

public sealed class CacheTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 17, 4, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Directory.CreateTempSubdirectory("ai-cache-").FullName;

    private string Data => Path.Combine(_root, "plugin");

    private string Temp => Path.Combine(_root, "tmp", "ai-translate");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CleansByTheRules()
    {
        File("plugin/cache/inlibrary/bg.gemini.gemini.srt", daysOld: 100);    // finished subtitles: kept while the film is there
        File("plugin/cache/inlibrary/transcript.gemini.json", daysOld: 100);
        File("plugin/cache/inlibrary/batches/a.json", daysOld: 20);           // a job that never finished, 20 days ago
        File("plugin/cache/inlibrary/chunks/h/000.json", daysOld: 2);         // … and one from 2 days ago (may continue)
        File("plugin/cache/gonelong/bg.sub-en.gemini.srt", daysOld: 40);      // film left the library a while ago
        File("plugin/cache/gonerecently/bg.sub-en.gemini.srt", daysOld: 5);   // … or only just (an unavailable library?)
        File("plugin/dub/clips/c1.wav", daysOld: 40, bytes: 100);             // spoken lines: unused for 40 days
        File("plugin/dub/clips/c2.wav", daysOld: 10, bytes: 300);             // over the size limit, oldest first
        File("plugin/dub/clips/c3.wav", daysOld: 1, bytes: 300);
        File("plugin/dub/clips/c4.wav", daysOld: 0, bytes: 300);
        File("plugin/dub/gonelong/old.wav", daysOld: 40, bytes: 50);          // old per-film clips of a film that is gone
        File("plugin/ledger.jsonl", daysOld: 100);                            // never touched
        File("tmp/ai-translate/crashed/chunk000.mp3", daysOld: 2);
        File("tmp/ai-translate/running/chunk000.mp3", daysOld: 0);

        var result = CacheCleaner.CleanByRules(Data, Temp, new HashSet<string>(["inlibrary"]), voiceDays: 30, voiceMaxBytes: 700, Now);

        Assert.Equal(
            [
                "plugin/cache/gonerecently/bg.sub-en.gemini.srt",
                "plugin/cache/inlibrary/bg.gemini.gemini.srt",
                "plugin/cache/inlibrary/chunks/h/000.json",
                "plugin/cache/inlibrary/transcript.gemini.json",
                "plugin/dub/clips/c3.wav",
                "plugin/dub/clips/c4.wav",
                "plugin/ledger.jsonl",
                "tmp/ai-translate/running/chunk000.mp3",
            ],
            Remaining());
        Assert.Equal(6, result.Files);
        Assert.False(Directory.Exists(Path.Combine(Data, "cache", "gonelong")));     // emptied folders go too
        Assert.True(Directory.Exists(Temp));
    }

    [Fact]
    public void UnknownLibraryKeepsEveryFilmsCache()
    {
        File("plugin/cache/gonelong/bg.sub-en.gemini.srt", daysOld: 400);
        CacheCleaner.CleanByRules(Data, Temp, libraryHashes: null, voiceDays: 0, voiceMaxBytes: 0, Now);
        Assert.Equal(["plugin/cache/gonelong/bg.sub-en.gemini.srt"], Remaining());
    }

    [Fact]
    public void MeasuresAndClearsOneKind()
    {
        File("plugin/cache/m/bg.gemini.gemini.srt", daysOld: 1, bytes: 70);
        File("plugin/cache/m/batches/a.json", daysOld: 1, bytes: 30);
        File("plugin/dub/clips/c1.wav", daysOld: 1, bytes: 1000);
        File("tmp/ai-translate/old/x.raw", daysOld: 1, bytes: 5);

        var usage = CacheCleaner.Measure(Data, Temp).ToDictionary(u => u.Kind, u => u.Bytes);
        Assert.Equal(70, usage[CacheCleaner.Subtitles]);
        Assert.Equal(1000, usage[CacheCleaner.Voice]);
        Assert.Equal(30, usage[CacheCleaner.Resume]);
        Assert.Equal(5, usage[CacheCleaner.Temp]);

        Assert.Equal(new CleanResult(1, 1000), CacheCleaner.Clear(Data, Temp, CacheCleaner.Voice, Now));
        Assert.Equal(new CleanResult(1, 70), CacheCleaner.Clear(Data, Temp, CacheCleaner.Subtitles, Now));   // not the resume data
        Assert.Equal(["plugin/cache/m/batches/a.json", "tmp/ai-translate/old/x.raw"], Remaining());
        Assert.Throws<ArgumentException>(() => CacheCleaner.Clear(Data, Temp, "ledger", Now));
    }

    private void File(string relative, int daysOld, int bytes = 10)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        System.IO.File.SetLastWriteTimeUtc(path, Now - TimeSpan.FromDays(daysOld) - TimeSpan.FromMinutes(1));
    }

    private List<string> Remaining() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
}
