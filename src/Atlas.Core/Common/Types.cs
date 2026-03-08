namespace Atlas.Core.Common;

/// <summary>Option type: Call or Put.</summary>
public enum OptionType { Call, Put }

/// <summary>Order side.</summary>
public enum Side { Buy, Sell }

/// <summary>Trade execution venue type.</summary>
public enum VenueType { CLOB, RFQ, Block, Package }

/// <summary>Immutable market snapshot for a single underlying.</summary>
public sealed record MarketSnapshot(
    string Symbol,
    double Spot,
    double ForwardRate,
    DateTimeOffset Timestamp);

/// <summary>Single option contract specification.</summary>
public sealed record OptionContract(
    string Instrument,
    string Underlying,
    double Strike,
    OptionType Type,
    DateTimeOffset Expiry,
    double TimeToExpiry);

/// <summary>Complete set of Greeks for a position.</summary>
public sealed record GreeksResult(
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    double Vanna,
    double Volga,
    double Charm,
    double Rho)
{
    public static GreeksResult Zero => new(0, 0, 0, 0, 0, 0, 0, 0);

    public static GreeksResult operator +(GreeksResult a, GreeksResult b) =>
        new(a.Delta + b.Delta, a.Gamma + b.Gamma, a.Vega + b.Vega,
            a.Theta + b.Theta, a.Vanna + b.Vanna, a.Volga + b.Volga,
            a.Charm + b.Charm, a.Rho + b.Rho);

    public GreeksResult Scale(double factor) =>
        new(Delta * factor, Gamma * factor, Vega * factor,
            Theta * factor, Vanna * factor, Volga * factor,
            Charm * factor, Rho * factor);
}

/// <summary>Pricing result from any model, with full traceability.</summary>
public sealed record PricingResult(
    string Model,
    double Price,
    double ImpliedVol,
    GreeksResult Greeks,
    DateTimeOffset ComputedAt,
    TimeSpan ComputeTime,
    Dictionary<string, object>? ModelParams = null);

/// <summary>A single leg of a multi-leg package.</summary>
public sealed record PackageLeg(
    OptionContract Contract,
    Side Side,
    double Quantity);

/// <summary>A multi-leg package (strategy).</summary>
public sealed record Package(
    string Id,
    string Name,
    IReadOnlyList<PackageLeg> Legs);

/// <summary>Executed trade with full audit trail.</summary>
public sealed record Trade(
    string TradeId,
    DateTimeOffset Timestamp,
    string Instrument,
    OptionType Type,
    Side Side,
    double Strike,
    double Quantity,
    double Price,
    double ImpliedVol,
    double SpotAtExecution,
    double DeltaAtExecution,
    string PricingModel,
    VenueType Venue,
    string? CounterpartyId,
    string? PackageId,
    string? StrategyName,
    GreeksResult GreeksAtExecution,
    TimeSpan LatencyToFill);

/// <summary>Scenario stress specification.</summary>
public sealed record ScenarioSpec(
    string Name,
    double SpotShockPct,
    double VolShiftAbs,
    double SkewTwist = 0,
    double CorrelationShock = 0);

/// <summary>Result of a scenario stress test.</summary>
public sealed record ScenarioResult(
    string Name,
    double PnL,
    GreeksResult StressedGreeks);
