using Atlas.Api.Models;
using Atlas.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/experimental/bot")]
public sealed class ExperimentalController : ControllerBase
{
    private readonly IExperimentalAutoTraderService _autoTrader;

    public ExperimentalController(IExperimentalAutoTraderService autoTrader)
    {
        _autoTrader = autoTrader;
    }

    [HttpGet("snapshot")]
    public async Task<ActionResult<ExperimentalBotSnapshot>> GetSnapshot(
        [FromQuery] string asset = "BTC",
        CancellationToken ct = default)
    {
        var snapshot = await _autoTrader.GetSnapshotAsync(asset, ct);
        return Ok(snapshot);
    }

    [HttpGet("explain")]
    public async Task<ActionResult<ExperimentalBotModelExplainSnapshot>> GetModelExplain(
        [FromQuery] string asset = "BTC",
        CancellationToken ct = default)
    {
        var explain = await _autoTrader.GetModelExplainAsync(asset, ct);
        return Ok(explain);
    }

    [HttpPost("configure")]
    public async Task<ActionResult<ExperimentalBotSnapshot>> Configure(
        [FromQuery] string asset = "BTC",
        [FromBody] ExperimentalBotConfigRequest? request = null,
        CancellationToken ct = default)
    {
        var snapshot = await _autoTrader.ConfigureAsync(asset, request ?? new ExperimentalBotConfigRequest(), ct);
        return Ok(snapshot);
    }

    [HttpPost("run")]
    public async Task<ActionResult<ExperimentalBotSnapshot>> RunCycles(
        [FromQuery] string asset = "BTC",
        [FromQuery] int cycles = 1,
        CancellationToken ct = default)
    {
        var snapshot = await _autoTrader.RunCycleAsync(asset, cycles, ct);
        return Ok(snapshot);
    }

    [HttpPost("reset")]
    public async Task<ActionResult<ExperimentalBotSnapshot>> Reset(
        [FromQuery] string asset = "BTC",
        CancellationToken ct = default)
    {
        var snapshot = await _autoTrader.ResetAsync(asset, ct);
        return Ok(snapshot);
    }
}
