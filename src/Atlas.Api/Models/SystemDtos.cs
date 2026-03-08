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
