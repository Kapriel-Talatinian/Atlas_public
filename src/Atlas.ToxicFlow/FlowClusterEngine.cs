using Atlas.Core.Common;
using Atlas.ToxicFlow.Models;
using Atlas.ToxicFlow.Metrics;

namespace Atlas.ToxicFlow;

/// <summary>
/// Flow Clustering Engine.
/// Classifies trade flow into toxicity clusters based on multi-dimensional features:
///
/// Feature vector per trade:
/// - markout_1s, markout_5s, markout_5m (direction + magnitude)
/// - latency_ms (fill speed)
/// - delta_at_exec (directional exposure taken)
/// - iv_move_5m (vol prediction accuracy)
/// - size_vs_avg (relative trade size)
/// - time_to_expiry (proximity to settlement)
/// - is_package_leg (part of a multi-leg)
/// - venue_type (CLOB vs RFQ vs Block)
///
/// Classification uses rule-based heuristics (swap with ML model for production):
/// - k-means clustering on feature vectors
/// - Decision tree for label assignment
///
/// For production: train XGBoost on labeled toxic flow data.
/// </summary>
public class FlowClusterEngine
{
    private readonly MarkoutAnalyzer _markoutAnalyzer = new();
    private readonly FlowToxicityIndex _toxicityIndex = new();

    /// <summary>
    /// Enrich a trade with toxicity features and classify into a cluster.
    /// </summary>
    public EnrichedFlow ClassifyTrade(Trade trade, IReadOnlyList<Trade>? recentTrades = null)
    {
        var markout = _markoutAnalyzer.ComputeMarkout(trade);
        var features = ExtractFeatures(trade, markout, recentTrades);
        var (clusterType, flags) = ClassifyCluster(features, trade, markout);
        double toxScore = ComputeToxicityScore(features, markout);
        var level = ScoreToLevel(toxScore);

        return new EnrichedFlow(trade, markout, toxScore, level, clusterType, flags, features);
    }

    /// <summary>
    /// Classify an entire batch of trades and produce cluster summaries.
    /// </summary>
    public ToxicFlowDashboard AnalyzeBatch(IReadOnlyList<Trade> trades)
    {
        var enriched = trades.Select(t => ClassifyTrade(t, trades)).ToList();
        var toxicTrades = enriched.Where(e => e.Level >= ToxicityLevel.Medium).ToList();

        // Group into clusters
        var clusters = enriched
            .GroupBy(e => e.ClusterType)
            .Select(g => new FlowCluster(
                Type: g.Key,
                Label: ClusterLabel(g.Key),
                Description: ClusterDescription(g.Key),
                TradeCount: g.Count(),
                AvgToxicityScore: g.Average(e => e.ToxicityScore),
                TotalNotional: g.Sum(e => Math.Abs(e.Trade.Price * e.Trade.Quantity)),
                AvgMarkout5s: g.Where(e => e.Markout != null).DefaultIfEmpty().Average(e => e?.Markout?.Markout5s ?? 0),
                PnLImpact: g.Where(e => e.Markout != null).Sum(e => e.Markout?.Markout5m ?? 0),
                AvgLevel: ScoreToLevel(g.Average(e => e.ToxicityScore)),
                TopCounterparties: g.Where(e => e.Trade.CounterpartyId != null)
                    .GroupBy(e => e.Trade.CounterpartyId!)
                    .OrderByDescending(cg => cg.Average(e => e.ToxicityScore))
                    .Take(3)
                    .Select(cg => cg.Key)
                    .ToList()))
            .OrderByDescending(c => c.AvgToxicityScore)
            .ToList();

        // Per-counterparty profiles
        var cpProfiles = enriched
            .Where(e => e.Trade.CounterpartyId != null)
            .GroupBy(e => e.Trade.CounterpartyId!)
            .Select(g =>
            {
                var withMarkout = g.Where(e => e.Markout != null).ToList();
                var markoutHistory = withMarkout
                    .Select(e => (e.Trade, e.Markout!))
                    .ToList();
                double avgToxicity = g.Average(e => e.ToxicityScore);
                double adverseSelectionScore = markoutHistory.Count > 0
                    ? _toxicityIndex.AdverseSelectionScore(markoutHistory)
                    : avgToxicity;
                double profileScore = MathUtils.Clamp((avgToxicity + adverseSelectionScore) / 2.0, 0, 1);

                return new CounterpartyProfile(
                    CounterpartyId: g.Key,
                    TradeCount: g.Count(),
                    AvgMarkout5s: withMarkout.DefaultIfEmpty().Average(e => e?.Markout?.Markout5s ?? 0),
                    AvgMarkout5m: withMarkout.DefaultIfEmpty().Average(e => e?.Markout?.Markout5m ?? 0),
                    AdverseSelectionScore: adverseSelectionScore,
                    FlowToxicityIndex: _toxicityIndex.ComputeVpin(g.Select(e => e.Trade).ToList()),
                    AvgLatencyMs: g.Average(e => e.Trade.LatencyToFill.TotalMilliseconds),
                    PctDirectional: ComputeDirectionalPct(g.Select(e => e.Trade).ToList()),
                    AvgSizeUsd: g.Average(e => Math.Abs(e.Trade.Price * e.Trade.Quantity)),
                    Level: ScoreToLevel(profileScore),
                    ClusterType: g.GroupBy(e => e.ClusterType).OrderByDescending(cg => cg.Count()).First().Key,
                    Flags: g.SelectMany(e => e.Flags).Distinct().ToList());
            })
            .OrderByDescending(cp => cp.AdverseSelectionScore)
            .ToList();
        double toxicNotional = toxicTrades.Sum(e => Math.Abs(e.Trade.Price * e.Trade.Quantity));

        return new ToxicFlowDashboard(
            Timestamp: DateTimeOffset.UtcNow,
            TotalTrades: trades.Count,
            ToxicTrades: toxicTrades.Count,
            ToxicPct: trades.Count > 0 ? (double)toxicTrades.Count / trades.Count * 100 : 0,
            TotalToxicNotional: toxicNotional,
            EstimatedAdverseSelectionCost: toxicTrades.Where(e => e.Markout != null).Sum(e => Math.Abs(e.Markout!.Markout5m)),
            Clusters: clusters,
            TopToxicCounterparties: cpProfiles.Where(cp => cp.Level >= ToxicityLevel.Medium).Take(10).ToList(),
            RecentAlerts: enriched.Where(e => e.Level >= ToxicityLevel.High).OrderByDescending(e => e.ToxicityScore).Take(20).ToList());
    }

