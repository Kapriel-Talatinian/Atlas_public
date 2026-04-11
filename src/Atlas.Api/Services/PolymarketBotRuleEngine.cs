using Atlas.Api.Models;

namespace Atlas.Api.Services;

public sealed record PolymarketBotSignalAssessment(
    bool BotEligible,
    string BlockReason,
    double EntryPrice,
    double SelectedEdgePct);

public static class PolymarketBotRuleEngine
{
    public static PolymarketBotSignalAssessment AssessSignal(
        PolymarketMarketSignal signal,
        int lookaheadMinutes)
    {
        var reasons = new List<string>();

        bool pass = string.Equals(signal.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase);
        double entryPrice = UsesPrimaryOutcome(signal.RecommendedSide)
            ? signal.MarketYesPrice
            : signal.MarketNoPrice;
        double selectedEdgePct = Math.Max(signal.EdgeYesPct, signal.EdgeNoPct);

        if (pass)
            reasons.Add("recommended side is Pass");
        if (signal.MinutesToExpiry < 1 || signal.MinutesToExpiry > lookaheadMinutes)
            reasons.Add($"horizon {signal.MinutesToExpiry:0.0}m outside bot window");
        if (signal.ExecutionPlan.IndicativeEntryPrice < 0.05 || signal.ExecutionPlan.IndicativeEntryPrice > 0.92)
            reasons.Add($"indicative entry {signal.ExecutionPlan.IndicativeEntryPrice:0.000} outside [0.05, 0.92]");
        if (selectedEdgePct < 0.015)
            reasons.Add($"edge {selectedEdgePct:P2} below 1.50%");
        if (signal.QualityScore < 46)
            reasons.Add($"quality {signal.QualityScore:0.0} below 46");
        if (signal.ConvictionScore < 55)
            reasons.Add($"conviction {signal.ConvictionScore:0.0} below 55");
        if (signal.LiquidityUsd < 5_000)
            reasons.Add($"liquidity {signal.LiquidityUsd:0} below 5000");
        if (signal.Spread > 0.05)
            reasons.Add($"spread {signal.Spread:P2} above 5%");
        if (signal.DistanceToStrikePct > 0.05)
            reasons.Add($"strike distance {signal.DistanceToStrikePct:P1} above 5%");

        return new PolymarketBotSignalAssessment(
            BotEligible: reasons.Count == 0,
            BlockReason: reasons.Count == 0 ? "bot-ready" : string.Join("; ", reasons),
            EntryPrice: entryPrice,
            SelectedEdgePct: selectedEdgePct);
    }

    public static bool UsesPrimaryOutcome(string side) =>
        side.Equals("Buy Yes", StringComparison.OrdinalIgnoreCase) ||
        side.Equals("Buy Up", StringComparison.OrdinalIgnoreCase);
}
