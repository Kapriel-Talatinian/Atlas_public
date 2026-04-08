using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface INeuralTradingBrainService
{
    Task<NeuralSignalSnapshot> GetAssetSignalAsync(string asset, CancellationToken ct = default);
    Task<IReadOnlyList<NeuralSignalSnapshot>> GetPortfolioSignalsAsync(CancellationToken ct = default);
}

public sealed class NeuralTradingBrainService : INeuralTradingBrainService
{
    private sealed record TermNode(
        DateTimeOffset Expiry,
        int DaysToExpiry,
        double AtmIv,
        double Skew,
        double Convexity,
        double OiImbalance,
        double TurnoverScore,
        double ExpectedMovePct,
        double LiquidityScore);

    private sealed record BrainGlobalFeatures(
        double LiveBias,
        double MarketMicro,
        double TermSlope,
        double FlowPressure,
        double Basis,
        double VolOfVol,
        double SkewSignal,
        double MarketLiquidity,
        double PcrPositioning,
        double OptionEdge,
        double RelativeValueEdge,
        double ExposurePressure);

    private sealed record SparseKernelStep((int Channel, double Weight)[] Terms);

    private sealed record FilterDefinition(
        string Name,
        SparseKernelStep[] Steps,
        double Bias,
        string Interpretation);

    private sealed record CandidateInternal(
        string Source,
        string Name,
        string Bias,
        double BaseScore,
        double Confidence,
        StrategyAnalysisResult Analysis,
        string Thesis);

    private readonly IOptionsAnalyticsService _analytics;
    private readonly IOptionsMarketDataService _marketData;

    private static readonly FilterDefinition[] Filters =
    [
        new(
            "front_vol_expansion",
            [
                new SparseKernelStep([(0, 1.10), (5, 0.70), (6, -0.40), (11, 0.25)]),
                new SparseKernelStep([(0, 1.20), (1, -0.35), (8, 0.30), (13, 0.18)])
            ],
            Bias: 0.06,
            Interpretation: "Front-end vol is expanding into actionable convexity."),
        new(
            "downside_skew_stress",
            [
                new SparseKernelStep([(1, -1.10), (2, 0.65), (11, -0.55), (12, -0.20)]),
                new SparseKernelStep([(1, -1.20), (3, -0.45), (13, 0.35)]),
                new SparseKernelStep([(1, -0.95), (5, 0.35), (8, -0.20)])
            ],
            Bias: 0.08,
            Interpretation: "Downside hedge demand and skew stress are dominating the tape."),
        new(
            "carry_trend_alignment",
            [
                new SparseKernelStep([(8, 0.65), (9, 0.35), (10, 0.55), (12, 0.50)]),
                new SparseKernelStep([(3, 0.40), (4, 0.55), (7, 0.35), (11, 0.25)]),
                new SparseKernelStep([(8, 0.55), (10, 0.45), (12, 0.40)])
            ],
            Bias: 0.02,
            Interpretation: "Carry, basis and positioning are aligned with the term structure."),
        new(
            "compression_breakout",
            [
                new SparseKernelStep([(0, -0.85), (5, -0.45), (7, 0.50), (11, 0.42)]),
                new SparseKernelStep([(0, -0.65), (8, 0.42), (9, 0.30), (4, 0.28)]),
                new SparseKernelStep([(5, 0.55), (11, 0.40), (13, 0.32)])
            ],
            Bias: 0.52,
            Interpretation: "Low-vol compression is at risk of breaking into directional expansion."),
        new(
            "gamma_bidding",
            [
                new SparseKernelStep([(2, 0.95), (4, 0.45), (7, 0.55), (9, 0.25)]),
                new SparseKernelStep([(2, 1.05), (5, 0.35), (11, 0.20)]),
                new SparseKernelStep([(2, 0.90), (4, 0.30), (7, 0.45)])
            ],
            Bias: 0.04,
            Interpretation: "Convexity is being bid across the surface."),
        new(
            "term_inversion_stress",
            [
                new SparseKernelStep([(10, -0.95), (0, 0.42), (13, 0.35)]),
                new SparseKernelStep([(10, -1.10), (1, -0.30), (11, -0.25)]),
                new SparseKernelStep([(10, -0.85), (5, 0.25), (12, -0.18)])
            ],
            Bias: 0.06,
            Interpretation: "Term structure stress favors defensive convex structures.")
    ];

