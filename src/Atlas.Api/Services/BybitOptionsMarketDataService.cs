using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Atlas.Api.Models;
using Atlas.Core.Common;
using Atlas.Core.Models;

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
    private static readonly IReadOnlyList<string> Assets = ["BTC", "ETH", "SOL", "WTI"];
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

    private sealed record CleaningStats(
        int InputCount,
        int OutputCount,
        int DroppedInvalid,
        int DroppedOutlier,
        int Deduplicated);

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
        if (asset == "WTI")
            return [GenerateSyntheticWtiResult(asset)];

        var tasks = new List<Task<SourceFetchResult>>
        {
            FetchBybitAsync(asset, ct)
        };

        // Deribit has liquid BTC/ETH books; SOL fallback remains Bybit/cache; WTI is synthetic.
        if (asset is "BTC" or "ETH")
            tasks.Add(FetchDeribitAsync(asset, ct));

        return await Task.WhenAll(tasks);
    }

    private SourceFetchResult GenerateSyntheticWtiResult(string asset)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var quotes = BuildSyntheticWtiChain(now);
            return FinalizeSourceResult("SYNTHETIC", asset, quotes, now, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            return FinalizeSourceResult("SYNTHETIC", asset, [], DateTimeOffset.UtcNow, sw.ElapsedMilliseconds, ex.Message);
        }
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
        var (cleanedQuotes, cleaning) = CleanQuotes(asset, quotes, sourceTimestamp);
        bool isStale = DateTimeOffset.UtcNow - sourceTimestamp > SourceStaleAfter;
        bool healthy = string.IsNullOrWhiteSpace(error) && cleanedQuotes.Count > 0;

        UpdateSourceHealth(source, asset, healthy, isStale, latencyMs, cleanedQuotes.Count, error);

        if (!healthy)
            _monitoring.IncrementCounter("marketdata.source.error");

        if (cleaning.DroppedInvalid > 0 || cleaning.DroppedOutlier > 0 || cleaning.Deduplicated > 0)
        {
            _monitoring.IncrementCounter("marketdata.cleaning.events");
            _monitoring.RecordGauge("marketdata.cleaning.dropped.invalid", cleaning.DroppedInvalid);
            _monitoring.RecordGauge("marketdata.cleaning.dropped.outlier", cleaning.DroppedOutlier);
            _monitoring.RecordGauge("marketdata.cleaning.deduplicated", cleaning.Deduplicated);
        }

        return new SourceFetchResult(
            Source: source,
            Asset: asset,
            Quotes: healthy
                ? cleanedQuotes.Select(q => q with { IsStale = isStale }).ToList()
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

    private static IReadOnlyList<LiveOptionQuote> BuildSyntheticWtiChain(DateTimeOffset now)
    {
        // Synthetic fallback to keep analytics/trading flows alive for WTI when venue options are unavailable.
        double hours = now.ToUnixTimeSeconds() / 3600.0;
        double spot = 78.0 + 2.4 * Math.Sin(hours * 0.11);
        double rate = 0.03;

        int[] expiryDays = [7, 14, 30, 60, 90];
        double[] moneyness = [0.75, 0.80, 0.85, 0.90, 0.95, 1.00, 1.05, 1.10, 1.15, 1.20, 1.25];
        var quotes = new List<LiveOptionQuote>(expiryDays.Length * moneyness.Length * 2);

        foreach (int days in expiryDays)
        {
            var expiry = new DateTimeOffset(now.UtcDateTime.Date.AddDays(days).AddHours(14), TimeSpan.Zero);
            double t = Math.Max((expiry - now).TotalDays / 365.25, 1.0 / 365.25);
            double termBump = 0.02 * Math.Sqrt(days / 30.0);

            foreach (double m in moneyness)
            {
                double strike = Math.Round(spot * m, 2, MidpointRounding.AwayFromZero);
                double skewBump = 0.07 * (1.0 - m);
                double iv = Math.Clamp(0.34 + termBump + skewBump, 0.18, 0.75);

                quotes.Add(BuildSyntheticWtiQuote("WTI", spot, strike, expiry, t, iv, rate, OptionRight.Call, now));
                quotes.Add(BuildSyntheticWtiQuote("WTI", spot, strike, expiry, t, iv, rate, OptionRight.Put, now));
            }
        }

        return quotes;
    }

    private static LiveOptionQuote BuildSyntheticWtiQuote(
        string asset,
        double spot,
        double strike,
        DateTimeOffset expiry,
        double t,
        double iv,
        double rate,
        OptionRight right,
        DateTimeOffset now)
    {
        OptionType optionType = right == OptionRight.Call ? OptionType.Call : OptionType.Put;
        double mark = Math.Max(0.01, BlackScholes.Price(spot, strike, iv, t, rate, optionType));
        double spread = Math.Max(0.01, mark * 0.03);
        double bid = Math.Max(0.0, mark - spread / 2.0);
        double ask = mark + spread / 2.0;
        double mid = (bid + ask) / 2.0;

        double m = strike / spot;
        double oiBase = 1500.0 * Math.Exp(-Math.Abs(m - 1.0) * 5.5) * Math.Exp(-t * 0.85);
        double openInterest = Math.Max(12.0, oiBase);
        double volume24h = Math.Max(3.0, openInterest * 0.06);
        double turnover24h = volume24h * mid * 100.0;

        return new LiveOptionQuote(
            Symbol: $"{asset}-{expiry.UtcDateTime.ToString("ddMMMyy", CultureInfo.InvariantCulture).ToUpperInvariant()}-{strike.ToString("0.##", CultureInfo.InvariantCulture)}-{(right == OptionRight.Call ? "C" : "P")}-USD",
            Asset: asset,
            Expiry: expiry,
            Strike: strike,
            Right: right,
            Bid: bid,
            Ask: ask,
            Mark: mark,
            Mid: mid,
            MarkIv: iv,
            Delta: BlackScholes.Delta(spot, strike, rate, iv, t, optionType),
            Gamma: BlackScholes.Gamma(spot, strike, rate, iv, t),
            Vega: BlackScholes.Vega(spot, strike, rate, iv, t),
            Theta: BlackScholes.Theta(spot, strike, rate, iv, t, optionType),
            OpenInterest: openInterest,
            Volume24h: volume24h,
            Turnover24h: turnover24h,
            UnderlyingPrice: spot,
            Timestamp: now,
            Venue: "SYNTH",
            SourceTimestamp: now,
            IsStale: false);
    }

    private (IReadOnlyList<LiveOptionQuote> Quotes, CleaningStats Stats) CleanQuotes(
        string asset,
        IReadOnlyList<LiveOptionQuote> quotes,
        DateTimeOffset sourceTimestamp)
    {
        if (quotes.Count == 0)
            return ([], new CleaningStats(0, 0, 0, 0, 0));

        int droppedInvalid = 0;
        int droppedOutlier = 0;
        var normalizedAsset = asset.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var sanitized = new List<LiveOptionQuote>(quotes.Count);

        foreach (var quote in quotes)
        {
            if (!quote.Asset.Equals(normalizedAsset, StringComparison.OrdinalIgnoreCase))
            {
                droppedInvalid++;
                continue;
            }

            if (!double.IsFinite(quote.Strike) || quote.Strike <= 0 || quote.Expiry == default)
            {
                droppedInvalid++;
                continue;
            }

            if (quote.Expiry < now.AddDays(-2) || quote.Expiry > now.AddYears(3))
            {
                droppedInvalid++;
                continue;
            }

            double underlying = SanitizePositive(quote.UnderlyingPrice);
            if (underlying <= 0)
            {
                droppedInvalid++;
                continue;
            }

            double bid = SanitizeNonNegative(quote.Bid);
            double ask = SanitizeNonNegative(quote.Ask);
            if (bid > 0 && ask > 0 && ask < bid)
                (bid, ask) = (ask, bid);

            double mark = SanitizeNonNegative(quote.Mark);
            double midFallback = SanitizeNonNegative(quote.Mid);
            double mid = ComputeMid(bid, ask, midFallback > 0 ? midFallback : mark);
            if (mark <= 0) mark = mid;
            if (mid <= 0 || mark <= 0)
            {
                droppedInvalid++;
                continue;
            }

            double markIv = NormalizeIv(quote.MarkIv);
            if (markIv <= 0)
                markIv = EstimateFallbackIv(underlying, quote.Strike, quote.Expiry, now);
            if (markIv <= 0 || !double.IsFinite(markIv))
            {
                droppedInvalid++;
                continue;
            }

            double openInterest = SanitizeNonNegative(quote.OpenInterest);
            double volume24h = SanitizeNonNegative(quote.Volume24h);
            double turnover24h = SanitizeNonNegative(quote.Turnover24h);
            if (turnover24h <= 0 && volume24h > 0)
                turnover24h = volume24h * mid;

            sanitized.Add(quote with
            {
                Asset = normalizedAsset,
                Bid = bid,
                Ask = ask,
                Mark = mark,
                Mid = mid,
                MarkIv = markIv,
                Delta = ClampFinite(quote.Delta, -1.20, 1.20),
                Gamma = ClampFinite(quote.Gamma, -2.0, 2.0),
                Vega = ClampFinite(quote.Vega, -12_000.0, 12_000.0),
                Theta = ClampFinite(quote.Theta, -12_000.0, 12_000.0),
                OpenInterest = openInterest,
                Volume24h = volume24h,
                Turnover24h = turnover24h,
                UnderlyingPrice = underlying,
                Timestamp = now,
                SourceTimestamp = quote.SourceTimestamp ?? sourceTimestamp
            });
        }

        if (sanitized.Count == 0)
        {
            return ([], new CleaningStats(
                InputCount: quotes.Count,
                OutputCount: 0,
                DroppedInvalid: droppedInvalid,
                DroppedOutlier: 0,
                Deduplicated: 0));
        }

        var deduplicated = sanitized
            .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(q => q.SourceTimestamp ?? DateTimeOffset.MinValue)
                .ThenByDescending(q => q.Turnover24h)
                .ThenByDescending(q => q.OpenInterest)
                .First())
            .ToList();

        int deduplicatedCount = Math.Max(0, sanitized.Count - deduplicated.Count);
        var outlierFiltered = new List<LiveOptionQuote>(deduplicated.Count);

        foreach (var expiryGroup in deduplicated.GroupBy(q => q.Expiry.Date))
        {
            var bucket = expiryGroup.ToList();
            double medianSpot = ComputeMedian(bucket.Select(q => q.UnderlyingPrice).Where(v => v > 0));
            double medianIv = ComputeMedian(bucket.Select(q => q.MarkIv).Where(v => v > 0));
            double lowSpot = medianSpot > 0 ? medianSpot * 0.70 : 0;
            double highSpot = medianSpot > 0 ? medianSpot * 1.30 : double.MaxValue;

            foreach (var quote in bucket)
            {
                if (medianSpot > 0 && (quote.UnderlyingPrice < lowSpot || quote.UnderlyingPrice > highSpot))
                {
                    droppedOutlier++;
                    continue;
                }

                double markIv = quote.MarkIv;
                if (medianIv > 0)
                    markIv = Math.Clamp(markIv, Math.Max(0.02, medianIv * 0.25), Math.Min(5.0, medianIv * 4.0));

                double bid = quote.Bid;
                double ask = quote.Ask;
                double mid = quote.Mid > 0 ? quote.Mid : ComputeMid(bid, ask, quote.Mark);
                double maxSpreadAbs = Math.Max(0.02, mid * 2.4);
                if (ask > 0 && bid > 0 && ask - bid > maxSpreadAbs)
                {
                    double halfSpread = maxSpreadAbs / 2.0;
                    bid = Math.Max(0, mid - halfSpread);
                    ask = Math.Max(bid, mid + halfSpread);
                }

                outlierFiltered.Add(quote with
                {
                    Bid = bid,
                    Ask = ask,
                    Mid = ComputeMid(bid, ask, quote.Mark),
                    MarkIv = markIv
                });
            }
        }

        var ordered = outlierFiltered
            .OrderBy(q => q.Expiry)
            .ThenBy(q => q.Strike)
            .ThenBy(q => q.Right)
            .ToList();

        return (ordered, new CleaningStats(
            InputCount: quotes.Count,
            OutputCount: ordered.Count,
            DroppedInvalid: droppedInvalid,
            DroppedOutlier: droppedOutlier,
            Deduplicated: deduplicatedCount));
    }

    private static double SanitizePositive(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 0;
        return value;
    }

    private static double SanitizeNonNegative(double value)
    {
        if (!double.IsFinite(value) || value < 0) return 0;
        return value;
    }

    private static double ClampFinite(double value, double min, double max)
    {
        if (!double.IsFinite(value)) return 0;
        return MathUtils.Clamp(value, min, max);
    }

    private static double EstimateFallbackIv(double spot, double strike, DateTimeOffset expiry, DateTimeOffset now)
    {
        if (spot <= 0 || strike <= 0) return 0;
        double dte = Math.Max(1.0, (expiry - now).TotalDays);
        double term = 0.22 + 0.05 * Math.Sqrt(dte / 30.0);
        double moneyness = Math.Abs(strike / spot - 1.0);
        return Math.Clamp(term + moneyness * 0.55, 0.08, 2.2);
    }

    private static double ComputeMedian(IEnumerable<double> values)
    {
        var ordered = values
            .Where(double.IsFinite)
            .OrderBy(v => v)
            .ToList();
        if (ordered.Count == 0) return 0;

        int mid = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[mid - 1] + ordered[mid]) / 2.0
            : ordered[mid];
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
