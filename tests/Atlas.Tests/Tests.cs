using Atlas.Core.Common;
using Atlas.Core.Models;
using Atlas.ToxicFlow;
using Xunit;
using Atlas.ToxicFlow.Models;
namespace Atlas.Tests;

public class BlackScholesTests
{
    [Theory]
    [InlineData(100, 100, 0.2, 1.0, 0.05, 10.4506, 0.01)]  // ATM call
    [InlineData(100, 110, 0.2, 1.0, 0.05, 6.040, 0.01)]     // OTM call
    [InlineData(100, 90, 0.2, 1.0, 0.05, 16.6994, 0.01)]     // ITM call (corrected)
    [InlineData(100, 100, 0.5, 0.25, 0.05, 10.5193, 0.01)]   // High vol short expiry (corrected)
    public void CallPrice_MatchesKnownValues(double S, double K, double sigma, double T, double r,
        double expected, double tol)
    {
        var result = BlackScholes.CallPrice(S, K, sigma, T, r);
        Assert.InRange(result, expected - tol, expected + tol);
    }

    [Theory]
    [InlineData(100, 100, 0.2, 1.0, 0.05)]
    [InlineData(87250, 90000, 0.58, 0.082, 0.048)]  // Crypto-realistic
    [InlineData(87250, 80000, 0.65, 0.25, 0.048)]
    public void PutCallParity_Holds(double S, double K, double sigma, double T, double r)
    {
        double call = BlackScholes.CallPrice(S, K, sigma, T, r);
        double put = BlackScholes.PutPrice(S, K, sigma, T, r);
        double parity = call - put - S + K * Math.Exp(-r * T);
        Assert.InRange(Math.Abs(parity), 0, 1e-8);
    }

    [Fact]
    public void Greeks_SumRules()
    {
        // Delta should be ~0.5 for ATM
        double delta = BlackScholes.Delta(100, 100, 0.05, 0.2, 1.0, OptionType.Call);
        Assert.InRange(delta, 0.45, 0.65);

        // Call delta - put delta = 1
        double callD = BlackScholes.Delta(100, 100, 0.05, 0.2, 1.0, OptionType.Call);
        double putD = BlackScholes.Delta(100, 100, 0.05, 0.2, 1.0, OptionType.Put);
        Assert.InRange(callD - putD, 0.99, 1.01);
    }

    [Fact]
    public void Gamma_IsPositive_ForBothTypes()
    {
        double gamma = BlackScholes.Gamma(87250, 87000, 0.048, 0.58, 30.0 / 365.25);
        Assert.True(gamma > 0);
    }

    [Fact]
    public void Vega_IsPositive()
    {
        double vega = BlackScholes.Vega(87250, 87000, 0.048, 0.58, 30.0 / 365.25);
        Assert.True(vega > 0);
    }
}

public class ImpliedVolTests
{
    [Theory]
    [InlineData(0.20)]
    [InlineData(0.58)]
    [InlineData(1.20)]
    public void ImpliedVol_RoundTrips(double inputVol)
    {
        double S = 87250, K = 87000, r = 0.048, T = 30.0 / 365.25;
        double price = BlackScholes.CallPrice(S, K, inputVol, T, r);
        double recoveredVol = ImpliedVolSolver.Solve(price, S, K, r, T, OptionType.Call);
        Assert.InRange(recoveredVol, inputVol - 0.001, inputVol + 0.001);
    }
}

public class ModelConvergenceTests
{
    [Fact]
    public void MonteCarlo_ConvergesToBS()
    {
        double S = 100, K = 100, r = 0.05, sigma = 0.2, T = 1.0;
        double bsPrice = BlackScholes.CallPrice(S, K, sigma, T, r);
        double mcPrice = MonteCarlo.Price(S, K, r, sigma, T, OptionType.Call, 50000, 100, seed: 42);
        Assert.InRange(mcPrice, bsPrice - 0.5, bsPrice + 0.5);
    }

    [Fact]
    public void Binomial_ConvergesToBS()
    {
        double S = 100, K = 100, r = 0.05, sigma = 0.2, T = 1.0;
        double bsPrice = BlackScholes.CallPrice(S, K, sigma, T, r);
        double binPrice = BinomialTree.PriceRichardson(S, K, r, sigma, T, OptionType.Call, 200);
        Assert.InRange(binPrice, bsPrice - 0.05, bsPrice + 0.05);
    }
}

public class ToxicFlowTests
{
    [Fact]
    public void FlowClusterEngine_ProducesDashboard()
    {
        var engine = new FlowClusterEngine();
        var trades = GenerateTestTrades(50);
        var dashboard = engine.AnalyzeBatch(trades);

        Assert.Equal(50, dashboard.TotalTrades);
        Assert.True(dashboard.Clusters.Count > 0);
        Assert.True(dashboard.ToxicPct >= 0 && dashboard.ToxicPct <= 100);
    }

    [Fact]
    public void FlowClusterEngine_DetectsPattern()
    {
        var engine = new FlowClusterEngine();
        var trades = GenerateTestTrades(100);
        var dashboard = engine.AnalyzeBatch(trades);

        // Should identify at least some non-benign clusters
        Assert.Contains(dashboard.Clusters, c => c.Type != FlowClusterType.Benign);
    }

    private static List<Trade> GenerateTestTrades(int count)
    {
        var rng = new Random(42);
        var trades = new List<Trade>();
        for (int i = 0; i < count; i++)
        {
            trades.Add(new Trade(
                $"TEST-{i}", DateTimeOffset.UtcNow.AddMinutes(-i),
                "BTC-30D-87000-C", OptionType.Call,
                rng.NextDouble() > 0.5 ? Side.Buy : Side.Sell,
                87000, rng.Next(1, 20), 2500 + rng.NextDouble() * 500,
                0.58, 87250, 0.45, "BS", VenueType.CLOB,
                $"CP-{rng.Next(1, 5)}", null, null,
                GreeksResult.Zero, TimeSpan.FromMilliseconds(rng.NextDouble() * 500)));
        }
        return trades;
    }
}

