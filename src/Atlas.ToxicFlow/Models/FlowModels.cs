using Atlas.Core.Common;

namespace Atlas.ToxicFlow.Models;

/// <summary>Flow toxicity classification.</summary>
public enum ToxicityLevel { Safe, Low, Medium, High, Critical }

/// <summary>Flow cluster type identified by the clustering engine.</summary>
public enum FlowClusterType
{
    /// <summary>Normal market-making flow, fair two-sided.</summary>
    Benign,
    /// <summary>Latency arbitrage — sniping stale quotes before MM can update.</summary>
    StaleQuoteSniper,
    /// <summary>Informed directional flow — consistently correct on direction.</summary>
    InformedDirectional,
    /// <summary>Vol-informed — trades before large IV moves, vol event front-running.</summary>
    VolInformed,
    /// <summary>Momentum chaser — follows short-term trends, adversely selects post-fill.</summary>
    MomentumChaser,
    /// <summary>Package legging exploit — splits packages to leg against MMs.</summary>
    PackageLegger,
    /// <summary>Gamma scalper — accumulates gamma, delta-hedges frequently vs MMs.</summary>
    GammaScalper,
    /// <summary>Surface spoofer — places misleading vol quotes to move the surface.</summary>
    SurfaceSpoofer,
    /// <summary>Expiry manipulator — trades near settlement to push mark/index.</summary>
    ExpiryManipulator,
    /// <summary>Unclassified but suspicious pattern.</summary>
    Suspicious,
}

/// <summary>Markout P&L measurement at various horizons.</summary>
public sealed record MarkoutResult(
    string TradeId,
    double Markout1s,     // 1 second
    double Markout5s,     // 5 seconds
    double Markout30s,    // 30 seconds
    double Markout5m,     // 5 minutes
    double Markout30m,    // 30 minutes
    double MarkoutExpiry, // at option expiry (if available)
    double SpotMove1s,
    double SpotMove5s,
    double IvMove5m);

/// <summary>Per-counterparty toxicity profile.</summary>
public sealed record CounterpartyProfile(
    string CounterpartyId,
    int TradeCount,
    double AvgMarkout5s,
    double AvgMarkout5m,
    double AdverseSelectionScore,  // 0-1, higher = more toxic
    double FlowToxicityIndex,     // VPIN-like metric
    double AvgLatencyMs,
    double PctDirectional,         // % of trades that are one-sided
    double AvgSizeUsd,
    ToxicityLevel Level,
    FlowClusterType ClusterType,
    List<string> Flags);

/// <summary>A single flow event enriched with toxicity metrics.</summary>
public sealed record EnrichedFlow(
    Trade Trade,
    MarkoutResult? Markout,
    double ToxicityScore,
    ToxicityLevel Level,
    FlowClusterType ClusterType,
    List<string> Flags,
    Dictionary<string, double> Features);

/// <summary>Cluster summary for visualization.</summary>
public sealed record FlowCluster(
    FlowClusterType Type,
    string Label,
    string Description,
    int TradeCount,
    double AvgToxicityScore,
    double TotalNotional,
    double AvgMarkout5s,
    double PnLImpact,
    ToxicityLevel AvgLevel,
    List<string> TopCounterparties);

/// <summary>Aggregated toxic flow dashboard snapshot.</summary>
public sealed record ToxicFlowDashboard(
    DateTimeOffset Timestamp,
    int TotalTrades,
    int ToxicTrades,
    double ToxicPct,
    double TotalToxicNotional,
    double EstimatedAdverseSelectionCost,
    List<FlowCluster> Clusters,
    List<CounterpartyProfile> TopToxicCounterparties,
    List<EnrichedFlow> RecentAlerts);
