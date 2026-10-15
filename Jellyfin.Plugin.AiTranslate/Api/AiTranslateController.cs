using System;
using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiTranslate.Api;

/// <summary>Cost log and running jobs for the settings page (administrators only).</summary>
[ApiController]
[Route("AiTranslate")]
[Authorize(Policy = "RequiresElevation")]
public sealed class AiTranslateController : ControllerBase
{
    private readonly SubtitleJobService _jobs;

    public AiTranslateController(SubtitleJobService jobs)
    {
        _jobs = jobs;
    }

    [HttpGet("Status")]
    public ActionResult<object> GetStatus()
    {
        var now = DateTime.UtcNow;
        var entries = _jobs.Ledger.ReadAll();
        var month = entries.Where(e => e.Utc.Year == now.Year && e.Utc.Month == now.Month).ToList();
        return Ok(new
        {
            MonthTotalUsd = month.Sum(e => e.CostUsd),
            BudgetUsd = Plugin.Instance?.Configuration.MonthlyBudgetUsd ?? 0,
            ByProvider = month.GroupBy(e => e.Provider + " " + e.Model).Select(g => new { Name = g.Key, CostUsd = g.Sum(e => e.CostUsd), Minutes = g.Sum(e => e.AudioMinutes), InputTokens = g.Sum(e => e.InputTokens), OutputTokens = g.Sum(e => e.OutputTokens) }),
            Recent = entries.OrderByDescending(e => e.Utc).Take(50),
            Running = _jobs.Running,
        });
    }
}