    // ─── Feature Extraction ──────────────────────────────────

    private Dictionary<string, double> ExtractFeatures(Trade trade, MarkoutResult markout, IReadOnlyList<Trade>? recent)
    {
        double avgSize = recent?.Average(t => t.Quantity) ?? trade.Quantity;
        return new Dictionary<string, double>
        {
            ["markout_1s"] = markout.Markout1s,
            ["markout_5s"] = markout.Markout5s,
            ["markout_5m"] = markout.Markout5m,
            ["latency_ms"] = trade.LatencyToFill.TotalMilliseconds,
            ["delta"] = Math.Abs(trade.DeltaAtExecution),
            ["iv_move_5m"] = Math.Abs(markout.IvMove5m),
            ["size_ratio"] = avgSize > 0 ? trade.Quantity / avgSize : 1,
            ["is_package"] = trade.PackageId != null ? 1 : 0,
            ["spot_move_1s"] = Math.Abs(markout.SpotMove1s),
            ["spot_move_5s"] = Math.Abs(markout.SpotMove5s),
        };
    }

    // ─── Cluster Classification (rule-based, swap with ML) ───

    private (FlowClusterType type, List<string> flags) ClassifyCluster(
        Dictionary<string, double> features, Trade trade, MarkoutResult markout)
    {
        var flags = new List<string>();

        // Stale quote sniper: very fast fill + big 1s markout that mean-reverts
        if (features["latency_ms"] < 5 && Math.Abs(markout.Markout1s) > Math.Abs(markout.Markout5m) * 2)
        {
            flags.Add("FAST_FILL"); flags.Add("MEAN_REVERTING_MARKOUT");
            return (FlowClusterType.StaleQuoteSniper, flags);
        }

        // Vol informed: big IV move after trade
        if (features["iv_move_5m"] > 0.01)
        {
            flags.Add("PRE_VOL_MOVE");
            return (FlowClusterType.VolInformed, flags);
        }

        // Expiry manipulation: trade near settlement, large size
        if (trade.LatencyToFill.TotalMilliseconds < 10 && features["size_ratio"] > 3)
        {
            flags.Add("LARGE_NEAR_EXPIRY");
            return (FlowClusterType.ExpiryManipulator, flags);
        }

        // Informed directional: consistent positive markout across horizons
        if (markout.Markout5s > 0 && markout.Markout5m > 0 && markout.Markout30m > 0)
        {
            flags.Add("CONSISTENT_WINNER");
            return (FlowClusterType.InformedDirectional, flags);
        }

        // Momentum chaser: follows recent trend
        if (features["spot_move_5s"] > features["spot_move_1s"] * 1.5 && markout.Markout5m > 0)
        {
            flags.Add("TREND_FOLLOWING");
            return (FlowClusterType.MomentumChaser, flags);
        }

        // Package legger
        if (features["is_package"] > 0 && markout.Markout5s > 0)
        {
            flags.Add("PACKAGE_LEG_EXPLOIT");
            return (FlowClusterType.PackageLegger, flags);
        }

        // Gamma scalper: small delta, high gamma, frequent trading
        if (Math.Abs(trade.DeltaAtExecution) < 0.1 && Math.Abs(trade.GreeksAtExecution.Gamma) > 0.001)
        {
            return (FlowClusterType.GammaScalper, flags);
        }

        // Default: benign
        if (Math.Abs(markout.Markout5s) > 500 || Math.Abs(markout.Markout5m) > 2000)
        {
            flags.Add("ELEVATED_MARKOUT");
            return (FlowClusterType.Suspicious, flags);
        }

        return (FlowClusterType.Benign, flags);
    }

