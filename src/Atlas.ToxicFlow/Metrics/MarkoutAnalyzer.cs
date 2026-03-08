using Atlas.Core.Common;
using Atlas.ToxicFlow.Models;

namespace Atlas.ToxicFlow.Metrics;

/// <summary>
/// Markout P&L Analyzer.
/// Measures how much a trade moves against the market maker after execution.
///
/// Key insight: If a taker's trades consistently show positive markout (the price moves
/// in their favor after the fill), they are likely informed and the MM is being adversely selected.
///
/// Horizons: 1s, 5s, 30s, 5m, 30m — each reveals different information:
/// - 1s/5s: latency arbitrage, stale quote sniping
/// - 30s/5m: informed directional flow
/// - 30m+: strategy-level information advantage
/// </summary>
public class MarkoutAnalyzer
{
    /// <summary>
    /// Compute markout for a trade given subsequent price observations.
    /// In demo mode, simulates realistic markout patterns.
    /// In production, feed actual post-trade price ticks.
    /// </summary>
    public MarkoutResult ComputeMarkout(Trade trade, IReadOnlyList<(TimeSpan offset, double spot, double iv)>? postTradePrices = null)
    {
        if (postTradePrices != null && postTradePrices.Count > 0)
            return ComputeRealMarkout(trade, postTradePrices);

        return ComputeDemoMarkout(trade);
    }

    private MarkoutResult ComputeRealMarkout(Trade trade, IReadOnlyList<(TimeSpan offset, double spot, double iv)> prices)
    {
        // PRODUCTION PATH: use actual post-trade prices
        double sign = trade.Side == Side.Buy ? 1.0 : -1.0;
        double baseSpot = trade.SpotAtExecution;
        double baseIv = trade.ImpliedVol;

        double FindSpotAt(TimeSpan target)
        {
            var closest = prices.MinBy(p => Math.Abs((p.offset - target).TotalMilliseconds));
            return closest.spot;
        }

        double FindIvAt(TimeSpan target)
        {
            var closest = prices.MinBy(p => Math.Abs((p.offset - target).TotalMilliseconds));
            return closest.iv;
        }

        double spot1s = FindSpotAt(TimeSpan.FromSeconds(1));
        double spot5s = FindSpotAt(TimeSpan.FromSeconds(5));
        double spot30s = FindSpotAt(TimeSpan.FromSeconds(30));
        double spot5m = FindSpotAt(TimeSpan.FromMinutes(5));
        double spot30m = FindSpotAt(TimeSpan.FromMinutes(30));
        double iv5m = FindIvAt(TimeSpan.FromMinutes(5));

        // Markout = sign * delta * (spotMove) + sign * vega * (ivMove)
        double delta = trade.DeltaAtExecution;
        double vega = trade.GreeksAtExecution.Vega;

        return new MarkoutResult(
            TradeId: trade.TradeId,
            Markout1s: sign * delta * (spot1s - baseSpot) * trade.Quantity,
            Markout5s: sign * delta * (spot5s - baseSpot) * trade.Quantity,
            Markout30s: sign * delta * (spot30s - baseSpot) * trade.Quantity,
            Markout5m: sign * (delta * (spot5m - baseSpot) + vega * (iv5m - baseIv) * 100) * trade.Quantity,
            Markout30m: sign * delta * (spot30m - baseSpot) * trade.Quantity,
            MarkoutExpiry: 0,
            SpotMove1s: spot1s - baseSpot,
            SpotMove5s: spot5s - baseSpot,
            IvMove5m: iv5m - baseIv);
    }

    /// <summary>
    /// DEMO MODE: Generate realistic markout patterns based on trade characteristics.
    /// Different flow types have different markout signatures.
    /// Swap this with real market data feed for production.
    /// </summary>
    private MarkoutResult ComputeDemoMarkout(Trade trade)
    {
        var rng = new Random(trade.TradeId.GetHashCode());
        double sign = trade.Side == Side.Buy ? 1.0 : -1.0;

        // Base random walk
        double drift = (rng.NextDouble() - 0.48) * 0.001; // slight adverse selection bias
        double vol = 0.0005; // 5 bps per sqrt(second)

        // Some trades are genuinely toxic (20% of flow typically)
        bool isToxic = rng.NextDouble() < 0.22;
        if (isToxic) drift = sign * Math.Abs(drift) * 3; // informed flow moves in their favor

        // Latency arb pattern: big 1s markout, mean-reverts
        bool isLatencyArb = trade.LatencyToFill.TotalMilliseconds < 5 && rng.NextDouble() < 0.3;
        double latencyBoost = isLatencyArb ? sign * 0.002 : 0;

        double spot = trade.SpotAtExecution;
        double m1s = (drift * 1 + latencyBoost * 2 + rng.NextGaussian() * vol * 1) * spot;
        double m5s = (drift * 5 + latencyBoost * 0.5 + rng.NextGaussian() * vol * 2.2) * spot;
        double m30s = (drift * 30 + rng.NextGaussian() * vol * 5.5) * spot;
        double m5m = (drift * 300 + rng.NextGaussian() * vol * 17) * spot;
        double m30m = (drift * 1800 + rng.NextGaussian() * vol * 42) * spot;

        double delta = trade.DeltaAtExecution;
        return new MarkoutResult(
            TradeId: trade.TradeId,
            Markout1s: sign * delta * m1s * trade.Quantity,
            Markout5s: sign * delta * m5s * trade.Quantity,
            Markout30s: sign * delta * m30s * trade.Quantity,
            Markout5m: sign * delta * m5m * trade.Quantity,
            Markout30m: sign * delta * m30m * trade.Quantity,
            MarkoutExpiry: 0,
            SpotMove1s: m1s,
            SpotMove5s: m5s,
            IvMove5m: (rng.NextDouble() - 0.5) * 0.02);
    }
}

/// <summary>Extension for Gaussian random.</summary>
internal static class RandomExtensions
{
    public static double NextGaussian(this Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
