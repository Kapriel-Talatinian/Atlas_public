using Atlas.Core.Common;

namespace Atlas.Api.Models;

public enum OrderType
{
    Market,
    Limit
}

public enum AlgoExecutionStyle
{
    Twap,
    Vwap,
    Pov
}

public enum OrderStatus
{
    Received,
    Accepted,
    PartiallyFilled,
    Filled,
    Rejected,
    Cancelled,
    Expired
}

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record TradingOrderRequest(
    string Symbol,
    TradeDirection Side,
    double Quantity,
    OrderType Type = OrderType.Market,
    double? LimitPrice = null,
    string? ClientOrderId = null,
    int MaxRetries = 2,
    bool AllowPartialFill = true,
    double? MaxSlippagePct = null);

public sealed record CancelOrderRequest(
    string OrderId,
    string Reason = "manual-cancel",
    string UpdatedBy = "desk");

public sealed record ReplaceOrderRequest(
    string OrderId,
    double? Quantity = null,
    OrderType? Type = null,
    double? LimitPrice = null,
    int? MaxRetries = null,
    bool? AllowPartialFill = null,
    double? MaxSlippagePct = null,
    string Reason = "replace",
    string UpdatedBy = "desk");

public sealed record TradingOrderFillExecution(
    string FillId,
    int Attempt,
    double Quantity,
    double Price,
    double Fees,
    double SlippagePct,
    DateTimeOffset Timestamp,
    string Venue = "PAPER");

public sealed record TradingOrderReport(
    string OrderId,
    string Symbol,
    TradeDirection Side,
    double Quantity,
    OrderType Type,
    OrderStatus Status,
    double RequestedPrice,
    double FillPrice,
    double Fees,
    string? RejectReason,
    string? ClientOrderId,
    DateTimeOffset Timestamp,
    double FilledQuantity = 0,
    double RemainingQuantity = 0,
    double AvgFillPrice = 0,
    double SlippagePct = 0,
    double EffectiveFeeRate = 0,
    double ExecutionQualityScore = 0,
    int RetryCount = 0,
    bool IdempotentReplay = false,
    IReadOnlyList<string>? StateTrace = null,
    IReadOnlyList<TradingOrderFillExecution>? Fills = null,
    double Notional = 0);

public sealed record OrderReplaceResult(
    TradingOrderReport CancelledOrder,
    TradingOrderReport NewOrder,
    DateTimeOffset Timestamp,
    string Reason);

public sealed record OmsReconciliationIssue(
    string Severity,
    string Category,
    string OrderId,
    string Message,
    DateTimeOffset Timestamp);

public sealed record OmsReconciliationReport(
    int TotalOrdersChecked,
    int OpenOrdersChecked,
    int IssueCount,
    int CriticalCount,
    bool Healthy,
    IReadOnlyList<OmsReconciliationIssue> Issues,
    DateTimeOffset Timestamp);

public sealed record TradingPosition(
    string Symbol,
    string Asset,
    double NetQuantity,
    double AvgEntryPrice,
    double MarkPrice,
    double Notional,
    double UnrealizedPnl,
    double RealizedPnl,
    GreeksResult Greeks,
    DateTimeOffset UpdatedAt,
    double InitialMarginRequirement = 0,
    double MaintenanceMarginRequirement = 0,
    string MarginMode = "Portfolio");

public sealed record RiskLimitConfig(
    double MaxOrderNotional = 250_000,
    double MaxGrossNotional = 3_000_000,
    double MaxNetDelta = 300,
    double MaxNetGamma = 50,
    double MaxNetVega = 2_000,
    double MaxNetThetaAbs = 6_000,
    double MaxSymbolAbsQuantity = 250,
    double MaxOrderQuantity = 150,
    double MaxAssetGrossNotional = 1_750_000,
    double MaxConcentrationPct = 0.45,
    double MaxDailyLoss = 200_000,
    int MaxOpenOrders = 400,
    double MaxSlippagePct = 0.08,
    double TakerFeeRate = 0.0004,
    double MakerFeeRate = 0.0002,
    bool KillSwitchDefault = false,
    double LiquidationBufferPct = 0.08,
    double MarginAddOnPerGamma = 250,
    double MarginAddOnPerVega = 3.0);

public sealed record PortfolioRiskSnapshot(
    double GrossNotional,
    double NetDelta,
    double NetGamma,
    double NetVega,
    double NetTheta,
    double UnrealizedPnl,
    double RealizedPnl,
    bool Breached,
    IReadOnlyList<string> Flags,
    DateTimeOffset Timestamp,
    double InitialMargin = 0,
    double MaintenanceMargin = 0,
    double Equity = 0,
    double AvailableMargin = 0,
    double MarginRatio = 0,
    bool LiquidationTriggered = false,
    double LargestPositionNotional = 0,
    double LargestPositionConcentrationPct = 0,
    bool KillSwitchActive = false,
    double DailyPnl = 0);

