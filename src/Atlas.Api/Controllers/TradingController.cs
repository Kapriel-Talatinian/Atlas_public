using Atlas.Api.Models;
using Atlas.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/trading")]
public class TradingController : ControllerBase
{
    private readonly IPaperTradingService _paperTrading;

    public TradingController(IPaperTradingService paperTrading)
    {
        _paperTrading = paperTrading;
    }

    [HttpGet("limits")]
    public ActionResult<RiskLimitConfig> GetLimits() => Ok(_paperTrading.Limits);

    [HttpGet("orders")]
    public async Task<ActionResult<IReadOnlyList<TradingOrderReport>>> GetOrders(
        [FromQuery] int limit = 200,
        CancellationToken ct = default)
    {
        var orders = await _paperTrading.GetOrdersAsync(limit, ct);
        return Ok(orders);
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<IReadOnlyList<TradingNotification>>> GetNotifications(
        [FromQuery] int limit = 120,
        CancellationToken ct = default)
    {
        var notifications = await _paperTrading.GetNotificationsAsync(limit, ct);
        return Ok(notifications);
    }

    [HttpPost("orders/retry")]
    public async Task<ActionResult<IReadOnlyList<TradingOrderReport>>> RetryOpenOrders(
        [FromQuery] int maxOrders = 25,
        CancellationToken ct = default)
    {
        var reports = await _paperTrading.RetryOpenOrdersAsync(maxOrders, ct);
        return Ok(reports);
    }

    [HttpGet("killswitch")]
    public async Task<ActionResult<KillSwitchState>> GetKillSwitch(CancellationToken ct = default)
    {
        var state = await _paperTrading.GetKillSwitchAsync(ct);
        return Ok(state);
    }

    [HttpPost("killswitch")]
    public async Task<ActionResult<KillSwitchState>> SetKillSwitch(
        [FromBody] KillSwitchRequest request,
        CancellationToken ct = default)
    {
        if (request is null) return BadRequest("request body is required");
        var state = await _paperTrading.SetKillSwitchAsync(request, ct);
        return Ok(state);
    }

    [HttpGet("positions")]
    public async Task<ActionResult<IReadOnlyList<TradingPosition>>> GetPositions(CancellationToken ct = default)
    {
        var positions = await _paperTrading.GetPositionsAsync(ct);
        return Ok(positions);
    }

    [HttpGet("risk")]
    public async Task<ActionResult<PortfolioRiskSnapshot>> GetRisk(CancellationToken ct = default)
    {
        var risk = await _paperTrading.GetRiskAsync(ct);
        return Ok(risk);
    }

    [HttpGet("book")]
    public async Task<ActionResult<TradingBookSnapshot>> GetBook(
        [FromQuery] int orderLimit = 150,
        CancellationToken ct = default)
    {
        var book = await _paperTrading.GetBookAsync(orderLimit, ct);
        return Ok(book);
    }

    [HttpPost("orders")]
    public async Task<ActionResult<TradingOrderReport>> PlaceOrder(
        [FromBody] TradingOrderRequest request,
        CancellationToken ct = default)
    {
        var report = await _paperTrading.PlaceOrderAsync(request, ct);
        if (report.Status == OrderStatus.Rejected)
            return BadRequest(report);
        return Ok(report);
    }

    [HttpPost("preview")]
    public async Task<ActionResult<PreTradePreviewResult>> PreviewOrder(
        [FromBody] TradingOrderRequest request,
        CancellationToken ct = default)
    {
        var preview = await _paperTrading.PreviewOrderAsync(request, ct);
        if (!preview.Accepted)
            return BadRequest(preview);
        return Ok(preview);
    }

    [HttpPost("stress")]
    public async Task<ActionResult<StressTestResult>> RunStress(
        [FromBody] StressTestRequest? request,
        CancellationToken ct = default)
    {
        var result = await _paperTrading.RunStressTestAsync(request, ct);
        return Ok(result);
    }

    [HttpPost("reset")]
    public ActionResult Reset()
    {
        _paperTrading.Reset();
        return Ok(new { status = "reset", timestamp = DateTimeOffset.UtcNow });
    }
}
