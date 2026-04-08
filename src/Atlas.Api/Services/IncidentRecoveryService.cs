using Atlas.Api.Models;

namespace Atlas.Api.Services;

public interface IIncidentRecoveryService
{
    Task<RecoveryPlaybook> BuildPlaybookAsync(CancellationToken ct = default);
    Task<RecoveryExecutionResult> ExecuteAsync(bool dryRun = false, CancellationToken ct = default);
}

public sealed class IncidentRecoveryService : IIncidentRecoveryService
{
    private readonly ISystemMonitoringService _monitoring;
    private readonly IOptionsMarketDataService _marketData;
    private readonly IPaperTradingService _trading;

    public IncidentRecoveryService(
        ISystemMonitoringService monitoring,
        IOptionsMarketDataService marketData,
        IPaperTradingService trading)
    {
        _monitoring = monitoring;
        _marketData = marketData;
        _trading = trading;
    }

    public async Task<RecoveryPlaybook> BuildPlaybookAsync(CancellationToken ct = default)
    {
        var statuses = await _marketData.GetStatusesAsync(ct);
        var alerts = _monitoring.GetActiveAlerts();
        var slo = _monitoring.GetSloReport();
        var killSwitch = await _trading.GetKillSwitchAsync(ct);

        int critical = alerts.Count(a => a.Severity == NotificationSeverity.Critical);
        int warning = alerts.Count(a => a.Severity == NotificationSeverity.Warning);
        int stale = statuses.Count(s => s.IsStale || s.QuoteCount <= 0);
        bool degraded = critical > 0 || stale > 0 || slo.Breached;

        var actions = new List<RecoveryAction>();

        if (critical > 0 && !killSwitch.IsActive)
        {
            actions.Add(new RecoveryAction(
                Id: "REC-KS-ON",
                Category: "risk",
                Description: "Enable global kill-switch to block new orders during incident.",
                Severity: "critical",
                Automatic: true,
                Executed: false,
                Status: "planned",
                Timestamp: DateTimeOffset.UtcNow));
        }

        if (stale > 0)
        {
            actions.Add(new RecoveryAction(
                Id: "REC-MD-FALLBACK",
                Category: "market-data",
                Description: "Keep fallback feeds enabled and monitor source recovery.",
                Severity: "warning",
                Automatic: true,
                Executed: false,
                Status: "planned",
                Timestamp: DateTimeOffset.UtcNow));
        }

        if (slo.Breached)
        {
            actions.Add(new RecoveryAction(
                Id: "REC-SLO-CAPACITY",
                Category: "slo",
                Description: "SLO breach detected. Scale service and throttle heavy jobs.",
                Severity: "warning",
                Automatic: false,
                Executed: false,
                Status: "manual",
                Timestamp: DateTimeOffset.UtcNow));
        }

        actions.Add(new RecoveryAction(
            Id: "REC-OMS-RECON",
            Category: "oms",
            Description: "Run OMS reconciliation and inspect mismatches.",
            Severity: degraded ? "warning" : "info",
            Automatic: true,
            Executed: false,
            Status: "planned",
            Timestamp: DateTimeOffset.UtcNow));

        if (degraded)
        {
            actions.Add(new RecoveryAction(
                Id: "REC-OPS-RUNBOOK",
                Category: "runbook",
                Description: "Escalate to on-call and execute incident runbook timeline.",
                Severity: "warning",
                Automatic: false,
                Executed: false,
                Status: "manual",
                Timestamp: DateTimeOffset.UtcNow));
        }

        return new RecoveryPlaybook(
            Degraded: degraded,
            CriticalAlerts: critical,
            WarningAlerts: warning,
            StaleAssets: stale,
            SloBreached: slo.Breached,
            SloFlags: slo.Flags,
            KillSwitchActive: killSwitch.IsActive,
            Actions: actions,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<RecoveryExecutionResult> ExecuteAsync(bool dryRun = false, CancellationToken ct = default)
    {
        RecoveryPlaybook playbook = await BuildPlaybookAsync(ct);
        var executed = new List<RecoveryAction>();

        foreach (var action in playbook.Actions)
        {
            if (!action.Automatic)
            {
                executed.Add(action with { Status = "manual-required" });
                continue;
            }

            if (dryRun)
            {
                executed.Add(action with { Status = "dry-run", Executed = false });
                continue;
            }

            try
            {
                switch (action.Id)
                {
                    case "REC-KS-ON":
                        await _trading.SetKillSwitchAsync(new KillSwitchRequest(true, "incident-recovery", "auto-recovery"), ct);
                        break;
                    case "REC-MD-FALLBACK":
                        // Fallback logic is automatic in market-data service. Here we only emit an alert.
                        _monitoring.PublishAlert("recovery", NotificationSeverity.Warning, "Fallback mode confirmed during recovery.");
                        break;
                    case "REC-OMS-RECON":
                        await _trading.ReconcileOrdersAsync(500, ct);
                        break;
                }

                executed.Add(action with { Executed = true, Status = "executed", Timestamp = DateTimeOffset.UtcNow });
            }
            catch (Exception ex)
            {
                executed.Add(action with { Executed = false, Status = "failed:" + ex.GetType().Name, Timestamp = DateTimeOffset.UtcNow });
            }
        }

        return new RecoveryExecutionResult(
            Applied: !dryRun,
            ActionsExecuted: executed.Count(a => a.Executed),
            Actions: executed,
            Timestamp: DateTimeOffset.UtcNow);
    }
}
