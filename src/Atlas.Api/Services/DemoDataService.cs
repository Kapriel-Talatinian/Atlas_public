using Atlas.Core.Common;
using Atlas.Core.Models;

namespace Atlas.Api.Services;

/// <summary>
/// DEMO DATA SERVICE
/// Generates realistic trade flow with controlled toxic patterns.
///
/// ┌─────────────────────────────────────────────────────┐
/// │  FOR PRODUCTION: Replace this class with:            │
/// │  - DeribitWebSocketFeed for live trades              │
/// │  - Database reader for historical analysis           │
/// │  - Kafka/Redis stream consumer for real-time flow    │
/// │                                                      │
/// │  Interface: IMarketDataProvider                       │
/// │  Swap DemoDataService → LiveDataService in DI        │
/// └─────────────────────────────────────────────────────┘
/// </summary>
public class DemoDataService : IMarketDataProvider
{
    private readonly Random _rng = new(42);
    private readonly List<Trade> _tradeHistory = new();
    private readonly string[] _counterparties = [
        "MM-Alpha", "MM-Beta", "MM-Gamma", "MM-Delta", "MM-Epsilon",
        "HF-Citrine", "HF-Obsidian", "HF-Quartz",
        "PROP-Volt", "PROP-Apex", "PROP-Zenith",
        "ARB-Flash", "ARB-Quantum", "ARB-Nexus",
        "RETAIL-Pool", "WHALE-001", "WHALE-002"
    ];
    private readonly string[] _strategies = [
        "Long Call", "Long Put", "Straddle", "Risk Reversal",
        "Iron Condor", "Calendar Spread", "Butterfly", "Strangle",
        "Bull Call Spread", "Bear Put Spread", "Collar", "Seagull"
    ];

    public DemoDataService()
    {
        GenerateHistory(500);
    }

    public IReadOnlyList<Trade> GetTradeHistory() => _tradeHistory.AsReadOnly();

    public IReadOnlyList<Trade> GetRecentTrades(int count) =>
        _tradeHistory.OrderByDescending(t => t.Timestamp).Take(count).ToList();

    public MarketSnapshot GetSnapshot(string symbol = "BTC") => new(
        Symbol: symbol,
        Spot: 87250,
        ForwardRate: 0.048,
        Timestamp: DateTimeOffset.UtcNow);

    private void GenerateHistory(int count)
    {
        double spot = 87250;
        DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddHours(-8);

        for (int i = 0; i < count; i++)
        {
            // Simulate spot random walk
            spot *= Math.Exp((_rng.NextDouble() - 0.498) * 0.001);
            baseTime = baseTime.AddSeconds(_rng.Next(10, 120));

            var cp = _counterparties[_rng.Next(_counterparties.Length)];
            bool isToxicActor = cp.StartsWith("ARB-") || cp == "WHALE-001";
            bool isMM = cp.StartsWith("MM-");

            // Strike selection
            double strikeOffset = (_rng.NextDouble() - 0.5) * 0.2;
            double strike = Math.Round(spot * (1 + strikeOffset) / 500) * 500;
            var optType = _rng.NextDouble() > 0.45 ? OptionType.Call : OptionType.Put;
            var side = _rng.NextDouble() > 0.5 ? Side.Buy : Side.Sell;

            // Toxic actors have patterns
            if (isToxicActor)
            {
                side = _rng.NextDouble() > 0.3 ? Side.Buy : Side.Sell; // directional bias
            }

            double tte = new[] { 1, 2, 7, 14, 30, 60, 90 }[_rng.Next(7)];
            double T = tte / 365.25;
            double iv = 0.55 + (_rng.NextDouble() - 0.5) * 0.2;
            double price = BlackScholes.Price(spot, strike, iv, T, 0.048, optType);
            double qty = isToxicActor
                ? _rng.Next(10, 50) // toxic = larger size
                : isMM ? _rng.Next(1, 15) : _rng.Next(1, 25);

            double delta = BlackScholes.Delta(spot, strike, 0.048, iv, T, optType);
            var greeks = BlackScholes.AllGreeks(spot, strike, 0.048, iv, T, optType);

            var venue = _rng.NextDouble() < 0.4 ? VenueType.CLOB
                : _rng.NextDouble() < 0.7 ? VenueType.RFQ : VenueType.Block;

            double latencyMs = isToxicActor && cp.StartsWith("ARB-")
                ? _rng.NextDouble() * 5  // very fast
                : _rng.NextDouble() * 500 + 20;

            string? packageId = _rng.NextDouble() < 0.35 ? $"PKG-{_rng.Next(1000, 9999)}" : null;

            _tradeHistory.Add(new Trade(
                TradeId: $"ATL-{i + 10000:D5}",
                Timestamp: baseTime,
                Instrument: $"BTC-{baseTime.AddDays(tte):ddMMMyy}-{strike:F0}-{(optType == OptionType.Call ? "C" : "P")}",
                Type: optType,
                Side: side,
                Strike: strike,
                Quantity: qty,
                Price: price,
                ImpliedVol: iv,
                SpotAtExecution: spot,
                DeltaAtExecution: delta,
                PricingModel: new[] { "BS", "Heston", "SABR", "MC" }[_rng.Next(4)],
                Venue: venue,
                CounterpartyId: cp,
                PackageId: packageId,
                StrategyName: packageId != null ? _strategies[_rng.Next(_strategies.Length)] : null,
                GreeksAtExecution: greeks,
                LatencyToFill: TimeSpan.FromMilliseconds(latencyMs)));
        }
    }
}

public interface IMarketDataProvider
{
    IReadOnlyList<Trade> GetTradeHistory();
    IReadOnlyList<Trade> GetRecentTrades(int count);
    MarketSnapshot GetSnapshot(string symbol = "BTC");
}
