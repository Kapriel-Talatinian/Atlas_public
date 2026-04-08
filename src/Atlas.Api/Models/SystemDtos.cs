namespace Atlas.Api.Models;

public sealed record MarketDataSourceStatus(
    string Source,
    string Asset,
    bool Healthy,
    bool IsFallback,
    bool IsStale,
    int QuoteCount,
    long LastLatencyMs,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    DateTimeOffset SnapshotAt);

public sealed record MarketDataCompositeStatus(
    string Asset,
    string ActiveSource,
    bool IsStale,
    DateTimeOffset AsOf,
    long SourceLagMs,
    int QuoteCount,
    IReadOnlyList<MarketDataSourceStatus> Sources,
    DateTimeOffset SnapshotAt);

public sealed record ApiMetricSnapshot(
    string Name,
    double Value,
    DateTimeOffset UpdatedAt,
    string Unit = "count");

public sealed record OpsAlert(
    string Id,
    NotificationSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset TriggeredAt,
    bool Active = true);

public sealed record OpsSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<ApiMetricSnapshot> Metrics,
    IReadOnlyList<OpsAlert> ActiveAlerts,
    IReadOnlyList<MarketDataCompositeStatus> MarketData,
    bool Degraded);

public sealed record SloTarget(
    string Name,
    double Objective,
    string Comparator,
    string Unit);

public sealed record SloWindowSnapshot(
    string Window,
    double RequestCount,
    double AvailabilityRatio,
    double ErrorRate,
    double P95LatencyMs,
    bool AvailabilityBreached,
    bool LatencyBreached);

public sealed record SloReport(
    IReadOnlyList<SloTarget> Targets,
    IReadOnlyList<SloWindowSnapshot> Windows,
    bool Breached,
    IReadOnlyList<string> Flags,
    DateTimeOffset Timestamp);

public sealed record RecoveryAction(
    string Id,
    string Category,
    string Description,
    string Severity,
    bool Automatic,
    bool Executed,
    string Status,
    DateTimeOffset Timestamp);

public sealed record RecoveryPlaybook(
    bool Degraded,
    int CriticalAlerts,
    int WarningAlerts,
    int StaleAssets,
    bool SloBreached,
    IReadOnlyList<string> SloFlags,
    bool KillSwitchActive,
    IReadOnlyList<RecoveryAction> Actions,
    DateTimeOffset Timestamp);

public sealed record RecoveryExecutionResult(
    bool Applied,
    int ActionsExecuted,
    IReadOnlyList<RecoveryAction> Actions,
    DateTimeOffset Timestamp);