    public NeuralTradingBrainService(IOptionsAnalyticsService analytics, IOptionsMarketDataService marketData)
    {
        _analytics = analytics;
        _marketData = marketData;
    }

    public async Task<IReadOnlyList<NeuralSignalSnapshot>> GetPortfolioSignalsAsync(CancellationToken ct = default)
    {
        string[] assets = ["BTC", "ETH", "SOL"];
        var tasks = assets.Select(asset => GetAssetSignalAsync(asset, ct));
        var snapshots = await Task.WhenAll(tasks);
        return snapshots.OrderByDescending(snapshot => snapshot.Confidence).ToList();
    }

    public async Task<NeuralSignalSnapshot> GetAssetSignalAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = asset.ToUpperInvariant();
        var chainTask = _marketData.GetOptionChainAsync(normalizedAsset, ct);
        var liveBiasTask = _analytics.GetLiveBiasAsync(normalizedAsset, 30, ct);
        var regimeTask = _analytics.GetRegimeAsync(normalizedAsset, ct);
        var signalBoardTask = _analytics.GetSignalBoardAsync(normalizedAsset, null, "all", 140, ct);
        var recommendationsTask = _analytics.GetRecommendationsAsync(normalizedAsset, null, 1, "balanced", ct);
        var rvTask = _analytics.GetRelativeValueBoardAsync(normalizedAsset, null, 12, ct);
        await Task.WhenAll(chainTask, liveBiasTask, regimeTask, signalBoardTask, recommendationsTask, rvTask);

        var chain = chainTask.Result;
        var liveBias = liveBiasTask.Result;
        var regime = regimeTask.Result;
        var signalBoard = signalBoardTask.Result;
        var recommendations = recommendationsTask.Result;
        var rv = rvTask.Result;

        if (chain.Count == 0)
        {
            return new NeuralSignalSnapshot(
                Asset: normalizedAsset,
                Bias: "Neutral",
                VolatilityBias: "Neutral",
                Score: 0,
                Confidence: 0,
                RecommendedStructure: "No trade",
                EntryPlan: "Wait for executable market data.",
                ExitPlan: "No active trade.",
                RiskPlan: "Flat.",
                Summary: $"No market data available for {normalizedAsset}.",
                MacroReasoning: "Macro branch idle because there is no chain to embed.",
                MicroReasoning: "Micro branch idle because there is no executable book.",
                MathReasoning: "Tensor unavailable.",
                SequenceLength: 0,
                ChannelCount: 0,
                Filters: [],
                TopPositiveDrivers: [],
                TopNegativeDrivers: [],
                Candidates: [],
                Timestamp: DateTimeOffset.UtcNow);
        }

        double spot = regime.Spot > 0 ? regime.Spot : ReferenceSpot(chain);
        if (spot <= 0)
            spot = chain.Select(q => q.UnderlyingPrice).FirstOrDefault(v => v > 0);
        if (spot <= 0)
            spot = 1;

        var termNodes = BuildTermNodes(chain, spot);
        var global = BuildGlobalFeatures(liveBias, regime, signalBoard, rv);
        double[][] tensor = BuildTensor(termNodes, global);
        var filterActivations = EvaluateFilters(tensor);
        var contributions = BuildContributions(global, filterActivations);
        (double directionScore, double volScore, double timingScore) = ComputeHeadScores(global, filterActivations);
        string bias = ClassifyBias(directionScore, volScore);
        string volBias = ClassifyVolatilityBias(volScore);

        var candidates = BuildCandidates(recommendations, rv, bias, volBias, directionScore, volScore)
            .Take(4)
            .ToList();
        NeuralTradeCandidate? topCandidate = candidates.FirstOrDefault();

        string recommendedStructure = topCandidate?.Name ?? RecommendStructureFromHeads(bias, volBias, timingScore);
        double finalScore = MathUtils.Clamp(
            directionScore * 58 + volScore * 42 + timingScore * 16,
            -100,
            100);
        double confidence = MathUtils.Clamp(
            36 + Math.Abs(finalScore) * 0.42 +
            filterActivations.OrderByDescending(f => f.Activation).Take(3).Average(f => f.Activation) * 28 +
            liveBias.ConfidenceScore * 0.12,
            5,
            99);

