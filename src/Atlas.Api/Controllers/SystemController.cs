using Atlas.Api.Models;
using Atlas.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/system")]
public class SystemController : ControllerBase
{
    private readonly ISystemMonitoringService _monitoring;
    private readonly IOptionsMarketDataService _marketData;

    public SystemController(ISystemMonitoringService monitoring, IOptionsMarketDataService marketData)
    {
        _monitoring = monitoring;
        _marketData = marketData;
    }

    [HttpGet("health")]
    public async Task<ActionResult> GetHealth(CancellationToken ct = default)
    {
        var statuses = await _marketData.GetStatusesAsync(ct);
        bool staleMarketData = statuses.Any(s => s.IsStale || s.QuoteCount <= 0);
        var alerts = _monitoring.GetActiveAlerts();
        bool criticalAlerts = alerts.Any(a => a.Severity == NotificationSeverity.Critical);

        return Ok(new
        {
            ok = !staleMarketData && !criticalAlerts,
            degraded = staleMarketData || criticalAlerts,
            service = "atlas-api",
            marketData = statuses,
            criticalAlertCount = alerts.Count(a => a.Severity == NotificationSeverity.Critical),
            warningAlertCount = alerts.Count(a => a.Severity == NotificationSeverity.Warning),
            timestamp = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("metrics")]
    public ActionResult<IReadOnlyList<ApiMetricSnapshot>> GetMetrics()
    {
        return Ok(_monitoring.GetMetrics());
    }

    [HttpGet("alerts")]
    public ActionResult<IReadOnlyList<OpsAlert>> GetAlerts()
    {
        return Ok(_monitoring.GetActiveAlerts());
    }

    [HttpGet("market-data")]
    public async Task<ActionResult<IReadOnlyList<MarketDataCompositeStatus>>> GetMarketDataStatus(CancellationToken ct = default)
    {
        var statuses = await _marketData.GetStatusesAsync(ct);
        return Ok(statuses);
    }

    [HttpGet("ops")]
    public async Task<ActionResult<OpsSnapshot>> GetOpsSnapshot(CancellationToken ct = default)
    {
        var statuses = await _marketData.GetStatusesAsync(ct);
        return Ok(_monitoring.BuildSnapshot(statuses));
    }
}