public class NumericalStabilityTests
{
    [Fact]
    public void BlackScholes_ZeroVol_UsesDeterministicLimit()
    {
        const double S = 100;
        const double K = 100;
        const double r = 0.05;
        const double T = 1.0;

        double call = BlackScholes.CallPrice(S, K, 0, T, r);
        double put = BlackScholes.PutPrice(S, K, 0, T, r);
        double expectedCall = Math.Max(S - K * Math.Exp(-r * T), 0);
        double expectedPut = Math.Max(K * Math.Exp(-r * T) - S, 0);

        Assert.InRange(call, expectedCall - 1e-10, expectedCall + 1e-10);
        Assert.InRange(put, expectedPut - 1e-10, expectedPut + 1e-10);
    }

    [Fact]
    public void BinomialRichardson_HandlesVerySmallStepCount()
    {
        double price = BinomialTree.PriceRichardson(100, 100, 0.05, 0.2, 1.0, OptionType.Call, 1);
        Assert.True(double.IsFinite(price));
        Assert.True(price > 0);
    }

    [Fact]
    public void ImpliedVol_RespectsNoArbitrageBounds()
    {
        const double S = 100;
        const double K = 100;
        const double r = 0.01;
        const double T = 1.0;

        double veryLowPrice = 1e-8;
        double veryHighPrice = 1000;

        double lowIv = ImpliedVolSolver.Solve(veryLowPrice, S, K, r, T, OptionType.Call);
        double highIv = ImpliedVolSolver.Solve(veryHighPrice, S, K, r, T, OptionType.Call);

        Assert.InRange(lowIv, 0.01, 0.02);
        Assert.InRange(highIv, 4.99, 5.0);
    }
}

public class GreeksConsistencyTests
{
    [Theory]
    [InlineData(87250, 87000, 0.048, 0.58, 30.0 / 365.25, OptionType.Call)]
    [InlineData(87250, 92000, 0.048, 0.74, 14.0 / 365.25, OptionType.Put)]
    [InlineData(1945, 2000, 0.03, 0.92, 45.0 / 365.25, OptionType.Call)]
    public void BlackScholes_AnalyticalGreeks_MatchFiniteDifferences(
        double S,
        double K,
        double r,
        double sigma,
        double T,
        OptionType type)
    {
        const double hSRel = 1e-4;
        const double hVol = 1e-4;
        const double hT = 1e-5;

        double hS = Math.Max(0.01, S * hSRel);
        double pBase = BlackScholes.Price(S, K, sigma, T, r, type);
        double pUp = BlackScholes.Price(S + hS, K, sigma, T, r, type);
        double pDn = BlackScholes.Price(S - hS, K, sigma, T, r, type);

        double fdDelta = (pUp - pDn) / (2 * hS);
        double fdGamma = (pUp - 2 * pBase + pDn) / (hS * hS);

        double vUp = BlackScholes.Price(S, K, sigma + hVol, T, r, type);
        double vDn = BlackScholes.Price(S, K, sigma - hVol, T, r, type);
        double fdVegaPerAbsVol = (vUp - vDn) / (2 * hVol);
        double fdVega = fdVegaPerAbsVol / 100.0;

        double tDn = BlackScholes.Price(S, K, sigma, Math.Max(1e-8, T - hT), r, type);
        double fdTheta = (tDn - pBase) / hT / 365.25;

        double delta = BlackScholes.Delta(S, K, r, sigma, T, type);
        double gamma = BlackScholes.Gamma(S, K, r, sigma, T);
        double vega = BlackScholes.Vega(S, K, r, sigma, T);
        double theta = BlackScholes.Theta(S, K, r, sigma, T, type);

        Assert.InRange(Math.Abs(delta - fdDelta), 0, 5e-4);
        Assert.InRange(Math.Abs(gamma - fdGamma), 0, 5e-6);
        Assert.InRange(Math.Abs(vega - fdVega), 0, 5e-4);
        Assert.InRange(Math.Abs(theta - fdTheta), 0, 1.5e-2);
    }
}

public class ModelRegressionSnapshotTests
{
    [Theory]
    [InlineData(87250, 87000, 0.048, 0.58, 30.0 / 365.25, OptionType.Call, 6060.5981626468, 5681.3602378945, 593.0493581384)]
    [InlineData(87250, 92000, 0.048, 0.74, 14.0 / 365.25, OptionType.Put, 7779.7319076556, 7539.9418914670, 4580.8907272802)]
    [InlineData(1945, 2000, 0.03, 0.92, 45.0 / 365.25, OptionType.Call, 229.4307820354, 193.2994609661, 0.0000198875)]
    public void PricingModels_NonRegressionSnapshot(
        double S,
        double K,
        double r,
        double sigma,
        double T,
        OptionType type,
        double expectedBs,
        double expectedHeston,
        double expectedSabr)
    {
        double bs = BlackScholes.Price(S, K, sigma, T, r, type);
        double heston = HestonModel.Price(S, K, r, sigma, T, type);
        double sabr = SabrModel.Price(S, K, r, sigma, T, type);

        Assert.InRange(bs, expectedBs - 1e-6, expectedBs + 1e-6);
        Assert.InRange(heston, expectedHeston - 1e-6, expectedHeston + 1e-6);
        Assert.InRange(sabr, expectedSabr - 1e-6, expectedSabr + 1e-6);
    }
}
