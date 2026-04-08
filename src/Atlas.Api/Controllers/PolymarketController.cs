using Atlas.Api.Models;
using Atlas.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/polymarket")]
public sealed class PolymarketController : ControllerBase
{
    private readonly IPolymarketBotService _botService;

    public PolymarketController(IPolymarketBotService botService)
    {
        _botService = botService;
    }

    [HttpGet("live")]
    public async Task<ActionResult<PolymarketLiveSnapshot>> GetLiveSnapshot(
        [FromQuery] int lookaheadMinutes = 24 * 60,
        [FromQuery] int maxMarkets = 24,
        CancellationToken ct = default)
    {
        PolymarketLiveSnapshot snapshot = await _botService.GetSnapshotAsync(lookaheadMinutes, maxMarkets, ct);
        return Ok(snapshot);
    }
}
