using Atlas.Core.Common;

namespace Atlas.Api.Models;

public enum OrderType
{
    Market,
    Limit
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
    IReadOnlyList<StressScenario> Scenarios);

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
