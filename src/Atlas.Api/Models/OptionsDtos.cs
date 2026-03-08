using Atlas.Core.Common;

namespace Atlas.Api.Models;

public enum OptionRight
{
    Call,
    Put
}

public enum TradeDirection
{
    Buy,
    Sell
}

public sealed record LiveOptionQuote(
    string Symbol,
    string Asset,
    DateTimeOffset Expiry,
    double Strike,
    OptionRight Right,
    double Bid,
    double Ask,
    double Mark,
    double Mid,
    double MarkIv,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    double OpenInterest,
    double Volume24h,
    double Turnover24h,
    double UnderlyingPrice,
    DateTimeOffset Timestamp,
    string Venue = "UNKNOWN",
    DateTimeOffset? SourceTimestamp = null,
    bool IsStale = false);

public sealed record ExpiryTermPoint(
    DateTimeOffset Expiry,
    int DaysToExpiry,
    double AtmIv);

public sealed record AssetMarketOverview(
    string Asset,
    double UnderlyingPrice,
    double AtmIv,
    double RiskReversal25D,
    int ListedOptions,
    double OpenInterest,
    double Turnover24h,
    double PutCallOpenInterestRatio,
    IReadOnlyList<ExpiryTermPoint> TermStructure,
    DateTimeOffset Timestamp);

public sealed record VolSurfacePoint(
    string Asset,
    DateTimeOffset Expiry,
    int DaysToExpiry,
    double Strike,
    double Moneyness,
    OptionRight Right,
    double MarkIv);

public sealed record StrategyLegDefinition(
    string Symbol,
    TradeDirection Direction,
    double Quantity);

public sealed record StrategyAnalyzeRequest(
    string Asset,
    string Name,
    IReadOnlyList<StrategyLegDefinition> Legs,
    double ShockRangePct = 0.35,
    int GridPoints = 121);

public sealed record StrategyLegAnalysis(
    string Symbol,
    TradeDirection Direction,
    double Quantity,
    DateTimeOffset Expiry,
    double Strike,
    OptionRight Right,
    double EntryPrice,
    double MarkPrice,
    double MarkIv,
    GreeksResult Greeks);

public sealed record PayoffPoint(
    double Spot,
    double Pnl);

public sealed record StrategyAnalysisResult(
    string Name,
    string Asset,
    double UnderlyingPrice,
    double NetPremium,
    double MaxProfit,
    double MaxLoss,
    IReadOnlyList<double> Breakevens,
    GreeksResult AggregateGreeks,
    IReadOnlyList<StrategyLegAnalysis> Legs,
    IReadOnlyList<PayoffPoint> PayoffCurve,
    DateTimeOffset Timestamp);

public sealed record OptionModelSnapshot(
    string Symbol,
    string Asset,
    DateTimeOffset Expiry,
    double Strike,
    OptionRight Right,
    double Spot,
    double TimeToExpiryYears,
    double Bid,
    double Ask,
    double Mid,
    double Mark,
    double MarkIv,
    double FairBs,
    double FairHeston,
    double FairSabr,
    double FairComposite,
    double ModelDispersionPct,
    double EdgeVsMidPct,
    double EdgeVsMarkPct,
    double ImpliedMoveAbs,
    double ImpliedMovePct,
    double ProbItm,
    double ProbTouchApprox,
    double LiquidityScore,
    double ConfidenceScore,
    string Signal,
    GreeksResult Greeks,
    DateTimeOffset Timestamp);

public sealed record ModelFitMetric(
    string Model,
    double MeanAbsErrorPct,
    double RootMeanSquareErrorPct,
    int SampleCount);

public sealed record ModelCalibrationSnapshot(
    string Asset,
    DateTimeOffset? Expiry,
    double Spot,
    double AtmIv30D,
    double Skew25D,
    double TermSlope30To90,
    double HestonKappa,
    double HestonTheta,
    double HestonXi,
    double HestonRho,
    double SabrAlpha,
    double SabrBeta,
    double SabrRho,
    double SabrNu,
    double ConfidenceScore,
    IReadOnlyList<ModelFitMetric> FitMetrics,
    DateTimeOffset Timestamp);

