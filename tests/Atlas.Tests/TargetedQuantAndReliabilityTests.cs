using System.Net;
using System.Reflection;
using System.Text;
using Atlas.Api.Models;
using Atlas.Api.Services;
using Atlas.Core.Common;
using Atlas.Core.Models;
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
