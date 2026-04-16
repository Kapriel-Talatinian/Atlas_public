using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Api.Services;

public interface IPolymarketClobClient
{
    bool IsConfigured { get; }
    Task<ClobOrderResult> PlaceOrderAsync(ClobPlaceOrderRequest request, CancellationToken ct = default);
    Task<ClobCancelResult> CancelOrderAsync(string orderId, CancellationToken ct = default);
    Task<ClobCancelResult> CancelAllOrdersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClobOpenOrder>> GetOpenOrdersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClobTradeRecord>> GetTradesAsync(string? marketId = null, int limit = 100, CancellationToken ct = default);
    Task<ClobBalanceSnapshot> GetBalanceAsync(CancellationToken ct = default);
}

public sealed record ClobPlaceOrderRequest(
    string TokenId,
    double Price,
    double Size,
    PolymarketOrderSide Side,
    string OrderType = "GTC");

public sealed record ClobOrderResult(
    bool Success,
    string OrderId,
    string Status,
    string? ErrorMessage = null);

public sealed record ClobCancelResult(
    bool Success,
    int CancelledCount,
    string? ErrorMessage = null);

public sealed record ClobOpenOrder(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("market")] string MarketId,
    [property: JsonPropertyName("asset_id")] string TokenId,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("original_size")] string OriginalSize,
    [property: JsonPropertyName("size_matched")] string SizeMatched,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

public sealed record ClobTradeRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("market")] string MarketId,
    [property: JsonPropertyName("asset_id")] string TokenId,
    [property: JsonPropertyName("side")] string Side,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("price")] string Price,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("match_time")] DateTimeOffset? MatchTime);

public sealed record ClobBalanceSnapshot(
    double AvailableUsdc,
    double LockedUsdc,
    double TotalUsdc);

public sealed class PolymarketClobClient : IPolymarketClobClient
{
    // For serializing outbound requests: keep property names as-is so we
    // control the exact casing Polymarket expects (tokenID, feeRateBps, etc.)
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // For deserializing responses from Polymarket (they use snake_case)
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPolymarketSigningService _signing;
    private readonly ILogger<PolymarketClobClient> _logger;
    private readonly string _apiKey;
    private readonly string _apiPassphrase;

    public PolymarketClobClient(
        IHttpClientFactory httpClientFactory,
        IPolymarketSigningService signing,
        ILogger<PolymarketClobClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _signing = signing;
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("POLYMARKET_API_KEY")?.Trim() ?? string.Empty;
        _apiPassphrase = Environment.GetEnvironmentVariable("POLYMARKET_API_PASSPHRASE")?.Trim() ?? string.Empty;
    }

    public bool IsConfigured => _signing.IsConfigured && !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ClobOrderResult> PlaceOrderAsync(ClobPlaceOrderRequest request, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ClobOrderResult(false, string.Empty, "rejected", "CLOB client is not configured");

        try
        {
            PolymarketSignedOrder signed = _signing.SignOrder(new PolymarketOrderPayload(
                TokenId: request.TokenId,
                Price: request.Price,
                Size: request.Size,
                Side: request.Side));

            var payload = new
            {
                order = new
                {
                    salt = long.Parse(signed.Salt),
                    maker = signed.Maker,
                    signer = signed.Signer,
                    taker = signed.Taker,
                    tokenId = signed.TokenId,
                    makerAmount = signed.MakerAmount,
                    takerAmount = signed.TakerAmount,
                    expiration = signed.Expiration.ToString(),
                    nonce = signed.Nonce,
                    feeRateBps = signed.FeeRateBps,
                    side = signed.Side == PolymarketOrderSide.Buy ? "BUY" : "SELL",
                    signatureType = signed.SignatureType,
                    signature = signed.Signature
                },
                owner = _apiKey,
                orderType = request.OrderType
            };

            string body = JsonSerializer.Serialize(payload, SerializeOptions);
            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Post, "/order", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("CLOB order placement failed: {StatusCode} {Body}", response.StatusCode, errorBody);
                return new ClobOrderResult(false, string.Empty, "rejected", $"{response.StatusCode}: {errorBody}");
            }

            ClobOrderResponse? result = await response.Content.ReadFromJsonAsync<ClobOrderResponse>(DeserializeOptions, ct);
            string orderId = result?.OrderId ?? result?.Id ?? string.Empty;
            string status = result?.Status ?? "submitted";

            _logger.LogInformation(
                "CLOB order placed: {OrderId} {Side} {Size}@{Price} token={TokenId} status={Status}",
                orderId, request.Side, request.Size, request.Price, request.TokenId, status);

            return new ClobOrderResult(true, orderId, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLOB order placement exception");
            return new ClobOrderResult(false, string.Empty, "error", ex.Message);
        }
    }

