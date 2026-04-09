using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface IPolymarketLiveService
{
    Task<PolymarketLiveSnapshot> GetLiveSnapshotAsync(int lookaheadMinutes = 24 * 60, int maxMarkets = 24, CancellationToken ct = default);
}

public static partial class PolymarketSignalMath
{
    [GeneratedRegex(@"(?<asset>Bitcoin|BTC|Ethereum|ETH|Solana|SOL)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetRegex();

    [GeneratedRegex(@"(?<relation>above|below)\s+\$?(?<strike>[\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SimpleThresholdRegex();

    [GeneratedRegex(@"between\s+\$?(?<low>[\d,]+(?:\.\d+)?)\s+and\s+\$?(?<high>[\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BetweenRegex();

    [GeneratedRegex(@"up\s+or\s+down", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpDownRegex();

    public static bool LooksLikeDirectionalQuestion(string question) =>
        !string.IsNullOrWhiteSpace(question) && UpDownRegex().IsMatch(question);

    public static bool TryParseTradeableQuestion(string question, out PolymarketParsedQuestion? parsed, double? directionalReferencePrice = null)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(question))
            return false;

        Match assetMatch = AssetRegex().Match(question);
        if (!assetMatch.Success)
            return false;

        string asset = NormalizeAsset(assetMatch.Groups["asset"].Value);

        Match between = BetweenRegex().Match(question);
        if (between.Success &&
            TryParseMoney(between.Groups["low"].Value, out double low) &&
            TryParseMoney(between.Groups["high"].Value, out double high))
        {
            if (high <= low)
                return false;

            parsed = new PolymarketParsedQuestion(
                Asset: asset,
                Relation: PolymarketThresholdRelation.Between,
                LowerStrike: low,
                UpperStrike: high,
                RawQuestion: question);
            return true;
        }

        double referencePrice = directionalReferencePrice.GetValueOrDefault();
        if (LooksLikeDirectionalQuestion(question) && referencePrice > 0)
        {
            parsed = new PolymarketParsedQuestion(
                Asset: asset,
                Relation: PolymarketThresholdRelation.Above,
                LowerStrike: referencePrice,
                UpperStrike: null,
                RawQuestion: question);
            return true;
        }

        Match simple = SimpleThresholdRegex().Match(question);
        if (!simple.Success || !TryParseMoney(simple.Groups["strike"].Value, out double strike))
            return false;

        string relationRaw = simple.Groups["relation"].Value.Trim().ToLowerInvariant();
        PolymarketThresholdRelation relation = relationRaw switch
        {
            "above" => PolymarketThresholdRelation.Above,
            "below" => PolymarketThresholdRelation.Below,
            _ => PolymarketThresholdRelation.Above
        };

        parsed = new PolymarketParsedQuestion(
            Asset: asset,
            Relation: relation,
            LowerStrike: strike,
            UpperStrike: null,
            RawQuestion: question);
        return true;
    }

    public static double ComputeFairYesProbability(
        double spot,
        double annualizedVol,
        DateTimeOffset expiry,
        DateTimeOffset now,
        PolymarketParsedQuestion parsed,
        double directionalBiasShift = 0)
    {
        if (spot <= 0)
            return 0.5;

        double sigma = MathUtils.Clamp(annualizedVol, 0.05, 3.5);
        double t = Math.Max((expiry - now).TotalDays / 365.25, 1.0 / (365.25 * 24.0 * 60.0));

        double fair = parsed.Relation switch
        {
            PolymarketThresholdRelation.Above => ProbabilityAbove(spot, parsed.LowerStrike, sigma, t),
            PolymarketThresholdRelation.Below => 1.0 - ProbabilityAbove(spot, parsed.LowerStrike, sigma, t),
            PolymarketThresholdRelation.Between when parsed.UpperStrike.HasValue =>
                ProbabilityBetween(spot, parsed.LowerStrike, parsed.UpperStrike.Value, sigma, t),
            PolymarketThresholdRelation.Outside when parsed.UpperStrike.HasValue =>
                1.0 - ProbabilityBetween(spot, parsed.LowerStrike, parsed.UpperStrike.Value, sigma, t),
            _ => 0.5
        };

        double adjusted = fair + directionalBiasShift;
        return MathUtils.Clamp(adjusted, 0.001, 0.999);
    }

    private static double ProbabilityAbove(double spot, double strike, double sigma, double timeYears)
    {
        if (strike <= 0)
            return 1.0;

        if (timeYears <= 0 || sigma <= 0)
            return spot > strike ? 1.0 : 0.0;

        double sqrtT = Math.Sqrt(timeYears);
        double d2 = (Math.Log(Math.Max(spot, 1e-9) / Math.Max(strike, 1e-9)) - 0.5 * sigma * sigma * timeYears) / (sigma * sqrtT);
        return MathUtils.NormalCdf(d2);
    }

    private static double ProbabilityBetween(double spot, double low, double high, double sigma, double timeYears)
    {
        if (high <= low)
            return 0;

        double pHigh = ProbabilityAbove(spot, high, sigma, timeYears);
        double pLow = ProbabilityAbove(spot, low, sigma, timeYears);
        return MathUtils.Clamp(pLow - pHigh, 0, 1);
    }

    private static bool TryParseMoney(string raw, out double value) =>
        double.TryParse(raw.Replace(",", string.Empty), out value);

    private static string NormalizeAsset(string raw)
    {
        string normalized = raw.Trim().ToUpperInvariant();
        return normalized switch
        {
            "BITCOIN" => "BTC",
            "ETHEREUM" => "ETH",
            "SOLANA" => "SOL",
            _ => normalized
        };
    }
}

public sealed class PolymarketLiveService : IPolymarketLiveService
{
    private const int DefaultEventSearchLimit = 6;
    private static readonly string[] SupportedAssets = ["BTC", "ETH", "SOL"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsAnalyticsService _analytics;
    private readonly ILogger<PolymarketLiveService> _logger;

    private sealed record SearchResponse(
        [property: JsonPropertyName("events")] List<GammaEventSearchItem>? Events);

    private sealed record GammaEventSearchItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("endDate")] DateTimeOffset EndDate,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("closed")] bool Closed);

    private sealed record GammaEventDetail(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("endDate")] DateTimeOffset EndDate,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("closed")] bool Closed,
        [property: JsonPropertyName("eventMetadata")] GammaEventMetadata? EventMetadata,
        [property: JsonPropertyName("markets")] List<GammaMarket>? Markets);

    private sealed record GammaEventMetadata(
        [property: JsonPropertyName("priceToBeat")] double? PriceToBeat,
        [property: JsonPropertyName("finalPrice")] double? FinalPrice);

    private sealed record GammaMarket(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("question")] string Question,
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("endDate")] DateTimeOffset EndDate,
        [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("outcomes")] string? Outcomes,
        [property: JsonPropertyName("liquidityNum")] double? LiquidityNum,
        [property: JsonPropertyName("volume24hr")] double? Volume24h,
        [property: JsonPropertyName("bestBid")] double? BestBid,
        [property: JsonPropertyName("bestAsk")] double? BestAsk,
        [property: JsonPropertyName("spread")] double? Spread,
        [property: JsonPropertyName("outcomePrices")] string? OutcomePrices,
        [property: JsonPropertyName("enableOrderBook")] bool EnableOrderBook,
        [property: JsonPropertyName("acceptingOrders")] bool? AcceptingOrders);

