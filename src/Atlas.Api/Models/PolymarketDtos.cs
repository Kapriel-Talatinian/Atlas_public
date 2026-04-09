namespace Atlas.Api.Models;

public enum PolymarketThresholdRelation
{
    Above,
    Below,
    Between,
    Outside
}

public sealed record PolymarketParsedQuestion(
    string Asset,
    PolymarketThresholdRelation Relation,
    double LowerStrike,
    double? UpperStrike,
    string RawQuestion);

public sealed record PolymarketReferenceAssetSnapshot(
    string Asset,
    double Spot,
    double AtmIv,
    string Regime,
    double RegimeConfidence,
    double LiveBiasScore,
    string LiveBiasLabel,
    DateTimeOffset Timestamp);

public sealed record PolymarketBotTierSnapshot(
    string Name,
    string Status,
    string Summary,
    double Metric,
    string Detail);

public sealed record PolymarketExecutionPlan(
    string Side,
    string OrderStyle,
    double IndicativeEntryPrice,
    double MaxPositionPct,
    int TimeStopMinutes,
    string ExitPlan,
    string RiskPlan);

public sealed record PolymarketMarketSignal(
    string EventId,
    string MarketId,
    string Asset,
    string Question,
    string Slug,
    DateTimeOffset Expiry,
    double MinutesToExpiry,
    double Spot,
    double StrikeLow,
    double? StrikeHigh,
    string ThresholdType,
    string SignalCategory,
    string DisplayLabel,
    string PrimaryOutcomeLabel,
    string SecondaryOutcomeLabel,
    double MarketYesPrice,
    double MarketNoPrice,
    double BestBid,
    double BestAsk,
    double Spread,
    double LiquidityUsd,
    double Volume24hUsd,
    double FairYesProbability,
    double FairNoProbability,
    double EdgeYesPct,
    double EdgeNoPct,
    double DistanceToStrikePct,
    double VolInput,
    double QualityScore,
    double ConvictionScore,
    string RecommendedSide,
    string Summary,
    string MacroReasoning,
    string MicroReasoning,
    string MathReasoning,
    PolymarketExecutionPlan ExecutionPlan,
    bool BotEligible,
    string BotEligibilityReason,
    double BotEntryPrice,
    double BotSelectedEdgePct);

public sealed record PolymarketScanStats(
    int RawEvents,
    int ActiveEvents,
    int RawMarkets,
    int TradeableMarkets,
    int NearExpiryMarkets,
    int ScannerSignals,
    int ActionableSignals);

public sealed record PolymarketRuntimeStatus(
    bool TradingEnabled,
    bool SignerConfigured,
    bool TelegramConfigured,
    bool ExecutionArmed,
    bool DailyLossLockActive,
    string RuntimeMode,
    string WalletAddressHint,
    double MaxTradeUsd,
    double DailyLossLimitUsd,
    string Summary);

public sealed record PolymarketBotPortfolioSnapshot(
    double StartingBalanceUsd,
    double CashBalanceUsd,
    double EquityUsd,
    double AvailableBalanceUsd,
    double GrossExposureUsd,
    double RealizedPnlUsd,
    double UnrealizedPnlUsd,
    double NetPnlUsd,
    double DailyPnlUsd,
    double MonthlyPnlUsd,
    double PeakEquityUsd,
    double DrawdownUsd,
    double DrawdownPct,
    int OpenPositionsCount,
    int ClosedPositionsCount,
    double WinRate,
    double AvgWinnerUsd,
    double AvgLoserUsd,
    double MaxTradeRiskUsd,
    DateTimeOffset Timestamp);

public sealed record PolymarketPosition(
    string PositionId,
    string MarketId,
    string Asset,
    string Question,
    string DisplayLabel,
    string SignalCategory,
    string PrimaryOutcomeLabel,
    string SecondaryOutcomeLabel,
    string Side,
    double StakeUsd,
    double Quantity,
    double EntryPrice,
    double CurrentPrice,
    double CurrentValueUsd,
    double UnrealizedPnlUsd,
    double UnrealizedPnlPct,
    double MaxLossUsd,
    double MaxPayoutUsd,
    double MaxProfitUsd,
    double ExpectedValueUsd,
    double RiskRewardRatio,
    double FairProbability,
    double MarketProbability,
    double EdgePct,
    double QualityScore,
    double ConvictionScore,
    string Thesis,
    string MacroReasoning,
    string MicroReasoning,
    string MathReasoning,
    string RiskPlan,
    DateTimeOffset EntryTime,
    DateTimeOffset Expiry,
    DateTimeOffset TimeStopAt,
    bool IsOpen,
    DateTimeOffset? ExitTime = null,
    double ExitPrice = 0,
    double RealizedPnlUsd = 0,
    string ExitReason = "open");

public sealed record PolymarketJournalEntry(
    DateTimeOffset Timestamp,
    string Type,
    string Headline,
    string Detail,
    string? PositionId,
    string? MarketId);

public sealed record PolymarketLiveSnapshot(
    string Status,
    string Summary,
    PolymarketRuntimeStatus Runtime,
    IReadOnlyList<PolymarketReferenceAssetSnapshot> Assets,
    IReadOnlyList<PolymarketBotTierSnapshot> BotTiers,
    IReadOnlyList<PolymarketMarketSignal> Opportunities,
    PolymarketScanStats Stats,
    IReadOnlyList<string> Notes,
    PolymarketBotPortfolioSnapshot Portfolio,
    IReadOnlyList<PolymarketPosition> OpenPositions,
    IReadOnlyList<PolymarketPosition> RecentClosedPositions,
    IReadOnlyList<PolymarketJournalEntry> Journal,
    DateTimeOffset Timestamp);