    public async Task<ClobCancelResult> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ClobCancelResult(false, 0, "CLOB client is not configured");

        try
        {
            string body = JsonSerializer.Serialize(new { orderID = orderId }, SerializeOptions);
            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Delete, "/order", body, ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("CLOB cancel failed for {OrderId}: {StatusCode} {Body}", orderId, response.StatusCode, errorBody);
                return new ClobCancelResult(false, 0, $"{response.StatusCode}: {errorBody}");
            }

            _logger.LogInformation("CLOB order cancelled: {OrderId}", orderId);
            return new ClobCancelResult(true, 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLOB cancel exception for {OrderId}", orderId);
            return new ClobCancelResult(false, 0, ex.Message);
        }
    }

    public async Task<ClobCancelResult> CancelAllOrdersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ClobCancelResult(false, 0, "CLOB client is not configured");

        try
        {
            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Delete, "/cancel-all", "{}", ct);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(ct);
                return new ClobCancelResult(false, 0, $"{response.StatusCode}: {errorBody}");
            }

            _logger.LogInformation("CLOB cancel-all submitted");
            return new ClobCancelResult(true, -1); // exact count unknown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CLOB cancel-all exception");
            return new ClobCancelResult(false, 0, ex.Message);
        }
    }

    public async Task<IReadOnlyList<ClobOpenOrder>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        try
        {
            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Get, "/orders?state=LIVE", null, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<ClobOpenOrder>>(DeserializeOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CLOB open orders");
            return [];
        }
    }

    public async Task<IReadOnlyList<ClobTradeRecord>> GetTradesAsync(string? marketId = null, int limit = 100, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        try
        {
            string path = marketId is not null
                ? $"/trades?market={Uri.EscapeDataString(marketId)}&limit={limit}"
                : $"/trades?limit={limit}";

            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Get, path, null, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<ClobTradeRecord>>(DeserializeOptions, ct) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CLOB trades");
            return [];
        }
    }

    public async Task<ClobBalanceSnapshot> GetBalanceAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new ClobBalanceSnapshot(0, 0, 0);

        try
        {
            using HttpResponseMessage response = await SendAuthenticatedAsync(HttpMethod.Get, "/balance", null, ct);
            response.EnsureSuccessStatusCode();

            ClobBalanceResponse? result = await response.Content.ReadFromJsonAsync<ClobBalanceResponse>(DeserializeOptions, ct);
            double available = ParseDouble(result?.Available);
            double locked = ParseDouble(result?.Locked);
            return new ClobBalanceSnapshot(available, locked, available + locked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch CLOB balance");
            return new ClobBalanceSnapshot(0, 0, 0);
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("polymarket-clob");
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string hmac = _signing.GenerateHmacSignature(method.Method, path, body ?? string.Empty, timestamp);

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("POLY_ADDRESS", _signing.WalletAddress);
        request.Headers.Add("POLY_SIGNATURE", hmac);
        request.Headers.Add("POLY_TIMESTAMP", timestamp.ToString());
        request.Headers.Add("POLY_API_KEY", _apiKey);
        request.Headers.Add("POLY_PASSPHRASE", _apiPassphrase);

        if (body is not null && method != HttpMethod.Get)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, ct);
    }

    private static double ParseDouble(string? value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : 0;

    private sealed record ClobOrderResponse(
        [property: JsonPropertyName("orderID")] string? OrderId,
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record ClobBalanceResponse(
        [property: JsonPropertyName("available")] string? Available,
        [property: JsonPropertyName("locked")] string? Locked);
}

public sealed class NoopPolymarketClobClient : IPolymarketClobClient
{
    public bool IsConfigured => false;
    public Task<ClobOrderResult> PlaceOrderAsync(ClobPlaceOrderRequest request, CancellationToken ct = default) =>
        Task.FromResult(new ClobOrderResult(false, string.Empty, "noop", "CLOB client is disabled"));
    public Task<ClobCancelResult> CancelOrderAsync(string orderId, CancellationToken ct = default) =>
        Task.FromResult(new ClobCancelResult(false, 0, "CLOB client is disabled"));
    public Task<ClobCancelResult> CancelAllOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult(new ClobCancelResult(false, 0, "CLOB client is disabled"));
    public Task<IReadOnlyList<ClobOpenOrder>> GetOpenOrdersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClobOpenOrder>>([]);
    public Task<IReadOnlyList<ClobTradeRecord>> GetTradesAsync(string? marketId = null, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClobTradeRecord>>([]);
    public Task<ClobBalanceSnapshot> GetBalanceAsync(CancellationToken ct = default) =>
        Task.FromResult(new ClobBalanceSnapshot(0, 0, 0));
}
