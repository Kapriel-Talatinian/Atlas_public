using System.Net;
using System.Reflection;
using System.Text;
using Atlas.Api.Models;
using Atlas.Api.Services;
using Atlas.Core.Common;
using Atlas.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Tests;

public class QuantTargetedTests
{
    [Fact]
    public void BlackScholes_CallPut_Parity()
    {
        const double S = 125;
        const double K = 120;
        const double sigma = 0.35;
        const double T = 0.75;
        const double r = 0.04;

        double call = BlackScholes.CallPrice(S, K, sigma, T, r);
        double put = BlackScholes.PutPrice(S, K, sigma, T, r);
        double parityResidual = call - put - S + K * Math.Exp(-r * T);

        Assert.InRange(Math.Abs(parityResidual), 0, 1e-8);
    }

    [Fact]
    public void ImpliedVolSolver_DeepOTM_Converges()
    {
        const double S = 100;
        const double K = 180;
        const double sigma = 0.92;
        const double T = 1.35;
        const double r = 0.02;

        double marketPrice = BlackScholes.CallPrice(S, K, sigma, T, r);
        double recovered = ImpliedVolSolver.Solve(marketPrice, S, K, r, T, OptionType.Call);
        double brent = ImpliedVolSolver.SolveBrent(marketPrice, S, K, r, T, OptionType.Call);

        Assert.InRange(recovered, sigma - 5e-3, sigma + 5e-3);
        Assert.InRange(brent, sigma - 5e-3, sigma + 5e-3);
    }

    [Fact]
    public void ImpliedVolSolver_ZeroVol_ReturnsIntrinsic()
    {
        const double S = 100;
        const double K = 100;
        const double T = 1.0;
        const double r = 0.03;

        double intrinsic = Math.Max(S - K * Math.Exp(-r * T), 0);
        double zeroVolPrice = BlackScholes.CallPrice(S, K, 0, T, r);
        double recovered = ImpliedVolSolver.Solve(zeroVolPrice, S, K, r, T, OptionType.Call);

        Assert.InRange(zeroVolPrice, intrinsic - 1e-10, intrinsic + 1e-10);
        Assert.InRange(recovered, 0.01, 0.02);
    }

    [Fact]
    public void ImpliedVolSolver_BrentAndNewton_Agree_Atm()
    {
        const double S = 100;
        const double K = 100;
        const double sigma = 0.28;
        const double T = 0.5;
        const double r = 0.01;

        double marketPrice = BlackScholes.CallPrice(S, K, sigma, T, r);
        double recovered = ImpliedVolSolver.Solve(marketPrice, S, K, r, T, OptionType.Call);
        double brent = ImpliedVolSolver.SolveBrent(marketPrice, S, K, r, T, OptionType.Call);

        Assert.InRange(Math.Abs(recovered - brent), 0, 1e-6);
    }

    [Fact]
    public void BinomialTree_Converges_ToBlackScholes_N500()
    {
        const double S = 100;
        const double K = 110;
        const double sigma = 0.25;
        const double T = 0.9;
        const double r = 0.03;

        double bs = BlackScholes.Price(S, K, sigma, T, r, OptionType.Call);
        double binomial = BinomialTree.PriceRichardson(S, K, r, sigma, T, OptionType.Call, 500);

        Assert.InRange(Math.Abs(binomial - bs), 0, 0.015);
    }

    [Fact]
    public void SabrModel_ATM_MatchesAlpha()
    {
        var sabr = new SabrParams(Alpha: 0.42, Beta: 0.5, Rho: -0.2, Nu: 0.7);
        double iv = SabrModel.ImpliedVol(100, 100, 1e-12, sabr);

        Assert.InRange(iv, 0.419999, 0.420001);
    }

    [Fact]
    public void HestonModel_ZeroTime_ReturnsIntrinsic()
    {
        double call = HestonModel.Price(120, 100, 0.03, 0.4, 0, OptionType.Call);
        double put = HestonModel.Price(80, 100, 0.03, 0.4, 0, OptionType.Put);

        Assert.Equal(20, call, 10);
        Assert.Equal(20, put, 10);
    }

    [Fact]
    public void HestonModel_Greeks_AreFinite()
    {
        GreeksResult greeks = HestonModel.Greeks(100, 105, 0.03, 0.55, 0.75, OptionType.Call);

        Assert.True(double.IsFinite(greeks.Delta));
        Assert.True(double.IsFinite(greeks.Gamma));
        Assert.True(double.IsFinite(greeks.Vega));
        Assert.True(double.IsFinite(greeks.Theta));
    }

    [Fact]
    public void MonteCarlo_PriceWithError_BoundsBlackScholes()
    {
        const double S = 100;
        const double K = 95;
        const double sigma = 0.22;
        const double T = 0.5;
        const double r = 0.02;

        double bs = BlackScholes.CallPrice(S, K, sigma, T, r);
        var (price, stdError) = MonteCarlo.PriceWithError(S, K, r, sigma, T, OptionType.Call, 80_000, 80);

        Assert.InRange(bs, price - 3 * stdError, price + 3 * stdError);
    }
}

public class OptionsAnalyticsExposureTests
{
    [Fact]
    public async Task ExposureGrid_RecomputesGreeks_WhenFeedGreeksAreZero()
    {
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddDays(14);
        var quotes = new[]
        {
            BuildExposureQuote("BTC-TEST-C", OptionRight.Call, expiry, strike: 100_000, markIv: 0.62, openInterest: 2_500, volume24h: 180),
            BuildExposureQuote("BTC-TEST-P", OptionRight.Put, expiry, strike: 100_000, markIv: 0.62, openInterest: 2_300, volume24h: 210)
        };

        var analytics = new OptionsAnalyticsService(new StaticMarketDataService(quotes));

        GreeksExposureGrid grid = await analytics.GetGreeksExposureGridAsync("BTC", maxExpiries: 2, maxStrikes: 8);

        Assert.NotEmpty(grid.Cells);
        GreeksExposureCell cell = Assert.Single(grid.Cells);
        Assert.True(Math.Abs(cell.DeltaExposure) > 1e-6);
        Assert.True(Math.Abs(cell.GammaExposure) > 1e-6);
        Assert.True(Math.Abs(cell.VegaExposure) > 1e-6);
        Assert.True(Math.Abs(cell.ThetaExposure) > 1e-6);
        Assert.NotEmpty(grid.TopHotspots);
        Assert.True(grid.TopHotspots[0].PinRiskScore > 0);
    }

    [Fact]
    public async Task RelativeValueBoard_DetectsCheapAndRichVolDislocations()
    {
        DateTimeOffset expiry = new(DateTime.UtcNow.Date.AddDays(21).AddHours(8), TimeSpan.Zero);
        var quotes = new[]
        {
            BuildRelativeValueQuote("BTC-TEST-90-C", OptionRight.Call, expiry, strike: 90_000, markIv: 0.40, delta: 0.78),
            BuildRelativeValueQuote("BTC-TEST-90-P", OptionRight.Put, expiry, strike: 90_000, markIv: 0.40, delta: -0.22),
            BuildRelativeValueQuote("BTC-TEST-100-C", OptionRight.Call, expiry, strike: 100_000, markIv: 0.55, delta: 0.52),
            BuildRelativeValueQuote("BTC-TEST-100-P", OptionRight.Put, expiry, strike: 100_000, markIv: 0.55, delta: -0.48),
            BuildRelativeValueQuote("BTC-TEST-110-C", OptionRight.Call, expiry, strike: 110_000, markIv: 0.90, delta: 0.26),
            BuildRelativeValueQuote("BTC-TEST-110-P", OptionRight.Put, expiry, strike: 110_000, markIv: 0.90, delta: -0.74),
        };

        var analytics = new OptionsAnalyticsService(new StaticMarketDataService(quotes));

        RelativeValueBoard board = await analytics.GetRelativeValueBoardAsync("BTC", expiry, limit: 12);

        Assert.NotEmpty(board.SurfaceNodes);
        Assert.NotEmpty(board.TopCheapVol);
        Assert.NotEmpty(board.TopRichVol);
        Assert.True(board.MaxRichVolPoints > board.MaxCheapVolPoints);
        Assert.True(board.TopCheapVol[0].ResidualVolPoints <= board.TopRichVol[0].ResidualVolPoints);
        Assert.Contains(board.TopCheapVol, signal => signal.Strike == 90_000 || signal.Strike == 100_000);
        Assert.Contains(board.TopRichVol, signal => signal.Strike == 110_000);
        Assert.True(board.SurfaceQualityScore > 0);
    }