        string macroReasoning = BuildMacroReasoning(normalizedAsset, liveBias, regime, contributions);
        string microReasoning = BuildMicroReasoning(contributions, rv, signalBoard);
        string mathReasoning = BuildMathReasoning(finalScore, directionScore, volScore, timingScore, topCandidate, filterActivations);
        string entryPlan = BuildEntryPlan(bias, volBias, confidence, topCandidate);
        string exitPlan = BuildExitPlan(topCandidate, timingScore, confidence);
        string riskPlan = BuildRiskPlan(topCandidate, bias, confidence);
        string summary =
            $"{bias} / {volBias} on {normalizedAsset} ({finalScore:+0.0;-0.0}) with {recommendedStructure}. " +
            $"CNN branch sees {filterActivations.OrderByDescending(f => f.Activation).FirstOrDefault()?.Interpretation?.ToLowerInvariant() ?? "balanced structure"}";

        return new NeuralSignalSnapshot(
            Asset: normalizedAsset,
            Bias: bias,
            VolatilityBias: volBias,
            Score: finalScore,
            Confidence: confidence,
            RecommendedStructure: recommendedStructure,
            EntryPlan: entryPlan,
            ExitPlan: exitPlan,
            RiskPlan: riskPlan,
            Summary: summary,
            MacroReasoning: macroReasoning,
            MicroReasoning: microReasoning,
            MathReasoning: mathReasoning,
            SequenceLength: tensor.Length,
            ChannelCount: tensor.Length > 0 ? tensor[0].Length : 0,
            Filters: filterActivations,
            TopPositiveDrivers: contributions.Where(c => c.Contribution >= 0).OrderByDescending(c => c.Contribution).Take(6).ToList(),
            TopNegativeDrivers: contributions.Where(c => c.Contribution < 0).OrderBy(c => c.Contribution).Take(6).ToList(),
            Candidates: candidates,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static List<TermNode> BuildTermNodes(IReadOnlyList<LiveOptionQuote> chain, double spot)
    {
        return chain
            .Where(q => q.Expiry > DateTimeOffset.UtcNow && q.MarkIv > 0)
            .GroupBy(q => q.Expiry)
            .OrderBy(g => g.Key)
            .Take(6)
            .Select(group =>
            {
                var list = group.ToList();
                int dte = Math.Max(1, (int)Math.Round((group.Key - DateTimeOffset.UtcNow).TotalDays));
                double atmStrike = list
                    .OrderBy(q => Math.Abs(q.Strike - spot))
                    .ThenByDescending(q => q.Turnover24h)
                    .Select(q => q.Strike)
                    .First();
                double atmIv = list
                    .Where(q => Math.Abs(q.Strike - atmStrike) / Math.Max(spot, 1e-9) <= 0.02)
                    .Select(q => q.MarkIv)
                    .DefaultIfEmpty(list.Average(q => q.MarkIv))
                    .Average();

                var call25 = list
                    .Where(q => q.Right == OptionRight.Call && q.Delta > 0)
                    .OrderBy(q => Math.Abs(q.Delta - 0.25))
                    .ThenByDescending(q => q.Turnover24h)
                    .FirstOrDefault();
                var put25 = list
                    .Where(q => q.Right == OptionRight.Put && q.Delta < 0)
                    .OrderBy(q => Math.Abs(q.Delta + 0.25))
                    .ThenByDescending(q => q.Turnover24h)
                    .FirstOrDefault();

                double callWing = list
                    .Where(q => q.Right == OptionRight.Call && q.Strike >= spot * 1.05)
                    .Select(q => q.MarkIv)
                    .DefaultIfEmpty(atmIv)
                    .Average();
                double putWing = list
                    .Where(q => q.Right == OptionRight.Put && q.Strike <= spot * 0.95)
                    .Select(q => q.MarkIv)
                    .DefaultIfEmpty(atmIv)
                    .Average();

                double callOi = list.Where(q => q.Right == OptionRight.Call).Sum(q => Math.Max(0, q.OpenInterest));
                double putOi = list.Where(q => q.Right == OptionRight.Put).Sum(q => Math.Max(0, q.OpenInterest));
                double turnover = list.Sum(q => Math.Max(0, q.Turnover24h));
                double avgSpread = list
                    .Select(q =>
                    {
                        double mid = EffectiveMid(q);
                        if (mid <= 0 || q.Bid <= 0 || q.Ask <= 0) return 1.0;
                        return MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 2.0);
                    })
                    .DefaultIfEmpty(1.0)
                    .Average();

                return new TermNode(
                    Expiry: group.Key,
                    DaysToExpiry: dte,
                    AtmIv: atmIv,
                    Skew: MathUtils.Clamp(((call25?.MarkIv ?? atmIv) - (put25?.MarkIv ?? atmIv)) / Math.Max(atmIv, 1e-9), -2, 2),
                    Convexity: MathUtils.Clamp((callWing + putWing - 2 * atmIv) / Math.Max(atmIv, 1e-9), -2, 2),
                    OiImbalance: MathUtils.Clamp((callOi - putOi) / Math.Max(callOi + putOi, 1e-9), -1, 1),
                    TurnoverScore: MathUtils.Clamp(Math.Log(1 + turnover) / 12.0, 0, 1.5),
                    ExpectedMovePct: MathUtils.Clamp(atmIv * Math.Sqrt(dte / 365.25), 0, 4),
                    LiquidityScore: MathUtils.Clamp(1.1 - avgSpread, -1, 1.5));
            })
            .ToList();
    }

