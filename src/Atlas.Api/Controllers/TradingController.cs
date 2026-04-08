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

    [HttpGet("margin-rules")]
    public ActionResult<MarginRulebook> GetMarginRules() => Ok(_paperTrading.MarginRules);

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

    [HttpPost("orders/cancel")]
    public async Task<ActionResult<TradingOrderReport>> CancelOrder(
        [FromBody] CancelOrderRequest request,
        CancellationToken ct = default)
    {
        var report = await _paperTrading.CancelOrderAsync(request, ct);
        if (report.Status == OrderStatus.Rejected)
            return BadRequest(report);
        return Ok(report);
    }

    [HttpPost("orders/replace")]
    public async Task<ActionResult<OrderReplaceResult>> ReplaceOrder(
        [FromBody] ReplaceOrderRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var result = await _paperTrading.ReplaceOrderAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("orders/reconcile")]
    public async Task<ActionResult<OmsReconciliationReport>> ReconcileOrders(
        [FromQuery] int limit = 400,
        CancellationToken ct = default)
    {
        var report = await _paperTrading.ReconcileOrdersAsync(limit, ct);
        return Ok(report);
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

    [HttpPost("algo/execute")]
    public async Task<ActionResult<AlgoExecutionReport>> ExecuteAlgo(
        [FromBody] AlgoExecutionRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var report = await _paperTrading.ExecuteAlgoOrderAsync(request, ct);
            return Ok(report);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("hedge/suggest")]
    public async Task<ActionResult<HedgeSuggestionResponse>> SuggestHedge(
        [FromBody] HedgeSuggestionRequest? request,
        CancellationToken ct = default)
    {
        var response = await _paperTrading.GetHedgeSuggestionAsync(request ?? new HedgeSuggestionRequest(), ct);
        return Ok(response);
    }

    [HttpPost("hedge/auto")]
    public async Task<ActionResult<AutoHedgeReport>> RunAutoHedge(
        [FromBody] AutoHedgeRequest? request,
        CancellationToken ct = default)
    {
        var response = await _paperTrading.RunAutoHedgeAsync(request ?? new AutoHedgeRequest(), ct);
        return Ok(response);
    }

    [HttpPost("portfolio/optimize")]
    public async Task<ActionResult<PortfolioOptimizationResponse>> OptimizePortfolio(
        [FromBody] PortfolioOptimizationRequest? request,
        CancellationToken ct = default)
    {
        var response = await _paperTrading.OptimizePortfolioAsync(request ?? new PortfolioOptimizationRequest(), ct);
        return Ok(response);
    }

    [HttpGet("history")]
    public async Task<ActionResult<TradingHistorySnapshot>> GetHistory(
        [FromQuery] int orderLimit = 250,
        [FromQuery] int positionLimit = 250,
        [FromQuery] int riskLimit = 250,
        [FromQuery] int auditLimit = 250,
        CancellationToken ct = default)
    {
        var snapshot = await _paperTrading.GetHistoryAsync(orderLimit, positionLimit, riskLimit, auditLimit, ct);
        return Ok(snapshot);
    }

    [HttpPost("reset")]
    public ActionResult Reset()
    {
        _paperTrading.Reset();
        return Ok(new { status = "reset", timestamp = DateTimeOffset.UtcNow });
    }
}