    [Fact]
    public async Task RelativeValueBoard_BuildsTradeIdeas_WithDefinedRiskStructures()
    {
        DateTimeOffset expiry = new(DateTime.UtcNow.Date.AddDays(21).AddHours(8), TimeSpan.Zero);
        var quotes = new[]
        {
            BuildRelativeValueQuote("BTC-TEST-90-C", OptionRight.Call, expiry, strike: 90_000, markIv: 0.40, delta: 0.78),
            BuildRelativeValueQuote("BTC-TEST-90-P", OptionRight.Put, expiry, strike: 90_000, markIv: 0.40, delta: -0.22),
            BuildRelativeValueQuote("BTC-TEST-100-C", OptionRight.Call, expiry, strike: 100_000, markIv: 0.55, delta: 0.52),
            BuildRelativeValueQuote("BTC-TEST-100-P", OptionRight.Put, expiry, strike: 100_000, markIv: 0.55, delta: -0.48),
            BuildRelativeValueQuote("BTC-TEST-110-C", OptionRight.Call, expiry, strike: 110_000, markIv: 0.90, delta: 0.26),
            BuildRelativeValueQuote("BTC-TEST-110-P", OptionRight.Put, expiry, strike: 110_000, markIv: 0.90, delta: -0.74),
        };

        var analytics = new OptionsAnalyticsService(new StaticMarketDataService(quotes));

        RelativeValueBoard board = await analytics.GetRelativeValueBoardAsync("BTC", expiry, limit: 12);

        Assert.NotEmpty(board.TradeIdeas);
        Assert.All(board.TradeIdeas, idea =>
        {
            Assert.False(string.IsNullOrWhiteSpace(idea.Name));
            Assert.True(idea.Score > 0);
            Assert.NotEmpty(idea.Analysis.Legs);
            Assert.True(Math.Abs(idea.Analysis.MaxLoss) >= 0);
        });
        Assert.Contains(board.TradeIdeas, idea => idea.Name.Contains("Spread", StringComparison.OrdinalIgnoreCase) || idea.Name.Contains("Butterfly", StringComparison.OrdinalIgnoreCase));
    }

    private static LiveOptionQuote BuildExposureQuote(
        string symbol,
        OptionRight right,
        DateTimeOffset expiry,
        double strike,
        double markIv,
        double openInterest,
        double volume24h)
    {
        return new LiveOptionQuote(
            Symbol: symbol,
            Asset: "BTC",
            Expiry: expiry,
            Strike: strike,
            Right: right,
            Bid: 1_250,
            Ask: 1_275,
            Mark: 1_262.5,
            Mid: 1_262.5,
            MarkIv: markIv,
            Delta: 0,
            Gamma: 0,
            Vega: 0,
            Theta: 0,
            OpenInterest: openInterest,
            Volume24h: volume24h,
            Turnover24h: volume24h * 1_262.5,
            UnderlyingPrice: 102_500,
            Timestamp: DateTimeOffset.UtcNow,
            Venue: "UNIT",
            SourceTimestamp: DateTimeOffset.UtcNow,
            IsStale: false);
    }