    private sealed record AssetContext(
        string Asset,
        PolymarketReferenceAssetSnapshot Reference,
        MacroBiasSnapshot LiveBias,
        VolRegimeSnapshot Regime);

    public PolymarketLiveService(
        IHttpClientFactory httpClientFactory,
        IOptionsAnalyticsService analytics,
        ILogger<PolymarketLiveService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _analytics = analytics;
        _logger = logger;
    }

    public async Task<PolymarketLiveSnapshot> GetLiveSnapshotAsync(int lookaheadMinutes = 24 * 60, int maxMarkets = 24, CancellationToken ct = default)
    {
        int safeLookahead = Math.Clamp(lookaheadMinutes, 5, 7 * 24 * 60);
        int safeMaxMarkets = Math.Clamp(maxMarkets, 6, 60);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var assetTasks = SupportedAssets.ToDictionary(
            asset => asset,
            asset => BuildAssetContextAsync(asset, ct),
            StringComparer.OrdinalIgnoreCase);

        await Task.WhenAll(assetTasks.Values);
        Dictionary<string, AssetContext> assetContexts = assetTasks.ToDictionary(kv => kv.Key, kv => kv.Value.Result, StringComparer.OrdinalIgnoreCase);

        List<GammaEventDetail> events = await LoadCryptoEventsAsync(safeLookahead, ct);
        int rawEvents = events.Count;
        List<GammaEventDetail> activeEvents = events
            .Where(evt => evt.Active && !evt.Closed)
            .OrderBy(evt => evt.EndDate)
            .ToList();

        var opportunities = new List<PolymarketMarketSignal>(safeMaxMarkets);
        int rawMarkets = 0;
        int tradeableMarkets = 0;
        int nearExpiryMarkets = 0;

        foreach (GammaEventDetail evt in activeEvents)
        {
            foreach (GammaMarket market in evt.Markets ?? [])
            {
                rawMarkets++;

                if (!market.EnableOrderBook || market.AcceptingOrders is false)
                    continue;

                if (!TryInferAsset(market.Question, out string? asset) || string.IsNullOrWhiteSpace(asset))
                    continue;

                if (!assetContexts.TryGetValue(asset, out AssetContext? assetContext))
                    continue;

                double minutesToExpiry = (market.EndDate - now).TotalMinutes;
                if (minutesToExpiry <= 0 || minutesToExpiry > safeLookahead)
                    continue;

                tradeableMarkets++;
                if (minutesToExpiry <= 30)
                    nearExpiryMarkets++;

                if (!TryBuildParsedQuestion(evt, market, assetContext, out PolymarketParsedQuestion? parsed))
                    continue;

                PolymarketMarketSignal signal = BuildSignal(evt, market, parsed!, assetContext, now, safeLookahead);
                if (!string.Equals(signal.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase))
                    opportunities.Add(signal);
                else if (opportunities.Count < safeMaxMarkets / 2)
                    opportunities.Add(signal);
            }
        }

        List<PolymarketMarketSignal> ranked = opportunities
            .OrderByDescending(signal => signal.ConvictionScore)
            .ThenByDescending(signal => signal.QualityScore)
            .ThenBy(signal => signal.MinutesToExpiry)
            .Take(safeMaxMarkets)
            .ToList();

        int scannerSignals = ranked.Count(signal => !string.Equals(signal.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase));
        int actionableSignals = ranked.Count(signal => signal.BotEligible);
        PolymarketRuntimeStatus runtime = BuildRuntimeStatus();
        IReadOnlyList<PolymarketReferenceAssetSnapshot> references = assetContexts.Values.Select(x => x.Reference).OrderBy(x => x.Asset).ToList();
        IReadOnlyList<PolymarketBotTierSnapshot> botTiers = BuildBotTiers(runtime, ranked, rawEvents, tradeableMarkets, nearExpiryMarkets, actionableSignals);
        IReadOnlyList<string> notes = BuildNotes(runtime, safeLookahead, activeEvents.Count, scannerSignals, actionableSignals);

        string status = actionableSignals > 0
            ? runtime.TradingEnabled && runtime.SignerConfigured ? "ready" : "analysis-ready"
            : scannerSignals > 0
                ? "watching-gates"
                : activeEvents.Count > 0 ? "watching" : "cold";

        string summary = actionableSignals > 0
            ? $"{actionableSignals} crypto market(s) currently clear both model edge and execution gates on Polymarket."
            : scannerSignals > 0
                ? $"{scannerSignals} crypto market(s) show model edge, but none currently clears the bot execution gates."
            : activeEvents.Count > 0
                ? "Polymarket crypto universe is live, but no short-dated market currently clears the edge and quality gates."
                : "No active short-dated Polymarket crypto event was discovered for BTC/ETH/SOL in the current scan window.";

        return new PolymarketLiveSnapshot(
            Status: status,
            Summary: summary,
            Runtime: runtime,
            Assets: references,
            BotTiers: botTiers,
            Opportunities: ranked,
            Stats: new PolymarketScanStats(
                RawEvents: rawEvents,
                ActiveEvents: activeEvents.Count,
                RawMarkets: rawMarkets,
                TradeableMarkets: tradeableMarkets,
                NearExpiryMarkets: nearExpiryMarkets,
                ScannerSignals: scannerSignals,
                ActionableSignals: actionableSignals),
            Notes: notes,
            Portfolio: new PolymarketBotPortfolioSnapshot(
                StartingBalanceUsd: 0,
                CashBalanceUsd: 0,
                EquityUsd: 0,
                AvailableBalanceUsd: 0,
                GrossExposureUsd: 0,
                RealizedPnlUsd: 0,
                UnrealizedPnlUsd: 0,
                NetPnlUsd: 0,
                DailyPnlUsd: 0,
                MonthlyPnlUsd: 0,
                PeakEquityUsd: 0,
                DrawdownUsd: 0,
                DrawdownPct: 0,
                OpenPositionsCount: 0,
                ClosedPositionsCount: 0,
                WinRate: 0,
                AvgWinnerUsd: 0,
                AvgLoserUsd: 0,
                MaxTradeRiskUsd: 0,
                Timestamp: now),
            OpenPositions: [],
            RecentClosedPositions: [],
            Journal: [],
            Timestamp: now);
    }