public sealed record OptionSignalRow(
    string Symbol,
    DateTimeOffset Expiry,
    double Strike,
    OptionRight Right,
    double Mid,
    double MarkIv,
    double FairComposite,
    double EdgePct,
    double ProbItm,
    double ProbTouchApprox,
    double LiquidityScore,
    double ConfidenceScore,
    string Signal,
    DateTimeOffset Timestamp);

public sealed record OptionSignalBoard(
    string Asset,
    DateTimeOffset? Expiry,
    double Spot,
    IReadOnlyList<OptionSignalRow> TopLongEdges,
    IReadOnlyList<OptionSignalRow> TopShortEdges,
    DateTimeOffset Timestamp);

public sealed record VolRegimeSnapshot(
    string Asset,
    double Spot,
    DateTimeOffset FrontExpiry,
    double AtmIvFront,
    double AtmIv30D,
    double AtmIv90D,
    double TermSlope30To90,
    double Skew25D,
    double PutCallOpenInterestRatio,
    double ExpectedMove7DAbs,
    double ExpectedMove7DPct,
    double ExpectedMove30DAbs,
    double ExpectedMove30DPct,
    string Regime,
    string Signal,
    double ConfidenceScore,
    DateTimeOffset Timestamp);

public sealed record StrategyRecommendation(
    string Name,
    double Score,
    double EdgeScorePct,
    double ConfidenceScore,
    double RegimeFitScore,
    string RiskLabel,
    string Thesis,
    StrategyAnalysisResult Analysis);

public sealed record StrategyRecommendationBoard(
    string Asset,
    string RiskProfile,
    VolRegimeSnapshot Regime,
    IReadOnlyList<StrategyRecommendation> Recommendations,
    DateTimeOffset Timestamp);

public sealed record StrategyOptimizationEntry(
    string Name,
    double Score,
    double DistanceScore,
    double SuggestedSizeMultiplier,
    GreeksResult ProjectedGreeks,
    double EdgeScorePct,
    double ConfidenceScore,
    double RegimeFitScore,
    string RiskLabel,
    string Thesis,
    StrategyAnalysisResult Analysis);

public sealed record StrategyOptimizationBoard(
    string Asset,
    string RiskProfile,
    double TargetDelta,
    double TargetVega,
    double TargetTheta,
    IReadOnlyList<StrategyOptimizationEntry> Entries,
    DateTimeOffset Timestamp);

public sealed record GreeksExposureCell(
    DateTimeOffset Expiry,
    int DaysToExpiry,
    double Strike,
    double DistanceToSpotPct,
    double OpenInterest,
    double Volume24h,
    double DeltaExposure,
    double GammaExposure,
    double VegaExposure,
    double ThetaExposure,
    double PinRiskScore);

public sealed record GreeksExposureHotspot(
    DateTimeOffset Expiry,
    int DaysToExpiry,
    double Strike,
    double DistanceToSpotPct,
    double OpenInterest,
    double GammaExposure,
    double VegaExposure,
    double PinRiskScore);

public sealed record GreeksExposureGrid(
    string Asset,
    double Spot,
    IReadOnlyList<GreeksExposureCell> Cells,
    IReadOnlyList<GreeksExposureHotspot> TopHotspots,
    DateTimeOffset Timestamp);

public sealed record ArbitrageAnomaly(
    string Type,
    DateTimeOffset Expiry,
    OptionRight Right,
    string SymbolA,
    string? SymbolB,
    string? SymbolC,
    double StrikeA,
    double StrikeB,
    double StrikeC,
    double Metric,
    double Threshold,
    double SeverityScore,
    string Description,
    double LiquidityScore = 0,
    double EstimatedCostPct = 0,
    double TradeabilityScore = 0);

public sealed record ArbitrageScanResult(
    string Asset,
    DateTimeOffset? Expiry,
    int Count,
    IReadOnlyList<ArbitrageAnomaly> Anomalies,
    DateTimeOffset Timestamp);