public sealed record TradingNotification(
    string Id,
    NotificationSeverity Severity,
    string Category,
    string Message,
    DateTimeOffset Timestamp,
    bool Acknowledged = false);

public sealed record TradingBookSnapshot(
    IReadOnlyList<TradingPosition> Positions,
    IReadOnlyList<TradingOrderReport> RecentOrders,
    PortfolioRiskSnapshot Risk,
    RiskLimitConfig Limits,
    DateTimeOffset Timestamp,
    IReadOnlyList<TradingOrderReport>? OpenOrders = null,
    IReadOnlyList<TradingNotification>? Notifications = null);

public sealed record MarginRulebook(
    double PortfolioInitialRate,
    double PortfolioMaintenanceRate,
    double PositionFloorInitial,
    double PositionFloorMaintenance,
    double GammaAddOn,
    double VegaAddOn,
    double ThetaAddOnRate,
    double LiquidationBufferPct,
    string Description,
    DateTimeOffset Timestamp);

public sealed record OrderSimulationResult(
    bool Accepted,
    string? RejectReason,
    double FillPrice,
    double RequestedPrice,
    double Fees,
    double ProjectedNetDelta,
    double ProjectedNetGamma,
    double ProjectedNetVega,
    double ProjectedGrossNotional,
    double ProjectedNetTheta = 0,
    double ProjectedConcentrationPct = 0,
    double ProjectedInitialMargin = 0,
    double ProjectedMaintenanceMargin = 0,
    double ProjectedEquity = 0,
    double ProjectedAvailableMargin = 0,
    double SlippagePctEstimate = 0,
    double QualityScoreEstimate = 0,
    double EstimatedFilledQuantity = 0,
    double EstimatedRemainingQuantity = 0);

public sealed record PreTradePreviewResult(
    bool Accepted,
    string? RejectReason,
    double RequestedPrice,
    double FillPrice,
    double EstimatedFees,
    PortfolioRiskSnapshot ProjectedRisk,
    double EstimatedInitialMargin,
    double EstimatedMaintenanceMargin,
    DateTimeOffset Timestamp,
    double EstimatedSlippagePct = 0,
    double EstimatedExecutionQuality = 0,
    double EstimatedFilledQuantity = 0,
    double EstimatedRemainingQuantity = 0,
    double EstimatedFeeRate = 0);

public sealed record StressScenario(
    string Name,
    double UnderlyingShockPct,
    double IvShockPct,
    int DaysForward = 0);

public sealed record StressTestRequest(
    IReadOnlyList<StressScenario> Scenarios,
    bool IncludeIntradaySet = false,
    MacroStressConfig? Macro = null);

public sealed record MacroStressConfig(
    double GrowthShock = 0,
    double InflationShock = 0,
    double PolicyShock = 0,
    double UsdShock = 0,
    double LiquidityShock = 0,
    double RiskAversionShock = 0);

public sealed record StressScenarioResult(
    string Name,
    double UnderlyingShockPct,
    double IvShockPct,
    int DaysForward,
    double EstimatedPnl,
    double EstimatedNetDelta,
    double EstimatedNetGamma,
    double EstimatedNetVega);

public sealed record StressTestResult(
    PortfolioRiskSnapshot BaseRisk,
    double WorstScenarioPnl,
    string WorstScenarioName,
    double BestScenarioPnl,
    string BestScenarioName,
    IReadOnlyList<StressScenarioResult> Scenarios,
    DateTimeOffset Timestamp);