    // ─── Scoring ─────────────────────────────────────────────

    private double ComputeToxicityScore(Dictionary<string, double> features, MarkoutResult markout)
    {
        double score = 0;

        // Markout-based (0-40%)
        double m5sNorm = MathUtils.Clamp(Math.Abs(markout.Markout5s) / 1000, 0, 0.2);
        double m5mNorm = MathUtils.Clamp(Math.Abs(markout.Markout5m) / 5000, 0, 0.2);
        score += m5sNorm + m5mNorm;

        // Latency-based (0-20%)
        if (features["latency_ms"] < 2) score += 0.20;
        else if (features["latency_ms"] < 10) score += 0.10;

        // Size-based (0-20%)
        score += MathUtils.Clamp((features["size_ratio"] - 1) / 5, 0, 0.20);

        // Vol prediction (0-20%)
        score += MathUtils.Clamp(features["iv_move_5m"] * 10, 0, 0.20);

        return MathUtils.Clamp(score, 0, 1);
    }

    private static ToxicityLevel ScoreToLevel(double score) => score switch
    {
        < 0.15 => ToxicityLevel.Safe,
        < 0.30 => ToxicityLevel.Low,
        < 0.50 => ToxicityLevel.Medium,
        < 0.75 => ToxicityLevel.High,
        _ => ToxicityLevel.Critical,
    };

    private static double ComputeDirectionalPct(IReadOnlyList<Trade> trades)
    {
        if (trades.Count == 0) return 0;
        int buys = trades.Count(t => t.Side == Side.Buy);
        return Math.Abs((double)buys / trades.Count - 0.5) * 2; // 0 = balanced, 1 = one-sided
    }

    private static string ClusterLabel(FlowClusterType t) => t switch
    {
        FlowClusterType.Benign => "Benign Flow",
        FlowClusterType.StaleQuoteSniper => "Stale Quote Sniper",
        FlowClusterType.InformedDirectional => "Informed Directional",
        FlowClusterType.VolInformed => "Vol-Informed",
        FlowClusterType.MomentumChaser => "Momentum Chaser",
        FlowClusterType.PackageLegger => "Package Legger",
        FlowClusterType.GammaScalper => "Gamma Scalper",
        FlowClusterType.SurfaceSpoofer => "Surface Spoofer",
        FlowClusterType.ExpiryManipulator => "Expiry Manipulator",
        _ => "Suspicious"
    };

    private static string ClusterDescription(FlowClusterType t) => t switch
    {
        FlowClusterType.StaleQuoteSniper => "Latency arb: fills stale quotes before MM updates. Fast fill + mean-reverting markout.",
        FlowClusterType.InformedDirectional => "Consistently profitable trades across all markout horizons. Likely has information edge.",
        FlowClusterType.VolInformed => "Trades before large IV moves. Vol event front-running or superior vol forecasting.",
        FlowClusterType.MomentumChaser => "Follows short-term price trends. Adversely selects post-fill via momentum.",
        FlowClusterType.PackageLegger => "Splits multi-leg packages to exploit individual leg pricing vs MMs.",
        FlowClusterType.GammaScalper => "Accumulates gamma positions, delta-hedges frequently against MM quotes.",
        FlowClusterType.ExpiryManipulator => "Large trades near settlement attempting to push mark/index price.",
        _ => "Normal market flow or unclassified pattern."
    };
}