    private static LiveOptionQuote BuildRelativeValueQuote(
        string symbol,
        OptionRight right,
        DateTimeOffset expiry,
        double strike,
        double markIv,
        double delta)
    {
        double spot = 100_000;
        double t = Math.Max((expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 365.25);
        OptionType optionType = right == OptionRight.Call ? OptionType.Call : OptionType.Put;
        double mid = BlackScholes.Price(spot, strike, markIv, t, 0.03, optionType);
        double spread = Math.Max(mid * 0.01, 5);
        return new LiveOptionQuote(
            Symbol: symbol,
            Asset: "BTC",
            Expiry: expiry,
            Strike: strike,
            Right: right,
            Bid: Math.Max(0, mid - spread / 2.0),
            Ask: mid + spread / 2.0,
            Mark: mid,
            Mid: mid,
            MarkIv: markIv,
            Delta: delta,
            Gamma: 0.002,
            Vega: 4.5,
            Theta: -1.8,
            OpenInterest: 1_500,
            Volume24h: 240,
            Turnover24h: mid * 240,
            UnderlyingPrice: spot,
            Timestamp: DateTimeOffset.UtcNow,
            Venue: "UNIT",
            SourceTimestamp: DateTimeOffset.UtcNow,
            IsStale: false);
    }
}

public class PolymarketSignalMathTests
{
    [Fact]
    public void PolymarketParser_ParsesAboveThresholdQuestion()
    {
        bool ok = PolymarketSignalMath.TryParseTradeableQuestion(
            "Will the price of Bitcoin be above $70,000 on April 8?",
            out PolymarketParsedQuestion? parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("BTC", parsed!.Asset);
        Assert.Equal(PolymarketThresholdRelation.Above, parsed.Relation);
        Assert.Equal(70_000, parsed.LowerStrike);
    }

    [Fact]
    public void PolymarketParser_ParsesBetweenThresholdQuestion()
    {
        bool ok = PolymarketSignalMath.TryParseTradeableQuestion(
            "Will the price of Ethereum be between $1,800 and $1,900 on April 8?",
            out PolymarketParsedQuestion? parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("ETH", parsed!.Asset);
        Assert.Equal(PolymarketThresholdRelation.Between, parsed.Relation);
        Assert.Equal(1_800, parsed.LowerStrike);
        Assert.Equal(1_900, parsed.UpperStrike);
    }

    [Fact]
    public void PolymarketParser_ParsesDirectionalUpDownQuestion_WhenReferenceProvided()
    {
        bool ok = PolymarketSignalMath.TryParseTradeableQuestion(
            "Bitcoin Up or Down - April 8, 2:00PM-2:05PM ET",
            out PolymarketParsedQuestion? parsed,
            directionalReferencePrice: 82_500);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("BTC", parsed!.Asset);
        Assert.Equal(PolymarketThresholdRelation.Above, parsed.Relation);
        Assert.Equal(82_500, parsed.LowerStrike);
    }

    [Fact]
    public void PolymarketFairProbability_AboveThreshold_BehavesSensibly()
    {
        var parsed = new PolymarketParsedQuestion(
            Asset: "BTC",
            Relation: PolymarketThresholdRelation.Above,
            LowerStrike: 100_000,
            UpperStrike: null,
            RawQuestion: "Will the price of Bitcoin be above $100,000 in 1 hour?");

        double bullish = PolymarketSignalMath.ComputeFairYesProbability(
            spot: 110_000,
            annualizedVol: 0.50,
            expiry: DateTimeOffset.UtcNow.AddHours(1),
            now: DateTimeOffset.UtcNow,
            parsed: parsed);

        double bearish = PolymarketSignalMath.ComputeFairYesProbability(
            spot: 90_000,
            annualizedVol: 0.50,
            expiry: DateTimeOffset.UtcNow.AddHours(1),
            now: DateTimeOffset.UtcNow,
            parsed: parsed);

        Assert.True(bullish > 0.5);
        Assert.True(bearish < 0.5);
        Assert.True(bullish > bearish);
    }
}

public class SharedExperimentalAutopilotTests
{
    [Fact]
    public async Task SharedPortfolioSnapshot_ReturnsMultiAssetBook()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = BuildExperimentalService(tempRoot);

            ExperimentalBotSnapshot snapshot = await service.GetSnapshotAsync("BTC");

            Assert.Equal("MULTI", snapshot.Asset);
            Assert.NotNull(snapshot.Assets);
            Assert.Contains("BTC", snapshot.Assets!);
            Assert.Contains("ETH", snapshot.Assets!);
            Assert.Contains("SOL", snapshot.Assets!);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SharedPortfolioAutopilot_OpensOnlyStructuredTrades()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = BuildExperimentalService(tempRoot);
            await service.ConfigureAsync("MULTI", new ExperimentalBotConfigRequest(
                Enabled: true,
                AutoTrade: true,
                MinConfidence: 40,
                StartingCapitalUsd: 5_000,
                MaxTradeRiskPct: 0.25,
                PortfolioRiskBudgetPct: 0.90));

            ExperimentalBotSnapshot snapshot = await service.RunCycleAsync("MULTI", cycles: 2);

            Assert.NotEmpty(snapshot.OpenTrades);
            Assert.All(snapshot.OpenTrades, trade =>
            {
                Assert.Equal("MULTI", snapshot.Asset);
                Assert.True((trade.Legs?.Count ?? 0) >= 2);
                Assert.False(string.IsNullOrWhiteSpace(trade.Asset));
                Assert.False(string.IsNullOrWhiteSpace(trade.StrategyTemplate));
                Assert.False(string.IsNullOrWhiteSpace(trade.MathSummary));
                Assert.True(Math.Abs(trade.MaxLoss) > 0);
            });
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SharedPortfolioSnapshot_EmbedsNeuralSignalsAndReasoning()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var service = BuildExperimentalService(tempRoot);
            await service.ConfigureAsync("MULTI", new ExperimentalBotConfigRequest(
                Enabled: true,
                AutoTrade: true,
                MinConfidence: 40,
                StartingCapitalUsd: 5_000,
                MaxTradeRiskPct: 0.25,
                PortfolioRiskBudgetPct: 0.90));

            ExperimentalBotSnapshot snapshot = await service.RunCycleAsync("MULTI", cycles: 2);

            Assert.NotNull(snapshot.NeuralSignals);
            Assert.NotEmpty(snapshot.NeuralSignals!);
            Assert.All(snapshot.NeuralSignals!, signal =>
            {
                Assert.False(string.IsNullOrWhiteSpace(signal.RecommendedStructure));
                Assert.False(string.IsNullOrWhiteSpace(signal.MacroReasoning));
                Assert.False(string.IsNullOrWhiteSpace(signal.MicroReasoning));
                Assert.False(string.IsNullOrWhiteSpace(signal.MathReasoning));
            });

            Assert.NotEmpty(snapshot.OpenTrades);
            Assert.Contains(snapshot.OpenTrades, trade =>
                trade.Rationale.Contains("Macro:", StringComparison.OrdinalIgnoreCase) &&
                trade.Rationale.Contains("Micro:", StringComparison.OrdinalIgnoreCase) &&
                trade.MathSummary.Contains("neuralScore", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApiRole_Snapshot_ReloadsPersistedState_WithoutRunningCycle()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var workerService = BuildExperimentalService(tempRoot, AtlasRuntimeRole.All);
            await workerService.ConfigureAsync("MULTI", new ExperimentalBotConfigRequest(
                Enabled: true,
                AutoTrade: true,
                MinConfidence: 40,
                StartingCapitalUsd: 5_000,
                MaxTradeRiskPct: 0.25,
                PortfolioRiskBudgetPct: 0.90));

            ExperimentalBotSnapshot workerSnapshot = await workerService.RunCycleAsync("MULTI", cycles: 2);
            Assert.NotEmpty(workerSnapshot.OpenTrades);

            var apiService = BuildExperimentalService(tempRoot, AtlasRuntimeRole.Api, quotes: Array.Empty<LiveOptionQuote>());
            ExperimentalBotSnapshot apiSnapshot = await apiService.GetSnapshotAsync("MULTI");

            Assert.Equal(workerSnapshot.Signal?.StrategyTemplate, apiSnapshot.Signal?.StrategyTemplate);
            Assert.Equal(workerSnapshot.OpenTrades.Count, apiSnapshot.OpenTrades.Count);
            Assert.NotEmpty(apiSnapshot.OpenTrades);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApiRole_RunCycle_ThrowsInvalidOperationException()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var apiService = BuildExperimentalService(tempRoot, AtlasRuntimeRole.Api);

            await Assert.ThrowsAsync<InvalidOperationException>(() => apiService.RunCycleAsync("MULTI", cycles: 1));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApiRole_Configure_ThrowsInvalidOperationException()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var apiService = BuildExperimentalService(tempRoot, AtlasRuntimeRole.Api);

            await Assert.ThrowsAsync<InvalidOperationException>(() => apiService.ConfigureAsync(
                "MULTI",
                new ExperimentalBotConfigRequest(Enabled: true)));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ApiRole_Reset_ThrowsInvalidOperationException()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var apiService = BuildExperimentalService(tempRoot, AtlasRuntimeRole.Api);

            await Assert.ThrowsAsync<InvalidOperationException>(() => apiService.ResetAsync("MULTI"));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static SharedPortfolioExperimentalAutoTraderService BuildExperimentalService(
        string tempRoot,
        AtlasRuntimeRole role = AtlasRuntimeRole.All,
        IEnumerable<LiveOptionQuote>? quotes = null)
    {
        DateTimeOffset near = new(DateTime.UtcNow.Date.AddDays(21).AddHours(8), TimeSpan.Zero);
        DateTimeOffset far = new(DateTime.UtcNow.Date.AddDays(49).AddHours(8), TimeSpan.Zero);

        var configuredQuotes = quotes?.ToList() ?? new List<LiveOptionQuote>();
        if (configuredQuotes.Count == 0 && quotes is null)
        {
            configuredQuotes.AddRange(BuildBotAssetQuotes("BTC", 100_000, near, far));
            configuredQuotes.AddRange(BuildBotAssetQuotes("ETH", 4_000, near, far));
            configuredQuotes.AddRange(BuildBotAssetQuotes("SOL", 180, near, far));
        }

        var marketData = new StaticMarketDataService(configuredQuotes);
        var analytics = new OptionsAnalyticsService(marketData);
        var brain = new NeuralTradingBrainService(analytics, marketData);
        var repository = new FileBotStateRepository(new BotFakeHostEnvironment(tempRoot), NullLogger<FileBotStateRepository>.Instance);
        var runtime = new AtlasRuntimeContext(role, $"test-{role.ToString().ToLowerInvariant()}", "unit-test");
        var monitoring = new SystemMonitoringService();

        return new SharedPortfolioExperimentalAutoTraderService(
            marketData,
            analytics,
            brain,
            repository,
            runtime,
            monitoring,
            NullLogger<SharedPortfolioExperimentalAutoTraderService>.Instance);
    }

    private static IEnumerable<LiveOptionQuote> BuildBotAssetQuotes(string asset, double spot, DateTimeOffset near, DateTimeOffset far)
    {
        double[] strikes = [spot * 0.85, spot * 0.95, spot, spot * 1.05, spot * 1.15];

        foreach (DateTimeOffset expiry in new[] { near, far })
        {
            double expiryBias = expiry == near ? 0.0 : 0.04;
            foreach (double strike in strikes)
            {
                double moneyness = strike / spot;
                double baseIv = 0.52 + expiryBias + Math.Abs(moneyness - 1.0) * 0.28;
                yield return BuildPortfolioBotQuote(asset, expiry, strike, OptionRight.Call, baseIv + (moneyness >= 1.05 ? 0.10 : -0.04), GuessDelta(moneyness, OptionRight.Call));
                yield return BuildPortfolioBotQuote(asset, expiry, strike, OptionRight.Put, baseIv + (moneyness <= 0.95 ? 0.12 : -0.02), GuessDelta(moneyness, OptionRight.Put));
            }
        }
    }

    private static LiveOptionQuote BuildPortfolioBotQuote(
        string asset,
        DateTimeOffset expiry,
        double strike,
        OptionRight right,
        double markIv,
        double delta)
    {
        double spot = asset switch
        {
            "ETH" => 4_000,
            "SOL" => 180,
            _ => 100_000
        };

        double t = Math.Max((expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 365.25);
        OptionType optionType = right == OptionRight.Call ? OptionType.Call : OptionType.Put;
        double mid = BlackScholes.Price(spot, strike, markIv, t, 0.03, optionType);
        double spread = Math.Max(mid * 0.012, asset == "SOL" ? 0.25 : asset == "ETH" ? 2 : 15);

        return new LiveOptionQuote(
            Symbol: $"{asset}-{expiry:ddMMMyy}-{strike:F0}-{(right == OptionRight.Call ? "C" : "P")}",
            Asset: asset,
            Expiry: expiry,
            Strike: strike,
            Right: right,
            Bid: Math.Max(0, mid - spread / 2.0),
            Ask: mid + spread / 2.0,
            Mark: mid,
            Mid: mid,
            MarkIv: markIv,
            Delta: delta,
            Gamma: 0.002,
            Vega: Math.Max(1.5, mid * 0.01),
            Theta: -Math.Max(0.2, mid * 0.002),
            OpenInterest: 1_800,
            Volume24h: 320,
            Turnover24h: mid * 320,
            UnderlyingPrice: spot,
            Timestamp: DateTimeOffset.UtcNow,
            Venue: "UNIT",
            SourceTimestamp: DateTimeOffset.UtcNow,
            IsStale: false);
    }

    private static double GuessDelta(double moneyness, OptionRight right)
    {
        double callDelta = moneyness switch
        {
            <= 0.85 => 0.84,
            <= 0.95 => 0.66,
            <= 1.02 => 0.52,
            <= 1.08 => 0.34,
            _ => 0.18
        };

        return right == OptionRight.Call ? callDelta : callDelta - 1.0;
    }

    private sealed class BotFakeHostEnvironment : IHostEnvironment
    {
        public BotFakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ApplicationName = "Atlas.Tests";
            EnvironmentName = "Development";
            ContentRootFileProvider = null!;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }
}

public class BotRuntimeInfrastructureTests
{
    [Fact]
    public void FileBotStateRepository_SaveThenLoad_RoundTripsState()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-state-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var repository = new FileBotStateRepository(
                new RuntimeFakeHostEnvironment(tempRoot),
                NullLogger<FileBotStateRepository>.Instance);

            BotStateRecord saved = repository.Save(new BotStateSaveRequest(
                BotKey: "MULTI",
                StateJson: "{\"equity\":1234.5}",
                ExpectedStateVersion: 0,
                LastEvaluationAt: DateTimeOffset.UtcNow,
                LastCycleStatus: "ok",
                LastCycleDurationMs: 87));

            BotStateRecord? loaded = repository.Load("MULTI");

            Assert.NotNull(loaded);
            Assert.Equal(saved.StateVersion, loaded!.StateVersion);
            Assert.Equal("{\"equity\":1234.5}", loaded.StateJson);
            Assert.Equal("ok", loaded.LastCycleStatus);
            Assert.Equal(87, loaded.LastCycleDurationMs);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FileBotStateRepository_ConflictingSave_Throws()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-bot-state-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var repository = new FileBotStateRepository(
                new RuntimeFakeHostEnvironment(tempRoot),
                NullLogger<FileBotStateRepository>.Instance);

            BotStateRecord initial = repository.Save(new BotStateSaveRequest(
                BotKey: "MULTI",
                StateJson: "{\"state\":1}",
                ExpectedStateVersion: 0,
                LastEvaluationAt: DateTimeOffset.UtcNow,
                LastCycleStatus: "ok",
                LastCycleDurationMs: 25));

            Assert.Equal(1, initial.StateVersion);

            Assert.Throws<BotStateConflictException>(() => repository.Save(new BotStateSaveRequest(
                BotKey: "MULTI",
                StateJson: "{\"state\":2}",
                ExpectedStateVersion: 0,
                LastEvaluationAt: DateTimeOffset.UtcNow,
                LastCycleStatus: "ok",
                LastCycleDurationMs: 26)));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SingleNodeLeaderElection_WorkerRole_AcquiresLeadership()
    {
        var service = new SingleNodeBotLeaderElectionService();
        var runtime = new AtlasRuntimeContext(AtlasRuntimeRole.BotWorker, "worker-1", "unit-test");

        BotLeaderLeaseSnapshot lease = service.AcquireOrRenew("MULTI", runtime, TimeSpan.FromSeconds(12));

        Assert.True(lease.IsLeader);
        Assert.Equal("worker-1", lease.OwnerInstanceId);
        Assert.True(lease.LeaseUntil > lease.CheckedAt);
        Assert.Equal(1, lease.FencingToken);
    }

    [Fact]
    public void SingleNodeLeaderElection_ApiRole_StaysStandby()
    {
        var service = new SingleNodeBotLeaderElectionService();
        var runtime = new AtlasRuntimeContext(AtlasRuntimeRole.Api, "api-1", "unit-test");

        BotLeaderLeaseSnapshot lease = service.AcquireOrRenew("MULTI", runtime, TimeSpan.FromSeconds(12));

        Assert.False(lease.IsLeader);
        Assert.Null(lease.OwnerInstanceId);
        Assert.Null(lease.LeaseUntil);
        Assert.Equal(0, lease.FencingToken);
    }

    private sealed class RuntimeFakeHostEnvironment : IHostEnvironment
    {
        public RuntimeFakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ApplicationName = "Atlas.Tests";
            EnvironmentName = "Development";
            ContentRootFileProvider = null!;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }
}

public class PostgresConnectionResolverTests
{
    [Fact]
    public void HasPostgresConfiguration_IgnoresMalformedExplicitUri_AndUsesPgFallback()
    {
        var previous = CaptureEnvironment(
            "BOT_RUNTIME_DB_CONNECTION_STRING",
            "PGHOST",
            "PGPORT",
            "PGUSER",
            "PGPASSWORD",
            "PGDATABASE",
            "RAILWAY_PRIVATE_DOMAIN",
            "DB_PORT",
            "DB_USER",
            "DB_PASSWORD",
            "DB_NAME");

        try
        {
            Environment.SetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING", "postgres://:@");
            Environment.SetEnvironmentVariable("PGHOST", "postgres.internal");
            Environment.SetEnvironmentVariable("PGPORT", "5432");
            Environment.SetEnvironmentVariable("PGUSER", "atlas");
            Environment.SetEnvironmentVariable("PGPASSWORD", "secret");
            Environment.SetEnvironmentVariable("PGDATABASE", "atlasdb");

            Assert.True(InvokeResolverHasPostgresConfiguration());

            string connectionString = InvokeResolverConnectionString();
            Assert.Contains("Host=postgres.internal", connectionString, StringComparison.Ordinal);
            Assert.Contains("Username=atlas", connectionString, StringComparison.Ordinal);
            Assert.Contains("Database=atlasdb", connectionString, StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    [Fact]
    public void ResolveConnectionString_NormalizesValidExplicitConnectionString()
    {
        var previous = CaptureEnvironment(
            "BOT_RUNTIME_DB_CONNECTION_STRING",
            "PGHOST",
            "PGPORT",
            "PGUSER",
            "PGPASSWORD",
            "PGDATABASE",
            "RAILWAY_PRIVATE_DOMAIN",
            "DB_PORT",
            "DB_USER",
            "DB_PASSWORD",
            "DB_NAME");

        try
        {
            Environment.SetEnvironmentVariable("BOT_RUNTIME_DB_CONNECTION_STRING", "\"Host=db.internal;Port=5432;Username=atlas;Password=secret;Database=atlasdb\"");
            Environment.SetEnvironmentVariable("PGHOST", null);
            Environment.SetEnvironmentVariable("PGPORT", null);
            Environment.SetEnvironmentVariable("PGUSER", null);
            Environment.SetEnvironmentVariable("PGPASSWORD", null);
            Environment.SetEnvironmentVariable("PGDATABASE", null);

            string connectionString = InvokeResolverConnectionString();

            Assert.Contains("Host=db.internal", connectionString, StringComparison.Ordinal);
            Assert.Contains("Username=atlas", connectionString, StringComparison.Ordinal);
            Assert.Contains("Database=atlasdb", connectionString, StringComparison.Ordinal);
        }
        finally
        {
            RestoreEnvironment(previous);
        }
    }

    private static bool InvokeResolverHasPostgresConfiguration()
    {
        Type resolverType = typeof(PostgresBotStateRepository).Assembly.GetType("Atlas.Api.Services.PostgresConnectionResolver")!;
        MethodInfo method = resolverType.GetMethod("HasPostgresConfiguration", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(null, null)!;
    }

    private static string InvokeResolverConnectionString()
    {
        Type resolverType = typeof(PostgresBotStateRepository).Assembly.GetType("Atlas.Api.Services.PostgresConnectionResolver")!;
        MethodInfo method = resolverType.GetMethod("ResolveConnectionString", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (string)method.Invoke(null, null)!;
    }

    private static Dictionary<string, string?> CaptureEnvironment(params string[] keys) =>
        keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

    private static void RestoreEnvironment(Dictionary<string, string?> values)
    {
        foreach ((string key, string? value) in values)
            Environment.SetEnvironmentVariable(key, value);
    }
}

public class PolymarketBotServiceTests
{
    [Fact]
    public void TelegramPolymarketMenuFormatter_Menu_ContainsCoreCommands()
    {
        string menu = TelegramPolymarketMenuFormatter.BuildMenu();

        Assert.Contains("/menu", menu, StringComparison.Ordinal);
        Assert.Contains("/status", menu, StringComparison.Ordinal);
        Assert.Contains("/pnl", menu, StringComparison.Ordinal);
        Assert.Contains("/metrics", menu, StringComparison.Ordinal);
        Assert.Contains("/positions", menu, StringComparison.Ordinal);
        Assert.Contains("/history", menu, StringComparison.Ordinal);
        Assert.Contains("/journal", menu, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/menu", "menu")]
    [InlineData("/pnl@AtlasBot", "pnl")]
    [InlineData("/metrics@AtlasBot", "metrics")]
    [InlineData("  /positions  ", "positions")]
    [InlineData("journal", "journal")]
    public void TelegramPolymarketMenuFormatter_NormalizeCommand_StripsTelegramSyntax(string raw, string expected)
    {
        Assert.Equal(expected, TelegramPolymarketMenuFormatter.NormalizeCommand(raw));
    }

    [Fact]
    public void TelegramPolymarketMenuFormatter_Pnl_IncludesEquityCashAndDaily()
    {
        string text = TelegramPolymarketMenuFormatter.BuildPnl(BuildLiveSnapshot(
            BuildSignal("MKT-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81)));

        Assert.Contains("ATLAS PNL", text, StringComparison.Ordinal);
        Assert.Contains("Equity:", text, StringComparison.Ordinal);
        Assert.Contains("Cash:", text, StringComparison.Ordinal);
        Assert.Contains("Daily:", text, StringComparison.Ordinal);
        Assert.Contains("Monthly:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TelegramPolymarketMenuFormatter_Metrics_IncludesRiskAndExposure()
    {
        string text = TelegramPolymarketMenuFormatter.BuildMetrics(BuildLiveSnapshot(
            BuildSignal("MKT-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81)));

        Assert.Contains("ATLAS METRICS", text, StringComparison.Ordinal);
        Assert.Contains("Win rate:", text, StringComparison.Ordinal);
        Assert.Contains("Avg winner:", text, StringComparison.Ordinal);
        Assert.Contains("Avg loser:", text, StringComparison.Ordinal);
        Assert.Contains("Drawdown:", text, StringComparison.Ordinal);
        Assert.Contains("Gross exposure:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolymarketBot_OpensOneDollarCappedPositions_AndPublishesJournal()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "25");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");

            var live = new StaticPolymarketBotLiveService(BuildLiveSnapshot(
                BuildSignal("MKT-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81),
                BuildSignal("MKT-2", "ETH", "Will Ethereum be below $3,500 in 25 minutes?", "Buy No", 0.63, 0.37, 0.31, 0.69, 25, 68, 79)));
            var telegram = new CaptureTelegramService();
            var service = BuildPolymarketBotService(tempRoot, live, telegram, AtlasRuntimeRole.All);

            PolymarketLiveSnapshot snapshot = await service.RunAutopilotAsync();

            Assert.Equal(2, snapshot.OpenPositions.Count);
            Assert.All(snapshot.OpenPositions, position => Assert.InRange(position.StakeUsd, 0.99, 1.01));
            Assert.Equal(23, snapshot.Portfolio.CashBalanceUsd, 10);
            Assert.True(snapshot.Journal.Count >= 2);
            Assert.Equal(2, telegram.Messages.Count);
            Assert.All(telegram.Messages, message => Assert.Contains("NEW ORDER |", message));
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PolymarketBot_DoesNotDuplicateSameMarketAcrossCycles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "10");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");

            var live = new StaticPolymarketBotLiveService(BuildLiveSnapshot(
                BuildSignal("MKT-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81)));
            var telegram = new CaptureTelegramService();
            var service = BuildPolymarketBotService(tempRoot, live, telegram, AtlasRuntimeRole.All);

            PolymarketLiveSnapshot first = await service.RunAutopilotAsync();
            PolymarketLiveSnapshot second = await service.RunAutopilotAsync();

            Assert.Single(first.OpenPositions);
            Assert.Single(second.OpenPositions);
            Assert.InRange(second.Portfolio.CashBalanceUsd, 9.0, 10.0); // Kelly sizing: stake varies with edge
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PolymarketBot_CanHoldThresholdAndDirectionalTrade_OnSameAsset()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE",
            "POLYMARKET_MAX_NEW_TRADES_PER_CYCLE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "10");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_NEW_TRADES_PER_CYCLE", "2");

            var live = new StaticPolymarketBotLiveService(BuildLiveSnapshot(
                BuildSignal("BTC-TRESH-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81),
                BuildDirectionalSignal("BTC-UPDOWN-1", "BTC", "Bitcoin Up or Down - April 8, 2:00PM-2:05PM ET", "Buy Up", 0.46, 0.54, 0.61, 0.39, 5, 76, 88, 81_250)));
            var telegram = new CaptureTelegramService();
            var service = BuildPolymarketBotService(tempRoot, live, telegram, AtlasRuntimeRole.All);

            PolymarketLiveSnapshot snapshot = await service.RunAutopilotAsync();

            Assert.Equal(2, snapshot.OpenPositions.Count);
            Assert.Equal(2, snapshot.OpenPositions.Count(position => position.Asset == "BTC"));
            Assert.Contains(snapshot.OpenPositions, position => position.SignalCategory == "threshold");
            Assert.Contains(snapshot.OpenPositions, position => position.SignalCategory == "directional");
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PolymarketBot_NoTrade_JournalsDominantGateReason_WhenScannerHasOnlyBorderlineSignals()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "10");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");

            var live = new StaticPolymarketBotLiveService(BuildLiveSnapshot(
                BuildSignal("MKT-LOW-Q", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 44, 80)));
            var telegram = new CaptureTelegramService();
            var service = BuildPolymarketBotService(tempRoot, live, telegram, AtlasRuntimeRole.All);

            PolymarketLiveSnapshot snapshot = await service.RunAutopilotAsync();

            Assert.Empty(snapshot.OpenPositions);
            Assert.Equal(1, snapshot.Stats.ScannerSignals);
            Assert.Equal(0, snapshot.Stats.ActionableSignals);
            Assert.Contains(snapshot.Journal, entry => entry.Type == "watch" && entry.Detail.Contains("Dominant reason:", StringComparison.Ordinal));
            Assert.Empty(telegram.Messages);
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PolymarketBot_TakeProfit_SendsTelegramWithEquityAndCash()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "10");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");

            int cycle = 0;
            var live = new StaticPolymarketBotLiveService(() =>
            {
                cycle++;
                return cycle == 1
                    ? BuildLiveSnapshot(
                        BuildSignal("MKT-TP-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Buy Yes", 0.42, 0.58, 0.57, 0.43, 20, 72, 81))
                    : BuildLiveSnapshot(
                        BuildSignal("MKT-TP-1", "BTC", "Will Bitcoin be above $80,000 in 20 minutes?", "Pass", 0.65, 0.35, 0.57, 0.43, 19, 72, 48));
            });
            var telegram = new CaptureTelegramService();
            var service = BuildPolymarketBotService(tempRoot, live, telegram, AtlasRuntimeRole.All);

            PolymarketLiveSnapshot opened = await service.RunAutopilotAsync();
            PolymarketLiveSnapshot closed = await service.RunAutopilotAsync();

            Assert.Single(opened.OpenPositions);
            Assert.Empty(closed.OpenPositions);
            Assert.True(telegram.Messages.Count >= 2);
            Assert.Contains(telegram.Messages, message => message.Contains("NEW ORDER |", StringComparison.Ordinal));
            Assert.Contains(telegram.Messages, message => message.Contains("TP |", StringComparison.Ordinal));
            Assert.Contains(telegram.Messages, message => message.Contains("EQUITY", StringComparison.Ordinal));
            Assert.Contains(telegram.Messages, message => message.Contains("CASH", StringComparison.Ordinal));
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PolymarketBotRuleEngine_FlagsBorderlineScannerSignal_AsNotBotReady()
    {
        PolymarketMarketSignal signal = BuildSignal(
            "MKT-BORDER",
            "ETH",
            "Will Ethereum be above $3,500 in 18 minutes?",
            "Buy Yes",
            0.44,
            0.56,
            0.57,
            0.43,
            18,
            45,
            78);

        PolymarketBotSignalAssessment assessment = PolymarketBotRuleEngine.AssessSignal(signal, 24 * 60);

        Assert.False(assessment.BotEligible);
        Assert.Contains("quality", assessment.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PolymarketBot_ApiRole_ReadsPersistedPortfolioState()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-poly-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var previous = CaptureEnvironment(
            "POLYMARKET_BOT_ENABLED",
            "POLYMARKET_EXECUTION_MODE",
            "POLYMARKET_MAX_TRADE_USD",
            "POLYMARKET_STARTING_BALANCE_USD",
            "POLYMARKET_DAILY_LOSS_LIMIT_USD",
            "POLYMARKET_BOT_EVALUATION_SECONDS",
            "POLYMARKET_REPORT_TIMEZONE");

        try
        {
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_ENABLED", "true");
            Environment.SetEnvironmentVariable("POLYMARKET_EXECUTION_MODE", "paper");
            Environment.SetEnvironmentVariable("POLYMARKET_MAX_TRADE_USD", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_STARTING_BALANCE_USD", "12");
            Environment.SetEnvironmentVariable("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5");
            Environment.SetEnvironmentVariable("POLYMARKET_BOT_EVALUATION_SECONDS", "1");
            Environment.SetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE", "UTC");

            var live = new StaticPolymarketBotLiveService(BuildLiveSnapshot(
                BuildSignal("MKT-1", "SOL", "Will Solana be above $160 in 15 minutes?", "Buy Yes", 0.37, 0.63, 0.59, 0.41, 15, 71, 84)));
            var worker = BuildPolymarketBotService(tempRoot, live, new CaptureTelegramService(), AtlasRuntimeRole.BotWorker);
            await worker.RunAutopilotAsync();

            var apiService = BuildPolymarketBotService(tempRoot, live, new CaptureTelegramService(), AtlasRuntimeRole.Api);
            PolymarketLiveSnapshot snapshot = await apiService.GetSnapshotAsync();

            Assert.Single(snapshot.OpenPositions);
            Assert.InRange(snapshot.Portfolio.CashBalanceUsd, 11.0, 12.0); // Kelly sizing: stake < 1$
            Assert.Equal("live-ready", snapshot.Status switch
            {
                "live-trading" => "live-ready",
                var other => other
            });
        }
        finally
        {
            RestoreEnvironment(previous);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static PolymarketBotService BuildPolymarketBotService(
        string tempRoot,
        IPolymarketLiveService liveService,
        ITelegramSignalService telegram,
        AtlasRuntimeRole role)
    {
        var repository = new FileBotStateRepository(new RuntimeFakeHostEnvironment(tempRoot), NullLogger<FileBotStateRepository>.Instance);
        var runtime = new AtlasRuntimeContext(role, $"poly-{role.ToString().ToLowerInvariant()}", "unit-test");
        return new PolymarketBotService(
            liveService,
            repository,
            runtime,
            telegram,
            new NoopPolymarketClobClient(),
            NullLogger<PolymarketBotService>.Instance);
    }

    private static PolymarketLiveSnapshot BuildLiveSnapshot(params PolymarketMarketSignal[] opportunities)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PolymarketLiveSnapshot(
            Status: "analysis-ready",
            Summary: "Test snapshot",
            Runtime: new PolymarketRuntimeStatus(
                TradingEnabled: false,
                SignerConfigured: false,
                TelegramConfigured: false,
                ExecutionArmed: false,
                DailyLossLockActive: false,
                RuntimeMode: "analysis-only",
                WalletAddressHint: "not-configured",
                MaxTradeUsd: 1,
                DailyLossLimitUsd: 5,
                Summary: "test"),
            Assets: new[]
            {
                new PolymarketReferenceAssetSnapshot("BTC", 81_250, 0.55, "Momentum", 77, 18, "Bullish", now),
                new PolymarketReferenceAssetSnapshot("ETH", 3_480, 0.63, "Compression", 69, -12, "Bearish", now),
                new PolymarketReferenceAssetSnapshot("SOL", 161, 0.78, "Expansion", 74, 21, "Bullish", now)
            },
            BotTiers: [],
            Opportunities: opportunities,
            Stats: new PolymarketScanStats(
                1,
                1,
                opportunities.Length,
                opportunities.Length,
                opportunities.Count(x => x.MinutesToExpiry <= 30),
                opportunities.Count(x => !string.Equals(x.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase)),
                opportunities.Count(x => x.BotEligible)),
            Notes: ["test"],
            Portfolio: new PolymarketBotPortfolioSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, now),
            OpenPositions: [],
            RecentClosedPositions: [],
            Journal: [],
            Timestamp: now);
    }

    private static PolymarketMarketSignal BuildSignal(
        string marketId,
        string asset,
        string question,
        string side,
        double marketYes,
        double marketNo,
        double fairYes,
        double fairNo,
        double minutes,
        double quality,
        double conviction)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PolymarketMarketSignal(
            EventId: $"EV-{marketId}",
            MarketId: marketId,
            Asset: asset,
            Question: question,
            Slug: marketId.ToLowerInvariant(),
            Expiry: now.AddMinutes(minutes),
            MinutesToExpiry: minutes,
            Spot: asset == "ETH" ? 3_480 : asset == "SOL" ? 161 : 81_250,
            StrikeLow: asset == "ETH" ? 3_500 : asset == "SOL" ? 160 : 80_000,
            StrikeHigh: null,
            ThresholdType: "Above",
            SignalCategory: "threshold",
            DisplayLabel: $"{asset} above {(asset == "ETH" ? 3_500 : asset == "SOL" ? 160 : 80_000):0}",
            PrimaryOutcomeLabel: "Yes",
            SecondaryOutcomeLabel: "No",
            MarketYesPrice: marketYes,
            MarketNoPrice: marketNo,
            BestBid: Math.Max(0, marketYes - 0.02),
            BestAsk: marketYes + 0.02,
            Spread: 0.04,
            LiquidityUsd: 10_000,
            Volume24hUsd: 750,
            FairYesProbability: fairYes,
            FairNoProbability: fairNo,
            EdgeYesPct: fairYes - marketYes,
            EdgeNoPct: fairNo - marketNo,
            DistanceToStrikePct: 0.01,
            VolInput: 0.55,
            QualityScore: quality,
            ConvictionScore: conviction,
            RecommendedSide: side,
            Summary: $"{asset} test edge",
            MacroReasoning: "macro test",
            MicroReasoning: "micro test",
            MathReasoning: "math test",
            ExecutionPlan: new PolymarketExecutionPlan(
                Side: side,
                OrderStyle: "passive-limit",
                IndicativeEntryPrice: side == "Buy Yes" ? marketYes : marketNo,
                MaxPositionPct: 0.01,
                TimeStopMinutes: (int)Math.Round(minutes * 0.5),
                ExitPlan: "exit test",
                RiskPlan: "risk test"),
            BotEligible: !string.Equals(side, "Pass", StringComparison.OrdinalIgnoreCase) && quality >= 46 && conviction >= 55,
            BotEligibilityReason: !string.Equals(side, "Pass", StringComparison.OrdinalIgnoreCase) && quality >= 46 && conviction >= 55 ? "bot-ready" : "quality or conviction below threshold",
            BotEntryPrice: side == "Buy Yes" ? marketYes : marketNo,
            BotSelectedEdgePct: Math.Max(fairYes - marketYes, fairNo - marketNo));
    }

    private static PolymarketMarketSignal BuildDirectionalSignal(
        string marketId,
        string asset,
        string question,
        string side,
        double marketYes,
        double marketNo,
        double fairYes,
        double fairNo,
        double minutes,
        double quality,
        double conviction,
        double referencePrice)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new PolymarketMarketSignal(
            EventId: $"EV-{marketId}",
            MarketId: marketId,
            Asset: asset,
            Question: question,
            Slug: marketId.ToLowerInvariant(),
            Expiry: now.AddMinutes(minutes),
            MinutesToExpiry: minutes,
            Spot: referencePrice,
            StrikeLow: referencePrice,
            StrikeHigh: null,
            ThresholdType: "Above",
            SignalCategory: "directional",
            DisplayLabel: $"{asset} 5m up/down",
            PrimaryOutcomeLabel: "Up",
            SecondaryOutcomeLabel: "Down",
            MarketYesPrice: marketYes,
            MarketNoPrice: marketNo,
            BestBid: Math.Max(0, marketYes - 0.02),
            BestAsk: marketYes + 0.02,
            Spread: 0.04,
            LiquidityUsd: 10_000,
            Volume24hUsd: 900,
            FairYesProbability: fairYes,
            FairNoProbability: fairNo,
            EdgeYesPct: fairYes - marketYes,
            EdgeNoPct: fairNo - marketNo,
            DistanceToStrikePct: 0.002,
            VolInput: 0.55,
            QualityScore: quality,
            ConvictionScore: conviction,
            RecommendedSide: side,
            Summary: $"{asset} directional test edge",
            MacroReasoning: "macro test",
            MicroReasoning: "micro test",
            MathReasoning: "math test",
            ExecutionPlan: new PolymarketExecutionPlan(
                Side: side,
                OrderStyle: "join-best-or-better",
                IndicativeEntryPrice: side == "Buy Up" ? marketYes : marketNo,
                MaxPositionPct: 0.01,
                TimeStopMinutes: (int)Math.Round(minutes * 0.5),
                ExitPlan: "exit test",
                RiskPlan: "risk test"),
            BotEligible: !string.Equals(side, "Pass", StringComparison.OrdinalIgnoreCase) && quality >= 46 && conviction >= 55,
            BotEligibilityReason: !string.Equals(side, "Pass", StringComparison.OrdinalIgnoreCase) && quality >= 46 && conviction >= 55 ? "bot-ready" : "quality or conviction below threshold",
            BotEntryPrice: side == "Buy Up" ? marketYes : marketNo,
            BotSelectedEdgePct: Math.Max(fairYes - marketYes, fairNo - marketNo));
    }

    private static Dictionary<string, string?> CaptureEnvironment(params string[] keys) =>
        keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);

    private static void RestoreEnvironment(Dictionary<string, string?> values)
    {
        foreach ((string key, string? value) in values)
            Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class StaticPolymarketBotLiveService : IPolymarketLiveService
    {
        private readonly Func<PolymarketLiveSnapshot> _factory;

        public StaticPolymarketBotLiveService(PolymarketLiveSnapshot snapshot)
        {
            _factory = () => snapshot;
        }

        public StaticPolymarketBotLiveService(Func<PolymarketLiveSnapshot> factory)
        {
            _factory = factory;
        }

        public Task<PolymarketLiveSnapshot> GetLiveSnapshotAsync(int lookaheadMinutes = 24 * 60, int maxMarkets = 24, CancellationToken ct = default) =>
            Task.FromResult(_factory());
    }

    private sealed class CaptureTelegramService : ITelegramSignalService
    {
        public bool IsConfigured => true;
        public List<string> Messages { get; } = [];

        public Task SendAsync(string message, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task SendWithKeyboardAsync(string message, TelegramInlineKeyboard keyboard, CancellationToken ct = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeFakeHostEnvironment : IHostEnvironment
    {
        public RuntimeFakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ApplicationName = "Atlas.Tests";
            EnvironmentName = "Development";
            ContentRootFileProvider = null!;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }
}

public class PaperTradingTargetedTests
{
    [Fact]
    public async Task PaperTrading_Idempotent_SameClientOrderId()
    {
        var service = CreatePaperTradingService(BuildQuote());
        var request = new TradingOrderRequest(
            Symbol: DefaultSymbol,
            Side: TradeDirection.Buy,
            Quantity: 1,
            Type: OrderType.Market,
            ClientOrderId: "CID-001");

        TradingOrderReport first = await service.PlaceOrderAsync(request);
        TradingOrderReport replay = await service.PlaceOrderAsync(request);

        Assert.Equal(first.OrderId, replay.OrderId);
        Assert.True(replay.IdempotentReplay);
        Assert.Contains("IdempotentReplay", replay.StateTrace ?? []);
    }

    [Fact]
    public async Task PaperTrading_FingerprintDuplicate_Rejected()
    {
        var service = CreatePaperTradingService(BuildQuote());
        var request = new TradingOrderRequest(
            Symbol: DefaultSymbol,
            Side: TradeDirection.Buy,
            Quantity: 2,
            Type: OrderType.Market);

        TradingOrderReport first = await service.PlaceOrderAsync(request);
        TradingOrderReport second = await service.PlaceOrderAsync(request);

        Assert.NotEqual(OrderStatus.Rejected, first.Status);
        Assert.Equal(OrderStatus.Rejected, second.Status);
        Assert.Equal("duplicate-order-fingerprint", second.RejectReason);
    }

    [Fact]
    public async Task PaperTrading_KillSwitch_BlocksNewOrders()
    {
        var service = CreatePaperTradingService(BuildQuote());
        await service.SetKillSwitchAsync(new KillSwitchRequest(true, "manual-test", "tests"));

        TradingOrderReport report = await service.PlaceOrderAsync(new TradingOrderRequest(
            Symbol: DefaultSymbol,
            Side: TradeDirection.Buy,
            Quantity: 1));

        Assert.Equal(OrderStatus.Rejected, report.Status);
        Assert.Contains("kill-switch active", report.RejectReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PaperTrading_PreviewOrder_ReturnsProjectedMargins()
    {
        var service = CreatePaperTradingService(BuildQuote());

        PreTradePreviewResult preview = await service.PreviewOrderAsync(new TradingOrderRequest(
            Symbol: DefaultSymbol,
            Side: TradeDirection.Buy,
            Quantity: 3,
            Type: OrderType.Market));

        Assert.True(preview.Accepted);
        Assert.True(preview.EstimatedInitialMargin >= 100);
        Assert.True(preview.EstimatedMaintenanceMargin >= 75);
        Assert.True(preview.ProjectedRisk.GrossNotional > 0);
    }

    [Fact]
    public async Task PaperTrading_RiskBreach_RejectsOrder()
    {
        var highDeltaQuote = BuildQuote(delta: 400, mark: 12, bid: 11.9, ask: 12.1);
        var service = CreatePaperTradingService(highDeltaQuote);

        TradingOrderReport report = await service.PlaceOrderAsync(new TradingOrderRequest(
            Symbol: DefaultSymbol,
            Side: TradeDirection.Buy,
            Quantity: 1));

        Assert.Equal(OrderStatus.Rejected, report.Status);
        Assert.Contains("risk breach", report.RejectReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("net-delta", report.RejectReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RiskSnapshot_NetDeltaBreach_Flags()
    {
        var service = CreatePaperTradingService(BuildQuote());
        var positions = new[]
        {
            new TradingPosition(
                Symbol: DefaultSymbol,
                Asset: "BTC",
                NetQuantity: 1,
                AvgEntryPrice: 10,
                MarkPrice: 10,
                Notional: 100_000,
                UnrealizedPnl: 0,
                RealizedPnl: 0,
                Greeks: new GreeksResult(350, 0, 0, 0, 0, 0, 0, 0),
                UpdatedAt: DateTimeOffset.UtcNow)
        };

        PortfolioRiskSnapshot snapshot = InvokeRiskSnapshot(service, positions);

        Assert.True(snapshot.Breached);
        Assert.Contains("net-delta", snapshot.Flags);
    }

    [Fact]
    public async Task RiskSnapshot_KillSwitchActive_Flags()
    {
        var service = CreatePaperTradingService(BuildQuote());
        await service.SetKillSwitchAsync(new KillSwitchRequest(true, "manual-test", "tests"));

        PortfolioRiskSnapshot snapshot = InvokeRiskSnapshot(service, Array.Empty<TradingPosition>());

        Assert.True(snapshot.Breached);
        Assert.True(snapshot.KillSwitchActive);
        Assert.Contains("kill-switch", snapshot.Flags);
    }

    private const string DefaultSymbol = "BTC-30APR26-100000-C-USD";

    private static PaperTradingService CreatePaperTradingService(params LiveOptionQuote[] quotes)
    {
        var monitoring = new SystemMonitoringService();
        var persistence = new InMemoryTradingPersistence();
        var marketData = new StaticMarketDataService(quotes);

        return new PaperTradingService(
            marketData,
            NullLogger<PaperTradingService>.Instance,
            monitoring,
            persistence);
    }

    private static PortfolioRiskSnapshot InvokeRiskSnapshot(PaperTradingService service, IReadOnlyList<TradingPosition> positions)
    {
        MethodInfo method = typeof(PaperTradingService).GetMethod("BuildRiskSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRiskSnapshot method not found.");

        return (PortfolioRiskSnapshot)(method.Invoke(service, [positions]) ?? throw new InvalidOperationException("Risk snapshot invocation returned null."));
    }

    private static LiveOptionQuote BuildQuote(
        string symbol = DefaultSymbol,
        double bid = 9.8,
        double ask = 10.2,
        double mark = 10.0,
        double mid = 10.0,
        double markIv = 0.55,
        double delta = 0.52,
        double gamma = 0.01,
        double vega = 2.4,
        double theta = -0.7,
        double openInterest = 1_000,
        double volume24h = 500,
        double turnover24h = 150_000,
        double underlying = 95_000)
    {
        return new LiveOptionQuote(
            Symbol: symbol,
            Asset: "BTC",
            Expiry: new DateTimeOffset(2026, 4, 30, 8, 0, 0, TimeSpan.Zero),
            Strike: 100_000,
            Right: OptionRight.Call,
            Bid: bid,
            Ask: ask,
            Mark: mark,
            Mid: mid,
            MarkIv: markIv,
            Delta: delta,
            Gamma: gamma,
            Vega: vega,
            Theta: theta,
            OpenInterest: openInterest,
            Volume24h: volume24h,
            Turnover24h: turnover24h,
            UnderlyingPrice: underlying,
            Timestamp: DateTimeOffset.UtcNow,
            Venue: "UNIT",
            SourceTimestamp: DateTimeOffset.UtcNow,
            IsStale: false);
    }
}

public class MarketDataFallbackTests
{
    [Fact]
    public async Task MarketData_FallbackToSynthetic_WhenPrimaryUnavailable()
    {
        var bybit = new SwitchableHandler();
        bybit.Fail(HttpStatusCode.Forbidden, "blocked");
        var deribit = new SwitchableHandler();
        deribit.Fail(HttpStatusCode.BadGateway, "unavailable");

        var monitoring = new SystemMonitoringService();
        var service = new ResilientOptionsMarketDataService(
            new NamedHttpClientFactory(bybit, deribit),
            NullLogger<ResilientOptionsMarketDataService>.Instance,
            monitoring,
            includeSyntheticFallback: true,
            syntheticChainFactory: null);

        IReadOnlyList<LiveOptionQuote> chain = await service.GetOptionChainAsync("BTC");
        MarketDataCompositeStatus status = await service.GetStatusAsync("BTC");

        Assert.NotEmpty(chain);
        Assert.All(chain, quote => Assert.Equal("SYNTH", quote.Venue));
        Assert.Equal("SYNTHETIC", status.ActiveSource);
        Assert.Contains(monitoring.GetActiveAlerts(), alert => alert.Message.Contains("Fallback source SYNTHETIC used for BTC.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MarketData_StaleCache_TriggersFallback()
    {
        var bybit = new SwitchableHandler();
        bybit.Succeed(BuildBybitPayload(DateTimeOffset.UtcNow));
        var deribit = new SwitchableHandler();
        deribit.Fail(HttpStatusCode.BadGateway, "unavailable");

        var monitoring = new SystemMonitoringService();
        var service = new ResilientOptionsMarketDataService(
            new NamedHttpClientFactory(bybit, deribit),
            NullLogger<ResilientOptionsMarketDataService>.Instance,
            monitoring,
            includeSyntheticFallback: false,
            syntheticChainFactory: (_, _) => []);

        IReadOnlyList<LiveOptionQuote> warm = await service.GetOptionChainAsync("BTC");
        Assert.NotEmpty(warm);

        bybit.Fail(HttpStatusCode.BadGateway, "down");
        await Task.Delay(TimeSpan.FromMilliseconds(6_300));

        IReadOnlyList<LiveOptionQuote> stale = await service.GetOptionChainAsync("BTC");

        Assert.NotEmpty(stale);
        Assert.All(stale, quote => Assert.True(quote.IsStale));
        Assert.Contains(monitoring.GetMetrics(), metric => metric.Name == "marketdata.cache_stale_served" && metric.Value >= 1);
    }

    [Fact]
    public async Task MarketData_MissingFeedGreeks_AreRecomputed()
    {
        var bybit = new SwitchableHandler();
        bybit.Succeed(BuildBybitPayload(DateTimeOffset.UtcNow));
        var deribit = new SwitchableHandler();
        deribit.Fail(HttpStatusCode.BadGateway, "unavailable");

        var service = new ResilientOptionsMarketDataService(
            new NamedHttpClientFactory(bybit, deribit),
            NullLogger<ResilientOptionsMarketDataService>.Instance,
            new SystemMonitoringService(),
            includeSyntheticFallback: false,
            syntheticChainFactory: (_, _) => []);

        IReadOnlyList<LiveOptionQuote> chain = await service.GetOptionChainAsync("BTC");
        LiveOptionQuote quote = Assert.Single(chain);

        Assert.True(Math.Abs(quote.Delta) > 1e-6);
        Assert.True(Math.Abs(quote.Gamma) > 1e-9);
        Assert.True(Math.Abs(quote.Vega) > 1e-6);
        Assert.True(Math.Abs(quote.Theta) > 1e-6);
    }

    private static string BuildBybitPayload(DateTimeOffset timestamp)
    {
        long ms = timestamp.ToUnixTimeMilliseconds();
        return $$"""
        {
          "retCode": 0,
          "time": {{ms}},
          "result": {
            "list": [
              {
                "symbol": "BTC-30APR26-100000-C-USDT",
                "bid1Price": "1200",
                "ask1Price": "1210",
                "markPrice": "1205",
                "markIv": "0.55",
                "delta": "0",
                "gamma": "0",
                "vega": "0",
                "theta": "0",
                "openInterest": "1500",
                "volume24h": "500",
                "turnover24h": "610000",
                "underlyingPrice": "82000"
              }
            ]
          }
        }
        """;
    }
}

internal sealed class StaticMarketDataService : IOptionsMarketDataService
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<LiveOptionQuote>> _chains;

    public StaticMarketDataService(IEnumerable<LiveOptionQuote> quotes)
    {
        _chains = quotes
            .GroupBy(q => q.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<LiveOptionQuote>)g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> SupportedAssets => _chains.Keys.ToList();

    public Task<IReadOnlyList<LiveOptionQuote>> GetOptionChainAsync(string asset, CancellationToken ct = default)
    {
        _chains.TryGetValue(asset.ToUpperInvariant(), out IReadOnlyList<LiveOptionQuote>? chain);
        return Task.FromResult(chain ?? Array.Empty<LiveOptionQuote>());
    }

    public Task<MarketDataCompositeStatus> GetStatusAsync(string asset, CancellationToken ct = default)
    {
        _chains.TryGetValue(asset.ToUpperInvariant(), out IReadOnlyList<LiveOptionQuote>? chain);
        int count = chain?.Count ?? 0;
        DateTimeOffset asOf = chain?.Select(q => q.SourceTimestamp ?? q.Timestamp).DefaultIfEmpty(DateTimeOffset.UtcNow).Max() ?? DateTimeOffset.UtcNow;

        return Task.FromResult(new MarketDataCompositeStatus(
            Asset: asset.ToUpperInvariant(),
            ActiveSource: count > 0 ? "UNIT" : "NONE",
            IsStale: false,
            AsOf: asOf,
            SourceLagMs: 0,
            QuoteCount: count,
            Sources:
            [
                new MarketDataSourceStatus(
                    Source: count > 0 ? "UNIT" : "NONE",
                    Asset: asset.ToUpperInvariant(),
                    Healthy: count > 0,
                    IsFallback: false,
                    IsStale: false,
                    QuoteCount: count,
                    LastLatencyMs: 0,
                    LastSuccessAt: asOf,
                    LastFailureAt: null,
                    LastError: null,
                    SnapshotAt: DateTimeOffset.UtcNow)
            ],
            SnapshotAt: DateTimeOffset.UtcNow));
    }

    public async Task<IReadOnlyList<MarketDataCompositeStatus>> GetStatusesAsync(CancellationToken ct = default)
    {
        var tasks = SupportedAssets.Select(asset => GetStatusAsync(asset, ct));
        return await Task.WhenAll(tasks);
    }
}

internal sealed class InMemoryTradingPersistence : ITradingPersistenceService
{
    public List<PersistedOrderEvent> Orders { get; } = [];
    public List<PersistedPositionEvent> Positions { get; } = [];
    public List<PersistedRiskEvent> Risks { get; } = [];
    public List<PersistedAuditEvent> AuditTrail { get; } = [];

    public void AppendOrderEvent(TradingOrderReport report, string source = "oms") =>
        Orders.Add(new PersistedOrderEvent(Orders.Count + 1, report.OrderId, report.Status, report.Symbol, source, DateTimeOffset.UtcNow, report));

    public void AppendPositionSnapshot(IReadOnlyList<TradingPosition> positions, string source = "positions") =>
        Positions.Add(new PersistedPositionEvent(Positions.Count + 1, source, DateTimeOffset.UtcNow, positions));

    public void AppendRiskEvent(PortfolioRiskSnapshot snapshot, string source = "risk") =>
        Risks.Add(new PersistedRiskEvent(Risks.Count + 1, source, DateTimeOffset.UtcNow, snapshot));

    public void AppendNotificationEvent(TradingNotification notification) =>
        AuditTrail.Add(new PersistedAuditEvent(AuditTrail.Count + 1, "notification", notification.Message, "{}", DateTimeOffset.UtcNow));

    public void AppendAuditEvent(string category, string message, string payloadJson = "{}") =>
        AuditTrail.Add(new PersistedAuditEvent(AuditTrail.Count + 1, category, message, payloadJson, DateTimeOffset.UtcNow));

    public IReadOnlyList<PersistedOrderEvent> GetOrderEvents(int limit = 500) => Orders.TakeLast(limit).ToList();
    public IReadOnlyList<PersistedPositionEvent> GetPositionEvents(int limit = 500) => Positions.TakeLast(limit).ToList();
    public IReadOnlyList<PersistedRiskEvent> GetRiskEvents(int limit = 500) => Risks.TakeLast(limit).ToList();
    public IReadOnlyList<PersistedAuditEvent> GetAuditEvents(int limit = 500) => AuditTrail.TakeLast(limit).ToList();
}

internal sealed class NamedHttpClientFactory : IHttpClientFactory
{
    private readonly HttpClient _bybit;
    private readonly HttpClient _deribit;

    public NamedHttpClientFactory(HttpMessageHandler bybitHandler, HttpMessageHandler deribitHandler)
    {
        _bybit = new HttpClient(bybitHandler) { BaseAddress = new Uri("https://unit.test") };
        _deribit = new HttpClient(deribitHandler) { BaseAddress = new Uri("https://unit.test") };
    }

    public HttpClient CreateClient(string name) => name switch
    {
        "bybit-options" => _bybit,
        "bytick-options" => _bybit,
        "deribit-options" => _deribit,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown client name.")
    };
}

internal sealed class SwitchableHandler : HttpMessageHandler
{
    private Func<HttpResponseMessage> _factory = () => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
        Content = new StringContent("no-response", Encoding.UTF8, "application/json")
    };

    public void Succeed(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        _factory = () => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    public void Fail(HttpStatusCode statusCode, string body) =>
        _factory = () => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(_factory());
}
