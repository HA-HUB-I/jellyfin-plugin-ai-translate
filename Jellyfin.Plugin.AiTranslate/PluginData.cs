using System;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>
/// The plugin's data folder (cost log, quotas, caches): the same path however early it is asked for. Services that were
/// created before the plugin object used to fall back to {data}/ai-translate, so the cost log could change place between
/// restarts — and the month's spending would then look like zero to the budget check.
/// </summary>
public static class PluginData
{
    private static readonly Lock MoveLock = new();
    private static bool _checked;

    public static string Folder(IApplicationPaths paths)
    {
        // Jellyfin's DataFolderPath is {plugins}/{assembly name}; computing the same lets early callers agree with it.
        string folder = Plugin.Instance?.DataFolderPath ?? Path.Combine(paths.PluginsPath, typeof(Plugin).Assembly.GetName().Name!);
        if (!_checked)
        {
            lock (MoveLock)
            {
                if (!_checked)
                {
                    MergeLegacy(Path.Combine(paths.DataPath, "ai-translate"), folder);
                    _checked = true;
                }
            }
        }

        return folder;
    }

    /// <summary>
    /// Moves the cost log (appended to the one already in <paramref name="folder"/>) and the quota counts out of the old
    /// fallback folder; the old files are kept as *.moved.
    /// </summary>
    internal static void MergeLegacy(string legacy, string folder)
    {
        try
        {
            string oldLedger = Path.Combine(legacy, "ledger.jsonl");
            if (File.Exists(oldLedger))
            {
                Directory.CreateDirectory(folder);
                string ledger = Path.Combine(folder, "ledger.jsonl");
                string old = File.ReadAllText(oldLedger);
                if (old.Length > 0 && !old.EndsWith('\n'))
                {
                    old += "\n";
                }

                File.WriteAllText(ledger + ".tmp", old + (File.Exists(ledger) ? File.ReadAllText(ledger) : string.Empty));
                File.Move(ledger + ".tmp", ledger, overwrite: true);
                File.Move(oldLedger, oldLedger + ".moved", overwrite: true);
            }

            string oldQuota = Path.Combine(legacy, "quota.json");
            if (File.Exists(oldQuota))
            {
                Directory.CreateDirectory(folder);
                string quota = Path.Combine(folder, "quota.json");
                if (!File.Exists(quota))
                {
                    File.Copy(oldQuota, quota);
                }

                File.Move(oldQuota, oldQuota + ".moved", overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the old files stay where they are and are tried again at the next start.
        }
    }
}
