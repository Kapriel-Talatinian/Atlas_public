using System.Collections.Concurrent;
using System.Diagnostics;
using Atlas.Api.Models;

namespace Atlas.Api.Services;

public interface ISystemMonitoringService
{
    void IncrementCounter(string name, double delta = 1, string unit = "count");
    void RecordGauge(string name, double value, string unit = "gauge");
    void ObserveRequest(string route, int statusCode, double durationMs);
    void PublishAlert(string source, NotificationSeverity severity, string message, TimeSpan? dedupWindow = null);
    IReadOnlyList<ApiMetricSnapshot> GetMetrics();
    IReadOnlyList<OpsAlert> GetActiveAlerts();
    OpsSnapshot BuildSnapshot(IReadOnlyList<MarketDataCompositeStatus> marketData);
}

public sealed class SystemMonitoringService : ISystemMonitoringService
{
    public static readonly ActivitySource ActivitySource = new("Atlas.Api");

    private const int MaxAlerts = 500;
    private const int MaxRouteLatencySamples = 400;

    private readonly ConcurrentDictionary<string, MetricState> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<double>> _routeLatencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _alertDedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<OpsAlert> _alerts = new();

    private sealed record MetricState(double Value, DateTimeOffset UpdatedAt, string Unit);

    public void IncrementCounter(string name, double delta = 1, string unit = "count")
    {
        _metrics.AddOrUpdate(
            name,
            _ => new MetricState(delta, DateTimeOffset.UtcNow, unit),
            (_, current) => new MetricState(current.Value + delta, DateTimeOffset.UtcNow, current.Unit));
    }

    public void RecordGauge(string name, double value, string unit = "gauge")
    {
        _metrics[name] = new MetricState(value, DateTimeOffset.UtcNow, unit);
    }

    public void ObserveRequest(string route, int statusCode, double durationMs)
    {
        IncrementCounter("http.requests.total");
        IncrementCounter($"http.status.{statusCode}");
        RecordGauge("http.request.last_ms", durationMs, "ms");

        string routeKey = string.IsNullOrWhiteSpace(route) ? "unknown" : route.ToLowerInvariant();
        var queue = _routeLatencies.GetOrAdd(routeKey, _ => new ConcurrentQueue<double>());
        queue.Enqueue(durationMs);
        while (queue.Count > MaxRouteLatencySamples && queue.TryDequeue(out _)) { }

        var samples = queue.ToArray();
        if (samples.Length > 0)
        {
            Array.Sort(samples);
            double p50 = Percentile(samples, 0.50);
            double p95 = Percentile(samples, 0.95);
            RecordGauge($"http.route.{routeKey}.p50_ms", p50, "ms");
            RecordGauge($"http.route.{routeKey}.p95_ms", p95, "ms");
        }

        if (statusCode >= 500)
            PublishAlert("http", NotificationSeverity.Critical, $"{route} responded {statusCode}");
        else if (durationMs >= 1200)
            PublishAlert("http", NotificationSeverity.Warning, $"Slow endpoint {route}: {durationMs:F0}ms");
    }

    public void PublishAlert(string source, NotificationSeverity severity, string message, TimeSpan? dedupWindow = null)
    {
        string safeSource = string.IsNullOrWhiteSpace(source) ? "system" : source.Trim().ToLowerInvariant();
        string safeMessage = string.IsNullOrWhiteSpace(message) ? "unspecified alert" : message.Trim();

        TimeSpan window = dedupWindow ?? TimeSpan.FromMinutes(3);
        string dedupKey = $"{safeSource}:{severity}:{safeMessage}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (_alertDedup.TryGetValue(dedupKey, out var last) && now - last < window)
            return;

        _alertDedup[dedupKey] = now;

        var alert = new OpsAlert(
            Id: $"ALR-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            Severity: severity,
            Source: safeSource,
            Message: safeMessage,
            TriggeredAt: now,
            Active: true);

        _alerts.Enqueue(alert);
        while (_alerts.Count > MaxAlerts && _alerts.TryDequeue(out _)) { }

        IncrementCounter($"alerts.{severity.ToString().ToLowerInvariant()}");
    }

    public IReadOnlyList<ApiMetricSnapshot> GetMetrics()
    {
        return _metrics
            .OrderBy(kv => kv.Key)
            .Select(kv => new ApiMetricSnapshot(
                Name: kv.Key,
                Value: kv.Value.Value,
                UpdatedAt: kv.Value.UpdatedAt,
                Unit: kv.Value.Unit))
            .ToList();
    }

    public IReadOnlyList<OpsAlert> GetActiveAlerts()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddHours(-12);
        return _alerts
            .Where(a => a.TriggeredAt >= cutoff)
            .OrderByDescending(a => a.TriggeredAt)
            .Take(120)
            .ToList();
    }

    public OpsSnapshot BuildSnapshot(IReadOnlyList<MarketDataCompositeStatus> marketData)
    {
        var metrics = GetMetrics();
        var alerts = GetActiveAlerts();

        bool degraded = alerts.Any(a => a.Severity == NotificationSeverity.Critical) ||
                        marketData.Any(m => m.IsStale || m.QuoteCount <= 0);

        return new OpsSnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            Metrics: metrics,
            ActiveAlerts: alerts,
            MarketData: marketData,
            Degraded: degraded);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        if (sortedValues.Length == 1) return sortedValues[0];

        double index = percentile * (sortedValues.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper) return sortedValues[lower];
        double weight = index - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }
}
