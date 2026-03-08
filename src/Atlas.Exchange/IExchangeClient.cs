namespace Atlas.Exchange;

/// <summary>
/// Exchange client interface. Implement for each venue.
///
/// DEMO: DemoExchangeClient returns synthetic data.
/// PRODUCTION: Implement DeribitClient, CoinbaseClient, etc.
///
/// Key integration points:
/// - REST: /public/auth, /private/buy, /private/sell, /public/get_instruments
/// - WebSocket: order book, trades, instrument updates
/// - Rate limits: implement exponential backoff with jitter
/// </summary>
public interface IExchangeClient
{
    Task<bool> AuthenticateAsync(string clientId, string secret, CancellationToken ct = default);
    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task<bool> CancelOrderAsync(string orderId, CancellationToken ct = default);
    Task CancelAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);
    Task<AccountSummary> GetAccountAsync(CancellationToken ct = default);
    IAsyncEnumerable<MarketTick> SubscribeTicksAsync(string instrument, CancellationToken ct = default);
    IAsyncEnumerable<OrderBookSnapshot> SubscribeBookAsync(string instrument, CancellationToken ct = default);
}

// ─── DTOs ────────────────────────────────────────────────

public record OrderRequest(
    string Instrument, string Side, double Amount,
    double? Price = null, string Type = "limit");

public record OrderResult(
    string OrderId, string Status, double FilledAmount,
    double AvgPrice, TimeSpan Latency);

public record Position(
    string Instrument, double Size, double AvgPrice,
    double MarkPrice, double UnrealizedPnl, double Delta);

public record AccountSummary(
    double Equity, double Balance, double MarginUsed,
    double AvailableMargin, double UnrealizedPnl);

public record MarketTick(
    string Instrument, double BestBid, double BestAsk,
    double MarkPrice, double IndexPrice, DateTimeOffset Timestamp);

public record OrderBookSnapshot(
    string Instrument,
    IReadOnlyList<(double Price, double Size)> Bids,
    IReadOnlyList<(double Price, double Size)> Asks,
    DateTimeOffset Timestamp);