    private async Task<AssetContext> BuildAssetContextAsync(string asset, CancellationToken ct)
    {
        AssetMarketOverview overview = await _analytics.GetOverviewAsync(asset, ct);
        VolRegimeSnapshot regime = await _analytics.GetRegimeAsync(asset, ct);
        MacroBiasSnapshot liveBias = await _analytics.GetLiveBiasAsync(asset, 1, ct);

        double fallbackVol = asset switch
        {
            "BTC" => 0.45,
            "ETH" => 0.60,
            "SOL" => 0.85,
            _ => 0.55
        };

        var reference = new PolymarketReferenceAssetSnapshot(
            Asset: asset,
            Spot: overview.UnderlyingPrice,
            AtmIv: overview.AtmIv > 0 ? overview.AtmIv : fallbackVol,
            Regime: regime.Regime,
            RegimeConfidence: regime.ConfidenceScore,
            LiveBiasScore: liveBias.BiasScore,
            LiveBiasLabel: liveBias.Bias,
            Timestamp: overview.Timestamp == default ? DateTimeOffset.UtcNow : overview.Timestamp);

        return new AssetContext(asset, reference, liveBias, regime);
    }

    private async Task<List<GammaEventDetail>> LoadCryptoEventsAsync(int lookaheadMinutes, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("polymarket-gamma");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeZoneInfo eastern = ResolveEasternTimeZone();
        DateTimeOffset easternNow = TimeZoneInfo.ConvertTime(now, eastern);
        DateTimeOffset easternTomorrow = easternNow.AddDays(1);

        var queries = BuildSearchQueries(easternNow, easternTomorrow);

        var uniqueIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var events = new List<GammaEventDetail>();

        foreach (string query in queries)
        {
            SearchResponse? search = await client.GetFromJsonAsync<SearchResponse>($"/public-search?q={Uri.EscapeDataString(query)}", ct);
            foreach (GammaEventSearchItem item in search?.Events?.Take(DefaultEventSearchLimit) ?? [])
            {
                if (!uniqueIds.Add(item.Id))
                    continue;

                if ((item.EndDate - now).TotalMinutes < -5 || (item.EndDate - now).TotalMinutes > lookaheadMinutes + 24 * 60)
                    continue;

                try
                {
                    GammaEventDetail? detail = await client.GetFromJsonAsync<GammaEventDetail>($"/events/{item.Id}", ct);
                    if (detail is not null)
                        events.Add(detail);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load Polymarket event detail for {EventId}", item.Id);
                }
            }
        }

        return events;
    }

