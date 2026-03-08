using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Atlas.Api.Models;

namespace Atlas.Api.Services;

public interface IOptionsMarketDataService
{
    IReadOnlyList<string> SupportedAssets { get; }
    Task<IReadOnlyList<LiveOptionQuote>> GetOptionChainAsync(string asset, CancellationToken ct = default);
    Task<MarketDataCompositeStatus> GetStatusAsync(string asset, CancellationToken ct = default);
    Task<IReadOnlyList<MarketDataCompositeStatus>> GetStatusesAsync(CancellationToken ct = default);
}

public sealed class ResilientOptionsMarketDataService : IOptionsMarketDataService
{
    private static readonly IReadOnlyList<string> Assets = ["BTC", "ETH", "SOL"];
    private static readonly TimeSpan FreshCacheTtl = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StaleCacheTtl = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SourceStaleAfter = TimeSpan.FromSeconds(18);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResilientOptionsMarketDataService> _logger;
    private readonly ISystemMonitoringService _monitoring;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceHealth> _sourceHealth = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        DateTimeOffset FetchedAt,
        IReadOnlyList<LiveOptionQuote> Quotes,
        string ActiveSource,
        bool IsStale);

    private sealed record SourceFetchResult(
        string Source,
        string Asset,
        IReadOnlyList<LiveOptionQuote> Quotes,
        DateTimeOffset SourceTimestamp,
        long LatencyMs,
        bool IsStale,
        string? Error);

    private sealed record SourceHealth(
        string Source,
        string Asset,
        bool Healthy,
        bool IsFallback,
        bool IsStale,
        int QuoteCount,
        long LastLatencyMs,
        DateTimeOffset? LastSuccessAt,
        DateTimeOffset? LastFailureAt,
        string? LastError,
        DateTimeOffset SnapshotAt);

    public ResilientOptionsMarketDataService(
        IHttpClientFactory httpClientFactory,
        ILogger<ResilientOptionsMarketDataService> logger,
        ISystemMonitoringService monitoring)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _monitoring = monitoring;
    }

    public IReadOnlyList<string> SupportedAssets => Assets;

    public async Task<IReadOnlyList<LiveOptionQuote>> GetOptionChainAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        if (_cache.TryGetValue(normalizedAsset, out var cached) && DateTimeOffset.UtcNow - cached.FetchedAt <= FreshCacheTtl)
            return cached.Quotes;

        var results = await FetchFromSourcesAsync(normalizedAsset, ct);

        var active = results
            .Where(r => r.Quotes.Count > 0)
            .OrderBy(r => r.IsStale)
            .ThenByDescending(r => r.Quotes.Count)
            .ThenByDescending(r => r.SourceTimestamp)
            .FirstOrDefault();

        if (active is not null)
        {
            bool usedFallback = !active.Source.Equals("BYBIT", StringComparison.OrdinalIgnoreCase);
            if (usedFallback)
            {
                _monitoring.IncrementCounter("marketdata.fallback.used");
                _monitoring.PublishAlert(
                    source: "marketdata",
                    severity: NotificationSeverity.Warning,
                    message: $"Fallback source {active.Source} used for {normalizedAsset}.");
            }

            if (active.IsStale)
            {
                _monitoring.IncrementCounter("marketdata.stale.detected");
                _monitoring.PublishAlert(
                    source: "marketdata",
                    severity: NotificationSeverity.Warning,
                    message: $"Stale source data for {normalizedAsset} from {active.Source}.");
            }

            _cache[normalizedAsset] = new CacheEntry(DateTimeOffset.UtcNow, active.Quotes, active.Source, active.IsStale);
            _monitoring.RecordGauge("marketdata.quote_count", active.Quotes.Count);
            _monitoring.RecordGauge("marketdata.source_lag_ms", Math.Max(0, (DateTimeOffset.UtcNow - active.SourceTimestamp).TotalMilliseconds));
            return active.Quotes;
        }

        if (_cache.TryGetValue(normalizedAsset, out var staleCached) && DateTimeOffset.UtcNow - staleCached.FetchedAt <= StaleCacheTtl)
        {
            _monitoring.IncrementCounter("marketdata.cache_stale_served");
            _monitoring.PublishAlert(
                source: "marketdata",
                severity: NotificationSeverity.Critical,
                message: $"Serving stale cache for {normalizedAsset}; all sources unavailable.");

            var stale = staleCached.Quotes
                .Select(q => q with { IsStale = true, Timestamp = DateTimeOffset.UtcNow })
                .ToList();
            return stale;
        }

        throw new InvalidOperationException($"No market data source available for {normalizedAsset}.");
    }

    public async Task<MarketDataCompositeStatus> GetStatusAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);

        // Warm cache/status if missing.
        if (!_cache.ContainsKey(normalizedAsset))
        {
            try { _ = await GetOptionChainAsync(normalizedAsset, ct); }
            catch { /* intentionally ignored for status response */ }
        }

        var snapshots = _sourceHealth.Values
            .Where(s => s.Asset.Equals(normalizedAsset, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Source)
            .Select(s => new MarketDataSourceStatus(
                Source: s.Source,
                Asset: s.Asset,
                Healthy: s.Healthy,
                IsFallback: s.IsFallback,
                IsStale: s.IsStale,
                QuoteCount: s.QuoteCount,
                LastLatencyMs: s.LastLatencyMs,
                LastSuccessAt: s.LastSuccessAt,
                LastFailureAt: s.LastFailureAt,
                LastError: s.LastError,
                SnapshotAt: s.SnapshotAt))
            .ToList();

        string activeSource = _cache.TryGetValue(normalizedAsset, out var cache) ? cache.ActiveSource : "NONE";
        DateTimeOffset asOf = _cache.TryGetValue(normalizedAsset, out cache) && cache.Quotes.Count > 0
            ? cache.Quotes.Max(q => q.SourceTimestamp ?? q.Timestamp)
            : DateTimeOffset.MinValue;
        long lagMs = asOf == DateTimeOffset.MinValue ? long.MaxValue : (long)Math.Max(0, (DateTimeOffset.UtcNow - asOf).TotalMilliseconds);

        return new MarketDataCompositeStatus(
            Asset: normalizedAsset,
            ActiveSource: activeSource,
            IsStale: cache?.IsStale ?? true,
            AsOf: asOf == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : asOf,
            SourceLagMs: lagMs,
            QuoteCount: cache?.Quotes.Count ?? 0,
            Sources: snapshots,
            SnapshotAt: DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<MarketDataCompositeStatus>> GetStatusesAsync(CancellationToken ct = default)
    {
        var tasks = SupportedAssets.Select(asset => GetStatusAsync(asset, ct));
        return await Task.WhenAll(tasks);
    }

    private async Task<IReadOnlyList<SourceFetchResult>> FetchFromSourcesAsync(string asset, CancellationToken ct)
    {
        var tasks = new List<Task<SourceFetchResult>>
        {
            FetchBybitAsync(asset, ct)
        };

        // Deribit has liquid BTC/ETH books; SOL fallback remains Bybit/cache.
        if (asset is "BTC" or "ETH")
            tasks.Add(FetchDeribitAsync(asset, ct));

        return await Task.WhenAll(tasks);
    }

    private async Task<SourceFetchResult> FetchBybitAsync(string asset, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _httpClientFactory.CreateClient("bybit-options");
            using var response = await client.GetAsync($"/v5/market/tickers?category=option&baseCoin={asset}", ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            int retCode = root.TryGetProperty("retCode", out var retCodeElement) && retCodeElement.TryGetInt32(out var code)
                ? code
                : -1;
            if (retCode != 0)
                throw new InvalidOperationException($"Bybit returned retCode={retCode} for {asset} options.");

            DateTimeOffset sourceTimestamp = ParseTimestampFromMsOrString(root, "time", DateTimeOffset.UtcNow);

            if (!root.TryGetProperty("result", out var resultElement) ||
                !resultElement.TryGetProperty("list", out var listElement) ||
                listElement.ValueKind != JsonValueKind.Array)
            {
                return FinalizeSourceResult("BYBIT", asset, [], sourceTimestamp, sw.ElapsedMilliseconds, null);
            }

            var quotes = new List<LiveOptionQuote>();
            foreach (var item in listElement.EnumerateArray())
            {
                string symbol = ReadString(item, "symbol");
                if (!TryParseBybitOptionSymbol(symbol, out var parsedAsset, out var expiry, out var strike, out var right))
                    continue;

                double bid = ParseDouble(item, "bid1Price");
                double ask = ParseDouble(item, "ask1Price");
                double mark = ParseDouble(item, "markPrice");
                if (mark <= 0) mark = ParseDouble(item, "lastPrice");
                double mid = ComputeMid(bid, ask, mark);

                double markIv = ParseDouble(item, "markIv");
                if (markIv <= 0)
                {
                    double bidIv = ParseDouble(item, "bid1Iv");
                    double askIv = ParseDouble(item, "ask1Iv");
                    markIv = bidIv > 0 && askIv > 0 ? (bidIv + askIv) / 2.0 : Math.Max(bidIv, askIv);
                }
                markIv = NormalizeIv(markIv);

                double underlyingPrice = ParseDouble(item, "underlyingPrice");
                if (underlyingPrice <= 0) underlyingPrice = ParseDouble(item, "indexPrice");

                quotes.Add(new LiveOptionQuote(
                    Symbol: symbol,
                    Asset: parsedAsset,
                    Expiry: expiry,
                    Strike: strike,
                    Right: right,
                    Bid: bid,
                    Ask: ask,
                    Mark: mark,
                    Mid: mid,
                    MarkIv: markIv,
                    Delta: ParseDouble(item, "delta"),
                    Gamma: ParseDouble(item, "gamma"),
                    Vega: ParseDouble(item, "vega"),
                    Theta: ParseDouble(item, "theta"),
                    OpenInterest: ParseDouble(item, "openInterest"),
                    Volume24h: ParseDouble(item, "volume24h"),
                    Turnover24h: ParseDouble(item, "turnover24h"),
                    UnderlyingPrice: underlyingPrice,
                    Timestamp: DateTimeOffset.UtcNow,
                    Venue: "BYBIT",
                    SourceTimestamp: sourceTimestamp,
                    IsStale: false));
            }

            return FinalizeSourceResult("BYBIT", asset, quotes, sourceTimestamp, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return FinalizeSourceResult("BYBIT", asset, [], DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private async Task<SourceFetchResult> FetchDeribitAsync(string asset, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var client = _httpClientFactory.CreateClient("deribit-options");
            using var response = await client.GetAsync($"/api/v2/public/get_book_summary_by_currency?currency={asset}&kind=option", ct);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var resultElement) || resultElement.ValueKind != JsonValueKind.Array)
                return FinalizeSourceResult("DERIBIT", asset, [], DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, null);

            DateTimeOffset sourceTimestamp = ParseDeribitTimestamp(root) ?? DateTimeOffset.UtcNow;
            var quotes = new List<LiveOptionQuote>();

            foreach (var item in resultElement.EnumerateArray())
            {
                string symbol = ReadString(item, "instrument_name");
                if (!TryParseDeribitOptionSymbol(symbol, out var parsedAsset, out var expiry, out var strike, out var right))
                    continue;

                double underlyingPrice = ParseDouble(item, "underlying_price");
                if (underlyingPrice <= 0) underlyingPrice = ParseDouble(item, "index_price");
                if (underlyingPrice <= 0) underlyingPrice = ParseDouble(item, "estimated_delivery_price");

                double bid = ParseDouble(item, "bid_price");
                double ask = ParseDouble(item, "ask_price");
                double mark = ParseDouble(item, "mark_price");

                bool likelyCoinPremium = underlyingPrice > 0 && Math.Max(Math.Max(bid, ask), mark) > 0 && Math.Max(Math.Max(bid, ask), mark) < 10;
                if (likelyCoinPremium)
                {
                    bid *= underlyingPrice;
                    ask *= underlyingPrice;
                    mark *= underlyingPrice;
                }

                double mid = ComputeMid(bid, ask, mark);
                double markIv = NormalizeIv(ParseDouble(item, "mark_iv"));
                if (markIv <= 0)
                {
                    double bidIv = NormalizeIv(ParseDouble(item, "bid_iv"));
                    double askIv = NormalizeIv(ParseDouble(item, "ask_iv"));
                    markIv = bidIv > 0 && askIv > 0 ? (bidIv + askIv) / 2.0 : Math.Max(bidIv, askIv);
                }

                quotes.Add(new LiveOptionQuote(
                    Symbol: symbol,
                    Asset: parsedAsset,
                    Expiry: expiry,
                    Strike: strike,
                    Right: right,
                    Bid: bid,
                    Ask: ask,
                    Mark: mark,
                    Mid: mid,
                    MarkIv: markIv,
                    Delta: ParseNestedDouble(item, "greeks", "delta"),
                    Gamma: ParseNestedDouble(item, "greeks", "gamma"),
                    Vega: ParseNestedDouble(item, "greeks", "vega"),
                    Theta: ParseNestedDouble(item, "greeks", "theta"),
                    OpenInterest: ParseDouble(item, "open_interest"),
                    Volume24h: ParseDouble(item, "volume"),
                    Turnover24h: ParseDouble(item, "volume_usd"),
                    UnderlyingPrice: underlyingPrice,
                    Timestamp: DateTimeOffset.UtcNow,
                    Venue: "DERIBIT",
                    SourceTimestamp: sourceTimestamp,
                    IsStale: false));
            }

            return FinalizeSourceResult("DERIBIT", asset, quotes, sourceTimestamp, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return FinalizeSourceResult("DERIBIT", asset, [], DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private SourceFetchResult FinalizeSourceResult(
        string source,
        string asset,
        IReadOnlyList<LiveOptionQuote> quotes,
        DateTimeOffset sourceTimestamp,
        long latencyMs,
        string? error)
    {
        bool isStale = DateTimeOffset.UtcNow - sourceTimestamp > SourceStaleAfter;
        bool healthy = string.IsNullOrWhiteSpace(error) && quotes.Count > 0;

        UpdateSourceHealth(source, asset, healthy, isStale, latencyMs, quotes.Count, error);

        if (!healthy)
            _monitoring.IncrementCounter("marketdata.source.error");

        return new SourceFetchResult(
            Source: source,
            Asset: asset,
            Quotes: healthy
                ? quotes.Select(q => q with { IsStale = isStale }).ToList()
                : [],
            SourceTimestamp: sourceTimestamp,
            LatencyMs: latencyMs,
            IsStale: isStale,
            Error: error);
    }

    private void UpdateSourceHealth(string source, string asset, bool healthy, bool isStale, long latencyMs, int quoteCount, string? error)
    {
        string key = $"{source}:{asset}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _sourceHealth.AddOrUpdate(
            key,
            _ => new SourceHealth(
                Source: source,
                Asset: asset,
                Healthy: healthy,
                IsFallback: !source.Equals("BYBIT", StringComparison.OrdinalIgnoreCase),
                IsStale: isStale,
                QuoteCount: quoteCount,
                LastLatencyMs: latencyMs,
                LastSuccessAt: healthy ? now : null,
                LastFailureAt: healthy ? null : now,
                LastError: error,
                SnapshotAt: now),
            (_, previous) => previous with
            {
                Healthy = healthy,
                IsFallback = !source.Equals("BYBIT", StringComparison.OrdinalIgnoreCase),
                IsStale = isStale,
                QuoteCount = quoteCount,
                LastLatencyMs = latencyMs,
                LastSuccessAt = healthy ? now : previous.LastSuccessAt,
                LastFailureAt = healthy ? previous.LastFailureAt : now,
                LastError = error,
                SnapshotAt = now
            });

        _monitoring.RecordGauge($"marketdata.latency.{source.ToLowerInvariant()}", latencyMs);
    }

    private static string NormalizeAsset(string asset)
    {
        string normalized = asset.Trim().ToUpperInvariant();
        if (!Assets.Contains(normalized))
            throw new ArgumentException($"Unsupported asset '{asset}'. Supported assets: {string.Join(", ", Assets)}");
        return normalized;
    }

    private static bool TryParseBybitOptionSymbol(
        string symbol,
        out string asset,
        out DateTimeOffset expiry,
        out double strike,
        out OptionRight right)
    {
        asset = string.Empty;
        expiry = default;
        strike = 0;
        right = OptionRight.Call;

        // Example: SOL-24APR26-122-C-USDT
        string[] parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;

        asset = parts[0].Trim().ToUpperInvariant();
        if (!DateTime.TryParseExact(parts[1], "ddMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out strike))
            return false;

        string sideCode = parts[3].Trim().ToUpperInvariant();
        right = sideCode switch
        {
            "C" => OptionRight.Call,
            "P" => OptionRight.Put,
            _ => right
        };
        if (sideCode is not ("C" or "P")) return false;

        expiry = new DateTimeOffset(expiryDate.Year, expiryDate.Month, expiryDate.Day, 8, 0, 0, TimeSpan.Zero);
        return true;
    }

    private static bool TryParseDeribitOptionSymbol(
        string symbol,
        out string asset,
        out DateTimeOffset expiry,
        out double strike,
        out OptionRight right)
    {
        asset = string.Empty;
        expiry = default;
        strike = 0;
        right = OptionRight.Call;

        // Example: BTC-29MAR24-60000-C
        string[] parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;

        asset = parts[0].Trim().ToUpperInvariant();
        if (!DateTime.TryParseExact(parts[1], "ddMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate))
            return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out strike))
            return false;

        string sideCode = parts[3].Trim().ToUpperInvariant();
        right = sideCode switch
        {
            "C" => OptionRight.Call,
            "P" => OptionRight.Put,
            _ => right
        };
        if (sideCode is not ("C" or "P")) return false;

        expiry = new DateTimeOffset(expiryDate.Year, expiryDate.Month, expiryDate.Day, 8, 0, 0, TimeSpan.Zero);
        return true;
    }

    private static DateTimeOffset ParseTimestampFromMsOrString(JsonElement root, string propertyName, DateTimeOffset fallback)
    {
        if (!root.TryGetProperty(propertyName, out var prop)) return fallback;

        if (prop.ValueKind == JsonValueKind.String && long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var msFromString))
            return DateTimeOffset.FromUnixTimeMilliseconds(msFromString);
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return fallback;
    }

    private static DateTimeOffset? ParseDeribitTimestamp(JsonElement root)
    {
        // Deribit returns microseconds since epoch in usOut/usIn.
        if (root.TryGetProperty("usOut", out var usOutElement) && usOutElement.TryGetInt64(out var usOut))
        {
            long ms = usOut / 1000;
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        if (root.TryGetProperty("usIn", out var usInElement) && usInElement.TryGetInt64(out var usIn))
        {
            long ms = usIn / 1000;
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return null;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return string.Empty;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static double ParseDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return 0;

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var numeric))
            return numeric;

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return 0;
    }

    private static double ParseNestedDouble(JsonElement element, string objectPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(objectPropertyName, out var nested) || nested.ValueKind != JsonValueKind.Object)
            return 0;
        return ParseDouble(nested, propertyName);
    }

    private static double NormalizeIv(double rawIv)
    {
        if (!double.IsFinite(rawIv) || rawIv <= 0) return 0;
        // Some venues provide iv in percentage points.
        double iv = rawIv > 3 ? rawIv / 100.0 : rawIv;
        return Math.Clamp(iv, 0.01, 5.0);
    }

    private static double ComputeMid(double bid, double ask, double fallback)
    {
        if (bid > 0 && ask > 0) return (bid + ask) / 2.0;
        if (bid > 0) return bid;
        if (ask > 0) return ask;
        return fallback;
    }
}
