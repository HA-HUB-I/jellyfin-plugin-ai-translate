using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiTranslate.Dubbing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiTranslate.Api;

/// <summary>Cost log, running jobs and voice-over for the settings page (administrators only).</summary>
[ApiController]
[Route("AiTranslate")]
[Authorize(Policy = "RequiresElevation")]
public sealed class AiTranslateController : ControllerBase
{
    private readonly SubtitleJobService _jobs;
    private readonly DubService _dub;

    public AiTranslateController(SubtitleJobService jobs, DubService dub)
    {
        _jobs = jobs;
        _dub = dub;
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
            Running = _jobs.Running.Concat(_dub.Running),
        });
    }

    /// <summary>Films/episodes matching the search that have subtitles in the language (voice-over candidates).</summary>
    [HttpGet("Dub/Candidates")]
    public ActionResult<object> DubCandidates([FromQuery] string search, [FromQuery] string language = "bg")
    {
        return Ok(_dub.Candidates(search ?? string.Empty, language).Select(v => new { v.Id, v.Name, v.ProductionYear, Exists = System.IO.File.Exists(DubService.OutputPath(v, language)) }));
    }

    [HttpGet("Dub/Estimate")]
    public async Task<ActionResult<DubEstimate>> DubEstimate([FromQuery] Guid itemId, [FromQuery] string language = "bg", CancellationToken cancellationToken = default)
    {
        return Ok(await _dub.EstimateAsync(itemId, language, cancellationToken).ConfigureAwait(false));
    }

    [HttpGet("Dub/Voices")]
    public ActionResult<object> DubVoices() => Ok(_dub.Tts.Voices.Select(v => new { v.Id, v.Description }));

    [HttpPost("Dub/Start")]
    public ActionResult DubStart([FromQuery] Guid itemId, [FromQuery] string language = "bg", [FromQuery] string? voice = null)
    {
        return _dub.Start(itemId, language, voice) ? Accepted() : Conflict("Already running");
    }
}
