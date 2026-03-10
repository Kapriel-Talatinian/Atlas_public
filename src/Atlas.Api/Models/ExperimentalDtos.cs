using Atlas.Core.Common;

namespace Atlas.Api.Models;

public sealed record ExperimentalBotConfig(
    bool Enabled = false,
    bool AutoTrade = false,
    bool AutoTune = true,
    int EvaluationIntervalSec = 20,
    double BasePositionSize = 1,
    int MaxOpenTrades = 4,
    double MinConfidence = 58,
    double StopLossPct = 0.35,
    double TakeProfitPct = 0.55,
    int MaxHoldingHours = 72,
    int AuditTargetTrades = 100,
    double StartingCapitalUsd = 1000);

public sealed record ExperimentalBotConfigRequest(
    bool? Enabled = null,
    bool? AutoTrade = null,
    bool? AutoTune = null,
    int? EvaluationIntervalSec = null,
    double? BasePositionSize = null,
    int? MaxOpenTrades = null,
    double? MinConfidence = null,
    double? StopLossPct = null,
    double? TakeProfitPct = null,
    int? MaxHoldingHours = null,
    int? AuditTargetTrades = null,
    double? StartingCapitalUsd = null);

public sealed record ExperimentalBotFeatureVector(
    double FlowImbalance,
    double SkewSignal,
    double VixProxy,
    double FuturesMomentum,
    double OrderbookPressure,
    double RestingPressure,
    double VolRegime,
    double TermSlope);

public sealed record ExperimentalBotSignal(
    string Bias,
    double Score,
    double Confidence,
    string StrategyTemplate,
    string Summary,
    IReadOnlyList<string> Drivers,
    ExperimentalBotFeatureVector Features,
    DateTimeOffset Timestamp);

public sealed record ExperimentalBotTrade(
    string TradeId,
    string Symbol,
    TradeDirection Side,
    double Quantity,
    double EntryPrice,
    double MarkPrice,
    double UnrealizedPnl,
    double UnrealizedPnlPct,
    DateTimeOffset EntryTime,
    string StrategyTemplate,
    string Rationale,
    bool IsOpen,
    DateTimeOffset? ExitTime = null,
    double ExitPrice = 0,
    double RealizedPnl = 0);

public sealed record ExperimentalBotStats(
    int TotalTrades,
    int ClosedTrades,
    int WinningTrades,
    int LosingTrades,
    double WinRate,
    double ProfitFactor,
    double RealizedPnl,
    double UnrealizedPnl,
    double NetPnl,
    double AvgTradePnl,
    double MaxDrawdown,
    double SharpeLike,
    double LearningRate,
    double RollingWinRate100 = 0,
    double RollingProfitFactor100 = 0,
    double RollingDrawdownPct100 = 0);

public sealed record ExperimentalBotPortfolioOverview(
    double StartingCapitalUsd,
    double EquityUsd,
    double PeakEquityUsd,
    double AvailableCapitalUsd,
    double OpenRiskNotionalUsd,
    double DrawdownUsd,
    double DrawdownPct);

public sealed record ExperimentalBotAuditSnapshot(
    int TargetTrades,
    int AuditedTrades,
    double CompletionPct,
    double RollingWinRate,
    double RollingProfitFactor,
    double RollingDrawdownPct,
    string Status);

public sealed record ExperimentalBotAuditEntry(
    DateTimeOffset Timestamp,
    string TradeId,
    string Symbol,
    double RealizedPnl,
    double RealizedPnlPct,
    bool Win,
    string ExitReason,
    double RollingWinRate,
    double RollingProfitFactor,
    double RollingDrawdownPct,
    string LearningComment);

public sealed record ExperimentalBotModelWeight(
    string Name,
    double Weight);

public sealed record ExperimentalBotDecision(
    DateTimeOffset Timestamp,
    string Bias,
    double Score,
    double Confidence,
    string Action,
    string Reason);

public sealed record ExperimentalBotSnapshot(
    string Asset,
    bool Running,
    ExperimentalBotConfig Config,
    ExperimentalBotSignal? Signal,
    ExperimentalBotStats Stats,
    ExperimentalBotPortfolioOverview Portfolio,
    ExperimentalBotAuditSnapshot Audit,
    IReadOnlyList<ExperimentalBotTrade> OpenTrades,
    IReadOnlyList<ExperimentalBotTrade> RecentClosedTrades,
    IReadOnlyList<ExperimentalBotAuditEntry> RecentAudits,
    IReadOnlyList<ExperimentalBotDecision> RecentDecisions,
    IReadOnlyList<ExperimentalBotModelWeight> Weights,
    DateTimeOffset StartedAt,
    DateTimeOffset Timestamp);