    private static BrainGlobalFeatures BuildGlobalFeatures(
        MacroBiasSnapshot liveBias,
        VolRegimeSnapshot regime,
        OptionSignalBoard signalBoard,
        RelativeValueBoard rv)
    {
        var drivers = liveBias.Drivers.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);
        double GetInput(string name) => drivers.TryGetValue(name, out var driver) ? driver.Input : 0;
        double optionEdge = signalBoard.TopLongEdges.Take(5).DefaultIfEmpty().Average(row => row?.EdgePct ?? 0);
        double rvEdge = rv.TopCheapVol.Take(4).DefaultIfEmpty().Average(row => Math.Abs(row?.ResidualVolPoints ?? 0)) / 12.0;
        double exposurePressure = rv.TopRichVol.Take(4).DefaultIfEmpty().Average(row => row?.ResidualZScore ?? 0) / 4.0;

        return new BrainGlobalFeatures(
            LiveBias: MathUtils.Clamp(liveBias.BiasScore / 100.0, -1, 1),
            MarketMicro: MathUtils.Clamp(liveBias.MarketMicroScore / 100.0, -1, 1),
            TermSlope: MathUtils.Clamp(regime.TermSlope30To90 * 12.0, -1, 1),
            FlowPressure: MathUtils.Clamp(GetInput("Flow pressure"), -1, 1),
            Basis: MathUtils.Clamp(GetInput("Forward basis proxy"), -1, 1),
            VolOfVol: MathUtils.Clamp(GetInput("Vol-of-vol stability"), -1, 1),
            SkewSignal: MathUtils.Clamp(GetInput("Skew signal"), -1, 1),
            MarketLiquidity: MathUtils.Clamp(GetInput("Market liquidity") / 10.0, -1, 1),
            PcrPositioning: MathUtils.Clamp(GetInput("Put/Call positioning"), -2, 2),
            OptionEdge: MathUtils.Clamp(optionEdge / 20.0, -1, 1),
            RelativeValueEdge: MathUtils.Clamp(rvEdge, -1, 1),
            ExposurePressure: MathUtils.Clamp(exposurePressure, -1, 1));
    }

    private static double[][] BuildTensor(IReadOnlyList<TermNode> nodes, BrainGlobalFeatures global)
    {
        return nodes
            .Select(node => new[]
            {
                MathUtils.Clamp((node.AtmIv - 0.60) / 0.35, -2, 2),
                node.Skew,
                node.Convexity,
                node.OiImbalance,
                node.TurnoverScore,
                MathUtils.Clamp(node.ExpectedMovePct, 0, 3),
                MathUtils.Clamp((node.DaysToExpiry - 30.0) / 45.0, -1.5, 2.0),
                node.LiquidityScore,
                global.LiveBias,
                global.MarketMicro,
                global.TermSlope,
                global.FlowPressure,
                global.Basis,
                global.VolOfVol
            })
            .ToArray();
    }

    private static IReadOnlyList<NeuralFilterActivation> EvaluateFilters(double[][] tensor)
    {
        if (tensor.Length == 0)
            return [];

        var activations = new List<NeuralFilterActivation>(Filters.Length);
        foreach (FilterDefinition filter in Filters)
        {
            double best = double.NegativeInfinity;
            int maxStart = tensor.Length - filter.Steps.Length;
            if (maxStart < 0)
                continue;

            for (int start = 0; start <= maxStart; start++)
            {
                double sum = filter.Bias;
                for (int stepIndex = 0; stepIndex < filter.Steps.Length; stepIndex++)
                {
                    double[] row = tensor[start + stepIndex];
                    foreach (var (channel, weight) in filter.Steps[stepIndex].Terms)
                        sum += row[channel] * weight;
                }

                double activation = Math.Max(0, sum);
                if (activation > best)
                    best = activation;
            }

            activations.Add(new NeuralFilterActivation(
                Name: filter.Name,
                Activation: double.IsFinite(best) ? best : 0,
                Interpretation: filter.Interpretation));
        }

        return activations.OrderByDescending(a => a.Activation).ToList();
    }

    private static List<NeuralSignalContribution> BuildContributions(
        BrainGlobalFeatures global,
        IReadOnlyList<NeuralFilterActivation> filterActivations)
    {
        var contributions = new List<NeuralSignalContribution>
        {
            BuildContribution("live_bias", "macro", global.LiveBias, 0.82, "Directional macro/positioning bias from live engine."),
            BuildContribution("market_micro", "micro", global.MarketMicro, 0.72, "Orderflow and liquidity composite."),
            BuildContribution("term_slope", "term", global.TermSlope, 0.64, "Curve shape across expiries."),
            BuildContribution("flow_pressure", "micro", global.FlowPressure, 0.70, "Aggressive tape pressure."),
            BuildContribution("basis", "macro", global.Basis, 0.54, "Carry / forward basis proxy."),
            BuildContribution("vol_of_vol", "micro", global.VolOfVol, -0.46, "Vol stability vs instability."),
            BuildContribution("skew_signal", "term", global.SkewSignal, -0.58, "Put/call skew asymmetry."),
            BuildContribution("market_liquidity", "micro", global.MarketLiquidity, 0.44, "Depth and executable spread quality."),
            BuildContribution("put_call_positioning", "macro", global.PcrPositioning, -0.38, "Positioning stress proxy."),
            BuildContribution("option_edge", "math", global.OptionEdge, 0.66, "Model edge from signal board."),
            BuildContribution("relative_value_edge", "math", global.RelativeValueEdge, 0.78, "No-arbitrage surface dislocation."),
            BuildContribution("exposure_pressure", "risk", global.ExposurePressure, -0.34, "Dealer / exposure pressure proxy.")
        };

        foreach (var filter in filterActivations)
        {
            double weight = filter.Name switch
            {
                "front_vol_expansion" => 0.72,
                "downside_skew_stress" => -0.80,
                "carry_trend_alignment" => 0.64,
                "compression_breakout" => 0.60,
                "gamma_bidding" => 0.68,
                "term_inversion_stress" => -0.56,
                _ => 0.25
            };
            contributions.Add(BuildContribution(filter.Name, "cnn-filter", filter.Activation, weight, filter.Interpretation));
        }

        return contributions
            .OrderByDescending(item => Math.Abs(item.Contribution))
            .ToList();
    }

    private static NeuralSignalContribution BuildContribution(
        string name,
        string bucket,
        double input,
        double weight,
        string explanation)
    {
        return new NeuralSignalContribution(
            Name: name,
            Bucket: bucket,
            Input: input,
            Weight: weight,
            Contribution: input * weight,
            Explanation: explanation);
    }

    private static (double DirectionScore, double VolScore, double TimingScore) ComputeHeadScores(
        BrainGlobalFeatures global,
        IReadOnlyList<NeuralFilterActivation> filterActivations)
    {
        double filter(string name) => filterActivations.FirstOrDefault(f => f.Name == name)?.Activation ?? 0;

        double direction =
            global.LiveBias * 0.34 +
            global.MarketMicro * 0.18 +
            global.TermSlope * 0.12 +
            global.Basis * 0.10 +
            global.FlowPressure * 0.12 +
            filter("carry_trend_alignment") * 0.18 +
            filter("compression_breakout") * 0.10 -
            filter("downside_skew_stress") * 0.14 -
            filter("term_inversion_stress") * 0.10;

        double vol =
            global.RelativeValueEdge * 0.22 +
            global.OptionEdge * 0.12 +
            global.VolOfVol * -0.08 +
            global.SkewSignal * -0.12 +
            filter("front_vol_expansion") * 0.20 +
            filter("gamma_bidding") * 0.18 +
            filter("compression_breakout") * 0.12 -
            filter("carry_trend_alignment") * 0.10 +
            filter("term_inversion_stress") * 0.10;

        double timing =
            global.MarketLiquidity * 0.18 +
            global.FlowPressure * 0.14 +
            global.OptionEdge * 0.18 +
            global.RelativeValueEdge * 0.22 +
            filter("compression_breakout") * 0.15 +
            filter("gamma_bidding") * 0.08 -
            Math.Abs(global.VolOfVol) * 0.08;

        return (
            MathUtils.Clamp(direction, -2.5, 2.5),
            MathUtils.Clamp(vol, -2.5, 2.5),
            MathUtils.Clamp(timing, -2.5, 2.5));
    }

    private static string ClassifyBias(double directionScore, double volScore)
    {
        if (volScore >= 0.35 && directionScore >= 0.15) return "Upside Long Vol";
        if (volScore >= 0.35 && directionScore <= -0.15) return "Downside Long Vol";
        if (directionScore >= 0.22) return "Bullish Structure";
        if (directionScore <= -0.22) return "Bearish Structure";
        if (volScore <= -0.20) return "Carry / Neutral Premium";
        return "Neutral / Wait";
    }

    private static string ClassifyVolatilityBias(double volScore)
    {
        return volScore switch
        {
            >= 0.35 => "Long Vol",
            <= -0.20 => "Short Vol",
            _ => "Mixed Vol"
        };
    }

    private static string RecommendStructureFromHeads(string bias, string volBias, double timingScore)
    {
        if (bias == "Upside Long Vol") return timingScore >= 0.25 ? "Call Debit Spread" : "Call Butterfly";
        if (bias == "Downside Long Vol") return timingScore >= 0.25 ? "Put Debit Spread" : "Put Butterfly";
        if (volBias == "Short Vol") return "Iron Condor";
        if (bias == "Bullish Structure") return "Bull Call Spread";
        if (bias == "Bearish Structure") return "Bear Put Spread";
        return "Wait / no structured edge";
    }

    private static List<NeuralTradeCandidate> BuildCandidates(
        StrategyRecommendationBoard recommendations,
        RelativeValueBoard rv,
        string bias,
        string volBias,
        double directionScore,
        double volScore)
    {
        var candidates = new List<CandidateInternal>();
        candidates.AddRange(recommendations.Recommendations
            .Where(rec => rec.Analysis.Legs.Count >= 2)
            .Select(rec => new CandidateInternal(
                Source: "recommendation",
                Name: rec.Name,
                Bias: ClassifyTradeBias(rec.Analysis),
                BaseScore: rec.Score,
                Confidence: rec.ConfidenceScore,
                Analysis: rec.Analysis,
                Thesis: rec.Thesis)));

        candidates.AddRange(rv.TradeIdeas
            .Where(idea => idea.Analysis.Legs.Count >= 2)
            .Select(idea => new CandidateInternal(
                Source: "relative-value",
                Name: idea.Name,
                Bias: ClassifyTradeBias(idea.Analysis, idea.Action),
                BaseScore: idea.Score,
                Confidence: idea.ConfidenceScore,
                Analysis: idea.Analysis,
                Thesis: idea.Thesis)));

        return candidates
            .Select(candidate =>
            {
                double structureFit = ScoreStructureFit(candidate.Name, bias, volBias);
                double directionalFit = ScoreDirectionalFit(candidate.Bias, directionScore, volScore);
                double riskBase = Math.Max(1, Math.Max(Math.Abs(candidate.Analysis.MaxLoss), Math.Abs(candidate.Analysis.NetPremium)));
                double evRatio = candidate.Analysis.ExpectedValue / riskBase;
                double score = MathUtils.Clamp(
                    candidate.BaseScore * 0.46 +
                    candidate.Confidence * 0.12 +
                    structureFit * 0.18 +
                    directionalFit * 0.10 +
                    MathUtils.Clamp(evRatio * 100, -40, 40) * 0.08 +
                    MathUtils.Clamp(candidate.Analysis.RewardRiskRatio, 0, 6) * 2.0 * 0.06,
                    1,
                    99);

                return new NeuralTradeCandidate(
                    Source: candidate.Source,
                    Name: candidate.Name,
                    Bias: candidate.Bias,
                    Score: score,
                    Confidence: MathUtils.Clamp(candidate.Confidence * 0.65 + structureFit * 0.35, 1, 99),
                    ExpectedValue: candidate.Analysis.ExpectedValue,
                    MaxProfit: candidate.Analysis.MaxProfit,
                    MaxLoss: candidate.Analysis.MaxLoss,
                    RewardRiskRatio: candidate.Analysis.RewardRiskRatio,
                    ProbabilityOfProfitApprox: candidate.Analysis.ProbabilityOfProfitApprox,
                    Thesis: candidate.Thesis);
            })
            .OrderByDescending(candidate => candidate.Score)
            .Take(4)
            .ToList();
    }

    private static double ScoreStructureFit(string name, string bias, string volBias)
    {
        string lower = name.ToLowerInvariant();
        double score = 35;

        if (bias == "Upside Long Vol")
        {
            if (lower.Contains("call") && lower.Contains("spread")) score += 32;
            if (lower.Contains("call") && lower.Contains("butterfly")) score += 20;
        }
        if (bias == "Downside Long Vol")
        {
            if (lower.Contains("put") && lower.Contains("spread")) score += 32;
            if (lower.Contains("put") && lower.Contains("butterfly")) score += 20;
        }
        if (volBias == "Short Vol")
        {
            if (lower.Contains("condor") || lower.Contains("butterfly")) score += 24;
            if (lower.Contains("calendar")) score -= 18;
        }
        if (volBias == "Long Vol")
        {
            if (lower.Contains("calendar")) score += 12;
            if (lower.Contains("spread")) score += 16;
        }

        if (lower.Contains("straddle") || lower.Contains("strangle"))
            score -= 10;

        return MathUtils.Clamp(score, 1, 99);
    }

    private static double ScoreDirectionalFit(string candidateBias, double directionScore, double volScore)
    {
        double score = candidateBias switch
        {
            "Long Vol" => Math.Max(0, volScore) * 45 + 45,
            "Short Vol" => Math.Max(0, -volScore) * 45 + 45,
            "Upside Structure" => Math.Max(0, directionScore) * 45 + 40,
            "Downside Structure" => Math.Max(0, -directionScore) * 45 + 40,
            _ => 40 + (1 - Math.Abs(directionScore)) * 15
        };
        return MathUtils.Clamp(score, 1, 99);
    }

    private static string ClassifyTradeBias(StrategyAnalysisResult analysis, string? action = null)
    {
        string lowerAction = action?.ToLowerInvariant() ?? string.Empty;
        if (lowerAction.Contains("buy vol") || lowerAction.Contains("cheap vol")) return "Long Vol";
        if (lowerAction.Contains("sell vol") || lowerAction.Contains("rich vol")) return "Short Vol";
        if (analysis.AggregateGreeks.Vega >= 10 && analysis.AggregateGreeks.Theta <= 0) return "Long Vol";
        if (analysis.AggregateGreeks.Vega <= -10 && analysis.AggregateGreeks.Theta >= 0) return "Short Vol";
        if (analysis.AggregateGreeks.Delta >= 0.15) return "Upside Structure";
        if (analysis.AggregateGreeks.Delta <= -0.15) return "Downside Structure";
        return "Relative Value";
    }

    private static string BuildMacroReasoning(
        string asset,
        MacroBiasSnapshot liveBias,
        VolRegimeSnapshot regime,
        IReadOnlyList<NeuralSignalContribution> contributions)
    {
        var macroDrivers = contributions
            .Where(c => c.Bucket == "macro" || c.Bucket == "term")
            .OrderByDescending(c => Math.Abs(c.Contribution))
            .Take(4)
            .Select(c => $"{c.Name} ({c.Contribution:+0.00;-0.00})")
            .ToList();

        return
            $"{asset} macro/term branch: bias {liveBias.Bias} ({liveBias.BiasScore:+0.0;-0.0}), regime {regime.Regime}, " +
            $"atm30 {regime.AtmIv30D:P1}, term slope {regime.TermSlope30To90:+0.0%;-0.0%}, skew {regime.Skew25D:+0.0%;-0.0%}. " +
            $"Top macro drivers: {string.Join(", ", macroDrivers)}.";
    }

    private static string BuildMicroReasoning(
        IReadOnlyList<NeuralSignalContribution> contributions,
        RelativeValueBoard rv,
        OptionSignalBoard signalBoard)
    {
        var microDrivers = contributions
            .Where(c => c.Bucket == "micro" || c.Bucket == "cnn-filter")
            .OrderByDescending(c => Math.Abs(c.Contribution))
            .Take(4)
            .Select(c => $"{c.Name} ({c.Contribution:+0.00;-0.00})")
            .ToList();

        double bestEdge = signalBoard.TopLongEdges.Take(3).DefaultIfEmpty().Average(row => row?.EdgePct ?? 0);
        double bestRv = rv.TopCheapVol.Take(3).DefaultIfEmpty().Average(row => row?.ResidualVolPoints ?? 0);
        return
            $"Micro branch: top option model edge {bestEdge:+0.0;-0.0}%, avg cheap vol residual {bestRv:+0.0;-0.0} pts. " +
            $"CNN tape filters: {string.Join(", ", microDrivers)}.";
    }

    private static string BuildMathReasoning(
        double finalScore,
        double directionScore,
        double volScore,
        double timingScore,
        NeuralTradeCandidate? candidate,
        IReadOnlyList<NeuralFilterActivation> filterActivations)
    {
        string topFilters = string.Join(
            ", ",
            filterActivations
                .OrderByDescending(f => f.Activation)
                .Take(3)
                .Select(f => $"{f.Name}={f.Activation:F2}"));

        string candidateMath = candidate is null
            ? "No structured candidate cleared ranking."
            : $"Top candidate {candidate.Name}: EV {candidate.ExpectedValue:F0}, max loss {Math.Abs(candidate.MaxLoss):F0}, RR {candidate.RewardRiskRatio:F2}, PoP {candidate.ProbabilityOfProfitApprox:P0}.";

        return
            $"CNN fusion score={finalScore:+0.0;-0.0}; direction={directionScore:+0.00;-0.00}, vol={volScore:+0.00;-0.00}, timing={timingScore:+0.00;-0.00}. " +
            $"Top activations: {topFilters}. {candidateMath}";
    }

    private static string BuildEntryPlan(
        string bias,
        string volBias,
        double confidence,
        NeuralTradeCandidate? candidate)
    {
        if (candidate is null)
            return "No entry. Wait for cleaner RV gap / better tradeability.";

        string confidenceTag = confidence >= 65 ? "aggressive ready" : confidence >= 50 ? "selective ready" : "watchlist";
        return
            $"{confidenceTag}: enter only if spreads stay tradable and the candidate remains top-ranked. " +
            $"Current preferred package: {candidate.Name} ({bias}, {volBias}).";
    }

    private static string BuildExitPlan(NeuralTradeCandidate? candidate, double timingScore, double confidence)
    {
        if (candidate is null)
            return "Flat. No exit plan needed.";

        return
            $"Exit on neural score decay below {(confidence >= 60 ? "40" : "35")} conf, " +
            $"timing factor reversal, or when EV capture is realized. Timing branch={timingScore:+0.00;-0.00}.";
    }

    private static string BuildRiskPlan(NeuralTradeCandidate? candidate, string bias, double confidence)
    {
        if (candidate is null)
            return "Keep risk idle.";

        double riskUnit = Math.Abs(candidate.MaxLoss);
        return
            $"Structure-first risk: max loss {riskUnit:F0} USD-equivalent on ranked candidate, " +
            $"use defined-risk multi-leg only. Bias={bias}, confidence={confidence:F1}.";
    }

    private static double ReferenceSpot(IReadOnlyList<LiveOptionQuote> quotes)
    {
        var spots = quotes
            .Select(q => q.UnderlyingPrice)
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToList();
        if (spots.Count == 0) return 0;
        int mid = spots.Count / 2;
        return spots.Count % 2 == 0 ? (spots[mid - 1] + spots[mid]) / 2.0 : spots[mid];
    }

    private static double EffectiveMid(LiveOptionQuote quote)
    {
        if (quote.Mid > 0) return quote.Mid;
        if (quote.Bid > 0 && quote.Ask > 0) return (quote.Bid + quote.Ask) / 2.0;
        if (quote.Mark > 0) return quote.Mark;
        if (quote.Bid > 0) return quote.Bid;
        return quote.Ask;
    }
}
