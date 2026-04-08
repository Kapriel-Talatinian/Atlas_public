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
    private readonly IIncidentRecoveryService _recovery;

    public SystemController(
        ISystemMonitoringService monitoring,
        IOptionsMarketDataService marketData,
        IIncidentRecoveryService recovery)
    {
        _monitoring = monitoring;
        _marketData = marketData;
        _recovery = recovery;
    }

    [HttpGet("health")]
    public async Task<ActionResult> GetHealth(CancellationToken ct = default)
    {
        var statuses = await _marketData.GetStatusesAsync(ct);
        bool staleMarketData = statuses.Any(s => s.IsStale || s.QuoteCount <= 0);
        var alerts = _monitoring.GetActiveAlerts();
        var slo = _monitoring.GetSloReport();
        bool criticalAlerts = alerts.Any(a => a.Severity == NotificationSeverity.Critical);

        return Ok(new
        {
            ok = !staleMarketData && !criticalAlerts && !slo.Breached,
            degraded = staleMarketData || criticalAlerts || slo.Breached,
            service = "atlas-api",
            marketData = statuses,
            criticalAlertCount = alerts.Count(a => a.Severity == NotificationSeverity.Critical),
            warningAlertCount = alerts.Count(a => a.Severity == NotificationSeverity.Warning),
            sloBreached = slo.Breached,
            sloFlags = slo.Flags,
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

    [HttpGet("slo")]
    public ActionResult<SloReport> GetSlo()
    {
        return Ok(_monitoring.GetSloReport());
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

    [HttpGet("recovery-playbook")]
    public async Task<ActionResult<RecoveryPlaybook>> GetRecoveryPlaybook(CancellationToken ct = default)
    {
        var playbook = await _recovery.BuildPlaybookAsync(ct);
        return Ok(playbook);
    }

    [HttpPost("recovery/execute")]
    public async Task<ActionResult<RecoveryExecutionResult>> ExecuteRecovery(
        [FromQuery] bool dryRun = false,
        CancellationToken ct = default)
    {
        var result = await _recovery.ExecuteAsync(dryRun, ct);
        return Ok(result);
    }
}
