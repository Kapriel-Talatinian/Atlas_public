using System.Globalization;
using System.Text.Json;
using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface IPaperTradingService
{
    RiskLimitConfig Limits { get; }
    MarginRulebook MarginRules { get; }
    Task<TradingOrderReport> PlaceOrderAsync(TradingOrderRequest request, CancellationToken ct = default);
    Task<PreTradePreviewResult> PreviewOrderAsync(TradingOrderRequest request, CancellationToken ct = default);
    Task<TradingOrderReport> CancelOrderAsync(CancelOrderRequest request, CancellationToken ct = default);
    Task<OrderReplaceResult> ReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken ct = default);
    Task<OmsReconciliationReport> ReconcileOrdersAsync(int limit = 400, CancellationToken ct = default);
    Task<AlgoExecutionReport> ExecuteAlgoOrderAsync(AlgoExecutionRequest request, CancellationToken ct = default);
    Task<HedgeSuggestionResponse> GetHedgeSuggestionAsync(HedgeSuggestionRequest request, CancellationToken ct = default);
    Task<AutoHedgeReport> RunAutoHedgeAsync(AutoHedgeRequest request, CancellationToken ct = default);
    Task<PortfolioOptimizationResponse> OptimizePortfolioAsync(PortfolioOptimizationRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TradingOrderReport>> GetOrdersAsync(int limit = 200, CancellationToken ct = default);
    Task<IReadOnlyList<TradingPosition>> GetPositionsAsync(CancellationToken ct = default);
    Task<PortfolioRiskSnapshot> GetRiskAsync(CancellationToken ct = default);
    Task<TradingBookSnapshot> GetBookAsync(int orderLimit = 150, CancellationToken ct = default);
    Task<StressTestResult> RunStressTestAsync(StressTestRequest? request, CancellationToken ct = default);
    Task<KillSwitchState> GetKillSwitchAsync(CancellationToken ct = default);
    Task<KillSwitchState> SetKillSwitchAsync(KillSwitchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TradingOrderReport>> RetryOpenOrdersAsync(int maxOrders = 25, CancellationToken ct = default);
    Task<IReadOnlyList<TradingNotification>> GetNotificationsAsync(int limit = 120, CancellationToken ct = default);
    Task<TradingHistorySnapshot> GetHistoryAsync(int orderLimit = 250, int positionLimit = 250, int riskLimit = 250, int auditLimit = 250, CancellationToken ct = default);
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
        public bool IsCancelled { get; set; }
        public string? CancelReason { get; set; }
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
    private readonly ITradingPersistenceService _persistence;
    private readonly object _positionPersistGate = new();
    private DateTimeOffset _lastPositionPersistAt = DateTimeOffset.MinValue;

    private readonly AccountState _account;
    private KillSwitchState _killSwitch;

    private const int MaxStoredOrders = 5000;
    private const int MaxStoredNotifications = 800;
    private const int FingerprintWindowSeconds = 2;

    public PaperTradingService(
        IOptionsMarketDataService marketData,
        ILogger<PaperTradingService> logger,
        ISystemMonitoringService monitoring,
        ITradingPersistenceService persistence)
    {
        _marketData = marketData;
        _logger = logger;
        _monitoring = monitoring;
        _persistence = persistence;

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

        _persistence.AppendAuditEvent("startup", "Paper trading service initialized.");
    }

    public RiskLimitConfig Limits { get; } = new();
    public MarginRulebook MarginRules { get; } = new(
        PortfolioInitialRate: 0.12,
        PortfolioMaintenanceRate: 0.72,
        PositionFloorInitial: 50,
        PositionFloorMaintenance: 35,
        GammaAddOn: 250,
        VegaAddOn: 3.0,
        ThetaAddOnRate: 0.02,
        LiquidationBufferPct: 0.08,
        Description: "Initial margin = gross*12% + |delta|*1.4 + |gamma|*250 + |vega|*3 + |theta|*0.02",
        Timestamp: DateTimeOffset.UtcNow);

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
            Symbol = quote.Symbol,
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

    public Task<TradingOrderReport> CancelOrderAsync(CancelOrderRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (request is null || string.IsNullOrWhiteSpace(request.OrderId))
            return Task.FromResult(BuildRejectedOrder(new TradingOrderRequest("-", TradeDirection.Buy, 0), "cancel-order-id-required"));

        TradingOrderReport? existing;
        WorkingOrderState? openState;
        lock (_gate)
        {
            _ordersById.TryGetValue(request.OrderId.Trim(), out existing);
            _openOrders.TryGetValue(request.OrderId.Trim(), out openState);
        }

        if (openState is null)
        {
            if (existing is not null)
                return Task.FromResult(existing);

            return Task.FromResult(BuildRejectedOrder(new TradingOrderRequest(request.OrderId, TradeDirection.Buy, 0), "unknown-order-id"));
        }

        openState.IsCancelled = true;
        openState.CancelReason = string.IsNullOrWhiteSpace(request.Reason) ? "manual-cancel" : request.Reason.Trim();
        openState.RemainingQuantity = 0;
        openState.StateTrace.Add("Cancelled");
        openState.UpdatedAt = DateTimeOffset.UtcNow;

        TradingOrderReport report = ToOrderReport(openState);
        lock (_gate)
        {
            _openOrders.Remove(openState.OrderId);
        }

        AddNotification(NotificationSeverity.Info, "order-cancel", $"Order {openState.OrderId} cancelled by {request.UpdatedBy}");
        PersistOrder(report);
        RecordOrderMonitoring(report);
        _persistence.AppendAuditEvent("oms", $"Order cancelled {openState.OrderId}", JsonSerializer.Serialize(request));
        return Task.FromResult(report);
    }

    public async Task<OrderReplaceResult> ReplaceOrderAsync(ReplaceOrderRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OrderId))
            throw new ArgumentException("orderId is required");

        TradingOrderReport cancelled = await CancelOrderAsync(
            new CancelOrderRequest(request.OrderId, request.Reason, request.UpdatedBy),
            ct);

        if (cancelled.Status is not OrderStatus.Cancelled and not OrderStatus.PartiallyFilled and not OrderStatus.Accepted)
            throw new InvalidOperationException($"cannot replace order in status {cancelled.Status}");

        double baseQty = Math.Max(cancelled.RemainingQuantity, 0);
        if (baseQty <= 1e-12)
            baseQty = Math.Max(cancelled.Quantity - cancelled.FilledQuantity, 0);
        if (baseQty <= 1e-12)
            throw new InvalidOperationException("no remaining quantity to replace");

        var replacement = new TradingOrderRequest(
            Symbol: cancelled.Symbol,
            Side: cancelled.Side,
            Quantity: request.Quantity ?? baseQty,
            Type: request.Type ?? cancelled.Type,
            LimitPrice: (request.Type ?? cancelled.Type) == OrderType.Limit
                ? (request.LimitPrice ?? (cancelled.Type == OrderType.Limit ? cancelled.RequestedPrice : null))
                : null,
            ClientOrderId: request.OrderId + "-R-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            MaxRetries: request.MaxRetries ?? Math.Max(cancelled.RetryCount, 1),
            AllowPartialFill: request.AllowPartialFill ?? true,
            MaxSlippagePct: request.MaxSlippagePct > 0 ? request.MaxSlippagePct : null);

        TradingOrderReport created = await PlaceOrderAsync(replacement, ct);
        var result = new OrderReplaceResult(
            CancelledOrder: cancelled,
            NewOrder: created,
            Timestamp: DateTimeOffset.UtcNow,
            Reason: request.Reason);

        _persistence.AppendAuditEvent("oms", $"Order replaced {request.OrderId} -> {created.OrderId}", JsonSerializer.Serialize(result));
        return result;
    }

    public Task<OmsReconciliationReport> ReconcileOrdersAsync(int limit = 400, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        int safeLimit = Math.Clamp(limit, 10, 5000);

        List<TradingOrderReport> orders;
        List<TradingOrderReport> open;
        lock (_gate)
        {
            orders = _orders
                .OrderByDescending(o => o.Timestamp)
                .Take(safeLimit)
                .ToList();
            open = _openOrders.Values.Select(ToOrderReport).ToList();
        }

        var issues = new List<OmsReconciliationIssue>();
        var duplicates = orders
            .GroupBy(o => o.OrderId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var duplicate in duplicates)
        {
            issues.Add(new OmsReconciliationIssue(
                Severity: "critical",
                Category: "duplicate-order-id",
                OrderId: duplicate.Key,
                Message: $"Duplicate order id detected ({duplicate.Count()})",
                Timestamp: DateTimeOffset.UtcNow));
        }

        foreach (var order in orders)
        {
            double fillQty = order.Fills?.Sum(f => f.Quantity) ?? 0;
            if (Math.Abs(fillQty - order.FilledQuantity) > 1e-6)
            {
                issues.Add(new OmsReconciliationIssue(
                    Severity: "critical",
                    Category: "filled-quantity-mismatch",
                    OrderId: order.OrderId,
                    Message: $"filledQuantity={order.FilledQuantity:F6} but sum(fills)={fillQty:F6}",
                    Timestamp: DateTimeOffset.UtcNow));
            }

            double feeFromFills = order.Fills?.Sum(f => f.Fees) ?? 0;
            if (Math.Abs(feeFromFills - order.Fees) > 1e-6)
            {
                issues.Add(new OmsReconciliationIssue(
                    Severity: "warning",
                    Category: "fees-mismatch",
                    OrderId: order.OrderId,
                    Message: $"fees={order.Fees:F6} but sum(fillFees)={feeFromFills:F6}",
                    Timestamp: DateTimeOffset.UtcNow));
            }
        }

        foreach (var openOrder in open)
        {
            if (openOrder.Status is OrderStatus.Filled or OrderStatus.Rejected or OrderStatus.Cancelled or OrderStatus.Expired)
            {
                issues.Add(new OmsReconciliationIssue(
                    Severity: "critical",
                    Category: "terminal-open-order",
                    OrderId: openOrder.OrderId,
                    Message: $"Open orders set contains terminal status {openOrder.Status}",
                    Timestamp: DateTimeOffset.UtcNow));
            }
        }

        int critical = issues.Count(i => i.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase));
        bool healthy = issues.Count == 0;
        var report = new OmsReconciliationReport(
            TotalOrdersChecked: orders.Count,
            OpenOrdersChecked: open.Count,
            IssueCount: issues.Count,
            CriticalCount: critical,
            Healthy: healthy,
            Issues: issues,
            Timestamp: DateTimeOffset.UtcNow);

        if (!healthy)
        {
            _monitoring.PublishAlert("oms", NotificationSeverity.Warning, $"OMS reconciliation found {issues.Count} issues ({critical} critical)");
            _persistence.AppendAuditEvent("oms-reconciliation", "issues-detected", JsonSerializer.Serialize(report));
        }

        return Task.FromResult(report);
    }

    public async Task<AlgoExecutionReport> ExecuteAlgoOrderAsync(AlgoExecutionRequest request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Symbol))
            throw new ArgumentException("symbol is required");
        if (!double.IsFinite(request.Quantity) || request.Quantity <= 0)
            throw new ArgumentException("quantity must be > 0");

        string algoId = $"ALG-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}";
        int slices = Math.Clamp(request.Slices, 1, 64);
        double remaining = request.Quantity;
        double[] weights = BuildSliceWeights(request.Style, slices);
        var children = new List<AlgoExecutionChild>(slices);
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var routeQty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var routeNotional = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var routeSlippageNotional = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var routeQualityQty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        LiveOptionQuote? refQuote = await TryFindQuoteAsync(request.Symbol, ct);
        double refVolume = Math.Max(1, refQuote?.Volume24h ?? 0);
        bool enforceParticipation = request.Style == AlgoExecutionStyle.Pov;
        double maxParticipation = MathUtils.Clamp(request.MaxParticipationPct, 0.01, 1.0);

        for (int i = 0; i < slices; i++)
        {
            ct.ThrowIfCancellationRequested();
            int left = slices - i;
            double plannedQty = left <= 1
                ? remaining
                : MathUtils.Clamp(request.Quantity * weights[i], 0.0001, remaining);

            if (enforceParticipation)
            {
                // POV caps child quantity by expected turnover participation.
                double expectedSliceFlow = Math.Max(0.5, refVolume / Math.Max(slices, 1));
                double participationCap = expectedSliceFlow * maxParticipation;
                plannedQty = Math.Min(plannedQty, participationCap);
            }

            double sliceQty = MathUtils.Clamp(plannedQty, 0.0001, remaining);
            if (sliceQty <= 1e-12)
                break;

            string venue = await EstimateBestVenueAsync(request.Symbol, sliceQty, request.Side, ct);
            var childRequest = new TradingOrderRequest(
                Symbol: request.Symbol,
                Side: request.Side,
                Quantity: sliceQty,
                Type: request.LimitPrice.HasValue ? OrderType.Limit : OrderType.Market,
                LimitPrice: request.LimitPrice,
                ClientOrderId: (request.ClientOrderId ?? algoId) + $"-S{i + 1:D2}",
                MaxRetries: Math.Clamp(request.MaxRetriesPerSlice, 0, 8),
                AllowPartialFill: request.AllowPartialFill,
                MaxSlippagePct: null);

            TradingOrderReport report = await PlaceOrderAsync(childRequest, ct);
            children.Add(new AlgoExecutionChild(
                Slice: i + 1,
                ScheduledAt: start.AddSeconds(i * Math.Max(1, request.IntervalSeconds)),
                Venue: venue,
                RequestedQuantity: sliceQty,
                Report: report,
                ParticipationPct: refVolume > 0 ? MathUtils.Clamp(report.FilledQuantity / refVolume, 0, 1) : 0,
                SliceWeight: weights[i]));

            remaining = Math.Max(0, remaining - report.FilledQuantity);
            routeQty[venue] = (routeQty.TryGetValue(venue, out var q) ? q : 0) + report.FilledQuantity;
            routeNotional[venue] = (routeNotional.TryGetValue(venue, out var n) ? n : 0) + report.Notional;
            routeSlippageNotional[venue] = (routeSlippageNotional.TryGetValue(venue, out var sn) ? sn : 0) + (report.SlippagePct * report.Notional);
            routeQualityQty[venue] = (routeQualityQty.TryGetValue(venue, out var qq) ? qq : 0) + (report.ExecutionQualityScore * Math.Max(report.FilledQuantity, 0));
            if (remaining <= 1e-12)
                break;

            if (i < slices - 1 && request.IntervalSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, request.IntervalSeconds)), ct);
        }

        double filled = children.Sum(c => c.Report.FilledQuantity);
        double totalFees = children.Sum(c => c.Report.Fees);
        double notional = children.Sum(c => c.Report.Notional);
        double avgFill = filled > 0 ? notional / filled : 0;
        double weightedSlippage = notional > 0
            ? children.Sum(c => c.Report.SlippagePct * c.Report.Notional) / notional
            : 0;
        double quality = children.Count > 0
            ? children.Average(c => c.Report.ExecutionQualityScore)
            : 0;

        var routing = routeNotional.Keys
            .OrderByDescending(venue => routeNotional[venue])
            .Select(venue =>
            {
                double qty = routeQty.GetValueOrDefault(venue, 0);
                double venueNotional = routeNotional.GetValueOrDefault(venue, 0);
                double slip = venueNotional > 0
                    ? routeSlippageNotional.GetValueOrDefault(venue, 0) / venueNotional
                    : 0;
                double venueQuality = qty > 0
                    ? routeQualityQty.GetValueOrDefault(venue, 0) / qty
                    : 0;
                return new AlgoRouteAllocation(
                    Venue: venue,
                    FilledQuantity: qty,
                    Notional: venueNotional,
                    AvgSlippagePct: slip,
                    AvgQualityScore: venueQuality);
            })
            .ToList();

        string notes = request.Style switch
        {
            AlgoExecutionStyle.Twap => "TWAP schedule with equalized time slices and routing by source health.",
            AlgoExecutionStyle.Vwap => "VWAP curve with liquidity-skewed slice weights and routing by source health.",
            AlgoExecutionStyle.Pov => $"POV schedule with max participation {maxParticipation:P1} per expected flow slice.",
            _ => "Algo execution."
        };

        var result = new AlgoExecutionReport(
            AlgoOrderId: algoId,
            Style: request.Style,
            Symbol: request.Symbol,
            Side: request.Side,
            RequestedQuantity: request.Quantity,
            FilledQuantity: filled,
            RemainingQuantity: Math.Max(0, request.Quantity - filled),
            AvgFillPrice: avgFill,
            TotalFees: totalFees,
            AggregateSlippagePct: weightedSlippage,
            ExecutionQualityScore: quality,
            Children: children,
            Timestamp: DateTimeOffset.UtcNow,
            Routing: routing,
            ExecutionNotes: notes);

        _persistence.AppendAuditEvent("algo-execution", $"Algo {algoId} executed", JsonSerializer.Serialize(result));
        return result;
    }

    public async Task<HedgeSuggestionResponse> GetHedgeSuggestionAsync(HedgeSuggestionRequest request, CancellationToken ct = default)
    {
        var before = await GetRiskAsync(ct);
        var positions = await GetPositionsAsync(ct);
        string[] assets = string.IsNullOrWhiteSpace(request.Asset)
            ? positions.Select(p => p.Asset).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("BTC").ToArray()
            : [request.Asset!.Trim().ToUpperInvariant()];

        var legs = new List<HedgeLegSuggestion>();
        foreach (string asset in assets)
        {
            if (legs.Count >= Math.Clamp(request.MaxLegs, 1, 6))
                break;

            var chain = await _marketData.GetOptionChainAsync(asset, ct);
            if (chain.Count == 0)
                continue;

            double deltaNeed = request.TargetDelta - before.NetDelta;
            double vegaNeed = request.TargetVega - before.NetVega;
            bool needPositiveDelta = deltaNeed > 0;
            OptionRight right = needPositiveDelta ? OptionRight.Call : OptionRight.Put;
            TradeDirection side = needPositiveDelta ? TradeDirection.Buy : TradeDirection.Buy;

            var candidate = chain
                .Where(q => q.Right == right && q.Expiry > DateTimeOffset.UtcNow.AddDays(3) && q.Mark > 0)
                .OrderBy(q => Math.Abs(q.Delta - (needPositiveDelta ? 0.35 : -0.35)))
                .ThenByDescending(q => q.OpenInterest + q.Volume24h)
                .FirstOrDefault();
            if (candidate is null)
                continue;

            double deltaPer = candidate.Delta;
            double qtyByDelta = Math.Abs(deltaPer) > 1e-6 ? Math.Abs(deltaNeed / deltaPer) : 0;
            double qtyByVega = Math.Abs(candidate.Vega) > 1e-6 ? Math.Abs(vegaNeed / candidate.Vega) : qtyByDelta;
            double qty = MathUtils.Clamp(
                Math.Max(0.1, Math.Min(qtyByDelta > 0 ? qtyByDelta : double.MaxValue, qtyByVega > 0 ? qtyByVega : double.MaxValue)),
                0.1,
                80);

            double px = FirstPositive(candidate.Ask, candidate.Mid, candidate.Mark, candidate.Bid);
            double notional = px * qty;
            if (notional > request.MaxNotionalPerLeg && notional > 0)
                qty = MathUtils.Clamp(request.MaxNotionalPerLeg / notional * qty, 0.1, qty);

            legs.Add(new HedgeLegSuggestion(
                Symbol: candidate.Symbol,
                Venue: candidate.Venue,
                Side: side,
                Quantity: qty,
                EstimatedPrice: px,
                EstimatedNotional: px * qty,
                DeltaImpact: candidate.Delta * qty,
                VegaImpact: candidate.Vega * qty,
                GammaImpact: candidate.Gamma * qty,
                Rationale: $"Reduce net delta/vega drift on {asset} with liquid {(right == OptionRight.Call ? "call" : "put")} strike"));
        }

        double projectedDelta = before.NetDelta + legs.Sum(l => l.Side == TradeDirection.Buy ? l.DeltaImpact : -l.DeltaImpact);
        double projectedVega = before.NetVega + legs.Sum(l => l.Side == TradeDirection.Buy ? l.VegaImpact : -l.VegaImpact);
        double projectedGamma = before.NetGamma + legs.Sum(l => l.Side == TradeDirection.Buy ? l.GammaImpact : -l.GammaImpact);

        var projected = before with
        {
            NetDelta = projectedDelta,
            NetVega = projectedVega,
            NetGamma = projectedGamma,
            Timestamp = DateTimeOffset.UtcNow
        };

        return new HedgeSuggestionResponse(
            BeforeRisk: before,
            ProjectedRisk: projected,
            Legs: legs,
            Summary: legs.Count == 0
                ? "No hedge candidate found with current constraints."
                : $"Generated {legs.Count} hedge legs; projected delta {projectedDelta:F2}, vega {projectedVega:F2}.",
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<AutoHedgeReport> RunAutoHedgeAsync(AutoHedgeRequest request, CancellationToken ct = default)
    {
        request ??= new AutoHedgeRequest();
        int maxLegs = Math.Clamp(request.MaxLegs, 1, 6);
        double maxNotional = Math.Max(100, request.MaxNotionalPerLeg);

        var hedgeRequest = new HedgeSuggestionRequest(
            Asset: request.Asset,
            TargetDelta: request.TargetDelta,
            TargetVega: request.TargetVega,
            TargetGamma: request.TargetGamma,
            MaxLegs: maxLegs,
            MaxNotionalPerLeg: maxNotional);

        HedgeSuggestionResponse suggestion = await GetHedgeSuggestionAsync(hedgeRequest, ct);
        PortfolioRiskSnapshot before = suggestion.BeforeRisk;
        var executions = new List<AutoHedgeLegExecution>(suggestion.Legs.Count);

        if (!request.Execute || suggestion.Legs.Count == 0)
        {
            PortfolioRiskSnapshot projected = suggestion.ProjectedRisk;
            string drySummary = request.Execute
                ? "No hedge leg available for execution under current constraints."
                : "Dry-run hedge plan generated (no execution requested).";

            return new AutoHedgeReport(
                Suggestion: suggestion,
                Executions: [],
                BeforeRisk: before,
                AfterRisk: projected,
                Executed: false,
                Summary: drySummary,
                Timestamp: DateTimeOffset.UtcNow);
        }

        foreach (var leg in suggestion.Legs.Take(maxLegs))
        {
            ct.ThrowIfCancellationRequested();

            if (request.UseAlgoExecution)
            {
                try
                {
                    var algo = await ExecuteAlgoOrderAsync(
                        new AlgoExecutionRequest(
                            Symbol: leg.Symbol,
                            Side: leg.Side,
                            Quantity: Math.Max(0.01, leg.Quantity),
                            Style: request.AlgoStyle,
                            Slices: Math.Clamp(request.AlgoSlices, 1, 24),
                            IntervalSeconds: Math.Clamp(request.AlgoIntervalSeconds, 1, 30),
                            MaxParticipationPct: 0.2,
                            LimitPrice: null,
                            AllowPartialFill: true,
                            MaxRetriesPerSlice: 1,
                            ClientOrderId: $"AUTO-HEDGE-{request.RequestedBy}-{Guid.NewGuid():N}"[..28]),
                        ct);

                    bool accepted = algo.Children.Any(c =>
                        c.Report.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Accepted);
                    string? reason = accepted
                        ? null
                        : algo.Children.Select(c => c.Report.RejectReason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ?? "algo-no-fill";

                    executions.Add(new AutoHedgeLegExecution(
                        Suggestion: leg,
                        Order: null,
                        AlgoOrder: algo,
                        Accepted: accepted,
                        RejectReason: reason));
                }
                catch (Exception ex)
                {
                    executions.Add(new AutoHedgeLegExecution(
                        Suggestion: leg,
                        Order: null,
                        AlgoOrder: null,
                        Accepted: false,
                        RejectReason: ex.Message));
                }

                continue;
            }

            TradingOrderReport report = await PlaceOrderAsync(
                new TradingOrderRequest(
                    Symbol: leg.Symbol,
                    Side: leg.Side,
                    Quantity: Math.Max(0.01, leg.Quantity),
                    Type: OrderType.Market,
                    LimitPrice: null,
                    ClientOrderId: $"AUTO-HEDGE-{request.RequestedBy}-{Guid.NewGuid():N}"[..28],
                    MaxRetries: 2,
                    AllowPartialFill: true,
                    MaxSlippagePct: Limits.MaxSlippagePct),
                ct);

            executions.Add(new AutoHedgeLegExecution(
                Suggestion: leg,
                Order: report,
                AlgoOrder: null,
                Accepted: report.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled or OrderStatus.Accepted,
                RejectReason: report.RejectReason));
        }

        PortfolioRiskSnapshot after = await GetRiskAsync(ct);
        int acceptedCount = executions.Count(x => x.Accepted);
        bool executed = acceptedCount > 0;
        string summary = $"Auto-hedge {acceptedCount}/{executions.Count} legs accepted | " +
                         $"delta {before.NetDelta:F2} -> {after.NetDelta:F2}, vega {before.NetVega:F2} -> {after.NetVega:F2}.";

        var result = new AutoHedgeReport(
            Suggestion: suggestion,
            Executions: executions,
            BeforeRisk: before,
            AfterRisk: after,
            Executed: executed,
            Summary: summary,
            Timestamp: DateTimeOffset.UtcNow);

        _persistence.AppendAuditEvent("auto-hedge", summary, JsonSerializer.Serialize(result));
        return result;
    }

    public async Task<PortfolioOptimizationResponse> OptimizePortfolioAsync(PortfolioOptimizationRequest request, CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(ct);
        double gross = positions.Sum(p => p.Notional);
        double capital = Math.Max(request.CapitalBudget, 1);

        var byAsset = positions
            .GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Asset = g.Key,
                Notional = g.Sum(x => x.Notional),
                Delta = g.Sum(x => x.Greeks.Delta),
                Vega = g.Sum(x => x.Greeks.Vega),
                Gamma = g.Sum(x => x.Greeks.Gamma)
            })
            .OrderByDescending(x => x.Notional)
            .ToList();

        if (byAsset.Count == 0)
        {
            return new PortfolioOptimizationResponse(
                CapitalBudget: capital,
                GrossNotional: 0,
                Budgets: [],
                Summary: "No open positions to optimize.",
                Timestamp: DateTimeOffset.UtcNow);
        }

        double deltaAbs = byAsset.Sum(x => Math.Abs(x.Delta));
        double vegaAbs = byAsset.Sum(x => Math.Abs(x.Vega));
        double gammaAbs = byAsset.Sum(x => Math.Abs(x.Gamma));

        var budgets = new List<AssetRiskBudget>(byAsset.Count);
        foreach (var row in byAsset)
        {
            double currentWeight = gross > 0 ? row.Notional / gross : 0;
            double deltaShare = deltaAbs > 0 ? Math.Abs(row.Delta) / deltaAbs : 0;
            double vegaShare = vegaAbs > 0 ? Math.Abs(row.Vega) / vegaAbs : 0;
            double gammaShare = gammaAbs > 0 ? Math.Abs(row.Gamma) / gammaAbs : 0;

            double riskWeight =
                deltaShare * request.MaxDeltaBudget +
                vegaShare * request.MaxVegaBudget +
                gammaShare * request.MaxGammaBudget;
            double cappedWeight = Math.Min(request.MaxAssetWeight, Math.Max(0.05, riskWeight));
            double targetNotional = capital * cappedWeight;
            double adjust = targetNotional - row.Notional;

            budgets.Add(new AssetRiskBudget(
                Asset: row.Asset,
                CurrentNotional: row.Notional,
                TargetNotional: targetNotional,
                Adjustment: adjust,
                Weight: currentWeight,
                DeltaShare: deltaShare,
                VegaShare: vegaShare,
                GammaShare: gammaShare,
                Action: adjust > 0 ? "increase-risk-budget" : "decrease-risk-budget"));
        }

        string summary = string.Join(
            " | ",
            budgets
                .OrderByDescending(b => Math.Abs(b.Adjustment))
                .Take(3)
                .Select(b => $"{b.Asset}:{(b.Adjustment >= 0 ? "+" : string.Empty)}{b.Adjustment:F0}"));

        return new PortfolioOptimizationResponse(
            CapitalBudget: capital,
            GrossNotional: gross,
            Budgets: budgets.OrderByDescending(x => x.CurrentNotional).ToList(),
            Summary: string.IsNullOrWhiteSpace(summary) ? "Portfolio near target risk budget." : summary,
            Timestamp: DateTimeOffset.UtcNow);
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

        _persistence.AppendAuditEvent(
            "kill-switch",
            request.IsActive ? "enabled" : "disabled",
            JsonSerializer.Serialize(_killSwitch));

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

        var snapshot = result
            .OrderByDescending(p => p.Notional)
            .ToList();

        MaybePersistPositionSnapshot(snapshot, "positions-query");
        return snapshot;
    }

    public async Task<PortfolioRiskSnapshot> GetRiskAsync(CancellationToken ct = default)
    {
        var positions = await GetPositionsAsync(ct);
        PortfolioRiskSnapshot snapshot = BuildRiskSnapshot(positions);
        _persistence.AppendRiskEvent(snapshot, "risk-query");
        return snapshot;
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

    public Task<TradingHistorySnapshot> GetHistoryAsync(
        int orderLimit = 250,
        int positionLimit = 250,
        int riskLimit = 250,
        int auditLimit = 250,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var history = new TradingHistorySnapshot(
            Orders: _persistence.GetOrderEvents(orderLimit),
            Positions: _persistence.GetPositionEvents(positionLimit),
            Risks: _persistence.GetRiskEvents(riskLimit),
            AuditTrail: _persistence.GetAuditEvents(auditLimit),
            Timestamp: DateTimeOffset.UtcNow);
        return Task.FromResult(history);
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
        _persistence.AppendAuditEvent("reset", "Paper trading state reset by API.");
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
        while (state.RemainingQuantity > 1e-12 && attempts <= state.MaxRetries && !state.IsCancelled)
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

        if (state.IsCancelled)
        {
            status = OrderStatus.Cancelled;
            rejectReason = state.CancelReason;
        }
        else if (filledQty <= 1e-12)
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
        var canonicalRequest = request with { Symbol = quote.Symbol };

        if (request.Type == OrderType.Limit && (!request.LimitPrice.HasValue || request.LimitPrice <= 0))
            return BuildRejectedSimulation("limit price must be set for limit order", request.LimitPrice ?? 0);

        if (!TryComputeExecutablePrice(canonicalRequest, quote, out double requestedPrice, out double executablePrice, out string? rejectReason))
            return BuildRejectedSimulation(rejectReason ?? "no executable price", requestedPrice);

        double estFillRatio = EstimateFillRatio(canonicalRequest, quote);
        if (!canonicalRequest.AllowPartialFill && estFillRatio < 0.999)
            return BuildRejectedSimulation("insufficient liquidity for full fill", requestedPrice);

        double estimatedFilledQty = canonicalRequest.Quantity * estFillRatio;
        double estimatedRemainingQty = Math.Max(0, canonicalRequest.Quantity - estimatedFilledQty);
        double simulatedNotional = executablePrice * canonicalRequest.Quantity;

        if (simulatedNotional > Limits.MaxOrderNotional)
            return BuildRejectedSimulation($"order notional exceeds limit ({Limits.MaxOrderNotional:F0})", requestedPrice);

        var states = SnapshotStates();
        double signedQty = canonicalRequest.Side == TradeDirection.Buy ? canonicalRequest.Quantity : -canonicalRequest.Quantity;
        double currentSymbolQty = states.FirstOrDefault(s => s.Symbol.Equals(canonicalRequest.Symbol, StringComparison.OrdinalIgnoreCase))?.NetQuantity ?? 0;
        double projectedSymbolQty = Math.Abs(currentSymbolQty + signedQty);
        if (projectedSymbolQty > Limits.MaxSymbolAbsQuantity)
            return BuildRejectedSimulation($"symbol quantity limit breached ({Limits.MaxSymbolAbsQuantity:F0})", requestedPrice);

        var projectedPositions = await SimulateProjectedPositionsAsync(states, quote, canonicalRequest, executablePrice, canonicalRequest.Quantity, ct);
        var risk = BuildRiskSnapshot(projectedPositions);
        if (risk.Breached)
            return BuildRejectedSimulation($"risk breach: {string.Join(", ", risk.Flags)}", requestedPrice);

        double slippagePct = ComputeAggregateSlippagePct(canonicalRequest.Side, requestedPrice, executablePrice);
        double maxSlippage = canonicalRequest.MaxSlippagePct ?? Limits.MaxSlippagePct;
        if (slippagePct > maxSlippage)
            return BuildRejectedSimulation($"slippage estimate {slippagePct:P2} exceeds max {maxSlippage:P2}", requestedPrice);

        double feeRate = canonicalRequest.Type == OrderType.Limit ? Limits.MakerFeeRate : Limits.TakerFeeRate;
        double fees = simulatedNotional * feeRate;
        double quality = ComputeExecutionQualityScore(slippagePct, feeRate, estimatedFilledQty, canonicalRequest.Quantity, canonicalRequest.MaxRetries);

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

        _persistence.AppendOrderEvent(report, "oms");

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
        if (chain.Count == 0)
            return null;

        string normalizedInput = NormalizeOptionSymbol(symbol);
        var exact = chain.FirstOrDefault(q =>
            NormalizeOptionSymbol(q.Symbol).Equals(normalizedInput, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        if (!TryParseLooseOptionSymbol(normalizedInput, out var parsedAsset, out var parsedExpiry, out var parsedStrike, out var parsedRight))
            return null;

        if (!parsedAsset.Equals(asset, StringComparison.OrdinalIgnoreCase))
            return null;

        var nearest = chain
            .Where(q => q.Right == parsedRight && q.Expiry.Date == parsedExpiry.Date)
            .Select(q => new
            {
                Quote = q,
                StrikeDiff = Math.Abs(q.Strike - parsedStrike)
            })
            .OrderBy(x => x.StrikeDiff)
            .FirstOrDefault();

        if (nearest is null)
            return null;

        double absTolerance = Math.Max(0.02, parsedStrike * 0.015);
        return nearest.StrikeDiff <= absTolerance ? nearest.Quote : null;
    }

    private async Task<string> EstimateBestVenueAsync(
        string symbol,
        double intendedQuantity,
        TradeDirection side,
        CancellationToken ct)
    {
        _ = side;
        string asset = ExtractAsset(symbol);
        LiveOptionQuote? quote = await TryFindQuoteAsync(symbol, ct);
        MarketDataCompositeStatus? status = null;

        try
        {
            status = await _marketData.GetStatusAsync(asset, ct);
        }
        catch
        {
            // Route fallback remains available via quote venue when status endpoint is degraded.
        }

        string quoteVenue = NormalizeVenueName(quote?.Venue);
        double requiredDepth = Math.Max(0.01, intendedQuantity);
        if (status is not null && status.Sources.Count > 0)
        {
            var ranked = status.Sources
                .OrderByDescending(s => s.Healthy && !s.IsStale)
                .ThenByDescending(s => s.Healthy)
                .ThenByDescending(s => s.QuoteCount >= requiredDepth)
                .ThenBy(s => s.IsFallback)
                .ThenBy(s => s.IsStale)
                .ThenByDescending(s => s.QuoteCount)
                .ThenBy(s => s.LastLatencyMs)
                .ToList();

            if (!string.IsNullOrWhiteSpace(quoteVenue))
            {
                var venueSource = ranked.FirstOrDefault(s =>
                    NormalizeVenueName(s.Source).Equals(quoteVenue, StringComparison.OrdinalIgnoreCase));
                if (venueSource is not null && venueSource.Healthy && !venueSource.IsStale)
                    return venueSource.Source;
            }

            var best = ranked.FirstOrDefault();
            if (best is not null)
                return best.Source;
        }

        if (!string.IsNullOrWhiteSpace(quoteVenue))
            return quoteVenue;

        return "SYNTHETIC";
    }

    private static double[] BuildSliceWeights(AlgoExecutionStyle style, int slices)
    {
        int safeSlices = Math.Clamp(slices, 1, 64);
        if (safeSlices == 1)
            return [1.0];

        var weights = new double[safeSlices];
        switch (style)
        {
            case AlgoExecutionStyle.Twap:
                for (int i = 0; i < safeSlices; i++)
                    weights[i] = 1.0;
                break;

            case AlgoExecutionStyle.Vwap:
                for (int i = 0; i < safeSlices; i++)
                {
                    // Simple U-curve profile (more expected flow at open/close buckets).
                    double x = safeSlices == 1 ? 0 : i / (double)(safeSlices - 1);
                    double edge = Math.Abs(2 * x - 1);
                    weights[i] = 0.75 + edge * 0.65;
                }
                break;

            case AlgoExecutionStyle.Pov:
                for (int i = 0; i < safeSlices; i++)
                    weights[i] = 1.0;
                break;

            default:
                for (int i = 0; i < safeSlices; i++)
                    weights[i] = 1.0;
                break;
        }

        double sum = weights.Sum();
        if (sum <= 1e-12)
            return Enumerable.Repeat(1.0 / safeSlices, safeSlices).ToArray();
        return weights.Select(w => w / sum).ToArray();
    }

    private static string NormalizeVenueName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string value = raw.Trim().ToUpperInvariant();
        if (value == "SYNTH")
            return "SYNTHETIC";
        return value;
    }

    private static string ExtractAsset(string symbol)
    {
        string[] parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("invalid option symbol");
        return parts[0].Trim().ToUpperInvariant();
    }

    private static string NormalizeOptionSymbol(string symbol)
    {
        var tokens = (symbol ?? string.Empty)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim().ToUpperInvariant())
            .ToList();

        if (tokens.Count >= 5 && (tokens[^1] is "USD" or "USDT" or "USDC"))
            tokens.RemoveAt(tokens.Count - 1);

        return string.Join("-", tokens);
    }

    private static bool TryParseLooseOptionSymbol(
        string symbol,
        out string asset,
        out DateTimeOffset expiry,
        out double strike,
        out OptionRight right)
    {
        asset = string.Empty;
        expiry = default;
        strike = 0;
        right = OptionRight.Call;

        string[] parts = NormalizeOptionSymbol(symbol)
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 4)
            return false;

        asset = parts[0].Trim().ToUpperInvariant();

        if (!DateTime.TryParseExact(
                parts[1].Trim().ToUpperInvariant(),
                "ddMMMyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expiryDate))
        {
            return false;
        }

        expiry = new DateTimeOffset(expiryDate, TimeSpan.Zero);

        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out strike))
            return false;

        string rightToken = parts[3].Trim().ToUpperInvariant();
        right = rightToken switch
        {
            "C" or "CALL" => OptionRight.Call,
            "P" or "PUT" => OptionRight.Put,
            _ => OptionRight.Call
        };

        return rightToken is "C" or "CALL" or "P" or "PUT";
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
        var scenarios = new List<StressScenario>();

        if (request?.Scenarios is { Count: > 0 })
        {
            scenarios.AddRange(
                request.Scenarios
                    .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                    .Select(s => new StressScenario(
                        Name: s.Name.Trim(),
                        UnderlyingShockPct: MathUtils.Clamp(s.UnderlyingShockPct, -0.9, 1.5),
                        IvShockPct: MathUtils.Clamp(s.IvShockPct, -0.95, 2.0),
                        DaysForward: Math.Clamp(s.DaysForward, 0, 365))));
        }
        else
        {
            scenarios.AddRange(
            [
                new StressScenario("Spot -10%", -0.10, 0),
                new StressScenario("Spot +10%", 0.10, 0),
                new StressScenario("Vol +20%", 0, 0.20),
                new StressScenario("Spot -15% / Vol +25%", -0.15, 0.25),
                new StressScenario("Spot +15% / Vol -20%", 0.15, -0.20)
            ]);
        }

        if (request?.IncludeIntradaySet == true)
        {
            scenarios.AddRange(
            [
                new StressScenario("Intraday risk-off pulse", -0.035, 0.07, 0),
                new StressScenario("Intraday squeeze", 0.028, -0.04, 0),
                new StressScenario("Orderbook vacuum down", -0.055, 0.11, 0),
                new StressScenario("Macro event shock", -0.08, 0.16, 1)
            ]);
        }

        if (request?.Macro is not null)
        {
            scenarios = scenarios
                .Select(s => ApplyMacroToScenario(s, request.Macro))
                .ToList();
        }

        return scenarios
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static StressScenario ApplyMacroToScenario(StressScenario scenario, MacroStressConfig macro)
    {
        double growth = MathUtils.Clamp(macro.GrowthShock, -1, 1);
        double inflation = MathUtils.Clamp(macro.InflationShock, -1, 1);
        double policy = MathUtils.Clamp(macro.PolicyShock, -1, 1);
        double usd = MathUtils.Clamp(macro.UsdShock, -1, 1);
        double liq = MathUtils.Clamp(macro.LiquidityShock, -1, 1);
        double risk = MathUtils.Clamp(macro.RiskAversionShock, -1, 1);

        double spotAdj =
            growth * 0.035 -
            inflation * 0.012 -
            policy * 0.018 -
            usd * 0.020 +
            liq * 0.030 -
            risk * 0.028;

        double ivAdj =
            inflation * 0.035 +
            policy * 0.040 -
            growth * 0.015 -
            liq * 0.030 +
            risk * 0.055 +
            usd * 0.015;

        return scenario with
        {
            UnderlyingShockPct = MathUtils.Clamp(scenario.UnderlyingShockPct + spotAdj, -0.9, 1.5),
            IvShockPct = MathUtils.Clamp(scenario.IvShockPct + ivAdj, -0.95, 2.0)
        };
    }

    private void MaybePersistPositionSnapshot(IReadOnlyList<TradingPosition> positions, string source)
    {
        if (positions.Count == 0)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_positionPersistGate)
        {
            if (now - _lastPositionPersistAt < TimeSpan.FromSeconds(6))
                return;
            _lastPositionPersistAt = now;
        }

        _persistence.AppendPositionSnapshot(positions, source);
    }

    private void AddNotification(NotificationSeverity severity, string category, string message)
    {
        TradingNotification notification;
        lock (_gate)
        {
            notification = new TradingNotification(
                Id: $"NTF-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
                Severity: severity,
                Category: category,
                Message: message,
                Timestamp: DateTimeOffset.UtcNow,
                Acknowledged: false);

            _notifications.Add(notification);

            if (_notifications.Count > MaxStoredNotifications)
                _notifications.RemoveRange(0, _notifications.Count - MaxStoredNotifications);
        }

        _persistence.AppendNotificationEvent(notification);
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