    private static bool TryInferAsset(string question, out string? asset)
    {
        asset = null;
        if (!PolymarketSignalMath.TryParseTradeableQuestion(question, out PolymarketParsedQuestion? parsed))
        {
            Match assetMatch = Regex.Match(question ?? string.Empty, @"(?<asset>Bitcoin|BTC|Ethereum|ETH|Solana|SOL)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!assetMatch.Success)
                return false;

            asset = assetMatch.Groups["asset"].Value.Trim().ToUpperInvariant() switch
            {
                "BITCOIN" => "BTC",
                "ETHEREUM" => "ETH",
                "SOLANA" => "SOL",
                var raw => raw
            };
            return !string.IsNullOrWhiteSpace(asset);
        }

        asset = parsed!.Asset;
        return true;
    }

    private static bool TryBuildParsedQuestion(
        GammaEventDetail evt,
        GammaMarket market,
        AssetContext context,
        out PolymarketParsedQuestion? parsed)
    {
        parsed = null;

        if (PolymarketSignalMath.LooksLikeDirectionalQuestion(market.Question))
        {
            double referencePrice = ResolveDirectionalReferencePrice(evt, market, context.Reference.Spot);
            return PolymarketSignalMath.TryParseTradeableQuestion(market.Question, out parsed, referencePrice);
        }

        return PolymarketSignalMath.TryParseTradeableQuestion(market.Question, out parsed);
    }

    private static PolymarketMarketSignal BuildSignal(
        GammaEventDetail evt,
        GammaMarket market,
        PolymarketParsedQuestion parsed,
        AssetContext context,
        DateTimeOffset now,
        int lookaheadMinutes)
    {
        (string primaryOutcomeLabel, string secondaryOutcomeLabel) = ParseOutcomeLabels(market.Outcomes);
        bool isDirectional = IsDirectionalMarket(market, primaryOutcomeLabel, secondaryOutcomeLabel);
        string signalCategory = isDirectional ? "directional" : "threshold";
        string displayLabel = isDirectional
            ? BuildDirectionalDisplayLabel(parsed.Asset, market)
            : BuildThresholdDisplayLabel(parsed);

        double bid = MathUtils.Clamp(market.BestBid ?? 0, 0, 1);
        double ask = MathUtils.Clamp(market.BestAsk ?? 0, 0, 1);
        (double parsedYes, double parsedNo) = ParseOutcomePrices(market.OutcomePrices);
        double midpointYes = bid > 0 && ask > 0 ? (bid + ask) / 2.0 : parsedYes;
        double marketYes = midpointYes > 0 ? midpointYes : parsedYes;
        if (marketYes <= 0)
            marketYes = 0.5;
        bool parsedLooksDefault = Math.Abs(parsedYes - 0.5) < 1e-9 && Math.Abs(parsedNo - 0.5) < 1e-9;
        bool parsedLooksIncoherent = Math.Abs((parsedYes + parsedNo) - 1.0) > 0.08;
        double marketNo = parsedLooksDefault || parsedLooksIncoherent
            ? MathUtils.Clamp(1.0 - marketYes, 0, 1)
            : parsedNo > 0
                ? parsedNo
                : MathUtils.Clamp(1.0 - marketYes, 0, 1);
        double spread = market.Spread ?? Math.Max(0, ask - bid);
        double minutes = Math.Max(0.1, (market.EndDate - now).TotalMinutes);
        double distancePct = parsed.Relation == PolymarketThresholdRelation.Between && parsed.UpperStrike.HasValue
            ? Math.Min(Math.Abs(context.Reference.Spot / parsed.LowerStrike - 1), Math.Abs(context.Reference.Spot / parsed.UpperStrike.Value - 1))
            : Math.Abs(context.Reference.Spot / Math.Max(parsed.LowerStrike, 1e-9) - 1.0);

        double biasShift = ComputeBiasShift(parsed, context.LiveBias.BiasScore);
        double fairYes = PolymarketSignalMath.ComputeFairYesProbability(
            context.Reference.Spot,
            context.Reference.AtmIv,
            market.EndDate,
            now,
            parsed,
            biasShift);
        double fairNo = 1.0 - fairYes;

        double entryYes = ask > 0 ? ask : marketYes;
        double entryNo = marketNo;
        double executionBuffer = Math.Max(0.01, spread * 1.35);
        double edgeYes = fairYes - entryYes - executionBuffer;
        double edgeNo = fairNo - entryNo - executionBuffer;

        double liquidity = Math.Max(0, market.LiquidityNum ?? 0);
        double volume24h = Math.Max(0, market.Volume24h ?? 0);
        double qualityScore = MathUtils.Clamp(
            42
            + Math.Log10(liquidity + 10) * 12
            + Math.Log10(volume24h + 10) * 8
            - spread * 140
            - distancePct * 120
            + Math.Min(minutes, 180) / 180.0 * 6,
            1,
            99);

        string buyFirst = BuySideLabel(primaryOutcomeLabel);
        string buySecond = BuySideLabel(secondaryOutcomeLabel);
        string side = "Pass";
        double selectedEdge = Math.Max(edgeYes, edgeNo);
        if (selectedEdge >= 0.012 && qualityScore >= 42 && edgeYes >= edgeNo)
            side = buyFirst;
        else if (selectedEdge >= 0.012 && qualityScore >= 42 && edgeNo > edgeYes)
            side = buySecond;

        double conviction = MathUtils.Clamp(
            qualityScore * 0.45
            + Math.Max(edgeYes, edgeNo) * 1800
            + (60 - Math.Min(minutes, 60)) * 0.22
            + context.Reference.RegimeConfidence * 0.08,
            1,
            99);

        string macro = $"Live bias {context.LiveBias.Bias} ({context.LiveBias.BiasScore:+0.0;-0.0}) with regime {context.Reference.Regime}.";
        string micro = isDirectional
            ? $"Reference {parsed.LowerStrike:0.##}, spot {context.Reference.Spot:0.##}, spread {(spread * 100):0.00}c, liquidity {liquidity:0}, time-to-resolution {minutes:0.0}m."
            : $"Spot {context.Reference.Spot:0.##}, strike distance {distancePct * 100:0.00}%, spread {(spread * 100):0.00}c, liquidity {liquidity:0}, time-to-resolution {minutes:0.0}m.";
        string math = isDirectional
            ? $"fair{primaryOutcomeLabel}={fairYes:P1}, market{primaryOutcomeLabel}={marketYes:P1}, market{secondaryOutcomeLabel}={marketNo:P1}, reference={parsed.LowerStrike:0.##}, sigma={context.Reference.AtmIv:P1}, biasShift={biasShift:+0.0%;-0.0%}, edge{primaryOutcomeLabel}={edgeYes:+0.0%;-0.0%}, edge{secondaryOutcomeLabel}={edgeNo:+0.0%;-0.0%}."
            : $"fairYes={fairYes:P1}, marketYes={marketYes:P1}, marketNo={marketNo:P1}, sigma={context.Reference.AtmIv:P1}, biasShift={biasShift:+0.0%;-0.0%}, edgeYes={edgeYes:+0.0%;-0.0%}, edgeNo={edgeNo:+0.0%;-0.0%}.";

        string summary = side switch
        {
            _ when side == buyFirst => isDirectional
                ? $"{parsed.Asset} short-horizon {primaryOutcomeLabel.ToLowerInvariant()} side looks underpriced."
                : $"{parsed.Asset} threshold looks underpriced on the {primaryOutcomeLabel} side.",
            _ when side == buySecond => isDirectional
                ? $"{parsed.Asset} short-horizon {secondaryOutcomeLabel.ToLowerInvariant()} side offers better expectancy."
                : $"{parsed.Asset} threshold looks overpriced on the {primaryOutcomeLabel} side; {secondaryOutcomeLabel} offers better expectancy.",
            _ => isDirectional
                ? $"{parsed.Asset} short-horizon directional market is live but does not clear the edge buffer."
                : $"{parsed.Asset} threshold is live but does not currently clear the edge buffer."
        };

        double indicativeEntry = side == buyFirst
            ? entryYes
            : side == buySecond
                ? entryNo
                : Math.Max(entryYes, entryNo);

        var executionPlan = new PolymarketExecutionPlan(
            Side: side,
            OrderStyle: minutes <= 15 ? "join-best-or-better" : "passive-limit",
            IndicativeEntryPrice: indicativeEntry,
            MaxPositionPct: MathUtils.Clamp((selectedEdge * 140 + qualityScore * 0.015) / 100.0, 0.0025, 0.025),
            TimeStopMinutes: (int)Math.Clamp(Math.Round(minutes * 0.55), 3, Math.Max(5, (int)Math.Round(minutes))),
            ExitPlan: side == "Pass"
                ? "Stand down. Recheck on next scan."
                : $"Take profits once model edge compresses below 0.5c or market reprices toward fair value. Hard exit by T-{Math.Max(1, (int)Math.Round(minutes * 0.15))}m.",
            RiskPlan: "Never cross the spread aggressively on thin books. Cancel if spread widens materially or live edge flips negative.");

        PolymarketMarketSignal signal = new(
            EventId: evt.Id,
            MarketId: market.Id,
            Asset: parsed.Asset,
            Question: market.Question,
            Slug: market.Slug,
            Expiry: market.EndDate,
            MinutesToExpiry: minutes,
            Spot: context.Reference.Spot,
            StrikeLow: parsed.LowerStrike,
            StrikeHigh: parsed.UpperStrike,
            ThresholdType: parsed.Relation.ToString(),
            SignalCategory: signalCategory,
            DisplayLabel: displayLabel,
            PrimaryOutcomeLabel: primaryOutcomeLabel,
            SecondaryOutcomeLabel: secondaryOutcomeLabel,
            MarketYesPrice: marketYes,
            MarketNoPrice: marketNo,
            BestBid: bid,
            BestAsk: ask,
            Spread: spread,
            LiquidityUsd: liquidity,
            Volume24hUsd: volume24h,
            FairYesProbability: fairYes,
            FairNoProbability: fairNo,
            EdgeYesPct: edgeYes,
            EdgeNoPct: edgeNo,
            DistanceToStrikePct: distancePct,
            VolInput: context.Reference.AtmIv,
            QualityScore: qualityScore,
            ConvictionScore: conviction,
            RecommendedSide: side,
            Summary: summary,
            MacroReasoning: macro,
            MicroReasoning: micro,
            MathReasoning: math,
            ExecutionPlan: executionPlan,
            BotEligible: false,
            BotEligibilityReason: "not-assessed",
            BotEntryPrice: indicativeEntry,
            BotSelectedEdgePct: selectedEdge);

        PolymarketBotSignalAssessment assessment = PolymarketBotRuleEngine.AssessSignal(signal, lookaheadMinutes);
        return signal with
        {
            BotEligible = assessment.BotEligible,
            BotEligibilityReason = assessment.BlockReason,
            BotEntryPrice = assessment.EntryPrice,
            BotSelectedEdgePct = assessment.SelectedEdgePct
        };
    }

    private static double ResolveDirectionalReferencePrice(GammaEventDetail evt, GammaMarket market, double fallbackSpot)
    {
        double fromEvent = evt.EventMetadata?.PriceToBeat ?? 0;
        if (fromEvent > 0)
            return fromEvent;

        double fromDescription = TryExtractPriceToBeat(market.Description) ?? TryExtractPriceToBeat(evt.Description) ?? 0;
        if (fromDescription > 0)
            return fromDescription;

        return Math.Max(fallbackSpot, 1e-9);
    }

    private static double? TryExtractPriceToBeat(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        Match match = Regex.Match(description, @"price to beat[^$\d]*(\$?(?<px>[\d,]+(?:\.\d+)?))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return null;

        return double.TryParse(match.Groups["px"].Value.Replace("$", string.Empty).Replace(",", string.Empty), out double value)
            ? value
            : null;
    }

    private static (string primary, string secondary) ParseOutcomeLabels(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ("Yes", "No");

        try
        {
            string[]? values = System.Text.Json.JsonSerializer.Deserialize<string[]>(raw);
            if (values is { Length: >= 2 })
                return (NormalizeOutcomeLabel(values[0]), NormalizeOutcomeLabel(values[1]));
        }
        catch
        {
        }

        return ("Yes", "No");
    }

    private static string NormalizeOutcomeLabel(string raw)
    {
        string label = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(label))
            return "Yes";
        return char.ToUpperInvariant(label[0]) + label[1..].ToLowerInvariant();
    }

    private static bool IsDirectionalMarket(GammaMarket market, string primaryOutcomeLabel, string secondaryOutcomeLabel)
    {
        if (PolymarketSignalMath.LooksLikeDirectionalQuestion(market.Question))
            return true;

        return (primaryOutcomeLabel.Equals("Up", StringComparison.OrdinalIgnoreCase) &&
                secondaryOutcomeLabel.Equals("Down", StringComparison.OrdinalIgnoreCase)) ||
               (market.Slug?.Contains("updown", StringComparison.OrdinalIgnoreCase) ?? false) ||
               (market.Slug?.Contains("up-or-down", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string BuildThresholdDisplayLabel(PolymarketParsedQuestion parsed)
    {
        return parsed.Relation switch
        {
            PolymarketThresholdRelation.Above => $"{parsed.Asset} above {parsed.LowerStrike:0.##}",
            PolymarketThresholdRelation.Below => $"{parsed.Asset} below {parsed.LowerStrike:0.##}",
            PolymarketThresholdRelation.Between when parsed.UpperStrike.HasValue => $"{parsed.Asset} {parsed.LowerStrike:0.##}-{parsed.UpperStrike.Value:0.##}",
            PolymarketThresholdRelation.Outside when parsed.UpperStrike.HasValue => $"{parsed.Asset} outside {parsed.LowerStrike:0.##}-{parsed.UpperStrike.Value:0.##}",
            _ => parsed.RawQuestion
        };
    }

    private static string BuildDirectionalDisplayLabel(string asset, GammaMarket market)
    {
        string horizon = InferDirectionalWindowLabel(market);
        return string.IsNullOrWhiteSpace(horizon)
            ? $"{asset} up/down"
            : $"{asset} {horizon} up/down";
    }

    private static string InferDirectionalWindowLabel(GammaMarket market)
    {
        if (!string.IsNullOrWhiteSpace(market.Slug))
        {
            Match slugMatch = Regex.Match(market.Slug, @"(?<window>\d+\s*[mh])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (slugMatch.Success)
                return slugMatch.Groups["window"].Value.Replace(" ", string.Empty).ToLowerInvariant();
        }

        if (market.StartDate.HasValue)
        {
            double minutes = Math.Max(1, Math.Round((market.EndDate - market.StartDate.Value).TotalMinutes));
            if (minutes < 60)
                return $"{minutes:0}m";
            double hours = minutes / 60.0;
            return $"{hours:0.#}h";
        }

        return string.Empty;
    }

    private static string BuySideLabel(string outcomeLabel) => $"Buy {outcomeLabel}";

    private static List<string> BuildSearchQueries(DateTimeOffset easternNow, DateTimeOffset easternTomorrow)
    {
        var queries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string today = $"{easternNow.ToString("MMMM", CultureInfo.InvariantCulture)} {easternNow.Day}";
        string tomorrow = $"{easternTomorrow.ToString("MMMM", CultureInfo.InvariantCulture)} {easternTomorrow.Day}";
        foreach (string assetName in new[] { "bitcoin", "ethereum", "solana" })
        {
            queries.Add($"{assetName} above");
            queries.Add($"{assetName} above on {today}");
            queries.Add($"{assetName} above on {tomorrow}");
            queries.Add($"{assetName} up or down");
            queries.Add($"{assetName} up or down 5m");
        }

        return queries.ToList();
    }

    private static TimeZoneInfo ResolveEasternTimeZone()
    {
        string[] ids = ["America/New_York", "Eastern Standard Time"];
        foreach (string id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.Utc;
    }

    private static double ComputeBiasShift(PolymarketParsedQuestion parsed, double biasScore)
    {
        double normalized = MathUtils.Clamp(biasScore / 100.0, -1, 1) * 0.025;
        return parsed.Relation switch
        {
            PolymarketThresholdRelation.Above => normalized,
            PolymarketThresholdRelation.Below => -normalized,
            _ => 0
        };
    }

    private static (double yes, double no) ParseOutcomePrices(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0.5, 0.5);

        try
        {
            string[]? values = System.Text.Json.JsonSerializer.Deserialize<string[]>(raw);
            if (values is not null && values.Length >= 2)
            {
                double yes = double.TryParse(values[0], out double parsedYes) ? parsedYes : 0.5;
                double no = double.TryParse(values[1], out double parsedNo) ? parsedNo : 1 - yes;
                return (MathUtils.Clamp(yes, 0, 1), MathUtils.Clamp(no, 0, 1));
            }
        }
        catch
        {
        }

        try
        {
            double[]? values = System.Text.Json.JsonSerializer.Deserialize<double[]>(raw);
            if (values is not null && values.Length >= 2)
                return (MathUtils.Clamp(values[0], 0, 1), MathUtils.Clamp(values[1], 0, 1));
        }
        catch
        {
            return (0.5, 0.5);
        }

        return (0.5, 0.5);
    }

    private static PolymarketRuntimeStatus BuildRuntimeStatus()
    {
        bool tradingEnabled = string.Equals(Environment.GetEnvironmentVariable("POLYMARKET_TRADING_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
        bool signerConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("POLYMARKET_PRIVATE_KEY"));
        bool telegramConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID"));
        string walletAddress = Environment.GetEnvironmentVariable("POLYMARKET_WALLET_ADDRESS")?.Trim() ?? string.Empty;
        string walletHint = walletAddress.Length >= 10
            ? $"{walletAddress[..6]}...{walletAddress[^4..]}"
            : signerConfigured ? "configured" : "not-configured";
        double maxTradeUsd = ParseDoubleEnv("POLYMARKET_MAX_TRADE_USD", 1.0, 0.25, 5.0);
        double dailyLossLimitUsd = ParseDoubleEnv("POLYMARKET_DAILY_LOSS_LIMIT_USD", 5.0, 1.0, 50.0);

        string requestedMode = (Environment.GetEnvironmentVariable("POLYMARKET_EXECUTION_MODE") ?? "analysis-only").Trim().ToLowerInvariant();
        string mode = requestedMode switch
        {
            "paper" => "paper-autopilot",
            "dry-run" => "dry-run",
            "live" when tradingEnabled && signerConfigured => "live-armed",
            "live" when signerConfigured => "paper-live-ready",
            _ => signerConfigured ? "paper-live-ready" : "analysis-only"
        };
        string summary = mode switch
        {
            "live-armed" => "Live routing is armed. Keep per-trade risk fixed, reconcile fills, and monitor the kill-switch continuously.",
            "paper-autopilot" => "Autopilot is live in paper mode with local position accounting and guardrails.",
            "dry-run" => "Signals and execution intents are emitted, but no Polymarket order is submitted.",
            "paper-live-ready" => "Wallet signer is configured, but live routing is still disabled.",
            _ => "Public market scan and scoring are active. Live order placement remains disabled."
        };

        return new PolymarketRuntimeStatus(
            TradingEnabled: tradingEnabled,
            SignerConfigured: signerConfigured,
            TelegramConfigured: telegramConfigured,
            ExecutionArmed: mode is "paper-autopilot" or "dry-run" or "live-armed",
            DailyLossLockActive: false,
            RuntimeMode: mode,
            WalletAddressHint: walletHint,
            MaxTradeUsd: maxTradeUsd,
            DailyLossLimitUsd: dailyLossLimitUsd,
            Summary: summary);
    }

    private static double ParseDoubleEnv(string name, double fallback, double min, double max)
    {
        string raw = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        if (!double.TryParse(raw, out double parsed))
            return fallback;

        return MathUtils.Clamp(parsed, min, max);
    }

    private static IReadOnlyList<PolymarketBotTierSnapshot> BuildBotTiers(
        PolymarketRuntimeStatus runtime,
        IReadOnlyList<PolymarketMarketSignal> ranked,
        int rawEvents,
        int tradeableMarkets,
        int nearExpiryMarkets,
        int actionableSignals)
    {
        PolymarketMarketSignal? top = ranked.FirstOrDefault();
        return new[]
        {
            new PolymarketBotTierSnapshot(
                Name: "Scout",
                Status: tradeableMarkets > 0 ? "online" : "thin",
                Summary: $"Scanning active BTC / ETH / SOL threshold events across {rawEvents} Polymarket series.",
                Metric: tradeableMarkets,
                Detail: $"{nearExpiryMarkets} market(s) are inside the short-horizon window."),
            new PolymarketBotTierSnapshot(
                Name: "Quant",
                Status: top is null ? "waiting" : "priced",
                Summary: top is null ? "No crypto threshold currently clears parsing and quality rules." : top.Summary,
                Metric: top?.ConvictionScore ?? 0,
                Detail: top is null ? "Fair probability engine is standing by." : $"{top.Asset} {top.RecommendedSide} with conviction {top.ConvictionScore:0.0}."),
            new PolymarketBotTierSnapshot(
                Name: "Execution",
                Status: runtime.TradingEnabled && runtime.SignerConfigured ? "armed" : "guarded",
                Summary: runtime.Summary,
                Metric: actionableSignals,
                Detail: "Real-money routing intentionally remains behind explicit runtime flags.")
        };
    }

    private static IReadOnlyList<string> BuildNotes(
        PolymarketRuntimeStatus runtime,
        int lookaheadMinutes,
        int activeEvents,
        int scannerSignals,
        int actionableSignals)
    {
        var notes = new List<string>
        {
            $"Focus window: {lookaheadMinutes} minutes.",
            "Universe is restricted to parseable BTC / ETH / SOL threshold markets so the fair-value engine stays auditable.",
            "Current scoring uses spot + ATM vol + live bias; it is not yet an authenticated order router."
        };

        if (!runtime.SignerConfigured)
            notes.Add("No Polymarket signer is configured on the server yet.");
        if (activeEvents == 0)
            notes.Add("No active short-dated crypto Polymarket event was found in the current search result set.");
        if (scannerSignals > 0 && actionableSignals == 0)
            notes.Add("Scanner signals exist, but the bot is waiting for tighter spreads, cleaner entry prices, stronger conviction or deeper liquidity before opening risk.");
        if (actionableSignals == 0 && activeEvents > 0)
            notes.Add("Markets are being monitored, but nothing currently clears the required edge buffer after spread.");

        return notes;
    }
}
