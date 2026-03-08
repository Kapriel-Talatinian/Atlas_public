using Atlas.Api.Models;
using Atlas.Core.Common;
using Atlas.Core.Models;

namespace Atlas.Api.Services;

public interface IOptionsAnalyticsService
{
    Task<AssetMarketOverview> GetOverviewAsync(string asset, CancellationToken ct = default);
    Task<IReadOnlyList<DateTimeOffset>> GetExpiriesAsync(string asset, CancellationToken ct = default);
    Task<IReadOnlyList<LiveOptionQuote>> GetChainAsync(
        string asset,
        DateTimeOffset? expiry = null,
        string type = "all",
        int limit = 220,
        CancellationToken ct = default);
    Task<IReadOnlyList<VolSurfacePoint>> GetSurfaceAsync(string asset, int limit = 600, CancellationToken ct = default);
    Task<IReadOnlyList<StrategyAnalysisResult>> BuildPresetStrategiesAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        CancellationToken ct = default);
    Task<StrategyAnalysisResult> AnalyzeAsync(StrategyAnalyzeRequest request, CancellationToken ct = default);
    Task<OptionModelSnapshot> GetModelSnapshotAsync(string symbol, CancellationToken ct = default);
    Task<ModelCalibrationSnapshot> GetModelCalibrationAsync(
        string asset,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default);
    Task<OptionSignalBoard> GetSignalBoardAsync(
        string asset,
        DateTimeOffset? expiry = null,
        string type = "all",
        int limit = 140,
        CancellationToken ct = default);
    Task<VolRegimeSnapshot> GetRegimeAsync(string asset, CancellationToken ct = default);
    Task<StrategyRecommendationBoard> GetRecommendationsAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        string riskProfile = "balanced",
        CancellationToken ct = default);
    Task<StrategyOptimizationBoard> OptimizeStrategiesAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        string riskProfile = "balanced",
        double targetDelta = 0,
        double targetVega = 0,
        double targetTheta = 0,
        CancellationToken ct = default);
    Task<GreeksExposureGrid> GetGreeksExposureGridAsync(
        string asset,
        DateTimeOffset? expiry = null,
        int maxExpiries = 6,
        int maxStrikes = 24,
        CancellationToken ct = default);
    Task<ArbitrageScanResult> GetArbitrageScanAsync(
        string asset,
        DateTimeOffset? expiry = null,
        int limit = 120,
        CancellationToken ct = default);
}

public sealed class OptionsAnalyticsService : IOptionsAnalyticsService
{
    private const double DefaultRiskFreeRate = 0.03;
    private readonly IOptionsMarketDataService _marketData;
    private sealed record AssetModelCalibration(
        string Asset,
        DateTimeOffset? Expiry,
        double Spot,
        double AtmIv30D,
        double Skew25D,
        double TermSlope30To90,
        HestonParams HestonParams,
        SabrParams SabrParams,
        double ConfidenceScore,
        IReadOnlyList<ModelFitMetric> FitMetrics);

    public OptionsAnalyticsService(IOptionsMarketDataService marketData)
    {
        _marketData = marketData;
    }

    public async Task<AssetMarketOverview> GetOverviewAsync(string asset, CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        if (chain.Count == 0)
            return new AssetMarketOverview(
                Asset: asset.ToUpperInvariant(),
                UnderlyingPrice: 0,
                AtmIv: 0,
                RiskReversal25D: 0,
                ListedOptions: 0,
                OpenInterest: 0,
                Turnover24h: 0,
                PutCallOpenInterestRatio: 0,
                TermStructure: [],
                Timestamp: DateTimeOffset.UtcNow);

        double spot = ReferenceSpot(chain);
        var expiries = chain
            .Select(q => q.Expiry)
            .Distinct()
            .OrderBy(e => e)
            .ToList();

        DateTimeOffset nearestExpiry = expiries.FirstOrDefault(e => e >= DateTimeOffset.UtcNow);
        if (nearestExpiry == default) nearestExpiry = expiries[0];

        var front = chain.Where(q => q.Expiry == nearestExpiry).ToList();
        double atmIv = ComputeAtmIv(front, spot);
        double rr25 = ComputeRiskReversal(front);

        double callOi = chain.Where(q => q.Right == OptionRight.Call).Sum(q => q.OpenInterest);
        double putOi = chain.Where(q => q.Right == OptionRight.Put).Sum(q => q.OpenInterest);
        double putCallOiRatio = callOi > 0 ? putOi / callOi : 0;

        var term = expiries
            .Take(8)
            .Select(expiryPoint =>
            {
                var byExpiry = chain.Where(q => q.Expiry == expiryPoint).ToList();
                int days = Math.Max(0, (int)Math.Round((expiryPoint - DateTimeOffset.UtcNow).TotalDays));
                return new ExpiryTermPoint(
                    Expiry: expiryPoint,
                    DaysToExpiry: days,
                    AtmIv: ComputeAtmIv(byExpiry, spot));
            })
            .ToList();

        return new AssetMarketOverview(
            Asset: chain[0].Asset,
            UnderlyingPrice: spot,
            AtmIv: atmIv,
            RiskReversal25D: rr25,
            ListedOptions: chain.Count,
            OpenInterest: chain.Sum(q => q.OpenInterest),
            Turnover24h: chain.Sum(q => q.Turnover24h),
            PutCallOpenInterestRatio: putCallOiRatio,
            TermStructure: term,
            Timestamp: chain.Max(q => q.Timestamp));
    }

    public async Task<IReadOnlyList<DateTimeOffset>> GetExpiriesAsync(string asset, CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        return chain
            .Select(q => q.Expiry)
            .Distinct()
            .OrderBy(e => e)
            .ToList();
    }

    public async Task<IReadOnlyList<LiveOptionQuote>> GetChainAsync(
        string asset,
        DateTimeOffset? expiry = null,
        string type = "all",
        int limit = 220,
        CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);

        if (expiry.HasValue)
            chain = chain.Where(q => q.Expiry.Date == expiry.Value.Date).ToList();

        string normalizedType = type.Trim().ToLowerInvariant();
        if (normalizedType == "call")
            chain = chain.Where(q => q.Right == OptionRight.Call).ToList();
        else if (normalizedType == "put")
            chain = chain.Where(q => q.Right == OptionRight.Put).ToList();

