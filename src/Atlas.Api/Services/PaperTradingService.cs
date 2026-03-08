using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface IPaperTradingService
{
    RiskLimitConfig Limits { get; }
    Task<TradingOrderReport> PlaceOrderAsync(TradingOrderRequest request, CancellationToken ct = default);
    Task<PreTradePreviewResult> PreviewOrderAsync(TradingOrderRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TradingOrderReport>> GetOrdersAsync(int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<TradingPosition>> GetPositionsAsync(CancellationToken ct = default);
    Task<PortfolioRiskSnapshot> GetRiskAsync(CancellationToken ct = default);
    Task<TradingBookSnapshot> GetBookAsync(int orderLimit = 150, CancellationToken ct = default);
    Task<StressTestResult> RunStressTestAsync(StressTestRequest? request, CancellationToken ct = default);
    Task<KillSwitchState> GetKillSwitchAsync(CancellationToken ct = default);
    Task<KillSwitchState> SetKillSwitchAsync(KillSwitchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TradingOrderReport>> RetryOpenOrdersAsync(int maxOrders = 25, CancellationToken ct = default);
    Task<IReadOnlyList<TradingNotification>> GetNotificationsAsync(int limit = 120, CancellationToken ct = default);
    void Reset();
}

public sealed class PaperTradingService : IPaperTradingService
{
    private sealed class PositionState
    {
        public required string Symbol { get; init; }
        public required string Asset { get; init; }
        public double NetQuantity { get; set; }
        public double AvgEntryPrice { get; set; }
        public double RealizedPnl { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public PositionState Clone() => new()
        {
            Symbol = Symbol,
            Asset = Asset,
            NetQuantity = NetQuantity,
            AvgEntryPrice = AvgEntryPrice,
            RealizedPnl = RealizedPnl,
            UpdatedAt = UpdatedAt
        };
    }

    private sealed class WorkingOrderState
    {
        public required string OrderId { get; init; }
        public required string Symbol { get; init; }
        public required string Asset { get; init; }
        public required TradeDirection Side { get; init; }
        public required OrderType Type { get; init; }
        public required double OriginalQuantity { get; init; }
        public required double RequestedPrice { get; init; }
        public required bool AllowPartialFill { get; init; }
        public required int MaxRetries { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public string? ClientOrderId { get; init; }
        public double? LimitPrice { get; init; }
        public int RetryCount { get; set; }
        public double RemainingQuantity { get; set; }
        public string? LastRejectReason { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public List<string> StateTrace { get; } = ["Received"];
        public List<TradingOrderFillExecution> Fills { get; } = [];

        public double FilledQuantity => OriginalQuantity - RemainingQuantity;
        public double TotalFees => Fills.Sum(f => f.Fees);
        public double AvgFillPrice => FilledQuantity > 0 ? Fills.Sum(f => f.Price * f.Quantity) / FilledQuantity : 0;
    }

    private sealed class AccountState
    {
        public double StartingEquity { get; init; }
        public DateOnly TradingDay { get; set; }
        public double DayStartEquity { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, PositionState> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkingOrderState> _openOrders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _idempotencyByClientOrderId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TradingOrderReport> _ordersById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TradingOrderReport> _orders = [];
    private readonly List<TradingNotification> _notifications = [];
    private readonly Dictionary<string, DateTimeOffset> _requestFingerprints = new(StringComparer.OrdinalIgnoreCase);

    private readonly IOptionsMarketDataService _marketData;
    private readonly ILogger<PaperTradingService> _logger;
    private readonly ISystemMonitoringService _monitoring;

    private readonly AccountState _account;
    private KillSwitchState _killSwitch;

    private const int MaxStoredOrders = 5000;
    private const int MaxStoredNotifications = 800;
    private const int FingerprintWindowSeconds = 2;

    public PaperTradingService(
        IOptionsMarketDataService marketData,
        ILogger<PaperTradingService> logger,
        ISystemMonitoringService monitoring)
    {
        _marketData = marketData;
        _logger = logger;
        _monitoring = monitoring;

        const double startingEquity = 2_500_000;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        _account = new AccountState
        {
            StartingEquity = startingEquity,
            DayStartEquity = startingEquity,
            TradingDay = today
        };

        _killSwitch = new KillSwitchState(
            IsActive: false,
            Reason: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            UpdatedBy: "system");
    }

    public RiskLimitConfig Limits { get; } = new();

    public async Task<TradingOrderReport> PlaceOrderAsync(TradingOrderRequest request, CancellationToken ct = default)
    {
        var normalizedRequest = NormalizeRequest(request);

        if (TryReplayIdempotent(normalizedRequest, out var replay))
            return replay;

        if (IsLikelyDuplicate(normalizedRequest))
            return PersistOrder(BuildRejectedOrder(normalizedRequest, "duplicate-order-fingerprint", normalizedRequest.LimitPrice ?? 0));

        var (quote, simulation) = await EvaluateOrderSimulationAsync(normalizedRequest, ct);
        if (simulation is null)
            return PersistOrder(BuildRejectedOrder(normalizedRequest, "simulation-unavailable"));

        if (!simulation.Accepted)
            return PersistOrder(BuildRejectedOrder(normalizedRequest, simulation.RejectReason, simulation.RequestedPrice));

        if (quote is null)
            return PersistOrder(BuildRejectedOrder(normalizedRequest, "quote-unavailable", simulation.RequestedPrice));

        string orderId = $"ORD-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
        var state = new WorkingOrderState
        {
            OrderId = orderId,
            Symbol = normalizedRequest.Symbol,
            Asset = quote.Asset,
            Side = normalizedRequest.Side,
            Type = normalizedRequest.Type,
            OriginalQuantity = normalizedRequest.Quantity,
            RemainingQuantity = normalizedRequest.Quantity,
            RequestedPrice = simulation.RequestedPrice,
            ClientOrderId = normalizedRequest.ClientOrderId,
            LimitPrice = normalizedRequest.LimitPrice,
            AllowPartialFill = normalizedRequest.AllowPartialFill,
            MaxRetries = Math.Clamp(normalizedRequest.MaxRetries, 0, 8),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        state.StateTrace.Add("Accepted");

        lock (_gate)
        {
            _openOrders[state.OrderId] = state;
            if (!string.IsNullOrWhiteSpace(state.ClientOrderId))
                _idempotencyByClientOrderId[state.ClientOrderId] = state.OrderId;
        }

        var report = await ExecuteWorkingOrderAsync(state.OrderId, ct);

        await MaybeRunLiquidationAsync(ct);
        return report;
    }

    public async Task<PreTradePreviewResult> PreviewOrderAsync(TradingOrderRequest request, CancellationToken ct = default)
    {
        var normalizedRequest = NormalizeRequest(request);
        var (quote, simulation) = await EvaluateOrderSimulationAsync(normalizedRequest, ct);

        if (simulation is null)
        {
            return new PreTradePreviewResult(
                Accepted: false,
                RejectReason: "simulation-unavailable",
                RequestedPrice: 0,
                FillPrice: 0,
                EstimatedFees: 0,
                ProjectedRisk: await GetRiskAsync(ct),
                EstimatedInitialMargin: 0,
                EstimatedMaintenanceMargin: 0,
                Timestamp: DateTimeOffset.UtcNow);
        }

        if (quote is null || !simulation.Accepted)
        {
            return new PreTradePreviewResult(
                Accepted: false,
                RejectReason: simulation.RejectReason,
                RequestedPrice: simulation.RequestedPrice,
                FillPrice: simulation.FillPrice,
                EstimatedFees: simulation.Fees,
                ProjectedRisk: await GetRiskAsync(ct),
                EstimatedInitialMargin: simulation.ProjectedInitialMargin,
                EstimatedMaintenanceMargin: simulation.ProjectedMaintenanceMargin,
                Timestamp: DateTimeOffset.UtcNow,
                EstimatedSlippagePct: simulation.SlippagePctEstimate,
                EstimatedExecutionQuality: simulation.QualityScoreEstimate,
                EstimatedFilledQuantity: simulation.EstimatedFilledQuantity,
                EstimatedRemainingQuantity: simulation.EstimatedRemainingQuantity,
                EstimatedFeeRate: simulation.FillPrice > 0 && normalizedRequest.Quantity > 0
                    ? simulation.Fees / (simulation.FillPrice * normalizedRequest.Quantity)
                    : 0);
        }

        return new PreTradePreviewResult(
            Accepted: true,
            RejectReason: null,
            RequestedPrice: simulation.RequestedPrice,
            FillPrice: simulation.FillPrice,
            EstimatedFees: simulation.Fees,
            ProjectedRisk: BuildProjectedRiskSnapshot(simulation),
            EstimatedInitialMargin: simulation.ProjectedInitialMargin,
            EstimatedMaintenanceMargin: simulation.ProjectedMaintenanceMargin,
            Timestamp: DateTimeOffset.UtcNow,
            EstimatedSlippagePct: simulation.SlippagePctEstimate,
            EstimatedExecutionQuality: simulation.QualityScoreEstimate,
            EstimatedFilledQuantity: simulation.EstimatedFilledQuantity,
            EstimatedRemainingQuantity: simulation.EstimatedRemainingQuantity,
            EstimatedFeeRate: simulation.FillPrice > 0 && normalizedRequest.Quantity > 0
                ? simulation.Fees / (simulation.FillPrice * normalizedRequest.Quantity)
                : 0);
    }

    public async Task<IReadOnlyList<TradingOrderReport>> RetryOpenOrdersAsync(int maxOrders = 25, CancellationToken ct = default)
    {
        List<string> toRetry;
        lock (_gate)
        {
            toRetry = _openOrders.Values
                .Where(o => o.RemainingQuantity > 1e-12)
                .OrderBy(o => o.CreatedAt)
                .Take(Math.Clamp(maxOrders, 1, 200))
                .Select(o => o.OrderId)
                .ToList();
        }

        var reports = new List<TradingOrderReport>(toRetry.Count);
        foreach (string orderId in toRetry)
        {
            var report = await ExecuteWorkingOrderAsync(orderId, ct);
            reports.Add(report);
        }

        await MaybeRunLiquidationAsync(ct);
        return reports;
    }

    public Task<KillSwitchState> GetKillSwitchAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_killSwitch);
        }
    }

    public Task<KillSwitchState> SetKillSwitchAsync(KillSwitchRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string reason = string.IsNullOrWhiteSpace(request.Reason) ? "manual-toggle" : request.Reason.Trim();
        string actor = string.IsNullOrWhiteSpace(request.UpdatedBy) ? "api" : request.UpdatedBy.Trim();

        lock (_gate)
        {
            _killSwitch = new KillSwitchState(
                IsActive: request.IsActive,
                Reason: reason,
                UpdatedAt: DateTimeOffset.UtcNow,
                UpdatedBy: actor);
        }

        AddNotification(
            request.IsActive ? NotificationSeverity.Critical : NotificationSeverity.Info,
            "kill-switch",
            request.IsActive
                ? $"Kill-switch enabled by {actor}: {reason}"
                : $"Kill-switch disabled by {actor}: {reason}");

        return Task.FromResult(_killSwitch);
    }

    public Task<IReadOnlyList<TradingOrderReport>> GetOrdersAsync(int limit = 200, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int safeLimit = Math.Clamp(limit, 1, 2500);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TradingOrderReport>>(
                _orders
                    .OrderByDescending(o => o.Timestamp)
                    .Take(safeLimit)
                    .ToList());
        }
    }

    public async Task<IReadOnlyList<TradingPosition>> GetPositionsAsync(CancellationToken ct = default)
    {
        var states = SnapshotStates();
        if (states.Count == 0) return [];

        var quotesBySymbol = await LoadQuotesBySymbolAsync(states.Select(s => s.Asset).Distinct(), ct);
        var result = new List<TradingPosition>(states.Count);

        foreach (var state in states)
        {
            quotesBySymbol.TryGetValue(state.Symbol, out var quote);
            double mark = EffectiveMark(quote, state.AvgEntryPrice);
            double unrealized = (mark - state.AvgEntryPrice) * state.NetQuantity;
            double notional = Math.Abs(mark * state.NetQuantity);

            GreeksResult greeks = quote is null
                ? GreeksResult.Zero
                : new GreeksResult(
                    Delta: quote.Delta * state.NetQuantity,
                    Gamma: quote.Gamma * state.NetQuantity,
                    Vega: quote.Vega * state.NetQuantity,
                    Theta: quote.Theta * state.NetQuantity,
                    Vanna: 0,
                    Volga: 0,
                    Charm: 0,
                    Rho: 0);

            var margin = EstimatePositionMargins(notional, greeks);

            result.Add(new TradingPosition(
                Symbol: state.Symbol,
                Asset: state.Asset,
                NetQuantity: state.NetQuantity,
                AvgEntryPrice: state.AvgEntryPrice,
                MarkPrice: mark,
                Notional: notional,
                UnrealizedPnl: unrealized,
                RealizedPnl: state.RealizedPnl,
                Greeks: greeks,
                UpdatedAt: state.UpdatedAt,
                InitialMarginRequirement: margin.Initial,
                MaintenanceMarginRequirement: margin.Maintenance,
                MarginMode: "Portfolio"));
        }

        return result
            .OrderByDescending(p => p.Notional)
            .ToList();
    }

    public async Task<PortfolioRiskSnapshot> GetRiskAsync(CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(ct);
        return BuildRiskSnapshot(positions);
    }

    public async Task<TradingBookSnapshot> GetBookAsync(int orderLimit = 150, CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(ct);
        var orders = await GetOrdersAsync(orderLimit, ct);
        var risk = BuildRiskSnapshot(positions);
        var notifications = await GetNotificationsAsync(80, ct);

        List<TradingOrderReport> openOrders;
        lock (_gate)
        {
            openOrders = _openOrders.Values
                .Select(ToOrderReport)
                .OrderByDescending(o => o.Timestamp)
                .Take(200)
                .ToList();
        }

        return new TradingBookSnapshot(
            Positions: positions,
            RecentOrders: orders,
            Risk: risk,
            Limits: Limits,
            Timestamp: DateTimeOffset.UtcNow,
            OpenOrders: openOrders,
            Notifications: notifications);
    }

    public Task<IReadOnlyList<TradingNotification>> GetNotificationsAsync(int limit = 120, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int safe = Math.Clamp(limit, 1, 500);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TradingNotification>>(
                _notifications
                    .OrderByDescending(n => n.Timestamp)
                    .Take(safe)
                    .ToList());
        }
    }

    public async Task<StressTestResult> RunStressTestAsync(StressTestRequest? request, CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(ct);
        var baseRisk = BuildRiskSnapshot(positions);
        var scenarios = NormalizeScenarios(request);

        if (positions.Count == 0)
        {
            return new StressTestResult(
                BaseRisk: baseRisk,
                WorstScenarioPnl: 0,
                WorstScenarioName: "-",
                BestScenarioPnl: 0,
                BestScenarioName: "-",
                Scenarios: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        var quotesBySymbol = await LoadQuotesBySymbolAsync(positions.Select(p => p.Asset).Distinct(), ct);
        var results = new List<StressScenarioResult>(scenarios.Count);

        foreach (var scenario in scenarios)
        {
            double estimatedPnl = 0;
            double estimatedDelta = 0;
            double estimatedGamma = 0;
            double estimatedVega = 0;

            foreach (var position in positions)
            {
                quotesBySymbol.TryGetValue(position.Symbol, out var quote);
                double baseSpot = quote is not null && quote.UnderlyingPrice > 0
                    ? quote.UnderlyingPrice
                    : Math.Max(position.MarkPrice, 1);
                double ds = baseSpot * scenario.UnderlyingShockPct;
                double dVol = scenario.IvShockPct;

                double delta = position.Greeks.Delta;
                double gamma = position.Greeks.Gamma;
                double vega = position.Greeks.Vega;
                double theta = position.Greeks.Theta;

                estimatedPnl +=
                    delta * ds +
                    0.5 * gamma * ds * ds +
                    vega * dVol +
                    theta * scenario.DaysForward;

                estimatedDelta += delta + gamma * ds;
                estimatedGamma += gamma;
                estimatedVega += vega;
            }

            results.Add(new StressScenarioResult(
                Name: scenario.Name,
                UnderlyingShockPct: scenario.UnderlyingShockPct,
                IvShockPct: scenario.IvShockPct,
                DaysForward: scenario.DaysForward,
                EstimatedPnl: estimatedPnl,
                EstimatedNetDelta: estimatedDelta,
                EstimatedNetGamma: estimatedGamma,
                EstimatedNetVega: estimatedVega));
        }

        var worst = results.MinBy(r => r.EstimatedPnl) ?? new StressScenarioResult("-", 0, 0, 0, 0, 0, 0, 0);
        var best = results.MaxBy(r => r.EstimatedPnl) ?? new StressScenarioResult("-", 0, 0, 0, 0, 0, 0, 0);

        return new StressTestResult(
            BaseRisk: baseRisk,
            WorstScenarioPnl: worst.EstimatedPnl,
            WorstScenarioName: worst.Name,
            BestScenarioPnl: best.EstimatedPnl,
            BestScenarioName: best.Name,
            Scenarios: results,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _positions.Clear();
            _openOrders.Clear();
            _ordersById.Clear();
            _orders.Clear();
            _idempotencyByClientOrderId.Clear();
            _notifications.Clear();
            _requestFingerprints.Clear();

            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            _account.TradingDay = today;
            _account.DayStartEquity = _account.StartingEquity;

            _killSwitch = _killSwitch with
            {
                IsActive = false,
                Reason = "reset",
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "system"
            };
        }

        _monitoring.PublishAlert("trading", NotificationSeverity.Info, "Paper trading state reset.");
    }

    private async Task<TradingOrderReport> ExecuteWorkingOrderAsync(string orderId, CancellationToken ct)
    {
        WorkingOrderState? state;
        lock (_gate)
        {
            _openOrders.TryGetValue(orderId, out state);
        }

        if (state is null)
        {
            return BuildRejectedOrder(
                new TradingOrderRequest(orderId, TradeDirection.Buy, 0),
                "unknown-order-id");
        }

        int attempts = 0;
        while (state.RemainingQuantity > 1e-12 && attempts <= state.MaxRetries)
        {
            ct.ThrowIfCancellationRequested();

            attempts += 1;
            state.RetryCount = attempts - 1;
            state.StateTrace.Add($"Attempt#{attempts}");

            var quote = await TryFindQuoteAsync(state.Symbol, ct);
            if (quote is null)
            {
                state.LastRejectReason = "quote-unavailable";
                state.StateTrace.Add("QuoteUnavailable");
                break;
            }

            if (!TryComputeExecutablePrice(state, quote, out var requestedPrice, out var executablePrice, out var rejectReason))
            {
                state.LastRejectReason = rejectReason;
                state.StateTrace.Add("NotExecutable");
                break;
            }

            var fillAttempt = SimulateFillAttempt(state, quote, executablePrice, requestedPrice, attempts);
            if (fillAttempt.FillQuantity <= 1e-12)
            {
                state.LastRejectReason = fillAttempt.Reason;
                state.StateTrace.Add("NoLiquidity");
                break;
            }

            double signedQty = state.Side == TradeDirection.Buy ? fillAttempt.FillQuantity : -fillAttempt.FillQuantity;
            ApplyFillToPosition(state.Symbol, state.Asset, signedQty, fillAttempt.FillPrice, fillAttempt.Fees);

            state.Fills.Add(new TradingOrderFillExecution(
                FillId: $"FIL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                Attempt: attempts,
                Quantity: fillAttempt.FillQuantity,
                Price: fillAttempt.FillPrice,
                Fees: fillAttempt.Fees,
                SlippagePct: fillAttempt.SlippagePct,
                Timestamp: DateTimeOffset.UtcNow,
                Venue: quote.Venue));

            state.RemainingQuantity = Math.Max(0, state.RemainingQuantity - fillAttempt.FillQuantity);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            state.StateTrace.Add(state.RemainingQuantity <= 1e-12 ? "Filled" : "PartiallyFilled");

            if (!state.AllowPartialFill && state.RemainingQuantity > 1e-12)
            {
                state.LastRejectReason = "partial-fill-not-allowed";
                break;
            }
        }

        var report = ToOrderReport(state);
        if (report.Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Expired)
        {
            lock (_gate)
            {
                _openOrders.Remove(state.OrderId);
            }
        }

        PersistOrder(report);
        RecordOrderMonitoring(report);
        return report;
    }

    private void RecordOrderMonitoring(TradingOrderReport report)
    {
        _monitoring.IncrementCounter("trading.orders.total");
        _monitoring.IncrementCounter($"trading.orders.status.{report.Status.ToString().ToLowerInvariant()}");
        _monitoring.RecordGauge("trading.execution.quality.last", report.ExecutionQualityScore);
        _monitoring.RecordGauge("trading.execution.slippage_pct.last", report.SlippagePct);

        if (report.Status == OrderStatus.Rejected)
        {
            _monitoring.IncrementCounter("trading.orders.rejected");
            _monitoring.PublishAlert(
                "trading",
                NotificationSeverity.Warning,
                $"Order rejected {report.Symbol}: {report.RejectReason}");
        }

        if (report.Status == OrderStatus.PartiallyFilled)
            _monitoring.PublishAlert("trading", NotificationSeverity.Warning, $"Order partially filled {report.OrderId}");
    }

    private TradingOrderReport ToOrderReport(WorkingOrderState state)
    {
        double filledQty = state.FilledQuantity;
        double remaining = Math.Max(0, state.RemainingQuantity);
        double avgFill = state.AvgFillPrice;
        double fillPrice = avgFill > 0 ? avgFill : 0;

        OrderStatus status;
        string? rejectReason = null;

        if (filledQty <= 1e-12)
        {
            if (!string.IsNullOrWhiteSpace(state.LastRejectReason))
            {
                status = OrderStatus.Rejected;
                rejectReason = state.LastRejectReason;
            }
            else
            {
                status = OrderStatus.Accepted;
            }
        }
        else if (remaining > 1e-12)
        {
            status = OrderStatus.PartiallyFilled;
            rejectReason = state.LastRejectReason;
        }
        else
        {
            status = OrderStatus.Filled;
        }

        double notional = avgFill * Math.Max(filledQty, 0);
        double feeRate = notional > 0 ? state.TotalFees / notional : 0;
        double slippagePct = ComputeAggregateSlippagePct(state.Side, state.RequestedPrice, avgFill);
        double quality = ComputeExecutionQualityScore(slippagePct, feeRate, filledQty, state.OriginalQuantity, state.RetryCount);

        return new TradingOrderReport(
            OrderId: state.OrderId,
            Symbol: state.Symbol,
            Side: state.Side,
            Quantity: state.OriginalQuantity,
            Type: state.Type,
            Status: status,
            RequestedPrice: state.RequestedPrice,
            FillPrice: fillPrice,
            Fees: state.TotalFees,
            RejectReason: rejectReason,
            ClientOrderId: state.ClientOrderId,
            Timestamp: state.UpdatedAt,
            FilledQuantity: filledQty,
            RemainingQuantity: remaining,
            AvgFillPrice: avgFill,
            SlippagePct: slippagePct,
            EffectiveFeeRate: feeRate,
            ExecutionQualityScore: quality,
            RetryCount: state.RetryCount,
            IdempotentReplay: false,
            StateTrace: state.StateTrace.ToList(),
            Fills: state.Fills.ToList(),
            Notional: notional);
    }

    private TradingOrderRequest NormalizeRequest(TradingOrderRequest request)
    {
        string symbol = (request.Symbol ?? string.Empty).Trim();
        return request with
        {
            Symbol = symbol,
            Quantity = Math.Abs(request.Quantity),
            MaxRetries = Math.Clamp(request.MaxRetries, 0, 8),
            MaxSlippagePct = request.MaxSlippagePct.HasValue
                ? MathUtils.Clamp(request.MaxSlippagePct.Value, 0.0001, 0.50)
                : null
        };
    }

    private bool TryReplayIdempotent(TradingOrderRequest request, out TradingOrderReport report)
    {
        report = default!;
        if (string.IsNullOrWhiteSpace(request.ClientOrderId))
            return false;

        lock (_gate)
        {
            if (!_idempotencyByClientOrderId.TryGetValue(request.ClientOrderId!, out string? existingOrderId))
                return false;
            if (!_ordersById.TryGetValue(existingOrderId, out var existing))
                return false;

            report = existing with
            {
                IdempotentReplay = true,
                StateTrace = (existing.StateTrace ?? []).Concat(["IdempotentReplay"]).ToList(),
                Timestamp = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    private bool IsLikelyDuplicate(TradingOrderRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ClientOrderId))
            return false;

        string fingerprint = BuildRequestFingerprint(request);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach (var key in _requestFingerprints.Keys.ToList())
            {
                if (now - _requestFingerprints[key] > TimeSpan.FromSeconds(20))
                    _requestFingerprints.Remove(key);
            }

            if (_requestFingerprints.TryGetValue(fingerprint, out var seenAt) && now - seenAt <= TimeSpan.FromSeconds(FingerprintWindowSeconds))
                return true;

            _requestFingerprints[fingerprint] = now;
            return false;
        }
    }

    private static string BuildRequestFingerprint(TradingOrderRequest request)
    {
        return string.Join(
            "|",
            request.Symbol.ToUpperInvariant(),
            request.Side,
            request.Type,
            Math.Round(request.Quantity, 6).ToString("F6"),
            Math.Round(request.LimitPrice ?? 0, 6).ToString("F6"));
    }

    private async Task<(LiveOptionQuote? Quote, OrderSimulationResult? Simulation)> EvaluateOrderSimulationAsync(
        TradingOrderRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return (null, BuildRejectedSimulation("symbol is required", request.LimitPrice ?? 0));
        if (!double.IsFinite(request.Quantity) || request.Quantity <= 0)
            return (null, BuildRejectedSimulation("quantity must be > 0", request.LimitPrice ?? 0));
        if (request.Quantity > Limits.MaxOrderQuantity)
            return (null, BuildRejectedSimulation($"order quantity exceeds limit ({Limits.MaxOrderQuantity:F0})", request.LimitPrice ?? 0));

        KillSwitchState kill;
        lock (_gate)
        {
            kill = _killSwitch;
            if (_openOrders.Count >= Limits.MaxOpenOrders)
            {
                return (null, BuildRejectedSimulation($"open-order limit reached ({Limits.MaxOpenOrders})", request.LimitPrice ?? 0));
            }
        }

        if (kill.IsActive)
            return (null, BuildRejectedSimulation($"kill-switch active: {kill.Reason}", request.LimitPrice ?? 0));

        var quote = await TryFindQuoteAsync(request.Symbol, ct);
        if (quote is null)
            return (null, BuildRejectedSimulation($"unknown symbol {request.Symbol}", request.LimitPrice ?? 0));

        var simulation = await SimulateOrderAsync(request, quote, ct);
        return (quote, simulation);
    }

    private async Task<OrderSimulationResult> SimulateOrderAsync(
        TradingOrderRequest request,
        LiveOptionQuote quote,
        CancellationToken ct)
    {
        if (request.Type == OrderType.Limit && (!request.LimitPrice.HasValue || request.LimitPrice <= 0))
            return BuildRejectedSimulation("limit price must be set for limit order", request.LimitPrice ?? 0);

        if (!TryComputeExecutablePrice(request, quote, out double requestedPrice, out double executablePrice, out string? rejectReason))
            return BuildRejectedSimulation(rejectReason ?? "no executable price", requestedPrice);

        double estFillRatio = EstimateFillRatio(request, quote);
        if (!request.AllowPartialFill && estFillRatio < 0.999)
            return BuildRejectedSimulation("insufficient liquidity for full fill", requestedPrice);

        double estimatedFilledQty = request.Quantity * estFillRatio;
        double estimatedRemainingQty = Math.Max(0, request.Quantity - estimatedFilledQty);
        double simulatedNotional = executablePrice * request.Quantity;

        if (simulatedNotional > Limits.MaxOrderNotional)
            return BuildRejectedSimulation($"order notional exceeds limit ({Limits.MaxOrderNotional:F0})", requestedPrice);

        var states = SnapshotStates();
        double signedQty = request.Side == TradeDirection.Buy ? request.Quantity : -request.Quantity;
        double currentSymbolQty = states.FirstOrDefault(s => s.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase))?.NetQuantity ?? 0;
        double projectedSymbolQty = Math.Abs(currentSymbolQty + signedQty);
        if (projectedSymbolQty > Limits.MaxSymbolAbsQuantity)
            return BuildRejectedSimulation($"symbol quantity limit breached ({Limits.MaxSymbolAbsQuantity:F0})", requestedPrice);

        var projectedPositions = await SimulateProjectedPositionsAsync(states, quote, request, executablePrice, request.Quantity, ct);
        var risk = BuildRiskSnapshot(projectedPositions);
        if (risk.Breached)
            return BuildRejectedSimulation($"risk breach: {string.Join(", ", risk.Flags)}", requestedPrice);

        double slippagePct = ComputeAggregateSlippagePct(request.Side, requestedPrice, executablePrice);
        double maxSlippage = request.MaxSlippagePct ?? Limits.MaxSlippagePct;
        if (slippagePct > maxSlippage)
            return BuildRejectedSimulation($"slippage estimate {slippagePct:P2} exceeds max {maxSlippage:P2}", requestedPrice);

        double feeRate = request.Type == OrderType.Limit ? Limits.MakerFeeRate : Limits.TakerFeeRate;
        double fees = simulatedNotional * feeRate;
        double quality = ComputeExecutionQualityScore(slippagePct, feeRate, estimatedFilledQty, request.Quantity, request.MaxRetries);

        return new OrderSimulationResult(
            Accepted: true,
            RejectReason: null,
            FillPrice: executablePrice,
            RequestedPrice: requestedPrice,
            Fees: fees,
            ProjectedNetDelta: risk.NetDelta,
            ProjectedNetGamma: risk.NetGamma,
            ProjectedNetVega: risk.NetVega,
            ProjectedGrossNotional: risk.GrossNotional,
            ProjectedNetTheta: risk.NetTheta,
            ProjectedConcentrationPct: risk.LargestPositionConcentrationPct,
            ProjectedInitialMargin: risk.InitialMargin,
            ProjectedMaintenanceMargin: risk.MaintenanceMargin,
            ProjectedEquity: risk.Equity,
            ProjectedAvailableMargin: risk.AvailableMargin,
            SlippagePctEstimate: slippagePct,
            QualityScoreEstimate: quality,
            EstimatedFilledQuantity: estimatedFilledQty,
            EstimatedRemainingQuantity: estimatedRemainingQty);
    }

    private static OrderSimulationResult BuildRejectedSimulation(string reason, double requestedPrice = 0)
    {
        return new OrderSimulationResult(
            Accepted: false,
            RejectReason: reason,
            FillPrice: 0,
            RequestedPrice: requestedPrice,
            Fees: 0,
            ProjectedNetDelta: 0,
            ProjectedNetGamma: 0,
            ProjectedNetVega: 0,
            ProjectedGrossNotional: 0);
    }

    private static bool TryComputeExecutablePrice(
        TradingOrderRequest request,
        LiveOptionQuote quote,
        out double requestedPrice,
        out double executablePrice,
        out string? rejectReason)
    {
        requestedPrice = request.Type == OrderType.Limit
            ? request.LimitPrice ?? 0
            : request.Side == TradeDirection.Buy
                ? FirstPositive(quote.Ask, quote.Mid, quote.Mark, quote.Bid)
                : FirstPositive(quote.Bid, quote.Mid, quote.Mark, quote.Ask);

        if (request.Type == OrderType.Market)
        {
            executablePrice = request.Side == TradeDirection.Buy
                ? FirstPositive(quote.Ask, quote.Mid, quote.Mark, quote.Bid)
                : FirstPositive(quote.Bid, quote.Mid, quote.Mark, quote.Ask);
            rejectReason = executablePrice <= 0 ? "no executable market price" : null;
            return executablePrice > 0;
        }

        double bestBuy = FirstPositive(quote.Ask, quote.Mid, quote.Mark, quote.Bid);
        double bestSell = FirstPositive(quote.Bid, quote.Mid, quote.Mark, quote.Ask);
        double limit = request.LimitPrice ?? 0;
        if (limit <= 0)
        {
            executablePrice = 0;
            rejectReason = "limit price must be > 0";
            return false;
        }

        if (request.Side == TradeDirection.Buy)
        {
            if (bestBuy > 0 && limit >= bestBuy)
            {
                executablePrice = bestBuy;
                rejectReason = null;
                return true;
            }

            executablePrice = 0;
            rejectReason = "buy limit does not cross ask";
            return false;
        }

        if (bestSell > 0 && limit <= bestSell)
        {
            executablePrice = bestSell;
            rejectReason = null;
            return true;
        }

        executablePrice = 0;
        rejectReason = "sell limit does not cross bid";
        return false;
    }

    private static bool TryComputeExecutablePrice(
        WorkingOrderState state,
        LiveOptionQuote quote,
        out double requestedPrice,
        out double executablePrice,
        out string? rejectReason)
    {
        return TryComputeExecutablePrice(
            new TradingOrderRequest(
                Symbol: state.Symbol,
                Side: state.Side,
                Quantity: state.RemainingQuantity,
                Type: state.Type,
                LimitPrice: state.LimitPrice,
                ClientOrderId: state.ClientOrderId,
                MaxRetries: state.MaxRetries,
                AllowPartialFill: state.AllowPartialFill),
            quote,
            out requestedPrice,
            out executablePrice,
            out rejectReason);
    }

    private static double EstimateFillRatio(TradingOrderRequest request, LiveOptionQuote quote)
    {
        double liquidityDepth =
            Math.Max(1, quote.Volume24h * 0.030 + quote.OpenInterest * 0.018 + quote.Turnover24h * 0.00005);
        double pressure = request.Quantity / liquidityDepth;
        double baseRatio = MathUtils.Clamp(1.0 - pressure * 0.30, 0.08, 1.0);

        if (request.Type == OrderType.Limit)
            baseRatio = MathUtils.Clamp(baseRatio * 0.88, 0.05, 1.0);

        return baseRatio;
    }

    private static (double FillQuantity, double FillPrice, double Fees, double SlippagePct, string? Reason) SimulateFillAttempt(
        WorkingOrderState state,
        LiveOptionQuote quote,
        double executablePrice,
        double requestedPrice,
        int attempt)
    {
        if (executablePrice <= 0 || state.RemainingQuantity <= 1e-12)
            return (0, 0, 0, 0, "invalid-price-or-qty");

        var request = new TradingOrderRequest(
            Symbol: state.Symbol,
            Side: state.Side,
            Quantity: state.RemainingQuantity,
            Type: state.Type,
            LimitPrice: state.LimitPrice,
            ClientOrderId: state.ClientOrderId,
            MaxRetries: state.MaxRetries,
            AllowPartialFill: state.AllowPartialFill);

        double ratio = EstimateFillRatio(request, quote);
        double retryBoost = 1.0 + Math.Min(0.5, attempt * 0.08);
        ratio = MathUtils.Clamp(ratio * retryBoost, 0.05, 1.0);
        double fillQty = Math.Min(state.RemainingQuantity, state.RemainingQuantity * ratio);

        if (fillQty <= 1e-8)
            return (0, 0, 0, 0, "insufficient-liquidity");

        double spreadPct = 0;
        double mid = FirstPositive(quote.Mid, quote.Mark, quote.Bid, quote.Ask);
        if (quote.Bid > 0 && quote.Ask > 0 && mid > 0)
            spreadPct = (quote.Ask - quote.Bid) / mid;

        double impactPct = MathUtils.Clamp((1 - ratio) * 0.02 + spreadPct * 0.15, 0, 0.25);
        double impactedPrice = state.Side == TradeDirection.Buy
            ? executablePrice * (1 + impactPct)
            : executablePrice * (1 - impactPct);

        double slippagePct = ComputeAggregateSlippagePct(state.Side, requestedPrice, impactedPrice);
        double feeRate = state.Type == OrderType.Limit ? 0.0002 : 0.0004;
        double fees = impactedPrice * fillQty * feeRate;

        return (fillQty, impactedPrice, fees, slippagePct, null);
    }

    private async Task<IReadOnlyList<TradingPosition>> SimulateProjectedPositionsAsync(
        IReadOnlyList<PositionState> baseStates,
        LiveOptionQuote quote,
        TradingOrderRequest request,
        double fillPrice,
        double simulatedFillQty,
        CancellationToken ct)
    {
        var projected = baseStates.Select(s => s.Clone()).ToList();
        double signedQty = request.Side == TradeDirection.Buy ? simulatedFillQty : -simulatedFillQty;
        var state = projected.FirstOrDefault(p => p.Symbol.Equals(request.Symbol, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            state = new PositionState
            {
                Symbol = request.Symbol,
                Asset = quote.Asset,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            projected.Add(state);
        }

        ApplyFill(state, signedQty, fillPrice, fees: 0);
        var quotesBySymbol = await LoadQuotesBySymbolAsync(projected.Select(s => s.Asset).Distinct(), ct);

        return projected
            .Where(s => Math.Abs(s.NetQuantity) > 1e-12)
            .Select(stateRow =>
            {
                quotesBySymbol.TryGetValue(stateRow.Symbol, out var q);
                double mark = EffectiveMark(q, stateRow.AvgEntryPrice);
                double notional = Math.Abs(mark * stateRow.NetQuantity);
                GreeksResult greeks = q is null
                    ? GreeksResult.Zero
                    : new GreeksResult(
                        Delta: q.Delta * stateRow.NetQuantity,
                        Gamma: q.Gamma * stateRow.NetQuantity,
                        Vega: q.Vega * stateRow.NetQuantity,
                        Theta: q.Theta * stateRow.NetQuantity,
                        Vanna: 0,
                        Volga: 0,
                        Charm: 0,
                        Rho: 0);

                var margin = EstimatePositionMargins(notional, greeks);

                return new TradingPosition(
                    Symbol: stateRow.Symbol,
                    Asset: stateRow.Asset,
                    NetQuantity: stateRow.NetQuantity,
                    AvgEntryPrice: stateRow.AvgEntryPrice,
                    MarkPrice: mark,
                    Notional: notional,
                    UnrealizedPnl: (mark - stateRow.AvgEntryPrice) * stateRow.NetQuantity,
                    RealizedPnl: stateRow.RealizedPnl,
                    Greeks: greeks,
                    UpdatedAt: stateRow.UpdatedAt,
                    InitialMarginRequirement: margin.Initial,
                    MaintenanceMarginRequirement: margin.Maintenance,
                    MarginMode: "Portfolio");
            })
            .ToList();
    }

    private PortfolioRiskSnapshot BuildProjectedRiskSnapshot(OrderSimulationResult simulation)
    {
        return new PortfolioRiskSnapshot(
            GrossNotional: simulation.ProjectedGrossNotional,
            NetDelta: simulation.ProjectedNetDelta,
            NetGamma: simulation.ProjectedNetGamma,
            NetVega: simulation.ProjectedNetVega,
            NetTheta: simulation.ProjectedNetTheta,
            UnrealizedPnl: 0,
            RealizedPnl: 0,
            Breached: false,
            Flags: [],
            Timestamp: DateTimeOffset.UtcNow,
            InitialMargin: simulation.ProjectedInitialMargin,
            MaintenanceMargin: simulation.ProjectedMaintenanceMargin,
            Equity: simulation.ProjectedEquity,
            AvailableMargin: simulation.ProjectedAvailableMargin,
            MarginRatio: simulation.ProjectedMaintenanceMargin > 0 ? simulation.ProjectedEquity / simulation.ProjectedMaintenanceMargin : 999,
            LiquidationTriggered: false,
            LargestPositionNotional: simulation.ProjectedGrossNotional * simulation.ProjectedConcentrationPct,
            LargestPositionConcentrationPct: simulation.ProjectedConcentrationPct,
            KillSwitchActive: false,
            DailyPnl: 0);
    }

    private async Task MaybeRunLiquidationAsync(CancellationToken ct)
    {
        var positions = await GetPositionsAsync(ct);
        var risk = BuildRiskSnapshot(positions);

        if (positions.Count == 0)
            return;

        double trigger = risk.MaintenanceMargin;
        if (risk.Equity >= trigger)
            return;

        _monitoring.PublishAlert(
            "risk",
            NotificationSeverity.Critical,
            $"Liquidation triggered. Equity={risk.Equity:F0}, Maintenance={risk.MaintenanceMargin:F0}");
        AddNotification(
            NotificationSeverity.Critical,
            "liquidation",
            $"Liquidation triggered. Equity={risk.Equity:F0} < maintenance margin={risk.MaintenanceMargin:F0}.");

        await SetKillSwitchAsync(new KillSwitchRequest(true, "auto-liquidation", "risk-engine"), ct);

        var quotesBySymbol = await LoadQuotesBySymbolAsync(positions.Select(p => p.Asset).Distinct(), ct);
        foreach (var position in positions.OrderByDescending(p => p.Notional))
        {
            if (!quotesBySymbol.TryGetValue(position.Symbol, out var quote))
                continue;

            double closeQty = Math.Abs(position.NetQuantity);
            if (closeQty <= 1e-9) continue;

            TradeDirection side = position.NetQuantity > 0 ? TradeDirection.Sell : TradeDirection.Buy;
            double rawPrice = side == TradeDirection.Sell
                ? FirstPositive(quote.Bid, quote.Mid, quote.Mark, quote.Ask)
                : FirstPositive(quote.Ask, quote.Mid, quote.Mark, quote.Bid);
            if (rawPrice <= 0) continue;

            double liquidationPrice = side == TradeDirection.Sell ? rawPrice * 0.99 : rawPrice * 1.01;
            double feeRate = Limits.TakerFeeRate;
            double fees = liquidationPrice * closeQty * feeRate;
            double signedQty = side == TradeDirection.Buy ? closeQty : -closeQty;
            ApplyFillToPosition(position.Symbol, position.Asset, signedQty, liquidationPrice, fees);

            var report = new TradingOrderReport(
                OrderId: $"LIQ-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                Symbol: position.Symbol,
                Side: side,
                Quantity: closeQty,
                Type: OrderType.Market,
                Status: OrderStatus.Filled,
                RequestedPrice: rawPrice,
                FillPrice: liquidationPrice,
                Fees: fees,
                RejectReason: null,
                ClientOrderId: "AUTO-LIQUIDATION",
                Timestamp: DateTimeOffset.UtcNow,
                FilledQuantity: closeQty,
                RemainingQuantity: 0,
                AvgFillPrice: liquidationPrice,
                SlippagePct: ComputeAggregateSlippagePct(side, rawPrice, liquidationPrice),
                EffectiveFeeRate: feeRate,
                ExecutionQualityScore: 20,
                RetryCount: 0,
                IdempotentReplay: false,
                StateTrace: ["Liquidation", "Filled"],
                Fills:
                [
                    new TradingOrderFillExecution(
                        FillId: $"FIL-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                        Attempt: 1,
                        Quantity: closeQty,
                        Price: liquidationPrice,
                        Fees: fees,
                        SlippagePct: ComputeAggregateSlippagePct(side, rawPrice, liquidationPrice),
                        Timestamp: DateTimeOffset.UtcNow,
                        Venue: quote.Venue)
                ],
                Notional: liquidationPrice * closeQty);

            PersistOrder(report);
            _monitoring.IncrementCounter("risk.liquidations.total");

            var refreshedRisk = await GetRiskAsync(ct);
            double releaseTarget = refreshedRisk.MaintenanceMargin * (1 + Limits.LiquidationBufferPct);
            if (refreshedRisk.Equity >= releaseTarget)
                break;
        }
    }

    private void ApplyFillToPosition(string symbol, string asset, double signedQty, double fillPrice, double fees)
    {
        lock (_gate)
        {
            if (!_positions.TryGetValue(symbol, out var state))
            {
                state = new PositionState
                {
                    Symbol = symbol,
                    Asset = asset,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _positions[symbol] = state;
            }

            ApplyFill(state, signedQty, fillPrice, fees);
            state.UpdatedAt = DateTimeOffset.UtcNow;
            if (Math.Abs(state.NetQuantity) <= 1e-12)
                _positions.Remove(symbol);
        }
    }

    private TradingOrderReport PersistOrder(TradingOrderReport report)
    {
        lock (_gate)
        {
            _ordersById[report.OrderId] = report;
            if (!string.IsNullOrWhiteSpace(report.ClientOrderId))
                _idempotencyByClientOrderId[report.ClientOrderId] = report.OrderId;

            int idx = _orders.FindIndex(o => o.OrderId.Equals(report.OrderId, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _orders[idx] = report;
            else
                _orders.Add(report);

            if (_orders.Count > MaxStoredOrders)
                _orders.RemoveRange(0, _orders.Count - MaxStoredOrders);
        }

        return report;
    }

    private TradingOrderReport BuildRejectedOrder(TradingOrderRequest request, string? reason, double requestedPrice = 0)
    {
        var report = new TradingOrderReport(
            OrderId: $"ORD-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            Symbol: request.Symbol,
            Side: request.Side,
            Quantity: request.Quantity,
            Type: request.Type,
            Status: OrderStatus.Rejected,
            RequestedPrice: requestedPrice,
            FillPrice: 0,
            Fees: 0,
            RejectReason: reason,
            ClientOrderId: request.ClientOrderId,
            Timestamp: DateTimeOffset.UtcNow,
            FilledQuantity: 0,
            RemainingQuantity: request.Quantity,
            AvgFillPrice: 0,
            SlippagePct: 0,
            EffectiveFeeRate: 0,
            ExecutionQualityScore: 0,
            RetryCount: Math.Clamp(request.MaxRetries, 0, 8),
            IdempotentReplay: false,
            StateTrace: ["Received", "Rejected"],
            Fills: [],
            Notional: 0);

        AddNotification(NotificationSeverity.Warning, "order-reject", $"Order rejected {request.Symbol}: {reason}");
        RecordOrderMonitoring(report);
        return report;
    }

    private static void ApplyFill(PositionState state, double signedQty, double fillPrice, double fees)
    {
        if (Math.Abs(signedQty) <= 1e-12) return;

        double oldQty = state.NetQuantity;
        if (Math.Abs(oldQty) <= 1e-12)
        {
            state.NetQuantity = signedQty;
            state.AvgEntryPrice = fillPrice;
            state.RealizedPnl -= fees;
            return;
        }

        if (Math.Sign(oldQty) == Math.Sign(signedQty))
        {
            double oldAbs = Math.Abs(oldQty);
            double addAbs = Math.Abs(signedQty);
            double totalAbs = oldAbs + addAbs;
            state.AvgEntryPrice = totalAbs > 0
                ? (oldAbs * state.AvgEntryPrice + addAbs * fillPrice) / totalAbs
                : 0;
            state.NetQuantity += signedQty;
            state.RealizedPnl -= fees;
            return;
        }

        double closeAbs = Math.Min(Math.Abs(oldQty), Math.Abs(signedQty));
        double realizedFromClose = oldQty > 0
            ? closeAbs * (fillPrice - state.AvgEntryPrice)
            : closeAbs * (state.AvgEntryPrice - fillPrice);

        double remaining = oldQty + signedQty;
        if (Math.Abs(remaining) <= 1e-12)
        {
            state.NetQuantity = 0;
            state.AvgEntryPrice = 0;
        }
        else if (Math.Sign(remaining) == Math.Sign(oldQty))
        {
            state.NetQuantity = remaining;
            // Average remains unchanged for partially closed positions.
        }
        else
        {
            state.NetQuantity = remaining;
            state.AvgEntryPrice = fillPrice;
        }

        state.RealizedPnl += realizedFromClose - fees;
    }

    private async Task<LiveOptionQuote?> TryFindQuoteAsync(string symbol, CancellationToken ct)
    {
        string asset = ExtractAsset(symbol);
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        return chain.FirstOrDefault(q => q.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractAsset(string symbol)
    {
        string[] parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("invalid option symbol");
        return parts[0].Trim().ToUpperInvariant();
    }

    private List<PositionState> SnapshotStates()
    {
        lock (_gate)
        {
            return _positions.Values
                .Select(p => p.Clone())
                .ToList();
        }
    }

    private async Task<Dictionary<string, LiveOptionQuote>> LoadQuotesBySymbolAsync(
        IEnumerable<string> assets,
        CancellationToken ct)
    {
        var normalizedAssets = assets
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var tasks = normalizedAssets.Select(asset => _marketData.GetOptionChainAsync(asset, ct));
        var chains = await Task.WhenAll(tasks);

        var map = new Dictionary<string, LiveOptionQuote>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in chains)
        {
            foreach (var quote in chain)
                map[quote.Symbol] = quote;
        }

        return map;
    }

    private PortfolioRiskSnapshot BuildRiskSnapshot(IReadOnlyList<TradingPosition> positions)
    {
        double gross = positions.Sum(p => p.Notional);
        double netDelta = positions.Sum(p => p.Greeks.Delta);
        double netGamma = positions.Sum(p => p.Greeks.Gamma);
        double netVega = positions.Sum(p => p.Greeks.Vega);
        double netTheta = positions.Sum(p => p.Greeks.Theta);
        double unrealized = positions.Sum(p => p.UnrealizedPnl);
        double realized = positions.Sum(p => p.RealizedPnl);

        double largestPosition = positions.Select(p => p.Notional).DefaultIfEmpty(0).Max();
        double concentration = gross > 0 ? largestPosition / gross : 0;
        double maxAssetGross = positions
            .GroupBy(p => p.Asset)
            .Select(g => g.Sum(x => x.Notional))
            .DefaultIfEmpty(0)
            .Max();

        var margin = EstimatePortfolioMargins(gross, netDelta, netGamma, netVega, netTheta);

        double equity;
        bool killSwitchActive;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        lock (_gate)
        {
            if (_account.TradingDay != today)
            {
                _account.TradingDay = today;
                _account.DayStartEquity = _account.StartingEquity + realized + unrealized;
            }

            killSwitchActive = _killSwitch.IsActive;
            equity = _account.StartingEquity + realized + unrealized;
        }

        double availableMargin = equity - margin.Initial;
        double marginRatio = margin.Maintenance > 0 ? equity / margin.Maintenance : 999;
        double dailyPnl;
        lock (_gate)
        {
            dailyPnl = equity - _account.DayStartEquity;
        }

        var flags = new List<string>();
        if (gross > Limits.MaxGrossNotional) flags.Add("gross-notional");
        if (Math.Abs(netDelta) > Limits.MaxNetDelta) flags.Add("net-delta");
        if (Math.Abs(netGamma) > Limits.MaxNetGamma) flags.Add("net-gamma");
        if (Math.Abs(netVega) > Limits.MaxNetVega) flags.Add("net-vega");
        if (Math.Abs(netTheta) > Limits.MaxNetThetaAbs) flags.Add("net-theta");
        if (gross >= 200_000 && concentration > Limits.MaxConcentrationPct) flags.Add("concentration");
        if (maxAssetGross > Limits.MaxAssetGrossNotional) flags.Add("asset-gross-notional");
        if (-dailyPnl > Limits.MaxDailyLoss) flags.Add("daily-loss");
        if (margin.Initial > equity) flags.Add("initial-margin");
        if (equity < margin.Maintenance) flags.Add("maintenance-margin");
        if (killSwitchActive) flags.Add("kill-switch");

        bool breached = flags.Count > 0;
        bool liquidationTriggered = equity < margin.Maintenance;

        if (breached)
            _monitoring.IncrementCounter("risk.breach.total");

        return new PortfolioRiskSnapshot(
            GrossNotional: gross,
            NetDelta: netDelta,
            NetGamma: netGamma,
            NetVega: netVega,
            NetTheta: netTheta,
            UnrealizedPnl: unrealized,
            RealizedPnl: realized,
            Breached: breached,
            Flags: flags,
            Timestamp: DateTimeOffset.UtcNow,
            InitialMargin: margin.Initial,
            MaintenanceMargin: margin.Maintenance,
            Equity: equity,
            AvailableMargin: availableMargin,
            MarginRatio: marginRatio,
            LiquidationTriggered: liquidationTriggered,
            LargestPositionNotional: largestPosition,
            LargestPositionConcentrationPct: concentration,
            KillSwitchActive: killSwitchActive,
            DailyPnl: dailyPnl);
    }

    private (double Initial, double Maintenance) EstimatePositionMargins(double notional, GreeksResult greeks)
    {
        double initial =
            notional * 0.14 +
            Math.Abs(greeks.Gamma) * Limits.MarginAddOnPerGamma +
            Math.Abs(greeks.Vega) * Limits.MarginAddOnPerVega +
            Math.Abs(greeks.Delta) * 1.4;

        initial = Math.Max(50, initial);
        double maintenance = Math.Max(35, initial * 0.72);
        return (initial, maintenance);
    }

    private (double Initial, double Maintenance) EstimatePortfolioMargins(
        double gross,
        double netDelta,
        double netGamma,
        double netVega,
        double netTheta)
    {
        double initial =
            gross * 0.12 +
            Math.Abs(netDelta) * 1.4 +
            Math.Abs(netGamma) * Limits.MarginAddOnPerGamma +
            Math.Abs(netVega) * Limits.MarginAddOnPerVega +
            Math.Abs(netTheta) * 0.02;

        initial = Math.Max(100, initial);
        double maintenance = Math.Max(75, initial * 0.72);
        return (initial, maintenance);
    }

    private static double ComputeAggregateSlippagePct(TradeDirection side, double referencePrice, double executedPrice)
    {
        if (referencePrice <= 0 || executedPrice <= 0) return 0;
        return side == TradeDirection.Buy
            ? Math.Max(0, (executedPrice - referencePrice) / referencePrice)
            : Math.Max(0, (referencePrice - executedPrice) / referencePrice);
    }

    private static double ComputeExecutionQualityScore(
        double slippagePct,
        double feeRate,
        double filledQty,
        double requestedQty,
        int retryCount)
    {
        double fillRatio = requestedQty > 0 ? MathUtils.Clamp(filledQty / requestedQty, 0, 1) : 0;
        double slippagePenalty = MathUtils.Clamp(slippagePct * 700, 0, 70);
        double feePenalty = MathUtils.Clamp(feeRate * 10000, 0, 18);
        double retryPenalty = MathUtils.Clamp(retryCount * 4, 0, 18);
        double fillBonus = fillRatio * 18;

        return MathUtils.Clamp(100 - slippagePenalty - feePenalty - retryPenalty + fillBonus, 1, 99);
    }

    private static IReadOnlyList<StressScenario> NormalizeScenarios(StressTestRequest? request)
    {
        if (request?.Scenarios is { Count: > 0 })
        {
            return request.Scenarios
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .Select(s => new StressScenario(
                    Name: s.Name.Trim(),
                    UnderlyingShockPct: MathUtils.Clamp(s.UnderlyingShockPct, -0.9, 1.5),
                    IvShockPct: MathUtils.Clamp(s.IvShockPct, -0.95, 2.0),
                    DaysForward: Math.Clamp(s.DaysForward, 0, 365)))
                .ToList();
        }

        return
        [
            new StressScenario("Spot -10%", -0.10, 0),
            new StressScenario("Spot +10%", 0.10, 0),
            new StressScenario("Vol +20%", 0, 0.20),
            new StressScenario("Spot -15% / Vol +25%", -0.15, 0.25),
            new StressScenario("Spot +15% / Vol -20%", 0.15, -0.20)
        ];
    }

    private void AddNotification(NotificationSeverity severity, string category, string message)
    {
        lock (_gate)
        {
            _notifications.Add(new TradingNotification(
                Id: $"NTF-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                Severity: severity,
                Category: category,
                Message: message,
                Timestamp: DateTimeOffset.UtcNow,
                Acknowledged: false));

            if (_notifications.Count > MaxStoredNotifications)
                _notifications.RemoveRange(0, _notifications.Count - MaxStoredNotifications);
        }
    }

    private static double EffectiveMark(LiveOptionQuote? quote, double fallback)
    {
        if (quote is null) return fallback;
        return FirstPositive(quote.Mark, quote.Mid, quote.Bid, quote.Ask, fallback);
    }

    private static double FirstPositive(params double[] values)
    {
        foreach (double value in values)
        {
            if (double.IsFinite(value) && value > 0) return value;
        }
        return 0;
    }
}
