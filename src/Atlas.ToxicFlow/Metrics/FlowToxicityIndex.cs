using Atlas.Core.Common;
using Atlas.ToxicFlow.Models;

namespace Atlas.ToxicFlow.Metrics;

/// <summary>
/// Flow Toxicity Index — inspired by VPIN (Volume-Synchronized Probability of Informed Trading).
///
/// Measures the fraction of volume that appears to come from informed traders:
/// - Classifies each volume bucket as buy-initiated or sell-initiated (using tick rule)
/// - Computes order imbalance across rolling buckets
/// - High imbalance = high probability of informed trading
///
/// Also computes per-counterparty Adverse Selection Score based on:
/// - Consistent markout direction (always winning → informed)
/// - Timing patterns (trades cluster around events)
/// - Size patterns (informed traders size up when confident)
/// - Latency patterns (fastest fills correlate with toxicity)
/// </summary>
public class FlowToxicityIndex
{
    private readonly int _bucketSize;
    private readonly int _numBuckets;

    public FlowToxicityIndex(int bucketSize = 50, int numBuckets = 20)
    {
        _bucketSize = bucketSize;
        _numBuckets = numBuckets;
    }

    /// <summary>
    /// Compute VPIN-like toxicity index for a set of trades.
    /// Returns value in [0,1]: 0 = all benign, 1 = all informed.
    /// </summary>
    public double ComputeVpin(IReadOnlyList<Trade> trades)
    {
        if (trades.Count < _bucketSize) return 0;

        var buckets = new List<(double buyVol, double sellVol)>();
        double currentBuyVol = 0, currentSellVol = 0;
        int currentCount = 0;

        foreach (var trade in trades.OrderBy(t => t.Timestamp))
        {
            double vol = Math.Abs(trade.Price * trade.Quantity);
            if (trade.Side == Side.Buy) currentBuyVol += vol;
            else currentSellVol += vol;

            currentCount++;
            if (currentCount >= _bucketSize)
            {
                buckets.Add((currentBuyVol, currentSellVol));
                currentBuyVol = 0; currentSellVol = 0; currentCount = 0;
            }
        }

        if (buckets.Count < 2) return 0;

        // VPIN = average |buyVol - sellVol| / (buyVol + sellVol) over rolling window
        int window = Math.Min(_numBuckets, buckets.Count);
        double sumImbalance = 0;
        double sumVolume = 0;
        for (int i = buckets.Count - window; i < buckets.Count; i++)
        {
            sumImbalance += Math.Abs(buckets[i].buyVol - buckets[i].sellVol);
            sumVolume += buckets[i].buyVol + buckets[i].sellVol;
        }

        return sumVolume > 0 ? sumImbalance / sumVolume : 0;
    }

    /// <summary>
    /// Score a counterparty's adverse selection risk based on their trade history.
    /// Returns score in [0,1]: 0 = harmless, 1 = maximum toxicity.
    /// </summary>
    public double AdverseSelectionScore(IReadOnlyList<(Trade trade, MarkoutResult markout)> history)
    {
        if (history.Count < 3) return 0;

        double score = 0;
        int factors = 0;

        // Factor 1: Markout consistency (0-0.3)
        // How often does the trade move in the taker's favor?
        int positive5s = history.Count(h => h.markout.Markout5s > 0);
        double winRate = (double)positive5s / history.Count;
        score += Math.Max(0, (winRate - 0.5) * 2) * 0.30;
        factors++;

        // Factor 2: Markout magnitude (0-0.25)
        double avgMarkout5s = history.Average(h => h.markout.Markout5s);
        double avgNotional = history.Average(h => Math.Abs(h.trade.Price * h.trade.Quantity));
        if (avgNotional > 0)
        {
            double markoutBps = avgMarkout5s / avgNotional * 10000;
            score += MathUtils.Clamp(markoutBps / 20, 0, 0.25);
        }
        factors++;

        // Factor 3: Latency pattern (0-0.20)
        double avgLatency = history.Average(h => h.trade.LatencyToFill.TotalMilliseconds);
        if (avgLatency < 5) score += 0.20;
        else if (avgLatency < 20) score += 0.10;
        factors++;

        // Factor 4: Size clustering (0-0.15)
        // Toxic flow tends to size up before big moves
        double sizeStdDev = StandardDev(history.Select(h => h.trade.Quantity));
        double sizeMean = history.Average(h => h.trade.Quantity);
        double sizeCV = sizeMean > 0 ? sizeStdDev / sizeMean : 0;
        if (sizeCV > 1.5) score += 0.15; // high variance = strategic sizing
        factors++;

        // Factor 5: Timing clustering (0-0.10)
        var timestamps = history.Select(h => h.trade.Timestamp).OrderBy(t => t).ToList();
        if (timestamps.Count > 2)
        {
            var gaps = timestamps.Zip(timestamps.Skip(1), (a, b) => (b - a).TotalSeconds).ToList();
            double gapStdDev = StandardDev(gaps);
            double gapMean = gaps.Average();
            if (gapMean > 0 && gapStdDev / gapMean > 2.0) score += 0.10; // bursty = event-driven
        }
        factors++;

        return MathUtils.Clamp(score, 0, 1);
    }

    private static double StandardDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        if (list.Count < 2) return 0;
        double mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / (list.Count - 1));
    }
}
