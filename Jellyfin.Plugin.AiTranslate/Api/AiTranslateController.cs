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
    private readonly Providers.ModelCatalog _models;
    private readonly CacheCleaner _cache;

    public AiTranslateController(SubtitleJobService jobs, DubService dub, Providers.ModelCatalog models, CacheCleaner cache)
    {
        _jobs = jobs;
        _dub = dub;
        _models = models;
        _cache = cache;
    }

    /// <summary>Providers and whether their key works (it is checked by listing the account's models).</summary>
    [HttpGet("Providers")]
    public async Task<ActionResult<object>> GetProviders(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var result = new System.Collections.Generic.List<object>();
        foreach (var (id, name, hasKey) in _jobs.ProviderKeys())
        {
            string? error = null;
            int models = 0;
            if (hasKey)
            {
                var lists = await _models.GetAsync(id, config, cancellationToken).ConfigureAwait(false);
                error = lists.Error;
                models = lists.Text.Count + lists.Transcribe.Count + lists.Tts.Count;
            }

            result.Add(new { Id = id, Name = name, HasKey = hasKey, KeyOk = hasKey && error is null, Error = error, Models = models });
        }

        return Ok(result);
    }

    /// <summary>Current models of both providers, grouped by use (for the model drop-downs).</summary>
    [HttpGet("Models")]
    public async Task<ActionResult<object>> GetModels(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var gemini = _models.GetAsync("gemini", config, cancellationToken);
        var openai = _models.GetAsync("openai", config, cancellationToken);
        return Ok(new { Gemini = await gemini.ConfigureAwait(false), OpenAi = await openai.ConfigureAwait(false) });
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
            ReservedUsd = _jobs.Ledger.Reserved,
            ByProvider = month.GroupBy(e => e.Provider + " " + e.Model).Select(g => new { Name = g.Key, CostUsd = g.Sum(e => e.CostUsd), Minutes = g.Sum(e => e.AudioMinutes), InputTokens = g.Sum(e => e.InputTokens), OutputTokens = g.Sum(e => e.OutputTokens) }),
            Recent = entries.OrderByDescending(e => e.Utc).Take(50),
            Running = _jobs.Running.Concat(_dub.Running),
        });
    }

    /// <summary>Films/episodes matching the search that have subtitles in the language (voice-over candidates).</summary>
    [HttpGet("Dub/Candidates")]
    public ActionResult<object> DubCandidates([FromQuery] string? search = null, [FromQuery] string language = "bg")
    {
        return Ok(_dub.Candidates(search ?? string.Empty, language).Select(v => new { v.Id, v.Name, v.ProductionYear, Exists = System.IO.File.Exists(DubService.OutputPath(v, language)) }));
    }

    [HttpGet("Dub/Estimate")]
    public async Task<ActionResult<DubEstimate>> DubEstimate(
        [FromQuery] Guid itemId,
        [FromQuery] string language = "bg",
        [FromQuery] string? provider = null,
        [FromQuery] string? voice = null,
        CancellationToken cancellationToken = default)
    {
        return Ok(await _dub.EstimateAsync(itemId, language, provider, voice, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Voice providers that have a key, with their voices and today's quota.</summary>
    [HttpGet("Dub/Providers")]
    public ActionResult<object> DubProviders()
    {
        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        return Ok(_dub.Providers.Select(p => new
        {
            p.Id,
            Name = p.DisplayName(config),
            RequestsPerDay = p.RequestsPerDay(config),
            UsedToday = _dub.Quota.UsedToday(p.Id + ":" + p.Model(config), DateTime.UtcNow),
            Voices = p.Voices.Select(v => new { v.Id, v.Description }),
        }));
    }

    [HttpPost("Dub/Start")]
    public ActionResult DubStart([FromQuery] Guid itemId, [FromQuery] string language = "bg", [FromQuery] string? provider = null, [FromQuery] string? voice = null)
    {
        string? refused = _dub.Start(itemId, language, provider, voice);
        return refused is null ? Accepted() : Conflict(refused);
    }

    /// <summary>Stops a running job (what is finished stays cached), or hides the note of one that ended with an error.</summary>
    [HttpPost("Jobs/{id}/Cancel")]
    public ActionResult CancelJob([FromRoute] string id) => _jobs.Cancel(id) || _dub.Cancel(id) ? Accepted() : NotFound();

    /// <summary>Disk space per kind of cached data.</summary>
    [HttpGet("Cache")]
    public ActionResult<object> GetCache() => Ok(_cache.Usage());

    /// <summary>Cleans by the rules (kind = "rules") or empties one kind (subtitles, voice, resume, temp).</summary>
    [HttpPost("Cache/Clean")]
    public ActionResult<CleanResult> CleanCache([FromQuery] string kind = "rules")
    {
        if (kind == "rules")
        {
            return Ok(_cache.CleanByRules(Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration()));
        }

        return kind is CacheCleaner.Subtitles or CacheCleaner.Voice or CacheCleaner.Resume or CacheCleaner.Temp
            ? Ok(_cache.Clear(kind))
            : BadRequest("Unknown cache kind");
    }
}