public sealed record KillSwitchState(
    bool IsActive,
    string? Reason,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record KillSwitchRequest(
    bool IsActive,
    string Reason,
    string UpdatedBy = "api");

public sealed record AlgoExecutionRequest(
    string Symbol,
    TradeDirection Side,
    double Quantity,
    AlgoExecutionStyle Style = AlgoExecutionStyle.Twap,
    int Slices = 6,
    int IntervalSeconds = 10,
    double MaxParticipationPct = 0.15,
    double? LimitPrice = null,
    bool AllowPartialFill = true,
    int MaxRetriesPerSlice = 1,
    string? ClientOrderId = null);

public sealed record AlgoExecutionChild(
    int Slice,
    DateTimeOffset ScheduledAt,
    string Venue,
    double RequestedQuantity,
    TradingOrderReport Report,
    double ParticipationPct = 0,
    double SliceWeight = 0);

public sealed record AlgoRouteAllocation(
    string Venue,
    double FilledQuantity,
    double Notional,
    double AvgSlippagePct,
    double AvgQualityScore);

public sealed record AlgoExecutionReport(
    string AlgoOrderId,
    AlgoExecutionStyle Style,
    string Symbol,
    TradeDirection Side,
    double RequestedQuantity,
    double FilledQuantity,
    double RemainingQuantity,
    double AvgFillPrice,
    double TotalFees,
    double AggregateSlippagePct,
    double ExecutionQualityScore,
    IReadOnlyList<AlgoExecutionChild> Children,
    DateTimeOffset Timestamp,
    IReadOnlyList<AlgoRouteAllocation>? Routing = null,
    string? ExecutionNotes = null);

public sealed record HedgeSuggestionRequest(
    string? Asset = null,
    double TargetDelta = 0,
    double TargetVega = 0,
    double TargetGamma = 0,
    int MaxLegs = 2,
    double MaxNotionalPerLeg = 180_000);

public sealed record HedgeLegSuggestion(
    string Symbol,
    string Venue,
    TradeDirection Side,
    double Quantity,
    double EstimatedPrice,
    double EstimatedNotional,
    double DeltaImpact,
    double VegaImpact,
    double GammaImpact,
    string Rationale);

public sealed record HedgeSuggestionResponse(
    PortfolioRiskSnapshot BeforeRisk,
    PortfolioRiskSnapshot ProjectedRisk,
    IReadOnlyList<HedgeLegSuggestion> Legs,
    string Summary,
    DateTimeOffset Timestamp);

public sealed record AutoHedgeRequest(
    string? Asset = null,
    double TargetDelta = 0,
    double TargetVega = 0,
    double TargetGamma = 0,
    int MaxLegs = 2,
    double MaxNotionalPerLeg = 180_000,
    bool Execute = false,
    bool UseAlgoExecution = true,
    AlgoExecutionStyle AlgoStyle = AlgoExecutionStyle.Twap,
    int AlgoSlices = 3,
    int AlgoIntervalSeconds = 1,
    string RequestedBy = "desk");

public sealed record AutoHedgeLegExecution(
    HedgeLegSuggestion Suggestion,
    TradingOrderReport? Order,
    AlgoExecutionReport? AlgoOrder,
    bool Accepted,
    string? RejectReason);

public sealed record AutoHedgeReport(
    HedgeSuggestionResponse Suggestion,
    IReadOnlyList<AutoHedgeLegExecution> Executions,
    PortfolioRiskSnapshot BeforeRisk,
    PortfolioRiskSnapshot AfterRisk,
    bool Executed,
    string Summary,
    DateTimeOffset Timestamp);

public sealed record PortfolioOptimizationRequest(
    double CapitalBudget = 2_500_000,
    double MaxAssetWeight = 0.45,
    double MaxDeltaBudget = 0.35,
    double MaxVegaBudget = 0.40,
    double MaxGammaBudget = 0.25);

public sealed record AssetRiskBudget(
    string Asset,
    double CurrentNotional,
    double TargetNotional,
    double Adjustment,
    double Weight,
    double DeltaShare,
    double VegaShare,
    double GammaShare,
    string Action);

public sealed record PortfolioOptimizationResponse(
    double CapitalBudget,
    double GrossNotional,
    IReadOnlyList<AssetRiskBudget> Budgets,
    string Summary,
    DateTimeOffset Timestamp);

public sealed record PersistedOrderEvent(
    long Sequence,
    string OrderId,
    OrderStatus Status,
    string Symbol,
    string Source,
    DateTimeOffset RecordedAt,
    TradingOrderReport Report);

public sealed record PersistedRiskEvent(
    long Sequence,
    string Source,
    DateTimeOffset RecordedAt,
    PortfolioRiskSnapshot Snapshot);

public sealed record PersistedPositionEvent(
    long Sequence,
    string Source,
    DateTimeOffset RecordedAt,
    IReadOnlyList<TradingPosition> Positions);

public sealed record PersistedAuditEvent(
    long Sequence,
    string Category,
    string Message,
    string PayloadJson,
    DateTimeOffset RecordedAt);

public sealed record TradingHistorySnapshot(
    IReadOnlyList<PersistedOrderEvent> Orders,
    IReadOnlyList<PersistedPositionEvent> Positions,
    IReadOnlyList<PersistedRiskEvent> Risks,
    IReadOnlyList<PersistedAuditEvent> AuditTrail,
    DateTimeOffset Timestamp);
