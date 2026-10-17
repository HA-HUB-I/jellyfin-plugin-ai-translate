using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Configuration;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AiTranslate;

/// <summary>Daily clean-up of the plugin's cache (Dashboard → Scheduled Tasks → AI Translate).</summary>
public sealed class CacheCleanupTask : IScheduledTask
{
    private readonly CacheCleaner _cleaner;

    public CacheCleanupTask(CacheCleaner cleaner)
    {
        _cleaner = cleaner;
    }

    public string Name => "Почистване на кеша";

    public string Key => "AiTranslateCacheCleanup";

    public string Description =>
        "Трие гласовите реплики на дублажа, които не са ползвани отдавна или надхвърлят лимита, недовършените задачи и остатъците от сривове. Готовите субтитри остават.";

    public string Category => "AI Translate";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _cleaner.CleanByRules(Plugin.Instance?.Configuration ?? new PluginConfiguration());
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }];
}