        int safeLimit = Math.Clamp(limit, 20, 2000);
        return chain
            .OrderBy(q => q.Expiry)
            .ThenBy(q => q.Strike)
            .ThenBy(q => q.Right)
            .Take(safeLimit)
            .ToList();
    }

    public async Task<IReadOnlyList<VolSurfacePoint>> GetSurfaceAsync(string asset, int limit = 600, CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        int safeLimit = Math.Clamp(limit, 100, 3000);

        return chain
            .Where(q => q.MarkIv > 0 && q.UnderlyingPrice > 0)
            .OrderByDescending(q => q.Turnover24h)
            .Take(safeLimit)
            .Select(q => new VolSurfacePoint(
                Asset: q.Asset,
                Expiry: q.Expiry,
                DaysToExpiry: Math.Max(0, (int)Math.Round((q.Expiry - DateTimeOffset.UtcNow).TotalDays)),
                Strike: q.Strike,
                Moneyness: q.Strike / q.UnderlyingPrice,
                Right: q.Right,
                MarkIv: q.MarkIv))
            .OrderBy(p => p.Expiry)
            .ThenBy(p => p.Strike)
            .ToList();
    }

    public async Task<IReadOnlyList<StrategyAnalysisResult>> BuildPresetStrategiesAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        if (chain.Count == 0) return [];

        double qty = Math.Max(0.01, size);
        var expiries = chain.Select(q => q.Expiry).Distinct().OrderBy(e => e).ToList();
        DateTime targetDate = expiry?.UtcDateTime.Date ?? expiries.FirstOrDefault(e => e >= DateTimeOffset.UtcNow).UtcDateTime.Date;
        if (targetDate == default) targetDate = expiries[0].UtcDateTime.Date;

        DateTimeOffset frontExpiry = expiries.First(e => e.UtcDateTime.Date == targetDate);
        var front = chain.Where(q => q.Expiry.Date == frontExpiry.Date).ToList();
        if (front.Count == 0) return [];

        double spot = ReferenceSpot(front);
        var calls = front.Where(q => q.Right == OptionRight.Call).OrderBy(q => q.Strike).ToList();
        var puts = front.Where(q => q.Right == OptionRight.Put).OrderBy(q => q.Strike).ToList();
        if (calls.Count == 0 || puts.Count == 0) return [];

        LiveOptionQuote? callAtm = ClosestByStrike(calls, spot);
        LiveOptionQuote? putAtm = ClosestByStrike(puts, spot);
        LiveOptionQuote? callUp1 = ClosestByStrike(calls, spot * 1.05);
        LiveOptionQuote? callUp2 = ClosestByStrike(calls, spot * 1.10);
        LiveOptionQuote? callDn1 = ClosestByStrike(calls, spot * 0.95);
        LiveOptionQuote? putDn1 = ClosestByStrike(puts, spot * 0.95);
        LiveOptionQuote? putDn2 = ClosestByStrike(puts, spot * 0.90);

        var results = new List<StrategyAnalysisResult>();
        TryAddPreset(results, "Long Straddle", asset, chain, [
            new StrategyLegDefinition(callAtm?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(putAtm?.Symbol ?? string.Empty, TradeDirection.Buy, qty)
        ]);

        TryAddPreset(results, "Long Strangle", asset, chain, [
            new StrategyLegDefinition(callUp1?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(putDn1?.Symbol ?? string.Empty, TradeDirection.Buy, qty)
        ]);

        TryAddPreset(results, "Bull Call Spread", asset, chain, [
            new StrategyLegDefinition(callAtm?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(callUp1?.Symbol ?? string.Empty, TradeDirection.Sell, qty)
        ]);

        TryAddPreset(results, "Bear Put Spread", asset, chain, [
            new StrategyLegDefinition(putAtm?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(putDn1?.Symbol ?? string.Empty, TradeDirection.Sell, qty)
        ]);

        TryAddPreset(results, "Risk Reversal (Long)", asset, chain, [
            new StrategyLegDefinition(callUp1?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(putDn1?.Symbol ?? string.Empty, TradeDirection.Sell, qty)
        ]);

        TryAddPreset(results, "Iron Condor", asset, chain, [
            new StrategyLegDefinition(putDn2?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(putDn1?.Symbol ?? string.Empty, TradeDirection.Sell, qty),
            new StrategyLegDefinition(callUp1?.Symbol ?? string.Empty, TradeDirection.Sell, qty),
            new StrategyLegDefinition(callUp2?.Symbol ?? string.Empty, TradeDirection.Buy, qty)
        ]);

        TryAddPreset(results, "Call Butterfly", asset, chain, [
            new StrategyLegDefinition(callDn1?.Symbol ?? string.Empty, TradeDirection.Buy, qty),
            new StrategyLegDefinition(callAtm?.Symbol ?? string.Empty, TradeDirection.Sell, qty * 2),
            new StrategyLegDefinition(callUp1?.Symbol ?? string.Empty, TradeDirection.Buy, qty)
        ]);

        if (expiries.Count >= 2 && callAtm is not null)
        {
            DateTimeOffset near = frontExpiry;
            DateTimeOffset far = expiries.FirstOrDefault(e => e > near);
            if (far == default) far = expiries.Last();
            var nearCall = chain
                .Where(q => q.Expiry == near && q.Right == OptionRight.Call)
                .OrderBy(q => Math.Abs(q.Strike - callAtm.Strike))
                .FirstOrDefault();
            var farCall = chain
                .Where(q => q.Expiry == far && q.Right == OptionRight.Call)
                .OrderBy(q => Math.Abs(q.Strike - callAtm.Strike))
                .FirstOrDefault();

            TryAddPreset(results, "Call Calendar", asset, chain, [
                new StrategyLegDefinition(nearCall?.Symbol ?? string.Empty, TradeDirection.Sell, qty),
                new StrategyLegDefinition(farCall?.Symbol ?? string.Empty, TradeDirection.Buy, qty)
            ]);
        }

        return results
            .OrderByDescending(r => Math.Abs(r.AggregateGreeks.Vega))
            .Take(8)
            .ToList();
    }

    public async Task<StrategyAnalysisResult> AnalyzeAsync(StrategyAnalyzeRequest request, CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(request.Asset, ct);
        return AnalyzeFromChain(
            name: string.IsNullOrWhiteSpace(request.Name) ? "Custom Strategy" : request.Name,
            asset: request.Asset,
            chain: chain,
            legs: request.Legs,
            shockRangePct: request.ShockRangePct,
            gridPoints: request.GridPoints);
    }

    public async Task<OptionModelSnapshot> GetModelSnapshotAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("symbol is required");

        string asset = ExtractAsset(symbol);
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        var quote = chain.FirstOrDefault(q => q.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (quote is null)
            throw new ArgumentException($"unknown option symbol: {symbol}");

        double fallbackSpot = ReferenceSpot(chain);
        var calibration = BuildAssetModelCalibration(chain, quote.Expiry);
        return BuildModelSnapshot(quote, fallbackSpot, calibration);
    }

    public async Task<ModelCalibrationSnapshot> GetModelCalibrationAsync(
        string asset,
        DateTimeOffset? expiry = null,
        CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        var calibration = BuildAssetModelCalibration(chain, expiry);
        return new ModelCalibrationSnapshot(
            Asset: calibration.Asset,
            Expiry: calibration.Expiry,
            Spot: calibration.Spot,
            AtmIv30D: calibration.AtmIv30D,
            Skew25D: calibration.Skew25D,
            TermSlope30To90: calibration.TermSlope30To90,
            HestonKappa: calibration.HestonParams.Kappa,
            HestonTheta: calibration.HestonParams.Theta,
            HestonXi: calibration.HestonParams.Xi,
            HestonRho: calibration.HestonParams.Rho,
            SabrAlpha: calibration.SabrParams.Alpha,
            SabrBeta: calibration.SabrParams.Beta,
            SabrRho: calibration.SabrParams.Rho,
            SabrNu: calibration.SabrParams.Nu,
            ConfidenceScore: calibration.ConfidenceScore,
            FitMetrics: calibration.FitMetrics,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<OptionSignalBoard> GetSignalBoardAsync(
        string asset,
        DateTimeOffset? expiry = null,
        string type = "all",
        int limit = 140,
        CancellationToken ct = default)
    {
        var fullChain = await _marketData.GetOptionChainAsync(asset, ct);
        var calibration = BuildAssetModelCalibration(fullChain, expiry);
        var chain = fullChain;
        if (expiry.HasValue)
            chain = chain.Where(q => q.Expiry.Date == expiry.Value.Date).ToList();

        string normalizedType = type.Trim().ToLowerInvariant();
        if (normalizedType == "call")
            chain = chain.Where(q => q.Right == OptionRight.Call).ToList();
        else if (normalizedType == "put")
            chain = chain.Where(q => q.Right == OptionRight.Put).ToList();

        if (chain.Count == 0)
        {
            return new OptionSignalBoard(
                Asset: asset.ToUpperInvariant(),
                Expiry: expiry,
                Spot: 0,
                TopLongEdges: [],
                TopShortEdges: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        int safeLimit = Math.Clamp(limit, 20, 400);
        double fallbackSpot = ReferenceSpot(chain.Count > 0 ? chain : fullChain);

        var snapshots = chain
            .Where(q => EffectiveMid(q) > 0 && q.MarkIv > 0)
            .Select(q => BuildModelSnapshot(q, fallbackSpot, calibration))
            .OrderByDescending(s => s.ConfidenceScore)
            .Take(safeLimit)
            .ToList();

        var rows = snapshots
            .Select(s => new OptionSignalRow(
                Symbol: s.Symbol,
                Expiry: s.Expiry,
                Strike: s.Strike,
                Right: s.Right,
                Mid: s.Mid,
                MarkIv: s.MarkIv,
                FairComposite: s.FairComposite,
                EdgePct: s.EdgeVsMidPct,
                ProbItm: s.ProbItm,
                ProbTouchApprox: s.ProbTouchApprox,
                LiquidityScore: s.LiquidityScore,
                ConfidenceScore: s.ConfidenceScore,
                Signal: s.Signal,
                Timestamp: s.Timestamp))
            .ToList();

        int sideTake = Math.Clamp(safeLimit / 3, 6, 40);
        var topLong = rows
            .Where(r => r.EdgePct > 0)
            .OrderByDescending(r => r.EdgePct * Math.Max(1, r.ConfidenceScore))
            .Take(sideTake)
            .ToList();

        var topShort = rows
            .Where(r => r.EdgePct < 0)
            .OrderBy(r => r.EdgePct * Math.Max(1, r.ConfidenceScore))
            .Take(sideTake)
            .ToList();

        return new OptionSignalBoard(
            Asset: asset.ToUpperInvariant(),
            Expiry: expiry,
            Spot: fallbackSpot,
            TopLongEdges: topLong,
            TopShortEdges: topShort,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<VolRegimeSnapshot> GetRegimeAsync(string asset, CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        if (chain.Count == 0)
        {
            return new VolRegimeSnapshot(
                Asset: asset.ToUpperInvariant(),
                Spot: 0,
                FrontExpiry: DateTimeOffset.UtcNow,
                AtmIvFront: 0,
                AtmIv30D: 0,
                AtmIv90D: 0,
                TermSlope30To90: 0,
                Skew25D: 0,
                PutCallOpenInterestRatio: 0,
                ExpectedMove7DAbs: 0,
                ExpectedMove7DPct: 0,
                ExpectedMove30DAbs: 0,
                ExpectedMove30DPct: 0,
                Regime: "Unavailable",
                Signal: "No data",
                ConfidenceScore: 1,
                Timestamp: DateTimeOffset.UtcNow);
        }

        double spot = ReferenceSpot(chain);
        var expiries = chain.Select(q => q.Expiry).Distinct().OrderBy(e => e).ToList();
        DateTimeOffset frontExpiry = expiries.FirstOrDefault(e => e >= DateTimeOffset.UtcNow);
        if (frontExpiry == default) frontExpiry = expiries[0];

        double atmFront = AtmForTargetDays(chain, spot, (frontExpiry - DateTimeOffset.UtcNow).TotalDays);
        double atm30 = AtmForTargetDays(chain, spot, 30);
        double atm90 = AtmForTargetDays(chain, spot, 90);
        double slope = atm90 - atm30;

        var front = chain.Where(q => q.Expiry.Date == frontExpiry.Date).ToList();
        double skew25d = ComputeRiskReversal(front);
        double callOi = chain.Where(q => q.Right == OptionRight.Call).Sum(q => q.OpenInterest);
        double putOi = chain.Where(q => q.Right == OptionRight.Put).Sum(q => q.OpenInterest);
        double pcr = callOi > 0 ? putOi / callOi : 0;

        double em7Abs = spot * atm30 * Math.Sqrt(7 / 365.25);
        double em30Abs = spot * atm30 * Math.Sqrt(30 / 365.25);
        double em7Pct = spot > 0 ? em7Abs / spot : 0;
        double em30Pct = spot > 0 ? em30Abs / spot : 0;

        string regime = ClassifyRegime(atm30, slope);
        string signal = BuildRegimeSignal(atm30, slope, skew25d, pcr);
        double confidence = MathUtils.Clamp(25 + expiries.Count * 6 + Math.Log(1 + callOi + putOi), 5, 95);

        return new VolRegimeSnapshot(
            Asset: asset.ToUpperInvariant(),
            Spot: spot,
            FrontExpiry: frontExpiry,
            AtmIvFront: atmFront,
            AtmIv30D: atm30,
            AtmIv90D: atm90,
            TermSlope30To90: slope,
            Skew25D: skew25d,
            PutCallOpenInterestRatio: pcr,
            ExpectedMove7DAbs: em7Abs,
            ExpectedMove7DPct: em7Pct,
            ExpectedMove30DAbs: em30Abs,
            ExpectedMove30DPct: em30Pct,
            Regime: regime,
            Signal: signal,
            ConfidenceScore: confidence,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<StrategyRecommendationBoard> GetRecommendationsAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        string riskProfile = "balanced",
        CancellationToken ct = default)
    {
        string normalizedRisk = NormalizeRiskProfile(riskProfile);
        var regime = await GetRegimeAsync(asset, ct);
        var presets = await BuildPresetStrategiesAsync(asset, expiry, size, ct);
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        var calibration = BuildAssetModelCalibration(chain, expiry);

        if (presets.Count == 0)
        {
            return new StrategyRecommendationBoard(
                Asset: asset.ToUpperInvariant(),
                RiskProfile: normalizedRisk,
                Regime: regime,
                Recommendations: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        double fallbackSpot = ReferenceSpot(chain);
        var byQuote = chain.ToDictionary(q => q.Symbol, StringComparer.OrdinalIgnoreCase);
        var byModel = new Dictionary<string, OptionModelSnapshot>(StringComparer.OrdinalIgnoreCase);
        var recommendations = new List<StrategyRecommendation>();

        foreach (var strategy in presets)
        {
            double weightedEdge = 0;
            double totalQty = 0;
            double avgConfidence = 0;
            int confidenceLegs = 0;

            foreach (var leg in strategy.Legs)
            {
                if (!byQuote.TryGetValue(leg.Symbol, out var quote)) continue;
                if (!byModel.TryGetValue(leg.Symbol, out var snapshot))
                {
                    snapshot = BuildModelSnapshot(quote, fallbackSpot, calibration);
                    byModel[leg.Symbol] = snapshot;
                }

                double basePrice = Math.Max(leg.EntryPrice, 1e-9);
                double edge = (snapshot.FairComposite - leg.EntryPrice) / basePrice;
                if (leg.Direction == TradeDirection.Sell)
                    edge *= -1;

                weightedEdge += edge * Math.Abs(leg.Quantity);
                totalQty += Math.Abs(leg.Quantity);
                avgConfidence += snapshot.ConfidenceScore;
                confidenceLegs++;
            }

            if (totalQty <= 0) continue;
            double edgeScorePct = (weightedEdge / totalQty) * 100;
            double confidence = confidenceLegs > 0 ? avgConfidence / confidenceLegs : 1;
            double regimeFit = ComputeRegimeFit(strategy, regime, normalizedRisk);

            if (!PassRiskProfile(strategy, normalizedRisk)) continue;

            double score = MathUtils.Clamp(
                edgeScorePct * 0.62 +
                confidence * 0.23 +
                regimeFit * 0.15, -200, 200);

            recommendations.Add(new StrategyRecommendation(
                Name: strategy.Name,
                Score: score,
                EdgeScorePct: edgeScorePct,
                ConfidenceScore: confidence,
                RegimeFitScore: regimeFit,
                RiskLabel: ComputeRiskLabel(strategy),
                Thesis: BuildStrategyThesis(strategy, regime, edgeScorePct),
                Analysis: strategy));
        }

        var ordered = recommendations
            .OrderByDescending(r => r.Score)
            .Take(8)
            .ToList();

        return new StrategyRecommendationBoard(
            Asset: asset.ToUpperInvariant(),
            RiskProfile: normalizedRisk,
            Regime: regime,
            Recommendations: ordered,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<StrategyOptimizationBoard> OptimizeStrategiesAsync(
        string asset,
        DateTimeOffset? expiry = null,
        double size = 1,
        string riskProfile = "balanced",
        double targetDelta = 0,
        double targetVega = 0,
        double targetTheta = 0,
        CancellationToken ct = default)
    {
        string normalizedRisk = NormalizeRiskProfile(riskProfile);
        var recommendationBoard = await GetRecommendationsAsync(asset, expiry, size, normalizedRisk, ct);
        if (recommendationBoard.Recommendations.Count == 0)
        {
            return new StrategyOptimizationBoard(
                Asset: asset.ToUpperInvariant(),
                RiskProfile: normalizedRisk,
                TargetDelta: targetDelta,
                TargetVega: targetVega,
                TargetTheta: targetTheta,
                Entries: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        var entries = recommendationBoard.Recommendations
            .Select(rec =>
            {
                double multiplier = ComputeSuggestedSizeMultiplier(rec.Analysis.AggregateGreeks, targetDelta, targetVega, targetTheta);
                var projectedGreeks = rec.Analysis.AggregateGreeks.Scale(multiplier);
                double distanceScore = ComputeDistanceScore(projectedGreeks, targetDelta, targetVega, targetTheta);
                double score = MathUtils.Clamp(rec.Score * 0.55 + distanceScore * 0.45, -200, 200);
                string thesis = $"{rec.Thesis} Optimized size x{multiplier:F2} for target greeks.";
                return new StrategyOptimizationEntry(
                    Name: rec.Name,
                    Score: score,
                    DistanceScore: distanceScore,
                    SuggestedSizeMultiplier: multiplier,
                    ProjectedGreeks: projectedGreeks,
                    EdgeScorePct: rec.EdgeScorePct,
                    ConfidenceScore: rec.ConfidenceScore,
                    RegimeFitScore: rec.RegimeFitScore,
                    RiskLabel: rec.RiskLabel,
                    Thesis: thesis,
                    Analysis: rec.Analysis);
            })
            .OrderByDescending(e => e.Score)
            .Take(8)
            .ToList();

        return new StrategyOptimizationBoard(
            Asset: recommendationBoard.Asset,
            RiskProfile: normalizedRisk,
            TargetDelta: targetDelta,
            TargetVega: targetVega,
            TargetTheta: targetTheta,
            Entries: entries,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<GreeksExposureGrid> GetGreeksExposureGridAsync(
        string asset,
        DateTimeOffset? expiry = null,
        int maxExpiries = 6,
        int maxStrikes = 24,
        CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        if (expiry.HasValue)
            chain = chain.Where(q => q.Expiry.Date == expiry.Value.Date).ToList();

        if (chain.Count == 0)
        {
            return new GreeksExposureGrid(
                Asset: asset.ToUpperInvariant(),
                Spot: 0,
                Cells: [],
                TopHotspots: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        int safeMaxExpiries = Math.Clamp(maxExpiries, 1, 12);
        int safeMaxStrikes = Math.Clamp(maxStrikes, 8, 64);
        double spot = ReferenceSpot(chain);
        var expiries = chain
            .Select(q => q.Expiry)
            .Distinct()
            .OrderBy(e => e)
            .Take(safeMaxExpiries)
            .ToList();

        var strikeSet = chain
            .Where(q => expiries.Contains(q.Expiry))
            .GroupBy(q => q.Strike)
            .Select(g => new
            {
                Strike = g.Key,
                Weight = g.Sum(x =>
                    Math.Max(0, x.OpenInterest) +
                    Math.Max(0, x.Volume24h) * 0.35 +
                    Math.Max(0, x.Turnover24h) * 0.0002)
            })
            .OrderByDescending(x => x.Weight)
            .Take(safeMaxStrikes)
            .Select(x => x.Strike)
            .ToHashSet();

        if (spot > 0)
        {
            double atmStrike = chain
                .OrderBy(q => Math.Abs(q.Strike - spot))
                .Select(q => q.Strike)
                .First();
            strikeSet.Add(atmStrike);
        }

        var scoped = chain
            .Where(q => expiries.Contains(q.Expiry) && strikeSet.Contains(q.Strike))
            .ToList();

        var cells = scoped
            .GroupBy(q => new { q.Expiry, q.Strike })
            .Select(group =>
            {
                double openInterest = 0;
                double volume24h = 0;
                double deltaExposure = 0;
                double gammaExposure = 0;
                double vegaExposure = 0;
                double thetaExposure = 0;

                foreach (var quote in group)
                {
                    double oi = Math.Max(0, quote.OpenInterest);
                    double vol = Math.Max(0, quote.Volume24h);
                    double weight = Math.Max(1, oi + vol * 0.35);
                    openInterest += oi;
                    volume24h += vol;
                    deltaExposure += quote.Delta * weight;
                    gammaExposure += quote.Gamma * weight * Math.Max(spot, 1);
                    vegaExposure += quote.Vega * weight;
                    thetaExposure += quote.Theta * weight;
                }

                double distancePct = spot > 0 ? group.Key.Strike / spot - 1 : 0;
                double pinRisk = Math.Abs(gammaExposure) *
                                 Math.Log(1 + openInterest + volume24h * 0.5) /
                                 Math.Max(Math.Abs(distancePct), 0.015);
                int dte = Math.Max(0, (int)Math.Round((group.Key.Expiry - DateTimeOffset.UtcNow).TotalDays));

                return new GreeksExposureCell(
                    Expiry: group.Key.Expiry,
                    DaysToExpiry: dte,
                    Strike: group.Key.Strike,
                    DistanceToSpotPct: distancePct,
                    OpenInterest: openInterest,
                    Volume24h: volume24h,
                    DeltaExposure: deltaExposure,
                    GammaExposure: gammaExposure,
                    VegaExposure: vegaExposure,
                    ThetaExposure: thetaExposure,
                    PinRiskScore: pinRisk);
            })
            .OrderBy(c => c.Expiry)
            .ThenBy(c => c.Strike)
            .ToList();

        var topHotspots = cells
            .Where(c => c.OpenInterest > 0 || Math.Abs(c.GammaExposure) > 0)
            .OrderByDescending(c => c.PinRiskScore)
            .ThenBy(c => Math.Abs(c.DistanceToSpotPct))
            .Take(15)
            .Select(c => new GreeksExposureHotspot(
                Expiry: c.Expiry,
                DaysToExpiry: c.DaysToExpiry,
                Strike: c.Strike,
                DistanceToSpotPct: c.DistanceToSpotPct,
                OpenInterest: c.OpenInterest,
                GammaExposure: c.GammaExposure,
                VegaExposure: c.VegaExposure,
                PinRiskScore: c.PinRiskScore))
            .ToList();

        return new GreeksExposureGrid(
            Asset: asset.ToUpperInvariant(),
            Spot: spot,
            Cells: cells,
            TopHotspots: topHotspots,
            Timestamp: DateTimeOffset.UtcNow);
    }

    public async Task<ArbitrageScanResult> GetArbitrageScanAsync(
        string asset,
        DateTimeOffset? expiry = null,
        int limit = 120,
        CancellationToken ct = default)
    {
        var chain = await _marketData.GetOptionChainAsync(asset, ct);
        if (expiry.HasValue)
            chain = chain.Where(q => q.Expiry.Date == expiry.Value.Date).ToList();

        int safeLimit = Math.Clamp(limit, 20, 400);
        if (chain.Count == 0)
        {
            return new ArbitrageScanResult(
                Asset: asset.ToUpperInvariant(),
                Expiry: expiry,
                Count: 0,
                Anomalies: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        double spot = ReferenceSpot(chain);
        var bySymbol = chain.ToDictionary(q => q.Symbol, StringComparer.OrdinalIgnoreCase);
        var anomalies = new List<ArbitrageAnomaly>();
        const double parityThreshold = 0.05;
        const double convexityThreshold = 0.03;
        const double monotonicityThreshold = 0.02;
        const double verticalBoundThreshold = 0.03;
        const double calendarThreshold = 0.03;

        foreach (var byExpiry in chain.GroupBy(q => q.Expiry.Date))
        {
            var expiryList = byExpiry.ToList();
            var calls = expiryList.Where(q => q.Right == OptionRight.Call).ToList();
            var puts = expiryList.Where(q => q.Right == OptionRight.Put).ToList();
            var putsByStrike = puts
                .GroupBy(p => p.Strike)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Turnover24h).First());

            foreach (var call in calls)
            {
                if (!putsByStrike.TryGetValue(call.Strike, out var put)) continue;
                double cMid = EffectiveMid(call);
                double pMid = EffectiveMid(put);
                if (cMid <= 0 || pMid <= 0) continue;

                double t = Math.Max((call.Expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 24.0 / 365.25);
                double rhs = spot - call.Strike * Math.Exp(-DefaultRiskFreeRate * t);
                double lhs = cMid - pMid;
                double dev = lhs - rhs;
                double norm = Math.Abs(dev) / Math.Max(cMid + pMid, 1e-9);
                if (norm < parityThreshold) continue;

                anomalies.Add(new ArbitrageAnomaly(
                    Type: "Put-Call Parity",
                    Expiry: call.Expiry,
                    Right: OptionRight.Call,
                    SymbolA: call.Symbol,
                    SymbolB: put.Symbol,
                    SymbolC: null,
                    StrikeA: call.Strike,
                    StrikeB: put.Strike,
                    StrikeC: 0,
                    Metric: norm,
                    Threshold: parityThreshold,
                    SeverityScore: norm * 100,
                    Description: $"Parity deviation {dev:F4} ({norm:P2})"));
            }

            foreach (OptionRight right in Enum.GetValues<OptionRight>())
            {
                var ordered = expiryList
                    .Where(q => q.Right == right)
                    .Where(q => EffectiveMid(q) > 0)
                    .OrderBy(q => q.Strike)
                    .ToList();

                for (int i = 1; i < ordered.Count; i++)
                {
                    var left = ordered[i - 1];
                    var rightQ = ordered[i];
                    double pLeft = EffectiveMid(left);
                    double pRight = EffectiveMid(rightQ);
                    if (pLeft <= 0 || pRight <= 0) continue;

                    bool strikeMonotonicityViolation = right == OptionRight.Call
                        ? pRight > pLeft
                        : pRight < pLeft;
                    if (strikeMonotonicityViolation)
                    {
                        double violation = right == OptionRight.Call ? pRight - pLeft : pLeft - pRight;
                        double denom = Math.Max(Math.Max(pLeft, pRight), 1e-9);
                        double norm = violation / denom;
                        if (norm >= monotonicityThreshold)
                        {
                            anomalies.Add(new ArbitrageAnomaly(
                                Type: right == OptionRight.Call ? "Call Monotonicity" : "Put Monotonicity",
                                Expiry: rightQ.Expiry,
                                Right: right,
                                SymbolA: left.Symbol,
                                SymbolB: rightQ.Symbol,
                                SymbolC: null,
                                StrikeA: left.Strike,
                                StrikeB: rightQ.Strike,
                                StrikeC: 0,
                                Metric: norm,
                                Threshold: monotonicityThreshold,
                                SeverityScore: norm * 100,
                                Description: $"Strike monotonicity violated ({norm:P2})"));
                        }
                    }

                    double t = Math.Max((rightQ.Expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 24.0 / 365.25);
                    double maxVertical = Math.Max((rightQ.Strike - left.Strike) * Math.Exp(-DefaultRiskFreeRate * t), 1e-9);
                    double spread = right == OptionRight.Call ? pLeft - pRight : pRight - pLeft;
                    if (spread < 0)
                    {
                        double norm = Math.Abs(spread) / Math.Max(Math.Max(pLeft, pRight), 1e-9);
                        if (norm >= verticalBoundThreshold)
                        {
                            anomalies.Add(new ArbitrageAnomaly(
                                Type: "Vertical Bound",
                                Expiry: rightQ.Expiry,
                                Right: right,
                                SymbolA: left.Symbol,
                                SymbolB: rightQ.Symbol,
                                SymbolC: null,
                                StrikeA: left.Strike,
                                StrikeB: rightQ.Strike,
                                StrikeC: 0,
                                Metric: norm,
                                Threshold: verticalBoundThreshold,
                                SeverityScore: norm * 100,
                                Description: $"Negative vertical spread value ({spread:F4}, {norm:P2})"));
                        }
                    }
                    else if (spread > maxVertical)
                    {
                        double excess = spread - maxVertical;
                        double norm = excess / Math.Max(maxVertical, 1e-9);
                        if (norm >= verticalBoundThreshold)
                        {
                            anomalies.Add(new ArbitrageAnomaly(
                                Type: "Vertical Bound",
                                Expiry: rightQ.Expiry,
                                Right: right,
                                SymbolA: left.Symbol,
                                SymbolB: rightQ.Symbol,
                                SymbolC: null,
                                StrikeA: left.Strike,
                                StrikeB: rightQ.Strike,
                                StrikeC: 0,
                                Metric: norm,
                                Threshold: verticalBoundThreshold,
                                SeverityScore: norm * 100,
                                Description: $"Vertical spread exceeds bound ({spread:F4} > {maxVertical:F4}, {norm:P2})"));
                        }
                    }
                }

                for (int i = 1; i < ordered.Count - 1; i++)
                {
                    var left = ordered[i - 1];
                    var mid = ordered[i];
                    var rightQ = ordered[i + 1];
                    double p1 = EffectiveMid(left);
                    double p2 = EffectiveMid(mid);
                    double p3 = EffectiveMid(rightQ);
                    double convexity = p1 - 2 * p2 + p3;
                    double norm = Math.Abs(convexity) / Math.Max(p2, 1e-9);
                    if (convexity >= 0 || norm < convexityThreshold) continue;

                    anomalies.Add(new ArbitrageAnomaly(
                        Type: "Butterfly Convexity",
                        Expiry: mid.Expiry,
                        Right: right,
                        SymbolA: left.Symbol,
                        SymbolB: mid.Symbol,
                        SymbolC: rightQ.Symbol,
                        StrikeA: left.Strike,
                        StrikeB: mid.Strike,
                        StrikeC: rightQ.Strike,
                        Metric: norm,
                        Threshold: convexityThreshold,
                        SeverityScore: norm * 100,
                        Description: $"Negative convexity detected ({convexity:F4}, {norm:P2})"));
                }
            }
        }

        var calendarGroups = chain
            .Where(q => EffectiveMid(q) > 0)
            .GroupBy(q => new { q.Right, q.Strike })
            .Where(g => g.Count() >= 2);

        foreach (var group in calendarGroups)
        {
            var ordered = group
                .OrderBy(q => q.Expiry)
                .ThenByDescending(q => q.Turnover24h)
                .ToList();

            for (int i = 1; i < ordered.Count; i++)
            {
                var near = ordered[i - 1];
                var far = ordered[i];
                if (far.Expiry <= near.Expiry) continue;
                double nearMid = EffectiveMid(near);
                double farMid = EffectiveMid(far);
                if (nearMid <= 0 || farMid <= 0 || farMid >= nearMid) continue;

                double norm = (nearMid - farMid) / Math.Max(nearMid, 1e-9);
                if (norm < calendarThreshold) continue;

                anomalies.Add(new ArbitrageAnomaly(
                    Type: "Calendar Monotonicity",
                    Expiry: far.Expiry,
                    Right: group.Key.Right,
                    SymbolA: near.Symbol,
                    SymbolB: far.Symbol,
                    SymbolC: null,
                    StrikeA: near.Strike,
                    StrikeB: far.Strike,
                    StrikeC: 0,
                    Metric: norm,
                    Threshold: calendarThreshold,
                    SeverityScore: norm * 100,
                    Description: $"Longer-dated option cheaper than nearer expiry ({norm:P2})"));
            }
        }

        var enriched = anomalies
            .Select(anomaly =>
            {
                var (liquidity, costPct, tradeability) = ComputeExecutionMetrics(anomaly, bySymbol);
                double executionFactor = 0.4 + 0.6 * (tradeability / 100.0);
                return anomaly with
                {
                    LiquidityScore = liquidity,
                    EstimatedCostPct = costPct,
                    TradeabilityScore = tradeability,
                    SeverityScore = anomaly.SeverityScore * executionFactor
                };
            })
            .ToList();

        var ranked = enriched
            .OrderByDescending(a => a.SeverityScore)
            .Take(safeLimit)
            .ToList();

        return new ArbitrageScanResult(
            Asset: asset.ToUpperInvariant(),
            Expiry: expiry,
            Count: ranked.Count,
            Anomalies: ranked,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static StrategyAnalysisResult AnalyzeFromChain(
        string name,
        string asset,
        IReadOnlyList<LiveOptionQuote> chain,
        IReadOnlyList<StrategyLegDefinition> legs,
        double shockRangePct = 0.35,
        int gridPoints = 121)
    {
        if (legs.Count == 0) throw new ArgumentException("At least one leg is required.");
        if (chain.Count == 0) throw new InvalidOperationException("No option quotes available.");

        var bySymbol = chain.ToDictionary(q => q.Symbol, StringComparer.OrdinalIgnoreCase);
        var analyzedLegs = new List<StrategyLegAnalysis>();

        double netPremium = 0;
        GreeksResult aggregateGreeks = GreeksResult.Zero;
        double referenceSpot = 0;

        foreach (var leg in legs)
        {
            if (!bySymbol.TryGetValue(leg.Symbol, out var quote))
                throw new ArgumentException($"Unknown option symbol: {leg.Symbol}");
            if (!double.IsFinite(leg.Quantity) || leg.Quantity <= 0)
                throw new ArgumentException($"Quantity must be > 0 for symbol {leg.Symbol}");

            double quantity = leg.Quantity;
            double entryPrice = leg.Direction == TradeDirection.Buy
                ? BestPriceForBuy(quote)
                : BestPriceForSell(quote);
            if (entryPrice <= 0) throw new InvalidOperationException($"No executable price for symbol {leg.Symbol}");

            int directionSign = leg.Direction == TradeDirection.Buy ? 1 : -1;
            int cashSign = leg.Direction == TradeDirection.Buy ? -1 : 1;
            netPremium += cashSign * entryPrice * quantity;

            var legGreeks = new GreeksResult(
                Delta: directionSign * quantity * quote.Delta,
                Gamma: directionSign * quantity * quote.Gamma,
                Vega: directionSign * quantity * quote.Vega,
                Theta: directionSign * quantity * quote.Theta,
                Vanna: 0,
                Volga: 0,
                Charm: 0,
                Rho: 0);
            aggregateGreeks += legGreeks;

            analyzedLegs.Add(new StrategyLegAnalysis(
                Symbol: quote.Symbol,
                Direction: leg.Direction,
                Quantity: quantity,
                Expiry: quote.Expiry,
                Strike: quote.Strike,
                Right: quote.Right,
                EntryPrice: entryPrice,
                MarkPrice: quote.Mark,
                MarkIv: quote.MarkIv,
                Greeks: legGreeks));

            if (quote.UnderlyingPrice > 0) referenceSpot = quote.UnderlyingPrice;
        }

        if (referenceSpot <= 0) referenceSpot = ReferenceSpot(chain);
        double shock = MathUtils.Clamp(shockRangePct, 0.05, 0.95);
        int points = Math.Clamp(gridPoints, 41, 401);

        double spotMin = referenceSpot * (1 - shock);
        double spotMax = referenceSpot * (1 + shock);
        var payoff = new List<PayoffPoint>(points);

        for (int i = 0; i < points; i++)
        {
            double x = (double)i / (points - 1);
            double spot = spotMin + x * (spotMax - spotMin);
            double pnl = 0;
            foreach (var leg in analyzedLegs)
            {
                double intrinsic = leg.Right == OptionRight.Call
                    ? Math.Max(spot - leg.Strike, 0)
                    : Math.Max(leg.Strike - spot, 0);

                pnl += leg.Direction == TradeDirection.Buy
                    ? (intrinsic - leg.EntryPrice) * leg.Quantity
                    : (leg.EntryPrice - intrinsic) * leg.Quantity;
            }
            payoff.Add(new PayoffPoint(spot, pnl));
        }

        double maxProfit = payoff.Max(p => p.Pnl);
        double maxLoss = payoff.Min(p => p.Pnl);
        var breakevens = ComputeBreakevens(payoff);

        return new StrategyAnalysisResult(
            Name: name,
            Asset: asset.ToUpperInvariant(),
            UnderlyingPrice: referenceSpot,
            NetPremium: netPremium,
            MaxProfit: maxProfit,
            MaxLoss: maxLoss,
            Breakevens: breakevens,
            AggregateGreeks: aggregateGreeks,
            Legs: analyzedLegs,
            PayoffCurve: payoff,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static void TryAddPreset(
        IList<StrategyAnalysisResult> sink,
        string name,
        string asset,
        IReadOnlyList<LiveOptionQuote> chain,
        IReadOnlyList<StrategyLegDefinition> legs)
    {
        if (legs.Any(l => string.IsNullOrWhiteSpace(l.Symbol) || l.Quantity <= 0)) return;
        try
        {
            sink.Add(AnalyzeFromChain(name, asset, chain, legs));
        }
        catch
        {
            // Ignore invalid presets for sparse books.
        }
    }

    private static LiveOptionQuote? ClosestByStrike(IReadOnlyList<LiveOptionQuote> quotes, double targetStrike)
    {
        return quotes
            .Where(q => q.Mark > 0 || q.Bid > 0 || q.Ask > 0)
            .OrderBy(q => Math.Abs(q.Strike - targetStrike))
            .ThenByDescending(q => q.Turnover24h)
            .FirstOrDefault();
    }

    private static double ReferenceSpot(IReadOnlyList<LiveOptionQuote> quotes)
    {
        var spots = quotes
            .Select(q => q.UnderlyingPrice)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();
        if (spots.Count == 0) return 0;
        int mid = spots.Count / 2;
        return spots.Count % 2 == 0 ? (spots[mid - 1] + spots[mid]) / 2.0 : spots[mid];
    }

    private static double ComputeAtmIv(IReadOnlyList<LiveOptionQuote> quotes, double spot)
    {
        var ivQuotes = quotes.Where(q => q.MarkIv > 0).ToList();
        if (ivQuotes.Count == 0) return 0;

        double atmStrike = ivQuotes
            .OrderBy(q => Math.Abs(q.Strike - spot))
            .ThenByDescending(q => q.Turnover24h)
            .First()
            .Strike;

        var atm = ivQuotes.Where(q => Math.Abs(q.Strike - atmStrike) < 1e-10).ToList();
        return atm.Count > 0 ? atm.Average(q => q.MarkIv) : ivQuotes.First().MarkIv;
    }

    private static AssetModelCalibration BuildAssetModelCalibration(
        IReadOnlyList<LiveOptionQuote> chain,
        DateTimeOffset? expiry = null)
    {
        string asset = chain.Count > 0 ? chain[0].Asset : "UNKNOWN";
        if (chain.Count == 0)
        {
            var fallbackHeston = AssetHestonParams(asset);
            var fallbackSabr = AssetSabrParams(asset);
            return new AssetModelCalibration(
                Asset: asset,
                Expiry: expiry,
                Spot: 0,
                AtmIv30D: 0,
                Skew25D: 0,
                TermSlope30To90: 0,
                HestonParams: fallbackHeston,
                SabrParams: fallbackSabr,
                ConfidenceScore: 5,
                FitMetrics: [
                    new ModelFitMetric("Black-Scholes", 0, 0, 0),
                    new ModelFitMetric("Heston", 0, 0, 0),
                    new ModelFitMetric("SABR", 0, 0, 0)
                ]);
        }

        var scoped = expiry.HasValue
            ? chain.Where(q => q.Expiry.Date == expiry.Value.Date).ToList()
            : chain.ToList();
        if (scoped.Count == 0) scoped = chain.ToList();

        double spot = ReferenceSpot(scoped);
        if (spot <= 0) spot = ReferenceSpot(chain);
        if (spot <= 0) spot = 1;

        double atm30 = AtmForTargetDays(chain, spot, 30);
        double atm90 = AtmForTargetDays(chain, spot, 90);
        double slope = atm90 - atm30;

        DateTimeOffset frontExpiry = scoped
            .Select(q => q.Expiry)
            .Distinct()
            .OrderBy(e => Math.Abs((e - DateTimeOffset.UtcNow).TotalDays))
            .FirstOrDefault();
        if (frontExpiry == default) frontExpiry = scoped[0].Expiry;
        var front = scoped.Where(q => q.Expiry.Date == frontExpiry.Date).ToList();
        double skew25 = ComputeRiskReversal(front);

        var baseHeston = AssetHestonParams(asset);
        var baseSabr = AssetSabrParams(asset);
        double atmRef = atm30 > 0 ? atm30 : Math.Max(0.2, baseHeston.Theta);

        var heston = new HestonParams(
            Kappa: MathUtils.Clamp(baseHeston.Kappa + Math.Abs(slope) * 12, 1.2, 7.0),
            Theta: MathUtils.Clamp(atmRef * (1 + Math.Abs(slope) * 0.7), 0.15, 1.6),
            Xi: MathUtils.Clamp(baseHeston.Xi + Math.Abs(skew25) * 8 + Math.Max(0, (atmRef - 0.55) * 1.2), 0.2, 2.2),
            Rho: MathUtils.Clamp(baseHeston.Rho + skew25 * 2.4, -0.95, 0.35));

        var sabr = new SabrParams(
            Alpha: MathUtils.Clamp(atmRef * 0.85, 0.05, 3.0),
            Beta: baseSabr.Beta,
            Rho: MathUtils.Clamp(baseSabr.Rho + skew25 * 2.1, -0.95, 0.5),
            Nu: MathUtils.Clamp(baseSabr.Nu + Math.Abs(skew25) * 6 + Math.Abs(slope) * 2, 0.1, 2.8));

        var fitMetrics = BuildModelFitMetrics(scoped, spot, heston, sabr);
        int samples = fitMetrics.Select(m => m.SampleCount).DefaultIfEmpty(0).Max();
        double meanMae = fitMetrics.Count > 0 ? fitMetrics.Average(m => m.MeanAbsErrorPct) : 60;
        double dispersion = fitMetrics.Count > 0
            ? Math.Abs(fitMetrics.Max(m => m.MeanAbsErrorPct) - fitMetrics.Min(m => m.MeanAbsErrorPct))
            : 50;
        double confidence = MathUtils.Clamp(95 - meanMae * 0.7 - dispersion * 0.4 + Math.Log(1 + samples) * 8, 5, 99);

        return new AssetModelCalibration(
            Asset: asset.ToUpperInvariant(),
            Expiry: expiry,
            Spot: spot,
            AtmIv30D: atm30,
            Skew25D: skew25,
            TermSlope30To90: slope,
            HestonParams: heston,
            SabrParams: sabr,
            ConfidenceScore: confidence,
            FitMetrics: fitMetrics);
    }

    private static IReadOnlyList<ModelFitMetric> BuildModelFitMetrics(
        IReadOnlyList<LiveOptionQuote> quotes,
        double fallbackSpot,
        HestonParams hestonParams,
        SabrParams sabrParams)
    {
        var sample = quotes
            .Where(q => EffectiveMid(q) > 0 && q.MarkIv > 0)
            .OrderByDescending(q => q.Turnover24h)
            .Take(220)
            .ToList();
        if (sample.Count == 0)
            return [
                new ModelFitMetric("Black-Scholes", 0, 0, 0),
                new ModelFitMetric("Heston", 0, 0, 0),
                new ModelFitMetric("SABR", 0, 0, 0)
            ];

        var bsErrors = new List<double>(sample.Count);
        var hestonErrors = new List<double>(sample.Count);
        var sabrErrors = new List<double>(sample.Count);

        foreach (var quote in sample)
        {
            double spot = quote.UnderlyingPrice > 0 ? quote.UnderlyingPrice : Math.Max(fallbackSpot, 1);
            double t = Math.Max((quote.Expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 24.0 / 365.25);
            double sigma = MathUtils.Clamp(quote.MarkIv, 0.05, 3.5);
            OptionType optionType = quote.Right == OptionRight.Call ? OptionType.Call : OptionType.Put;
            double mid = EffectiveMid(quote);
            if (mid <= 0) continue;

            double fairBs = SafePrice(() => BlackScholes.Price(spot, quote.Strike, sigma, t, DefaultRiskFreeRate, optionType));
            double fairHeston = SafePrice(() => HestonModel.Price(
                spot, quote.Strike, DefaultRiskFreeRate, sigma, t, optionType, hestonParams));
            double fairSabr = SafePrice(() => SabrModel.Price(
                spot, quote.Strike, DefaultRiskFreeRate, sigma, t, optionType, sabrParams));

            if (fairBs > 0) bsErrors.Add(Math.Abs((fairBs - mid) / mid) * 100);
            if (fairHeston > 0) hestonErrors.Add(Math.Abs((fairHeston - mid) / mid) * 100);
            if (fairSabr > 0) sabrErrors.Add(Math.Abs((fairSabr - mid) / mid) * 100);
        }

        return [
            BuildModelFitMetric("Black-Scholes", bsErrors),
            BuildModelFitMetric("Heston", hestonErrors),
            BuildModelFitMetric("SABR", sabrErrors)
        ];
    }

    private static ModelFitMetric BuildModelFitMetric(string model, IReadOnlyList<double> errors)
    {
        if (errors.Count == 0)
            return new ModelFitMetric(model, 0, 0, 0);
        double mae = errors.Average();
        double rmse = Math.Sqrt(errors.Average(e => e * e));
        return new ModelFitMetric(
            Model: model,
            MeanAbsErrorPct: mae,
            RootMeanSquareErrorPct: rmse,
            SampleCount: errors.Count);
    }

    private static OptionModelSnapshot BuildModelSnapshot(
        LiveOptionQuote quote,
        double fallbackSpot,
        AssetModelCalibration? calibration = null)
    {
        double spot = quote.UnderlyingPrice > 0 ? quote.UnderlyingPrice : Math.Max(fallbackSpot, 1);
        double t = Math.Max((quote.Expiry - DateTimeOffset.UtcNow).TotalDays / 365.25, 1.0 / 24.0 / 365.25);
        double sigma = MathUtils.Clamp(quote.MarkIv > 0 ? quote.MarkIv : 0.65, 0.05, 3.5);
        OptionType optionType = quote.Right == OptionRight.Call ? OptionType.Call : OptionType.Put;
        var hestonParams = calibration?.HestonParams ?? AssetHestonParams(quote.Asset);
        var sabrParams = calibration?.SabrParams ?? AssetSabrParams(quote.Asset);

        double fairBs = SafePrice(() => BlackScholes.Price(spot, quote.Strike, sigma, t, DefaultRiskFreeRate, optionType));
        double fairHeston = SafePrice(() => HestonModel.Price(
            spot, quote.Strike, DefaultRiskFreeRate, sigma, t, optionType, hestonParams));
        double fairSabr = SafePrice(() => SabrModel.Price(
            spot, quote.Strike, DefaultRiskFreeRate, sigma, t, optionType, sabrParams));

        var validModelPrices = new[] { fairBs, fairHeston, fairSabr }
            .Where(v => double.IsFinite(v) && v > 0)
            .ToList();
        double fairComposite = validModelPrices.Count > 0 ? validModelPrices.Average() : fairBs;
        double modelDispersionPct = fairComposite > 0 && validModelPrices.Count > 1
            ? (validModelPrices.Max() - validModelPrices.Min()) / fairComposite
            : 0;

        double mid = EffectiveMid(quote);
        double rawEdgeVsMid = mid > 0 ? (fairComposite - mid) / mid : 0;
        double rawEdgeVsMark = quote.Mark > 0 ? (fairComposite - quote.Mark) / quote.Mark : 0;
        double edgeVsMidPct = MathUtils.Clamp(rawEdgeVsMid, -1.5, 1.5);
        double edgeVsMarkPct = MathUtils.Clamp(rawEdgeVsMark, -1.5, 1.5);
        double spreadPct = quote.Ask > 0 && quote.Bid > 0 && mid > 0
            ? MathUtils.Clamp((quote.Ask - quote.Bid) / mid, 0, 5)
            : 2.0;

        double d2 = BlackScholes.D2(spot, quote.Strike, DefaultRiskFreeRate, sigma, t);
        double probItm = optionType == OptionType.Call ? MathUtils.NormalCdf(d2) : MathUtils.NormalCdf(-d2);
        double z = Math.Abs(Math.Log(Math.Max(quote.Strike, 1e-9) / Math.Max(spot, 1e-9))) / (sigma * Math.Sqrt(t));
        double probTouchApprox = MathUtils.Clamp(2 * (1 - MathUtils.NormalCdf(z)), 0, 1);

        double impliedMoveAbs = spot * sigma * Math.Sqrt(t);
        double impliedMovePct = spot > 0 ? impliedMoveAbs / spot : 0;
        var greeks = BlackScholes.AllGreeks(spot, quote.Strike, DefaultRiskFreeRate, sigma, t, optionType);

        double liquidityScore = Math.Log(1 +
            Math.Max(0, quote.OpenInterest) +
            Math.Max(0, quote.Volume24h) +
            Math.Max(0, quote.Turnover24h / Math.Max(spot, 1)));

        double confidenceScore = ComputeConfidenceScore(edgeVsMidPct, modelDispersionPct, liquidityScore, spreadPct);

        return new OptionModelSnapshot(
            Symbol: quote.Symbol,
            Asset: quote.Asset,
            Expiry: quote.Expiry,
            Strike: quote.Strike,
            Right: quote.Right,
            Spot: spot,
            TimeToExpiryYears: t,
            Bid: quote.Bid,
            Ask: quote.Ask,
            Mid: mid,
            Mark: quote.Mark,
            MarkIv: sigma,
            FairBs: fairBs,
            FairHeston: fairHeston,
            FairSabr: fairSabr,
            FairComposite: fairComposite,
            ModelDispersionPct: modelDispersionPct,
            EdgeVsMidPct: edgeVsMidPct,
            EdgeVsMarkPct: edgeVsMarkPct,
            ImpliedMoveAbs: impliedMoveAbs,
            ImpliedMovePct: impliedMovePct,
            ProbItm: probItm,
            ProbTouchApprox: probTouchApprox,
            LiquidityScore: liquidityScore,
            ConfidenceScore: confidenceScore,
            Signal: ComputeSignal(edgeVsMidPct, confidenceScore, spreadPct),
            Greeks: greeks,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static double AtmForTargetDays(IReadOnlyList<LiveOptionQuote> chain, double spot, double targetDays)
    {
        var byExpiry = chain
            .GroupBy(q => q.Expiry.Date)
            .Select(g =>
            {
                var expiry = g.First().Expiry;
                int days = Math.Max(0, (int)Math.Round((expiry - DateTimeOffset.UtcNow).TotalDays));
                var list = g.ToList();
                double atmIv = ComputeAtmIv(list, spot);
                return new { expiry, days, atmIv };
            })
            .Where(x => x.atmIv > 0)
            .OrderBy(x => Math.Abs(x.days - targetDays))
            .ToList();

        if (byExpiry.Count == 0) return 0;
        return byExpiry[0].atmIv;
    }

    private static string ClassifyRegime(double atm30, double slope)
    {
        if (atm30 >= 0.9) return "Crisis Vol";
        if (atm30 >= 0.65) return slope < -0.03 ? "High Vol Backwardation" : "High Vol";
        if (atm30 <= 0.35) return slope > 0.02 ? "Low Vol Contango" : "Low Vol";
        return slope > 0.03 ? "Mid Vol Contango" : slope < -0.03 ? "Mid Vol Backwardation" : "Mid Vol";
    }

    private static string BuildRegimeSignal(double atm30, double slope, double skew25d, double pcr)
    {
        if (atm30 <= 0.42 && slope >= 0) return "Long gamma / long vol candidates";
        if (atm30 >= 0.8 && slope < 0) return "Short premium carry candidates";
        if (skew25d < -0.05 && pcr > 0.9) return "Downside fear: puts expensive";
        if (skew25d > 0.03) return "Upside skew rich: call overwriting setups";
        return "Neutral carry with selective edges";
    }

    private static string NormalizeRiskProfile(string riskProfile)
    {
        string normalized = (riskProfile ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "conservative" => "conservative",
            "aggressive" => "aggressive",
            _ => "balanced"
        };
    }

    private static bool PassRiskProfile(StrategyAnalysisResult strategy, string riskProfile)
    {
        double loss = Math.Abs(strategy.MaxLoss);
        double premium = Math.Abs(strategy.NetPremium);
        double vega = Math.Abs(strategy.AggregateGreeks.Vega);

        return riskProfile switch
        {
            "conservative" => loss <= Math.Max(1500, premium * 3) && vega <= 120,
            "aggressive" => true,
            _ => loss <= Math.Max(4000, premium * 8) && vega <= 280
        };
    }

    private static double ComputeSuggestedSizeMultiplier(
        GreeksResult greeks,
        double targetDelta,
        double targetVega,
        double targetTheta)
    {
        var factors = new List<double>(3);
        if (Math.Abs(targetDelta) > 1e-9 && Math.Abs(greeks.Delta) > 1e-9)
            factors.Add(Math.Abs(targetDelta / greeks.Delta));
        if (Math.Abs(targetVega) > 1e-9 && Math.Abs(greeks.Vega) > 1e-9)
            factors.Add(Math.Abs(targetVega / greeks.Vega));
        if (Math.Abs(targetTheta) > 1e-9 && Math.Abs(greeks.Theta) > 1e-9)
            factors.Add(Math.Abs(targetTheta / greeks.Theta));

        if (factors.Count == 0) return 1;
        var ordered = factors.OrderBy(v => v).ToList();
        double median = ordered[ordered.Count / 2];
        return MathUtils.Clamp(median, 0.2, 8.0);
    }

    private static double ComputeDistanceScore(
        GreeksResult projectedGreeks,
        double targetDelta,
        double targetVega,
        double targetTheta)
    {
        double deltaDist = Math.Abs(projectedGreeks.Delta - targetDelta) / Math.Max(0.25, Math.Abs(targetDelta) + 0.25);
        double vegaDist = Math.Abs(projectedGreeks.Vega - targetVega) / Math.Max(25, Math.Abs(targetVega) + 25);
        double thetaDist = Math.Abs(projectedGreeks.Theta - targetTheta) / Math.Max(8, Math.Abs(targetTheta) + 8);
        double rawDistance = deltaDist + vegaDist + thetaDist;
        return MathUtils.Clamp(100 / (1 + rawDistance * 1.35), 1, 99);
    }

    private static (double LiquidityScore, double EstimatedCostPct, double TradeabilityScore) ComputeExecutionMetrics(
        ArbitrageAnomaly anomaly,
        IReadOnlyDictionary<string, LiveOptionQuote> bySymbol)
    {
        var legs = new List<LiveOptionQuote>(3);
        TryAddQuoteBySymbol(bySymbol, legs, anomaly.SymbolA);
        TryAddQuoteBySymbol(bySymbol, legs, anomaly.SymbolB);
        TryAddQuoteBySymbol(bySymbol, legs, anomaly.SymbolC);
        if (legs.Count == 0)
            return (0, 0.9, 1);

        double avgLiquidityRaw = legs.Average(q => Math.Log(1 +
            Math.Max(0, q.OpenInterest) +
            Math.Max(0, q.Volume24h) +
            Math.Max(0, q.Turnover24h)));
        double liquidityScore = MathUtils.Clamp(avgLiquidityRaw * 7.5, 1, 99);

        var spreads = legs
            .Select(q =>
            {
                double mid = EffectiveMid(q);
                if (q.Bid <= 0 || q.Ask <= 0 || mid <= 0) return 0.9;
                return MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 1.5);
            })
            .ToList();
        double estimatedCostPct = spreads.Count > 0 ? spreads.Average() : 0.9;
        double spreadPenalty = MathUtils.Clamp(estimatedCostPct * 100, 0, 100);
        double tradeability = MathUtils.Clamp(liquidityScore * 0.78 + (100 - spreadPenalty) * 0.22, 1, 99);
        return (liquidityScore, estimatedCostPct, tradeability);
    }

    private static void TryAddQuoteBySymbol(
        IReadOnlyDictionary<string, LiveOptionQuote> bySymbol,
        IList<LiveOptionQuote> sink,
        string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return;
        if (bySymbol.TryGetValue(symbol, out var quote))
            sink.Add(quote);
    }

    private static double ComputeRegimeFit(StrategyAnalysisResult strategy, VolRegimeSnapshot regime, string riskProfile)
    {
        string name = strategy.Name.ToLowerInvariant();
        double score = 45;

        if (regime.AtmIv30D <= 0.45 && (name.Contains("straddle") || name.Contains("strangle") || name.Contains("calendar")))
            score += 25;
        if (regime.AtmIv30D >= 0.75 && (name.Contains("condor") || name.Contains("spread")))
            score += 20;
        if (regime.Skew25D < -0.04 && (name.Contains("risk reversal") || name.Contains("put")))
            score += 16;
        if (Math.Abs(regime.TermSlope30To90) < 0.015 && name.Contains("calendar"))
            score -= 12;

        score += riskProfile switch
        {
            "conservative" when name.Contains("condor") || name.Contains("spread") => 10,
            "aggressive" when name.Contains("straddle") || name.Contains("strangle") => 10,
            _ => 0
        };

        return MathUtils.Clamp(score, 1, 99);
    }

    private static string ComputeRiskLabel(StrategyAnalysisResult strategy)
    {
        double maxLoss = Math.Abs(strategy.MaxLoss);
        double vega = Math.Abs(strategy.AggregateGreeks.Vega);
        double gamma = Math.Abs(strategy.AggregateGreeks.Gamma);

        if (maxLoss > 9000 || vega > 300 || gamma > 0.02) return "High";
        if (maxLoss > 3000 || vega > 120 || gamma > 0.008) return "Medium";
        return "Low";
    }

    private static string BuildStrategyThesis(StrategyAnalysisResult strategy, VolRegimeSnapshot regime, double edgeScorePct)
    {
        string dir = edgeScorePct >= 0 ? "positive" : "negative";
        return $"{strategy.Name}: {dir} model edge, regime={regime.Regime}, skew={regime.Skew25D:P1}, slope={regime.TermSlope30To90:P1}.";
    }

    private static string ExtractAsset(string symbol)
    {
        string[] parts = symbol.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException("invalid symbol format");
        return parts[0].ToUpperInvariant();
    }

    private static double EffectiveMid(LiveOptionQuote quote)
    {
        if (quote.Mid > 0) return quote.Mid;
        if (quote.Bid > 0 && quote.Ask > 0) return (quote.Bid + quote.Ask) / 2.0;
        if (quote.Mark > 0) return quote.Mark;
        if (quote.Bid > 0) return quote.Bid;
        return quote.Ask;
    }

    private static HestonParams AssetHestonParams(string asset)
    {
        return asset.ToUpperInvariant() switch
        {
            "ETH" => HestonParams.CryptoEth,
            "SOL" => HestonParams.CryptoSol,
            _ => HestonParams.CryptoBtc
        };
    }

    private static SabrParams AssetSabrParams(string asset)
    {
        return asset.ToUpperInvariant() switch
        {
            "ETH" => SabrParams.CryptoEth,
            "SOL" => new SabrParams(0.70, 0.5, -0.22, 0.90),
            _ => SabrParams.CryptoBtc
        };
    }

    private static double SafePrice(Func<double> compute)
    {
        try
        {
            double value = compute();
            if (!double.IsFinite(value) || value <= 0) return 0;
            return value;
        }
        catch
        {
            return 0;
        }
    }

    private static double ComputeConfidenceScore(double edgePct, double dispersionPct, double liquidityScore, double spreadPct)
    {
        double edgeScore = MathUtils.Clamp(Math.Abs(edgePct) * 100, 0, 100);
        double dispersionPenalty = MathUtils.Clamp(dispersionPct * 100, 0, 100);
        double spreadPenalty = MathUtils.Clamp(spreadPct * 100, 0, 100);
        double liqBoost = MathUtils.Clamp(liquidityScore * 8, 0, 45);
        return MathUtils.Clamp(edgeScore * 0.65 + liqBoost - dispersionPenalty * 0.35 - spreadPenalty * 0.45, 1, 99);
    }

    private static string ComputeSignal(double edgePct, double confidenceScore, double spreadPct)
    {
        if (spreadPct > 0.9) return "Neutral";
        if (Math.Abs(edgePct) < 0.02 || confidenceScore < 20) return "Neutral";
        if (edgePct > 0.08 && confidenceScore >= 45) return "Strong Long Vol Edge";
        if (edgePct > 0) return "Long Vol Edge";
        if (edgePct < -0.08 && confidenceScore >= 45) return "Strong Short Vol Edge";
        return "Short Vol Edge";
    }

    private static double ComputeRiskReversal(IReadOnlyList<LiveOptionQuote> quotes)
    {
        var call = quotes
            .Where(q => q.Right == OptionRight.Call && q.Delta > 0 && q.MarkIv > 0)
            .OrderBy(q => Math.Abs(q.Delta - 0.25))
            .FirstOrDefault();
        var put = quotes
            .Where(q => q.Right == OptionRight.Put && q.Delta < 0 && q.MarkIv > 0)
            .OrderBy(q => Math.Abs(q.Delta + 0.25))
            .FirstOrDefault();

        if (call is null || put is null) return 0;
        return call.MarkIv - put.MarkIv;
    }

    private static IReadOnlyList<double> ComputeBreakevens(IReadOnlyList<PayoffPoint> payoff)
    {
        var points = new List<double>();
        for (int i = 1; i < payoff.Count; i++)
        {
            double prev = payoff[i - 1].Pnl;
            double curr = payoff[i].Pnl;
            if (Math.Abs(curr) < 1e-10)
            {
                points.Add(payoff[i].Spot);
                continue;
            }
            if (prev * curr >= 0) continue;

            double s1 = payoff[i - 1].Spot;
            double s2 = payoff[i].Spot;
            double interpolated = s1 + (0 - prev) * (s2 - s1) / (curr - prev);
            points.Add(interpolated);
        }

        return points
            .DistinctBy(v => Math.Round(v, 4))
            .OrderBy(v => v)
            .ToList();
    }

    private static double BestPriceForBuy(LiveOptionQuote quote)
    {
        if (quote.Ask > 0) return quote.Ask;
        if (quote.Mid > 0) return quote.Mid;
        if (quote.Mark > 0) return quote.Mark;
        return quote.Bid;
    }

    private static double BestPriceForSell(LiveOptionQuote quote)
    {
        if (quote.Bid > 0) return quote.Bid;
        if (quote.Mid > 0) return quote.Mid;
        return quote.Mark;
    }
}
