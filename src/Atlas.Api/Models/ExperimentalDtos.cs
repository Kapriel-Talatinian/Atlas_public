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
    double StartingCapitalUsd = 1000,
    double PortfolioRiskBudgetPct = 0.88,
    double MaxAssetRiskPct = 0.42,
    double MaxTradeRiskPct = 0.14,
    int MaxNewTradesPerCycle = 1,
    IReadOnlyList<string>? ManagedAssets = null);

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
    double? StartingCapitalUsd = null,
    double? PortfolioRiskBudgetPct = null,
    double? MaxAssetRiskPct = null,
    double? MaxTradeRiskPct = null,
    int? MaxNewTradesPerCycle = null);

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
    double RealizedPnl = 0,
    string Asset = "",
    string Bias = "",
    double EntryNetPremium = 0,
    double CurrentLiquidationValue = 0,
    double MaxProfit = 0,
    double MaxLoss = 0,
    double RewardRiskRatio = 0,
    double ProbabilityOfProfitApprox = 0,
    double ExpectedValue = 0,
    double EntryScore = 0,
    double Confidence = 0,
    double RiskBudgetPct = 0,
    double PortfolioWeightPct = 0,
    string Thesis = "",
    string MathSummary = "",
    IReadOnlyList<string>? Drivers = null,
    IReadOnlyList<ExperimentalBotTradeLeg>? Legs = null,
    string ExitReason = "open");

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
    double RollingDrawdownPct100 = 0,
    int OpenTrades = 0,
    double CapitalUtilizationPct = 0);

public sealed record ExperimentalBotTradeLeg(
    string Symbol,
    string Asset,
    TradeDirection Direction,
    double Quantity,
    double EntryPrice,
    double MarkPrice,
    DateTimeOffset Expiry,
    double Strike,
    OptionRight Right);

public sealed record ExperimentalBotAssetAllocation(
    string Asset,
    int OpenTrades,
    double GrossExposureUsd,
    double OpenRiskUsd,
    double NetPnlUsd,
    double WeightPct);

public sealed record ExperimentalBotPortfolioOverview(
    double StartingCapitalUsd,
    double EquityUsd,
    double PeakEquityUsd,
    double AvailableCapitalUsd,
    double OpenRiskNotionalUsd,
    double DrawdownUsd,
    double DrawdownPct,
    double GrossExposureUsd = 0,
    int OpenTradesCount = 0,
    IReadOnlyList<ExperimentalBotAssetAllocation>? AssetAllocations = null);

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
    string LearningComment,
    string StrategyTemplate = "",
    string Asset = "",
    double MaxLoss = 0,
    double RewardRiskRatio = 0,
    string MathSummary = "");

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

public sealed record ExperimentalBotFeatureContribution(
    string Feature,
    double Weight,
    double FeatureValue,
    double Contribution);

public sealed record NeuralFilterActivation(
    string Name,
    double Activation,
    string Interpretation);

public sealed record NeuralSignalContribution(
    string Name,
    string Bucket,
    double Input,
    double Weight,
    double Contribution,
    string Explanation);

public sealed record NeuralTradeCandidate(
    string Source,
    string Name,
    string Bias,
    double Score,
    double Confidence,
    double ExpectedValue,
    double MaxProfit,
    double MaxLoss,
    double RewardRiskRatio,
    double ProbabilityOfProfitApprox,
    string Thesis);

public sealed record NeuralSignalSnapshot(
    string Asset,
    string Bias,
    string VolatilityBias,
    double Score,
    double Confidence,
    string RecommendedStructure,
    string EntryPlan,
    string ExitPlan,
    string RiskPlan,
    string Summary,
    string MacroReasoning,
    string MicroReasoning,
    string MathReasoning,
    int SequenceLength,
    int ChannelCount,
    IReadOnlyList<NeuralFilterActivation> Filters,
    IReadOnlyList<NeuralSignalContribution> TopPositiveDrivers,
    IReadOnlyList<NeuralSignalContribution> TopNegativeDrivers,
    IReadOnlyList<NeuralTradeCandidate> Candidates,
    DateTimeOffset Timestamp);

public sealed record ExperimentalBotModelExplainSnapshot(
    string Asset,
    string Bias,
    double Score,
    double Confidence,
    IReadOnlyList<ExperimentalBotFeatureContribution> TopPositiveContributors,
    IReadOnlyList<ExperimentalBotFeatureContribution> TopNegativeContributors,
    IReadOnlyList<ExperimentalBotAuditEntry> LatestAuditSamples,
    string Narrative,
    DateTimeOffset Timestamp);

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
    DateTimeOffset Timestamp,
    IReadOnlyList<string>? Assets = null,
    string EngineSummary = "",
    IReadOnlyList<NeuralSignalSnapshot>? NeuralSignals = null);
