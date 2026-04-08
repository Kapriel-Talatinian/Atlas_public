using System.Text.Json;
using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public sealed class SharedPortfolioExperimentalAutoTraderService : IExperimentalAutoTraderService
{
    private const string PortfolioKey = "MULTI";
    private static readonly string[] ManagedAssets = ["BTC", "ETH", "SOL"];
    private static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["flow_imbalance"] = 1.10,
        ["skew_signal"] = 0.94,
        ["vix_proxy"] = 0.85,
        ["futures_momentum"] = 1.02,
        ["orderbook_pressure"] = 0.82,
        ["resting_pressure"] = 0.70,
        ["vol_regime"] = 0.76,
        ["term_slope"] = 0.66,
        ["rv_edge"] = 1.18,
        ["tradeability"] = 0.74,
        ["expected_value"] = 1.12,
        ["reward_risk"] = 0.88,
        ["convexity"] = 0.92,
        ["confidence"] = 0.78,
        ["pop"] = 0.55
    };

    private const double LearningRate = 0.03;

    private sealed class InternalTradeLeg
    {
        public string Symbol { get; set; } = string.Empty;
        public string Asset { get; set; } = string.Empty;
        public TradeDirection Direction { get; set; } = TradeDirection.Buy;
        public double Quantity { get; set; }
        public double EntryPrice { get; set; }
        public double MarkPrice { get; set; }
        public DateTimeOffset Expiry { get; set; }
        public double Strike { get; set; }
        public OptionRight Right { get; set; }
    }

    private sealed class InternalTrade
    {
        public string TradeId { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string Asset { get; set; } = string.Empty;
        public string PrimarySymbol { get; set; } = string.Empty;
        public string StrategyTemplate { get; set; } = string.Empty;
        public string Bias { get; set; } = string.Empty;
        public double EntryNetPremium { get; set; }
        public double CurrentLiquidationValue { get; set; }
        public double UnrealizedPnl { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public double RealizedPnl { get; set; }
        public double MaxProfit { get; set; }
        public double MaxLoss { get; set; }
        public double RewardRiskRatio { get; set; }
        public double ProbabilityOfProfitApprox { get; set; }
        public double ExpectedValue { get; set; }
        public double EntryScore { get; set; }
        public double Confidence { get; set; }
        public double RiskBudgetPct { get; set; }
        public double PortfolioWeightPct { get; set; }
        public string Thesis { get; set; } = string.Empty;
        public string MathSummary { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public List<string> Drivers { get; set; } = [];
        public Dictionary<string, double> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<InternalTradeLeg> Legs { get; set; } = [];
        public bool IsOpen { get; set; } = true;
        public DateTimeOffset EntryTime { get; set; }
        public DateTimeOffset? ExitTime { get; set; }
        public string ExitReason { get; set; } = "open";
    }

    private sealed record SpotPoint(string Asset, DateTimeOffset Time, double Spot);

    private sealed class PersistedBotState
    {
        public ExperimentalBotConfig Config { get; set; } = DefaultConfig();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public ExperimentalBotSignal? LastSignal { get; set; }
        public List<NeuralSignalSnapshot> NeuralSignals { get; set; } = [];
        public List<InternalTrade> OpenTrades { get; set; } = [];
        public List<InternalTrade> ClosedTrades { get; set; } = [];
        public List<ExperimentalBotDecision> Decisions { get; set; } = [];
        public List<ExperimentalBotAuditEntry> Audits { get; set; } = [];
        public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<SpotPoint> SpotHistory { get; set; } = [];
        public double PeakEquity { get; set; }
        public double MaxDrawdown { get; set; }
    }

    private sealed class BotState
    {
        public ExperimentalBotConfig Config { get; set; } = DefaultConfig();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public ExperimentalBotSignal? LastSignal { get; set; }
        public long StateVersion { get; set; }
        public DateTimeOffset LastPersistedAt { get; set; }
        public string LastCycleStatus { get; set; } = "cold";
        public int LastCycleDurationMs { get; set; }
        public List<NeuralSignalSnapshot> NeuralSignals { get; } = [];
        public List<InternalTrade> OpenTrades { get; } = [];
        public List<InternalTrade> ClosedTrades { get; } = [];
        public List<ExperimentalBotAuditEntry> Audits { get; } = [];
        public List<ExperimentalBotDecision> Decisions { get; } = [];
        public List<SpotPoint> SpotHistory { get; } = [];
        public Dictionary<string, double> Weights { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public double PeakEquity { get; set; }
        public double MaxDrawdown { get; set; }
    }

    private sealed record AssetSignalContext(
        string Asset,
        IReadOnlyList<LiveOptionQuote> Chain,
        double Spot,
        ExperimentalBotFeatureVector Features,
        IReadOnlyList<string> Drivers,
        double LiquidityScore,
        VolRegimeSnapshot Regime,
        RelativeValueBoard RelativeValue,
        StrategyRecommendationBoard Recommendations,
        NeuralSignalSnapshot NeuralSignal,
        ExperimentalBotSignal Signal);

    private sealed record CandidateTrade(
        string Fingerprint,
        string Asset,
        string Source,
        string Name,
        string Bias,
        double Score,
        double Confidence,
        StrategyAnalysisResult Analysis,
        string Thesis,
        string Rationale,
        string MathSummary,
        string EntryPlan,
        string ExitPlan,
        string RiskPlan,
        IReadOnlyList<string> Drivers,
        Dictionary<string, double> Features);

    private readonly IOptionsMarketDataService _marketData;
    private readonly IOptionsAnalyticsService _analytics;
    private readonly INeuralTradingBrainService _brain;
    private readonly IBotStateRepository _repository;
    private readonly AtlasRuntimeContext _runtime;
    private readonly ISystemMonitoringService _monitoring;
    private readonly ILogger<SharedPortfolioExperimentalAutoTraderService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly BotState _state;

    public SharedPortfolioExperimentalAutoTraderService(
        IOptionsMarketDataService marketData,
        IOptionsAnalyticsService analytics,
        INeuralTradingBrainService brain,
        IBotStateRepository repository,
        AtlasRuntimeContext runtime,
        ISystemMonitoringService monitoring,
        ILogger<SharedPortfolioExperimentalAutoTraderService> logger)
    {
        _marketData = marketData;
        _analytics = analytics;
        _brain = brain;
        _repository = repository;
        _runtime = runtime;
        _monitoring = monitoring;
        _logger = logger;
        _state = LoadOrCreateState();
    }

    public async Task<ExperimentalBotSnapshot> GetSnapshotAsync(string asset, CancellationToken ct = default)
    {
        if (_runtime.CanRunBotLoop)
            await EvaluateIfDueAsync(force: false, cycles: 1, ct);
        else
            await RefreshFromRepositoryThreadSafeAsync(ct);
        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task<ExperimentalBotModelExplainSnapshot> GetModelExplainAsync(string asset, CancellationToken ct = default)
    {
        if (_runtime.CanRunBotLoop)
            await EvaluateIfDueAsync(force: false, cycles: 1, ct);
        else
            await RefreshFromRepositoryThreadSafeAsync(ct);

        await _state.Gate.WaitAsync(ct);
        try
        {
            ExperimentalBotSignal signal = _state.LastSignal ?? new ExperimentalBotSignal(
                Bias: "Neutral",
                Score: 0,
                Confidence: 0,
                StrategyTemplate: "None",
                Summary: "No live portfolio signal.",
                Drivers: [],
                Features: new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0),
                Timestamp: DateTimeOffset.UtcNow);

            Dictionary<string, double> featureMap = ExtractFeatureMap(signal.Features);
            var contributions = featureMap
                .Select(kv =>
                {
                    double weight = _state.Weights.TryGetValue(kv.Key, out var configured) ? configured : 1;
                    double contribution = weight * kv.Value;
                    return new ExperimentalBotFeatureContribution(kv.Key, weight, kv.Value, contribution);
                })
                .OrderByDescending(x => Math.Abs(x.Contribution))
                .ToList();

            var positives = contributions
                .Where(x => x.Contribution >= 0)
                .OrderByDescending(x => x.Contribution)
                .Take(4)
                .ToList();
            var negatives = contributions
                .Where(x => x.Contribution < 0)
                .OrderBy(x => x.Contribution)
                .Take(4)
                .ToList();
            var audits = _state.Audits
                .OrderByDescending(x => x.Timestamp)
                .Take(20)
                .ToList();

            string narrative =
                $"Shared portfolio autopilot across {string.Join("/", ManagedAssets)}. " +
                $"Current thesis: {signal.Summary} " +
                $"Top positive drivers: {string.Join(", ", positives.Select(p => $"{p.Feature}({p.Contribution:+0.00;-0.00})"))}. " +
                $"Top negative drivers: {string.Join(", ", negatives.Select(p => $"{p.Feature}({p.Contribution:+0.00;-0.00})"))}.";

            return new ExperimentalBotModelExplainSnapshot(
                Asset: PortfolioKey,
                Bias: signal.Bias,
                Score: signal.Score,
                Confidence: signal.Confidence,
                TopPositiveContributors: positives,
                TopNegativeContributors: negatives,
                LatestAuditSamples: audits,
                Narrative: narrative,
                Timestamp: DateTimeOffset.UtcNow);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    public async Task<ExperimentalBotSnapshot> ConfigureAsync(string asset, ExperimentalBotConfigRequest request, CancellationToken ct = default)
    {
        EnsureMutationAllowed();
        await _state.Gate.WaitAsync(ct);
        try
        {
            _state.Config = ApplyConfigRequest(_state.Config, request);
            RefreshDrawdownAnchors(_state);
            _state.LastCycleStatus = "configured";
            PersistStateNoLock(_state);
        }
        finally
        {
            _state.Gate.Release();
        }

        await EvaluateIfDueAsync(force: true, cycles: 1, ct);
        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task<ExperimentalBotSnapshot> RunCycleAsync(string asset, int cycles = 1, CancellationToken ct = default)
    {
        EnsureMutationAllowed();
        await EvaluateIfDueAsync(force: true, cycles: cycles, ct);
        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task<ExperimentalBotSnapshot> ResetAsync(string asset, CancellationToken ct = default)
    {
        EnsureMutationAllowed();
        await _state.Gate.WaitAsync(ct);
        try
        {
            BotState fresh = CreateDefaultState();
            ReplaceState(_state, fresh);
            _state.LastCycleStatus = "reset";
            PersistStateNoLock(_state);
        }
        finally
        {
            _state.Gate.Release();
        }

        await EvaluateIfDueAsync(force: true, cycles: 1, ct);
        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task RunAutopilotAsync(CancellationToken ct = default)
    {
        if (!_runtime.CanRunBotLoop)
            return;

        bool shouldRun;
        await _state.Gate.WaitAsync(ct);
        try
        {
            shouldRun = _state.Config.Enabled || _state.OpenTrades.Count > 0;
        }
        finally
        {
            _state.Gate.Release();
        }

        if (shouldRun)
            await EvaluateIfDueAsync(force: false, cycles: 1, ct);
    }

    private static ExperimentalBotConfig DefaultConfig()
    {
        return new ExperimentalBotConfig(
            Enabled: true,
            AutoTrade: true,
            AutoTune: true,
            EvaluationIntervalSec: 20,
            BasePositionSize: 1,
            MaxOpenTrades: 0,
            MinConfidence: 52,
            StopLossPct: 0.35,
            TakeProfitPct: 0.65,
            MaxHoldingHours: 96,
            AuditTargetTrades: 100,
            StartingCapitalUsd: 1000,
            PortfolioRiskBudgetPct: 0.88,
            MaxAssetRiskPct: 0.42,
            MaxTradeRiskPct: 0.14,
            MaxNewTradesPerCycle: 1,
            ManagedAssets: ManagedAssets);
    }

    private static ExperimentalBotConfig ApplyConfigRequest(ExperimentalBotConfig current, ExperimentalBotConfigRequest request)
    {
        return new ExperimentalBotConfig(
            Enabled: request.Enabled ?? current.Enabled,
            AutoTrade: request.AutoTrade ?? current.AutoTrade,
            AutoTune: request.AutoTune ?? current.AutoTune,
            EvaluationIntervalSec: Math.Clamp(request.EvaluationIntervalSec ?? current.EvaluationIntervalSec, 8, 120),
            BasePositionSize: MathUtils.Clamp(request.BasePositionSize ?? current.BasePositionSize, 0.05, 2.5),
            MaxOpenTrades: 0,
            MinConfidence: MathUtils.Clamp(request.MinConfidence ?? current.MinConfidence, 35, 95),
            StopLossPct: MathUtils.Clamp(request.StopLossPct ?? current.StopLossPct, 0.05, 0.95),
            TakeProfitPct: MathUtils.Clamp(request.TakeProfitPct ?? current.TakeProfitPct, 0.05, 3.0),
            MaxHoldingHours: Math.Clamp(request.MaxHoldingHours ?? current.MaxHoldingHours, 4, 720),
            AuditTargetTrades: Math.Clamp(request.AuditTargetTrades ?? current.AuditTargetTrades, 25, 5000),
            StartingCapitalUsd: MathUtils.Clamp(request.StartingCapitalUsd ?? current.StartingCapitalUsd, 100, 5_000_000),
            PortfolioRiskBudgetPct: MathUtils.Clamp(request.PortfolioRiskBudgetPct ?? current.PortfolioRiskBudgetPct, 0.20, 0.98),
            MaxAssetRiskPct: MathUtils.Clamp(request.MaxAssetRiskPct ?? current.MaxAssetRiskPct, 0.10, 0.70),
            MaxTradeRiskPct: MathUtils.Clamp(request.MaxTradeRiskPct ?? current.MaxTradeRiskPct, 0.02, 0.30),
            MaxNewTradesPerCycle: Math.Clamp(request.MaxNewTradesPerCycle ?? current.MaxNewTradesPerCycle, 1, 3),
            ManagedAssets: ManagedAssets);
    }

    private async Task EvaluateIfDueAsync(bool force, int cycles, CancellationToken ct)
    {
        int safeCycles = Math.Clamp(cycles, 1, 8);
        await _state.Gate.WaitAsync(ct);
        try
        {
            bool dirty = false;
            for (int i = 0; i < safeCycles; i++)
            {
                var cycleStopwatch = System.Diagnostics.Stopwatch.StartNew();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!force && _state.LastEvaluationAt != DateTimeOffset.MinValue)
                {
                    double elapsed = (now - _state.LastEvaluationAt).TotalSeconds;
                    if (elapsed < _state.Config.EvaluationIntervalSec)
                        break;
                }

                var contexts = new List<AssetSignalContext>();
                var byAssetQuotes = new Dictionary<string, IReadOnlyDictionary<string, LiveOptionQuote>>(StringComparer.OrdinalIgnoreCase);

                foreach (string asset in ManagedAssets)
                {
                    IReadOnlyList<LiveOptionQuote> chain = await _marketData.GetOptionChainAsync(asset, ct);
                    if (chain.Count == 0)
                        continue;

                    double spot = ReferenceSpot(chain);
                    if (spot <= 0)
                        spot = chain.Select(q => q.UnderlyingPrice).FirstOrDefault(v => v > 0);
                    if (spot <= 0)
                        continue;

                    AppendSpotHistory(_state, asset, now, spot);
                    var history = _state.SpotHistory.Where(point => point.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToList();
                    var (features, drivers, liquidityScore) = ComputeFeatureVector(chain, spot, history);
                    var recommendations = await _analytics.GetRecommendationsAsync(asset, size: 1, riskProfile: "balanced", ct: ct);
                    var rv = await _analytics.GetRelativeValueBoardAsync(asset, limit: 12, ct: ct);
                    var neural = await _brain.GetAssetSignalAsync(asset, ct);
                    ExperimentalBotSignal signal = BuildSignal(asset, _state.Weights, features, drivers, liquidityScore, now);

                    contexts.Add(new AssetSignalContext(
                        Asset: asset,
                        Chain: chain,
                        Spot: spot,
                        Features: features,
                        Drivers: drivers,
                        LiquidityScore: liquidityScore,
                        Regime: recommendations.Regime,
                        RelativeValue: rv,
                        Recommendations: recommendations,
                        NeuralSignal: neural,
                        Signal: signal));

                    byAssetQuotes[asset] = chain
                        .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => g.OrderByDescending(x => x.SourceTimestamp ?? x.Timestamp).First(),
                            StringComparer.OrdinalIgnoreCase);
                }

                var neuralByAsset = contexts.ToDictionary(
                    context => context.Asset,
                    context => context.NeuralSignal,
                    StringComparer.OrdinalIgnoreCase);
                _state.NeuralSignals.Clear();
                _state.NeuralSignals.AddRange(
                    neuralByAsset.Values
                        .OrderByDescending(signal => signal.Confidence)
                        .ThenByDescending(signal => signal.Score));

                var closed = UpdateOpenTrades(_state, byAssetQuotes, neuralByAsset, now);
                foreach (var trade in closed)
                {
                    LearnFromClosedTrade(_state, trade);
                    _state.Audits.Add(CreateAuditEntry(_state, trade, now));
                    _monitoring.IncrementCounter("experimental.bot.trades.closed");
                    if (trade.RealizedPnl >= 0)
                        _monitoring.IncrementCounter("experimental.bot.trades.win");
                    else
                        _monitoring.IncrementCounter("experimental.bot.trades.loss");
                }

                var candidates = BuildCandidates(contexts);
                _state.LastSignal = BuildPortfolioSignal(contexts, candidates, now);

                string action = "Hold";
                string reason = _state.LastSignal?.Summary ?? "No live thesis";
                int opened = 0;

                if (_state.Config.Enabled && _state.Config.AutoTrade && candidates.Count > 0)
                {
                    ExperimentalBotStats preStats = BuildStats(_state);
                    ExperimentalBotPortfolioOverview portfolio = BuildPortfolio(_state, preStats);
                    double confidenceGate = Math.Max(42, _state.Config.MinConfidence * 0.75);

                    foreach (var candidate in candidates)
                    {
                        if (opened >= _state.Config.MaxNewTradesPerCycle)
                            break;
                        if (candidate.Confidence < confidenceGate)
                            continue;
                        if (_state.OpenTrades.Any(t => t.IsOpen && t.Fingerprint.Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        double scale = ComputePositionScale(candidate, portfolio, _state.Config);
                        if (scale <= 0)
                            continue;

                        double scaledRisk = ComputeRiskBase(candidate.Analysis) * scale;
                        if (!PassPortfolioRiskGates(_state, portfolio, candidate.Asset, scaledRisk))
                            continue;

                        InternalTrade trade = CreateInternalTrade(candidate, scale, portfolio.EquityUsd, now);
                        _state.OpenTrades.Add(trade);
                        opened++;
                        action = $"Open {candidate.Asset} {candidate.Name}";
                        reason = candidate.Rationale;
                        _monitoring.IncrementCounter("experimental.bot.trades.opened");

                        preStats = BuildStats(_state);
                        portfolio = BuildPortfolio(_state, preStats);
                    }
                }

                if (_state.Config.AutoTune)
                {
                    string? tune = TryAutoTuneConfig(_state);
                    if (!string.IsNullOrWhiteSpace(tune))
                        RecordDecision(_state, _state.LastSignal, "Auto tune", tune);
                }

                RecordDecision(_state, _state.LastSignal, action, reason);
                _state.LastEvaluationAt = now;
                TrimState(_state);
                RefreshDrawdownAnchors(_state);
                cycleStopwatch.Stop();
                _state.LastCycleDurationMs = (int)Math.Clamp(cycleStopwatch.ElapsedMilliseconds, 0, int.MaxValue);
                _state.LastCycleStatus = contexts.Count == 0
                    ? "idle"
                    : opened > 0
                        ? "trading"
                        : closed.Count > 0
                            ? "managed"
                            : "ok";
                PersistStateNoLock(_state);
                PublishPortfolioMetrics(_state);
                dirty = true;
                _monitoring.IncrementCounter("experimental.bot.cycles");
            }

            if (!dirty && _state.LastSignal is null)
            {
                _state.LastSignal = new ExperimentalBotSignal(
                    Bias: "Neutral",
                    Score: 0,
                    Confidence: 0,
                    StrategyTemplate: "No market data",
                    Summary: "Waiting for executable BTC/ETH/SOL option chains.",
                    Drivers: ["No structured candidate available"],
                    Features: new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0),
                    Timestamp: DateTimeOffset.UtcNow);
                _state.LastCycleStatus = "cold";
            }
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private static ExperimentalBotSignal BuildPortfolioSignal(
        IReadOnlyList<AssetSignalContext> contexts,
        IReadOnlyList<CandidateTrade> candidates,
        DateTimeOffset now)
    {
        if (candidates.Count == 0)
        {
            double avgScore = contexts.Count > 0 ? contexts.Average(c => c.Signal.Score) : 0;
            double avgConfidence = contexts.Count > 0 ? contexts.Average(c => c.Signal.Confidence) : 0;
            IReadOnlyList<string> drivers = contexts
                .OrderByDescending(c => c.Signal.Confidence)
                .Take(2)
                .SelectMany(c => c.Drivers.Take(2))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            ExperimentalBotFeatureVector features = contexts.Count > 0
                ? AverageFeatures(contexts.Select(c => c.Features).ToList())
                : new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0);

            return new ExperimentalBotSignal(
                Bias: "Neutral",
                Score: avgScore,
                Confidence: avgConfidence,
                StrategyTemplate: "Hold cash / wait for structured edge",
                Summary: "No BTC/ETH/SOL structured option package clears the current shared risk budget.",
                Drivers: drivers,
                Features: features,
                Timestamp: now);
        }

        CandidateTrade top = candidates[0];
        AssetSignalContext context = contexts.First(c => c.Asset.Equals(top.Asset, StringComparison.OrdinalIgnoreCase));
        return new ExperimentalBotSignal(
            Bias: top.Bias,
            Score: top.Score,
            Confidence: top.Confidence,
            StrategyTemplate: top.Name,
            Summary: $"Best live package: {top.Asset} {top.Name}. {top.Thesis} Neural view: {context.NeuralSignal.Summary}",
            Drivers: top.Drivers.Take(4).ToList(),
            Features: context.Features,
            Timestamp: now);
    }

    private static ExperimentalBotFeatureVector AverageFeatures(IReadOnlyList<ExperimentalBotFeatureVector> features)
    {
        if (features.Count == 0)
            return new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0);

        return new ExperimentalBotFeatureVector(
            FlowImbalance: features.Average(x => x.FlowImbalance),
            SkewSignal: features.Average(x => x.SkewSignal),
            VixProxy: features.Average(x => x.VixProxy),
            FuturesMomentum: features.Average(x => x.FuturesMomentum),
            OrderbookPressure: features.Average(x => x.OrderbookPressure),
            RestingPressure: features.Average(x => x.RestingPressure),
            VolRegime: features.Average(x => x.VolRegime),
            TermSlope: features.Average(x => x.TermSlope));
    }

    private static List<CandidateTrade> BuildCandidates(IReadOnlyList<AssetSignalContext> contexts)
    {
        var candidates = new List<CandidateTrade>(32);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var context in contexts)
        {
            foreach (var idea in context.RelativeValue.TradeIdeas.Take(6))
            {
                CandidateTrade? candidate = CreateCandidateFromRelativeValue(context, idea);
                if (candidate is null || !seen.Add(candidate.Fingerprint))
                    continue;
                candidates.Add(candidate);
            }

            foreach (var rec in context.Recommendations.Recommendations.Take(6))
            {
                CandidateTrade? candidate = CreateCandidateFromRecommendation(context, rec);
                if (candidate is null || !seen.Add(candidate.Fingerprint))
                    continue;
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Confidence)
            .ToList();
    }

    private static CandidateTrade? CreateCandidateFromRelativeValue(AssetSignalContext context, RelativeValueTradeIdea idea)
    {
        StrategyAnalysisResult analysis = idea.Analysis;
        if (analysis.Legs.Count < 2 || !HasSingleExpiry(analysis))
            return null;

        string bias = ClassifyTradeBias(analysis, preferredAction: idea.Action);
        double riskBase = ComputeRiskBase(analysis);
        double evRatio = MathUtils.Clamp(analysis.ExpectedValue / Math.Max(riskBase, 1e-9), -1, 1);
        double convexityScore = ComputeConvexityScore(analysis);
        double neuralAlignment = ScoreNeuralAlignment(idea.Name, bias, context.NeuralSignal);
        double confidence = MathUtils.Clamp(
            idea.ConfidenceScore * 0.45 +
            context.Signal.Confidence * 0.25 +
            context.NeuralSignal.Confidence * 0.20 +
            idea.Score * 0.10,
            1,
            99);
        double score = MathUtils.Clamp(
            idea.Score * 0.48 +
            confidence * 0.14 +
            idea.TradeabilityScore * 0.08 +
            Math.Abs(evRatio) * 100.0 * 0.12 +
            convexityScore * 0.10 +
            context.Recommendations.Regime.ConfidenceScore * 0.05 +
            neuralAlignment * 0.10 +
            Math.Abs(context.NeuralSignal.Score) * 0.05,
            1,
            99);

        Dictionary<string, double> features = BuildCandidateFeatureMap(context.Features, analysis);
        features["rv_edge"] = MathUtils.Clamp(idea.ResidualVolPoints / 20.0, -1, 1);
        features["tradeability"] = MathUtils.Clamp(idea.TradeabilityScore / 100.0, -1, 1);
        features["confidence"] = MathUtils.Clamp(confidence / 100.0, -1, 1);
        features["expected_value"] = evRatio;
        features["reward_risk"] = MathUtils.Clamp(analysis.RewardRiskRatio / 4.0, 0, 1);
        features["pop"] = MathUtils.Clamp(analysis.ProbabilityOfProfitApprox * 2.0 - 1.0, -1, 1);
        features["neural_score"] = MathUtils.Clamp(context.NeuralSignal.Score / 100.0, -1, 1);
        features["neural_confidence"] = MathUtils.Clamp(context.NeuralSignal.Confidence / 100.0, 0, 1);
        features["neural_alignment"] = MathUtils.Clamp((neuralAlignment - 50.0) / 50.0, -1, 1);

        string rationale =
            $"{idea.Thesis} Shared portfolio selected this {context.Asset} package because RV score={idea.Score:F1}, " +
            $"tradeability={idea.TradeabilityScore:F1}, regime={context.Recommendations.Regime.Regime}, " +
            $"neuralAlignment={neuralAlignment:F1}. Macro: {context.NeuralSignal.MacroReasoning} Micro: {context.NeuralSignal.MicroReasoning}";
        string math =
            $"{BuildMathSummary(analysis, idea.ResidualVolPoints, confidence, score)} | " +
            $"neuralScore={context.NeuralSignal.Score:+0.0;-0.0}, neuralConf={context.NeuralSignal.Confidence:F1}, {context.NeuralSignal.MathReasoning}";

        return new CandidateTrade(
            Fingerprint: BuildCandidateFingerprint(context.Asset, idea.Name, analysis),
            Asset: context.Asset,
            Source: "relative-value",
            Name: idea.Name,
            Bias: bias,
            Score: score,
            Confidence: confidence,
            Analysis: analysis,
            Thesis: idea.Thesis,
            Rationale: rationale,
            MathSummary: math,
            EntryPlan: context.NeuralSignal.EntryPlan,
            ExitPlan: context.NeuralSignal.ExitPlan,
            RiskPlan: context.NeuralSignal.RiskPlan,
            Drivers: MergeDrivers(context.Drivers, context.NeuralSignal),
            Features: features);
    }

    private static CandidateTrade? CreateCandidateFromRecommendation(AssetSignalContext context, StrategyRecommendation recommendation)
    {
        StrategyAnalysisResult analysis = recommendation.Analysis;
        if (analysis.Legs.Count < 2 || !HasSingleExpiry(analysis))
            return null;

        string bias = ClassifyTradeBias(analysis);
        double riskBase = ComputeRiskBase(analysis);
        double evRatio = MathUtils.Clamp(analysis.ExpectedValue / Math.Max(riskBase, 1e-9), -1, 1);
        double convexityScore = ComputeConvexityScore(analysis);
        double neuralAlignment = ScoreNeuralAlignment(recommendation.Name, bias, context.NeuralSignal);
        double confidence = MathUtils.Clamp(
            recommendation.ConfidenceScore * 0.44 +
            context.Signal.Confidence * 0.24 +
            context.NeuralSignal.Confidence * 0.18 +
            recommendation.Score * 0.14,
            1,
            99);
        double score = MathUtils.Clamp(
            recommendation.Score * 0.44 +
            confidence * 0.14 +
            recommendation.RegimeFitScore * 0.10 +
            Math.Abs(evRatio) * 100.0 * 0.12 +
            convexityScore * 0.10 +
            Math.Abs(recommendation.EdgeScorePct) * 0.06 +
            neuralAlignment * 0.10 +
            Math.Abs(context.NeuralSignal.Score) * 0.04,
            1,
            99);

        Dictionary<string, double> features = BuildCandidateFeatureMap(context.Features, analysis);
        features["rv_edge"] = MathUtils.Clamp(recommendation.EdgeScorePct / 25.0, -1, 1);
        features["tradeability"] = MathUtils.Clamp((recommendation.ConfidenceScore + recommendation.RegimeFitScore) / 200.0, 0, 1);
        features["confidence"] = MathUtils.Clamp(confidence / 100.0, -1, 1);
        features["expected_value"] = evRatio;
        features["reward_risk"] = MathUtils.Clamp(analysis.RewardRiskRatio / 4.0, 0, 1);
        features["pop"] = MathUtils.Clamp(analysis.ProbabilityOfProfitApprox * 2.0 - 1.0, -1, 1);
        features["neural_score"] = MathUtils.Clamp(context.NeuralSignal.Score / 100.0, -1, 1);
        features["neural_confidence"] = MathUtils.Clamp(context.NeuralSignal.Confidence / 100.0, 0, 1);
        features["neural_alignment"] = MathUtils.Clamp((neuralAlignment - 50.0) / 50.0, -1, 1);

        string rationale =
            $"{recommendation.Thesis} Shared portfolio selected this {context.Asset} package because recScore={recommendation.Score:F1}, " +
            $"regimeFit={recommendation.RegimeFitScore:F1}, edge={recommendation.EdgeScorePct:+0.0;-0.0}%, " +
            $"neuralAlignment={neuralAlignment:F1}. Macro: {context.NeuralSignal.MacroReasoning} Micro: {context.NeuralSignal.MicroReasoning}";
        string math =
            $"{BuildMathSummary(analysis, recommendation.EdgeScorePct, confidence, score)} | " +
            $"neuralScore={context.NeuralSignal.Score:+0.0;-0.0}, neuralConf={context.NeuralSignal.Confidence:F1}, {context.NeuralSignal.MathReasoning}";

        return new CandidateTrade(
            Fingerprint: BuildCandidateFingerprint(context.Asset, recommendation.Name, analysis),
            Asset: context.Asset,
            Source: "recommendation",
            Name: recommendation.Name,
            Bias: bias,
            Score: score,
            Confidence: confidence,
            Analysis: analysis,
            Thesis: recommendation.Thesis,
            Rationale: rationale,
            MathSummary: math,
            EntryPlan: context.NeuralSignal.EntryPlan,
            ExitPlan: context.NeuralSignal.ExitPlan,
            RiskPlan: context.NeuralSignal.RiskPlan,
            Drivers: MergeDrivers(context.Drivers, context.NeuralSignal),
            Features: features);
    }

    private static IReadOnlyList<string> MergeDrivers(IReadOnlyList<string> existingDrivers, NeuralSignalSnapshot neuralSignal)
    {
        var neuralDrivers = neuralSignal.TopPositiveDrivers
            .Take(3)
            .Select(driver => $"{driver.Name}({driver.Contribution:+0.00;-0.00})");

        return existingDrivers
            .Concat(neuralDrivers)
            .Concat([neuralSignal.Bias, neuralSignal.VolatilityBias])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static double ScoreNeuralAlignment(string candidateName, string candidateBias, NeuralSignalSnapshot neuralSignal)
    {
        double score = 45;
        string lowerName = candidateName.ToLowerInvariant();
        string lowerRecommended = neuralSignal.RecommendedStructure.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(neuralSignal.RecommendedStructure))
        {
            if (lowerRecommended.Contains("call") && lowerName.Contains("call")) score += 12;
            if (lowerRecommended.Contains("put") && lowerName.Contains("put")) score += 12;
            if (lowerRecommended.Contains("calendar") && lowerName.Contains("calendar")) score += 16;
            if (lowerRecommended.Contains("butterfly") && lowerName.Contains("butterfly")) score += 14;
            if (lowerRecommended.Contains("spread") && lowerName.Contains("spread")) score += 10;
        }

        score += candidateBias switch
        {
            "Long Vol" when neuralSignal.VolatilityBias == "Long Vol" => 18,
            "Short Vol" when neuralSignal.VolatilityBias == "Short Vol" => 18,
            "Upside Structure" when neuralSignal.Bias.Contains("Upside", StringComparison.OrdinalIgnoreCase) || neuralSignal.Bias.Contains("Bullish", StringComparison.OrdinalIgnoreCase) => 20,
            "Downside Structure" when neuralSignal.Bias.Contains("Downside", StringComparison.OrdinalIgnoreCase) || neuralSignal.Bias.Contains("Bearish", StringComparison.OrdinalIgnoreCase) => 20,
            "Relative Value" when neuralSignal.Bias.Contains("Neutral", StringComparison.OrdinalIgnoreCase) => 8,
            _ => -10
        };

        score += MathUtils.Clamp(neuralSignal.Score / 8.0, -12, 12);
        score += MathUtils.Clamp((neuralSignal.Confidence - 50.0) / 4.0, -8, 12);

        return MathUtils.Clamp(score, 1, 99);
    }

    private static Dictionary<string, double> BuildCandidateFeatureMap(ExperimentalBotFeatureVector features, StrategyAnalysisResult analysis)
    {
        var map = ExtractFeatureMap(features);
        map["convexity"] = MathUtils.Clamp(
            (Math.Abs(analysis.AggregateGreeks.Gamma) * 2800.0 + Math.Abs(analysis.AggregateGreeks.Vega) * 0.08 - Math.Max(analysis.AggregateGreeks.Theta, 0) * 0.02) / 10.0,
            -1,
            1);
        return map;
    }

    private static string BuildCandidateFingerprint(string asset, string name, StrategyAnalysisResult analysis)
    {
        string legs = string.Join(
            "|",
            analysis.Legs
                .OrderBy(leg => leg.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(leg => $"{leg.Symbol}:{leg.Direction}:{leg.Quantity:F4}"));
        return $"{asset}|{name}|{legs}";
    }

    private static string ClassifyTradeBias(StrategyAnalysisResult analysis, string? preferredAction = null)
    {
        string lowerAction = preferredAction?.ToLowerInvariant() ?? string.Empty;
        if (lowerAction.Contains("cheap vol") || lowerAction.Contains("buy vol"))
            return "Long Vol";
        if (lowerAction.Contains("rich vol") || lowerAction.Contains("sell vol"))
            return "Short Vol";

        if (analysis.AggregateGreeks.Vega >= 10 && analysis.AggregateGreeks.Theta <= 0)
            return "Long Vol";
        if (analysis.AggregateGreeks.Vega <= -10 && analysis.AggregateGreeks.Theta >= 0)
            return "Short Vol";
        if (analysis.AggregateGreeks.Delta >= 0.15)
            return "Upside Structure";
        if (analysis.AggregateGreeks.Delta <= -0.15)
            return "Downside Structure";
        return "Relative Value";
    }

    private static double ComputeRiskBase(StrategyAnalysisResult analysis)
    {
        return Math.Max(
            1,
            Math.Max(Math.Abs(analysis.MaxLoss), Math.Abs(analysis.NetPremium)));
    }

    private static bool HasSingleExpiry(StrategyAnalysisResult analysis)
    {
        return analysis.Legs
            .Select(leg => leg.Expiry.Date)
            .Distinct()
            .Take(2)
            .Count() == 1;
    }

    private static double ComputeConvexityScore(StrategyAnalysisResult analysis)
    {
        return MathUtils.Clamp(
            Math.Abs(analysis.AggregateGreeks.Gamma) * 2400.0 +
            Math.Abs(analysis.AggregateGreeks.Vega) * 0.07 +
            Math.Max(-analysis.AggregateGreeks.Theta, 0) * 0.015,
            0,
            30);
    }

    private static string BuildMathSummary(StrategyAnalysisResult analysis, double edgeMetric, double confidence, double score)
    {
        return
            $"score={score:F1}, conf={confidence:F1}, edge={edgeMetric:+0.0;-0.0}, " +
            $"EV={analysis.ExpectedValue:F0}, PoP={analysis.ProbabilityOfProfitApprox:P0}, " +
            $"maxLoss={Math.Abs(analysis.MaxLoss):F0}, maxProfit={analysis.MaxProfit:F0}, RR={analysis.RewardRiskRatio:F2}, " +
            $"delta={analysis.AggregateGreeks.Delta:+0.00;-0.00}, vega={analysis.AggregateGreeks.Vega:+0.0;-0.0}, theta={analysis.AggregateGreeks.Theta:+0.0;-0.0}";
    }

    private static double ComputePositionScale(CandidateTrade candidate, ExperimentalBotPortfolioOverview portfolio, ExperimentalBotConfig config)
    {
        double riskBase = ComputeRiskBase(candidate.Analysis);
        double maxTradeRisk = portfolio.EquityUsd * config.MaxTradeRiskPct;
        if (maxTradeRisk <= 0 || riskBase <= 0)
            return 0;

        double confidenceFactor = MathUtils.Clamp(0.35 + candidate.Confidence / 100.0, 0.35, 1.4);
        double scoreFactor = MathUtils.Clamp(0.35 + candidate.Score / 100.0, 0.35, 1.4);
        double requested = config.BasePositionSize * confidenceFactor * scoreFactor;
        double cap = maxTradeRisk / riskBase;
        double scale = Math.Min(requested, cap);
        return MathUtils.Clamp(scale, 0, 5);
    }

    private static bool PassPortfolioRiskGates(
        BotState state,
        ExperimentalBotPortfolioOverview portfolio,
        string asset,
        double scaledRisk)
    {
        if (scaledRisk <= 0)
            return false;

        if (portfolio.OpenRiskNotionalUsd + scaledRisk > portfolio.EquityUsd * state.Config.PortfolioRiskBudgetPct)
            return false;

        double assetOpenRisk = state.OpenTrades
            .Where(t => t.IsOpen && t.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase))
            .Sum(t => Math.Abs(t.MaxLoss));
        if (assetOpenRisk + scaledRisk > portfolio.EquityUsd * state.Config.MaxAssetRiskPct)
            return false;

        return portfolio.AvailableCapitalUsd >= scaledRisk * 0.35;
    }

    private static InternalTrade CreateInternalTrade(CandidateTrade candidate, double scale, double equityAtEntry, DateTimeOffset now)
    {
        var legs = candidate.Analysis.Legs
            .Select(leg => new InternalTradeLeg
            {
                Symbol = leg.Symbol,
                Asset = candidate.Asset,
                Direction = leg.Direction,
                Quantity = leg.Quantity * scale,
                EntryPrice = leg.EntryPrice,
                MarkPrice = leg.EntryPrice,
                Expiry = leg.Expiry,
                Strike = leg.Strike,
                Right = leg.Right
            })
            .ToList();

        double entryNetPremium = candidate.Analysis.NetPremium * scale;
        double currentLiquidationValue = ComputeLiquidationValue(legs);
        double scaledMaxLoss = candidate.Analysis.MaxLoss * scale;
        double scaledMaxProfit = candidate.Analysis.MaxProfit * scale;
        double scaledExpectedValue = candidate.Analysis.ExpectedValue * scale;
        double scaledScore = candidate.Score;
        double scaledConfidence = candidate.Confidence;
        double riskBase = Math.Max(1, Math.Abs(scaledMaxLoss));

        return new InternalTrade
        {
            TradeId = $"BOT-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            Fingerprint = candidate.Fingerprint,
            Asset = candidate.Asset,
            PrimarySymbol = legs.FirstOrDefault()?.Symbol ?? candidate.Name,
            StrategyTemplate = candidate.Name,
            Bias = candidate.Bias,
            EntryNetPremium = entryNetPremium,
            CurrentLiquidationValue = currentLiquidationValue,
            UnrealizedPnl = 0,
            UnrealizedPnlPct = 0,
            RealizedPnl = 0,
            MaxProfit = scaledMaxProfit,
            MaxLoss = scaledMaxLoss,
            RewardRiskRatio = candidate.Analysis.RewardRiskRatio,
            ProbabilityOfProfitApprox = candidate.Analysis.ProbabilityOfProfitApprox,
            ExpectedValue = scaledExpectedValue,
            EntryScore = scaledScore,
            Confidence = scaledConfidence,
            RiskBudgetPct = equityAtEntry > 0 ? Math.Abs(scaledMaxLoss) / equityAtEntry : 0,
            PortfolioWeightPct = equityAtEntry > 0 ? Math.Abs(entryNetPremium) / equityAtEntry : 0,
            Thesis = candidate.Thesis,
            MathSummary =
                $"{candidate.MathSummary} | realizedScale={scale:F2}, realizedPremium={entryNetPremium:F0}, " +
                $"realizedMaxLoss={Math.Abs(scaledMaxLoss):F0}, realizedMaxProfit={scaledMaxProfit:F0}, realizedEV={scaledExpectedValue:F0} | " +
                $"entryPlan={candidate.EntryPlan} | exitPlan={candidate.ExitPlan} | riskPlan={candidate.RiskPlan}",
            Rationale = candidate.Rationale,
            Drivers = candidate.Drivers.ToList(),
            Features = new Dictionary<string, double>(candidate.Features, StringComparer.OrdinalIgnoreCase),
            Legs = legs,
            IsOpen = true,
            EntryTime = now,
            ExitReason = "open"
        };
    }

    private static List<InternalTrade> UpdateOpenTrades(
        BotState state,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, LiveOptionQuote>> byAssetQuotes,
        IReadOnlyDictionary<string, NeuralSignalSnapshot> neuralByAsset,
        DateTimeOffset now)
    {
        var closed = new List<InternalTrade>();
        foreach (var trade in state.OpenTrades.Where(t => t.IsOpen))
        {
            UpdateTradeMarks(trade, byAssetQuotes);
            double riskBase = Math.Max(1, Math.Max(Math.Abs(trade.MaxLoss), Math.Abs(trade.EntryNetPremium)));

            bool hitStop = trade.UnrealizedPnl <= -riskBase * state.Config.StopLossPct;
            bool hitTarget = trade.UnrealizedPnl >= riskBase * state.Config.TakeProfitPct;
            bool exceededHolding = now - trade.EntryTime >= TimeSpan.FromHours(state.Config.MaxHoldingHours);
            bool neuralExit = neuralByAsset.TryGetValue(trade.Asset, out var neuralSignal) && ShouldExitOnNeuralReversal(trade, neuralSignal);

            if (hitStop || hitTarget || exceededHolding || neuralExit)
            {
                trade.IsOpen = false;
                trade.ExitTime = now;
                trade.RealizedPnl = trade.UnrealizedPnl;
                trade.ExitReason = hitStop
                    ? "risk-stop"
                    : hitTarget
                        ? "target-hit"
                        : exceededHolding
                            ? "time-stop"
                            : "neural-reversal";
                closed.Add(trade);
            }
        }

        if (closed.Count > 0)
            state.OpenTrades.RemoveAll(trade => !trade.IsOpen);

        foreach (var trade in closed)
            state.ClosedTrades.Add(trade);

        RefreshDrawdownAnchors(state);
        return closed;
    }

    private static bool ShouldExitOnNeuralReversal(InternalTrade trade, NeuralSignalSnapshot neuralSignal)
    {
        if (neuralSignal.Confidence <= 28)
            return true;

        bool tradeIsLongVol = trade.Bias.Contains("Long Vol", StringComparison.OrdinalIgnoreCase);
        bool tradeIsShortVol = trade.Bias.Contains("Short Vol", StringComparison.OrdinalIgnoreCase);
        bool tradeIsUpside = trade.Bias.Contains("Upside", StringComparison.OrdinalIgnoreCase) || trade.Bias.Contains("Bullish", StringComparison.OrdinalIgnoreCase);
        bool tradeIsDownside = trade.Bias.Contains("Downside", StringComparison.OrdinalIgnoreCase) || trade.Bias.Contains("Bearish", StringComparison.OrdinalIgnoreCase);

        if (tradeIsLongVol && neuralSignal.VolatilityBias == "Short Vol" && neuralSignal.Score <= -10)
            return true;
        if (tradeIsShortVol && neuralSignal.VolatilityBias == "Long Vol" && neuralSignal.Score >= 10)
            return true;
        if (tradeIsUpside && (neuralSignal.Bias.Contains("Bearish", StringComparison.OrdinalIgnoreCase) || neuralSignal.Bias.Contains("Downside", StringComparison.OrdinalIgnoreCase)) && neuralSignal.Score <= -8)
            return true;
        if (tradeIsDownside && (neuralSignal.Bias.Contains("Bullish", StringComparison.OrdinalIgnoreCase) || neuralSignal.Bias.Contains("Upside", StringComparison.OrdinalIgnoreCase)) && neuralSignal.Score >= 8)
            return true;

        return false;
    }

    private static void UpdateTradeMarks(
        InternalTrade trade,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, LiveOptionQuote>> byAssetQuotes)
    {
        if (!byAssetQuotes.TryGetValue(trade.Asset, out var quotes))
        {
            trade.CurrentLiquidationValue = ComputeLiquidationValue(trade.Legs);
            trade.UnrealizedPnl = ComputePnl(trade.Legs);
            trade.UnrealizedPnlPct = ComputeTradeReturnPct(trade);
            return;
        }

        foreach (var leg in trade.Legs)
        {
            if (quotes.TryGetValue(leg.Symbol, out var quote))
                leg.MarkPrice = Math.Max(EffectiveMid(quote), leg.EntryPrice);
            else
                leg.MarkPrice = leg.EntryPrice;
        }

        trade.CurrentLiquidationValue = ComputeLiquidationValue(trade.Legs);
        trade.UnrealizedPnl = ComputePnl(trade.Legs);
        trade.UnrealizedPnlPct = ComputeTradeReturnPct(trade);
    }

    private static double ComputeLiquidationValue(IReadOnlyList<InternalTradeLeg> legs)
    {
        return legs.Sum(leg =>
        {
            int liquidationSign = leg.Direction == TradeDirection.Buy ? 1 : -1;
            return liquidationSign * leg.MarkPrice * leg.Quantity;
        });
    }

    private static double ComputePnl(IReadOnlyList<InternalTradeLeg> legs)
    {
        return legs.Sum(leg =>
        {
            int sign = leg.Direction == TradeDirection.Buy ? 1 : -1;
            return sign * (leg.MarkPrice - leg.EntryPrice) * leg.Quantity;
        });
    }

    private static double ComputeTradeReturnPct(InternalTrade trade)
    {
        double riskBase = Math.Max(1, Math.Max(Math.Abs(trade.MaxLoss), Math.Abs(trade.EntryNetPremium)));
        return trade.UnrealizedPnl / riskBase;
    }

    private static void LearnFromClosedTrade(BotState state, InternalTrade trade)
    {
        if (trade.Features.Count == 0)
            return;

        double outcome = trade.RealizedPnl >= 0 ? 1 : -1;
        foreach (var (key, featureValue) in trade.Features)
        {
            double current = state.Weights.TryGetValue(key, out var value) ? value : 1;
            double updated = MathUtils.Clamp(current + LearningRate * outcome * featureValue, -3.0, 3.0);
            if (DefaultWeights.TryGetValue(key, out var anchor))
                updated = updated * 0.992 + anchor * 0.008;
            state.Weights[key] = updated;
        }
    }

    private static ExperimentalBotAuditEntry CreateAuditEntry(BotState state, InternalTrade trade, DateTimeOffset now)
    {
        var rolling = ComputeRollingStats(state.ClosedTrades, state.Config.StartingCapitalUsd, 100);
        bool win = trade.RealizedPnl >= 0;
        string comment = win
            ? $"Winner. Structure behaved as expected. {SummarizeTopFeatures(trade.Features, positiveOnly: true)}."
            : $"Loser. Penalizing features after exit. {SummarizeTopFeatures(trade.Features, positiveOnly: false)}.";

        return new ExperimentalBotAuditEntry(
            Timestamp: now,
            TradeId: trade.TradeId,
            Symbol: trade.PrimarySymbol,
            RealizedPnl: trade.RealizedPnl,
            RealizedPnlPct: MathUtils.Clamp(trade.RealizedPnl / Math.Max(1, Math.Abs(trade.MaxLoss)), -5, 5),
            Win: win,
            ExitReason: trade.ExitReason,
            RollingWinRate: rolling.WinRate,
            RollingProfitFactor: rolling.ProfitFactor,
            RollingDrawdownPct: rolling.DrawdownPct,
            LearningComment: comment,
            StrategyTemplate: trade.StrategyTemplate,
            Asset: trade.Asset,
            MaxLoss: trade.MaxLoss,
            RewardRiskRatio: trade.RewardRiskRatio,
            MathSummary: trade.MathSummary);
    }

    private static string? TryAutoTuneConfig(BotState state)
    {
        if (state.ClosedTrades.Count < 8)
            return null;

        var rolling = ComputeRollingStats(state.ClosedTrades, state.Config.StartingCapitalUsd, 100);
        ExperimentalBotConfig current = state.Config;

        double nextMinConfidence = current.MinConfidence;
        double nextStop = current.StopLossPct;
        double nextTake = current.TakeProfitPct;
        double nextRiskBudget = current.PortfolioRiskBudgetPct;

        if (rolling.DrawdownPct > 0.16 || rolling.ProfitFactor < 0.85)
        {
            nextMinConfidence += 1.0;
            nextStop *= 0.95;
            nextRiskBudget *= 0.97;
        }
        else if (rolling.WinRate > 0.62 && rolling.ProfitFactor > 1.25 && rolling.DrawdownPct < 0.10)
        {
            nextMinConfidence -= 0.6;
            nextTake *= 1.03;
            nextRiskBudget *= 1.01;
        }
        else
        {
            return null;
        }

        ExperimentalBotConfig next = current with
        {
            MinConfidence = MathUtils.Clamp(nextMinConfidence, 35, 95),
            StopLossPct = MathUtils.Clamp(nextStop, 0.05, 0.95),
            TakeProfitPct = MathUtils.Clamp(nextTake, 0.05, 3.0),
            PortfolioRiskBudgetPct = MathUtils.Clamp(nextRiskBudget, 0.20, 0.98)
        };

        bool changed =
            Math.Abs(next.MinConfidence - current.MinConfidence) > 1e-9 ||
            Math.Abs(next.StopLossPct - current.StopLossPct) > 1e-9 ||
            Math.Abs(next.TakeProfitPct - current.TakeProfitPct) > 1e-9 ||
            Math.Abs(next.PortfolioRiskBudgetPct - current.PortfolioRiskBudgetPct) > 1e-9;

        if (!changed)
            return null;

        state.Config = next;
        return $"Rolling win={rolling.WinRate:P1}, pf={rolling.ProfitFactor:F2}, dd={rolling.DrawdownPct:P1} -> minConf={next.MinConfidence:F0}, stop={next.StopLossPct:P0}, tp={next.TakeProfitPct:P0}, riskBudget={next.PortfolioRiskBudgetPct:P0}.";
    }

    private static string SummarizeTopFeatures(IReadOnlyDictionary<string, double> features, bool positiveOnly)
    {
        if (features.Count == 0)
            return "no feature trace";

        var ordered = features
            .Where(kv => positiveOnly ? kv.Value >= 0 : kv.Value < 0)
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(3)
            .ToList();

        if (ordered.Count == 0)
            ordered = features.OrderByDescending(kv => Math.Abs(kv.Value)).Take(3).ToList();

        return string.Join(", ", ordered.Select(kv => $"{kv.Key}={(kv.Value >= 0 ? "+" : string.Empty)}{kv.Value:F2}"));
    }

    private static void RecordDecision(BotState state, ExperimentalBotSignal? signal, string action, string reason)
    {
        state.Decisions.Add(new ExperimentalBotDecision(
            Timestamp: DateTimeOffset.UtcNow,
            Bias: signal?.Bias ?? "Neutral",
            Score: signal?.Score ?? 0,
            Confidence: signal?.Confidence ?? 0,
            Action: action,
            Reason: reason));
    }

    private static void TrimState(BotState state)
    {
        const int maxClosed = 2000;
        const int maxDecisions = 1500;
        const int maxAudits = 2200;
        const int maxSpotPoints = 3000;

        if (state.ClosedTrades.Count > maxClosed)
            state.ClosedTrades.RemoveRange(0, state.ClosedTrades.Count - maxClosed);
        if (state.Decisions.Count > maxDecisions)
            state.Decisions.RemoveRange(0, state.Decisions.Count - maxDecisions);
        if (state.Audits.Count > maxAudits)
            state.Audits.RemoveRange(0, state.Audits.Count - maxAudits);
        if (state.SpotHistory.Count > maxSpotPoints)
            state.SpotHistory.RemoveRange(0, state.SpotHistory.Count - maxSpotPoints);
    }

    private static void RefreshDrawdownAnchors(BotState state)
    {
        ExperimentalBotStats stats = BuildStats(state);
        double equity = Math.Max(0, state.Config.StartingCapitalUsd + stats.NetPnl);
        if (state.PeakEquity <= 0)
            state.PeakEquity = Math.Max(state.Config.StartingCapitalUsd, equity);
        if (equity > state.PeakEquity)
            state.PeakEquity = equity;

        double drawdown = Math.Max(0, state.PeakEquity - equity);
        if (drawdown > state.MaxDrawdown)
            state.MaxDrawdown = drawdown;
    }

    private static (double WinRate, double ProfitFactor, double DrawdownPct) ComputeRollingStats(
        IReadOnlyList<InternalTrade> closedTrades,
        double startingCapital,
        int window)
    {
        if (closedTrades.Count == 0)
            return (0, 0, 0);

        int take = Math.Clamp(window, 1, 5000);
        int skip = Math.Max(0, closedTrades.Count - take);
        var sample = closedTrades.Skip(skip).ToList();

        int wins = sample.Count(t => t.RealizedPnl > 0);
        double winRate = sample.Count > 0 ? wins / (double)sample.Count : 0;
        double grossProfit = sample.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
        double grossLoss = sample.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl);
        double profitFactor = grossLoss < 0 ? grossProfit / Math.Abs(grossLoss) : grossProfit > 0 ? 99 : 0;

        double equity = Math.Max(startingCapital, 1e-9);
        double peak = equity;
        double maxDd = 0;
        foreach (var trade in sample)
        {
            equity += trade.RealizedPnl;
            if (equity > peak)
                peak = equity;
            maxDd = Math.Max(maxDd, peak - equity);
        }

        double ddPct = peak > 0 ? maxDd / peak : 0;
        return (winRate, profitFactor, MathUtils.Clamp(ddPct, 0, 1));
    }

    private static ExperimentalBotStats BuildStats(BotState state)
    {
        int openCount = state.OpenTrades.Count;
        int closedCount = state.ClosedTrades.Count;
        int wins = state.ClosedTrades.Count(t => t.RealizedPnl > 0);
        int losses = state.ClosedTrades.Count(t => t.RealizedPnl < 0);
        double realized = state.ClosedTrades.Sum(t => t.RealizedPnl);
        double unrealized = state.OpenTrades.Sum(t => t.UnrealizedPnl);
        double netPnl = realized + unrealized;
        double grossProfit = state.ClosedTrades.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
        double grossLoss = state.ClosedTrades.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl);
        double winRate = closedCount > 0 ? wins / (double)closedCount : 0;
        double profitFactor = grossLoss < 0 ? grossProfit / Math.Abs(grossLoss) : grossProfit > 0 ? 99 : 0;
        double avgTradePnl = closedCount > 0 ? realized / closedCount : 0;

        var returns = state.ClosedTrades
            .Select(trade => trade.RealizedPnl / Math.Max(1, Math.Abs(trade.MaxLoss)))
            .ToList();
        double sharpeLike = 0;
        if (returns.Count >= 3)
        {
            double mean = returns.Average();
            double variance = returns.Sum(x => Math.Pow(x - mean, 2)) / returns.Count;
            double std = Math.Sqrt(Math.Max(variance, 0));
            if (std > 1e-9)
                sharpeLike = mean / std * Math.Sqrt(Math.Min(returns.Count, 252));
        }

        var rolling = ComputeRollingStats(state.ClosedTrades, state.Config.StartingCapitalUsd, 100);
        double utilization = MathUtils.Clamp(
            ComputeOpenRiskNotional(state) / Math.Max(state.Config.StartingCapitalUsd + netPnl, 1e-9),
            0,
            2);

        return new ExperimentalBotStats(
            TotalTrades: openCount + closedCount,
            ClosedTrades: closedCount,
            WinningTrades: wins,
            LosingTrades: losses,
            WinRate: winRate,
            ProfitFactor: profitFactor,
            RealizedPnl: realized,
            UnrealizedPnl: unrealized,
            NetPnl: netPnl,
            AvgTradePnl: avgTradePnl,
            MaxDrawdown: state.MaxDrawdown,
            SharpeLike: sharpeLike,
            LearningRate: LearningRate,
            RollingWinRate100: rolling.WinRate,
            RollingProfitFactor100: rolling.ProfitFactor,
            RollingDrawdownPct100: rolling.DrawdownPct,
            OpenTrades: openCount,
            CapitalUtilizationPct: utilization);
    }

    private static ExperimentalBotPortfolioOverview BuildPortfolio(BotState state, ExperimentalBotStats stats)
    {
        double startingCapital = state.Config.StartingCapitalUsd;
        double equity = Math.Max(0, startingCapital + stats.NetPnl);
        double peak = Math.Max(state.PeakEquity, Math.Max(startingCapital, equity));
        double drawdownUsd = Math.Max(0, peak - equity);
        double drawdownPct = peak > 0 ? drawdownUsd / peak : 0;
        double openRisk = ComputeOpenRiskNotional(state);
        double grossExposure = ComputeGrossExposure(state);
        double available = Math.Max(0, equity - openRisk * 0.55);

        var allocations = ManagedAssets
            .Select(asset =>
            {
                var open = state.OpenTrades.Where(trade => trade.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToList();
                var closed = state.ClosedTrades.Where(trade => trade.Asset.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToList();
                double gross = open.Sum(trade => trade.Legs.Sum(leg => Math.Abs(leg.MarkPrice * leg.Quantity)));
                double risk = open.Sum(trade => Math.Abs(trade.MaxLoss));
                double pnl = open.Sum(trade => trade.UnrealizedPnl) + closed.Sum(trade => trade.RealizedPnl);
                double weightPct = equity > 0 ? gross / equity : 0;
                return new ExperimentalBotAssetAllocation(asset, open.Count, gross, risk, pnl, weightPct);
            })
            .Where(allocation => allocation.OpenTrades > 0 || Math.Abs(allocation.NetPnlUsd) > 1e-9)
            .OrderByDescending(allocation => allocation.GrossExposureUsd)
            .ToList();

        return new ExperimentalBotPortfolioOverview(
            StartingCapitalUsd: startingCapital,
            EquityUsd: equity,
            PeakEquityUsd: peak,
            AvailableCapitalUsd: available,
            OpenRiskNotionalUsd: openRisk,
            DrawdownUsd: drawdownUsd,
            DrawdownPct: drawdownPct,
            GrossExposureUsd: grossExposure,
            OpenTradesCount: state.OpenTrades.Count,
            AssetAllocations: allocations);
    }

    private static ExperimentalBotAuditSnapshot BuildAuditSnapshot(BotState state, ExperimentalBotStats stats)
    {
        int target = Math.Max(1, state.Config.AuditTargetTrades);
        int audited = state.ClosedTrades.Count;
        double completion = MathUtils.Clamp(audited / (double)target, 0, 1);

        string status = audited < target
            ? $"Learning {audited}/{target}"
            : stats.RollingWinRate100 >= 0.58 && stats.RollingProfitFactor100 >= 1.15 && stats.RollingDrawdownPct100 <= 0.18
                ? "Validated"
                : "Needs optimization";

        return new ExperimentalBotAuditSnapshot(
            TargetTrades: target,
            AuditedTrades: audited,
            CompletionPct: completion,
            RollingWinRate: stats.RollingWinRate100,
            RollingProfitFactor: stats.RollingProfitFactor100,
            RollingDrawdownPct: stats.RollingDrawdownPct100,
            Status: status);
    }

    private static ExperimentalBotSnapshot BuildSnapshot(BotState state)
    {
        ExperimentalBotStats stats = BuildStats(state);
        ExperimentalBotPortfolioOverview portfolio = BuildPortfolio(state, stats);
        ExperimentalBotAuditSnapshot audit = BuildAuditSnapshot(state, stats);

        var open = state.OpenTrades
            .OrderByDescending(t => t.EntryTime)
            .Select(ToDto)
            .ToList();
        var closed = state.ClosedTrades
            .OrderByDescending(t => t.ExitTime ?? t.EntryTime)
            .Take(80)
            .Select(ToDto)
            .ToList();
        var audits = state.Audits
            .OrderByDescending(a => a.Timestamp)
            .Take(120)
            .ToList();
        var decisions = state.Decisions
            .OrderByDescending(d => d.Timestamp)
            .Take(80)
            .ToList();
        var weights = state.Weights
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Select(kv => new ExperimentalBotModelWeight(kv.Key, kv.Value))
            .Take(16)
            .ToList();

        string engineSummary = state.LastSignal?.Summary ??
            "Shared BTC/ETH/SOL structured option portfolio. Autopilot ranks relative-value and recommendation packages, then allocates capital under portfolio risk budgets.";

        return new ExperimentalBotSnapshot(
            Asset: PortfolioKey,
            Running: state.Config.Enabled,
            Config: state.Config,
            Signal: state.LastSignal,
            Stats: stats,
            Portfolio: portfolio,
            Audit: audit,
            OpenTrades: open,
            RecentClosedTrades: closed,
            RecentAudits: audits,
            RecentDecisions: decisions,
            Weights: weights,
            StartedAt: state.StartedAt,
            Timestamp: DateTimeOffset.UtcNow,
            Assets: ManagedAssets,
            EngineSummary: engineSummary,
            NeuralSignals: state.NeuralSignals.ToList());
    }

    private static ExperimentalBotTrade ToDto(InternalTrade trade)
    {
        double firstLegQty = trade.Legs.FirstOrDefault()?.Quantity ?? 0;
        return new ExperimentalBotTrade(
            TradeId: trade.TradeId,
            Symbol: trade.PrimarySymbol,
            Side: TradeDirection.Buy,
            Quantity: firstLegQty,
            EntryPrice: Math.Abs(trade.EntryNetPremium),
            MarkPrice: trade.CurrentLiquidationValue,
            UnrealizedPnl: trade.UnrealizedPnl,
            UnrealizedPnlPct: trade.UnrealizedPnlPct,
            EntryTime: trade.EntryTime,
            StrategyTemplate: trade.StrategyTemplate,
            Rationale: trade.Rationale,
            IsOpen: trade.IsOpen,
            ExitTime: trade.ExitTime,
            ExitPrice: trade.CurrentLiquidationValue,
            RealizedPnl: trade.RealizedPnl,
            Asset: trade.Asset,
            Bias: trade.Bias,
            EntryNetPremium: trade.EntryNetPremium,
            CurrentLiquidationValue: trade.CurrentLiquidationValue,
            MaxProfit: trade.MaxProfit,
            MaxLoss: trade.MaxLoss,
            RewardRiskRatio: trade.RewardRiskRatio,
            ProbabilityOfProfitApprox: trade.ProbabilityOfProfitApprox,
            ExpectedValue: trade.ExpectedValue,
            EntryScore: trade.EntryScore,
            Confidence: trade.Confidence,
            RiskBudgetPct: trade.RiskBudgetPct,
            PortfolioWeightPct: trade.PortfolioWeightPct,
            Thesis: trade.Thesis,
            MathSummary: trade.MathSummary,
            Drivers: trade.Drivers.ToList(),
            Legs: trade.Legs.Select(leg => new ExperimentalBotTradeLeg(
                Symbol: leg.Symbol,
                Asset: leg.Asset,
                Direction: leg.Direction,
                Quantity: leg.Quantity,
                EntryPrice: leg.EntryPrice,
                MarkPrice: leg.MarkPrice,
                Expiry: leg.Expiry,
                Strike: leg.Strike,
                Right: leg.Right)).ToList(),
            ExitReason: trade.ExitReason);
    }

    private async Task<ExperimentalBotSnapshot> BuildSnapshotThreadSafeAsync(CancellationToken ct)
    {
        await _state.Gate.WaitAsync(ct);
        try
        {
            return BuildSnapshot(_state);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private static void ReplaceState(BotState target, BotState source)
    {
        target.Config = source.Config;
        target.StartedAt = source.StartedAt;
        target.LastEvaluationAt = source.LastEvaluationAt;
        target.LastSignal = source.LastSignal;
        target.StateVersion = source.StateVersion;
        target.LastPersistedAt = source.LastPersistedAt;
        target.LastCycleStatus = source.LastCycleStatus;
        target.LastCycleDurationMs = source.LastCycleDurationMs;
        target.PeakEquity = source.PeakEquity;
        target.MaxDrawdown = source.MaxDrawdown;

        target.NeuralSignals.Clear();
        target.NeuralSignals.AddRange(source.NeuralSignals);
        target.OpenTrades.Clear();
        target.OpenTrades.AddRange(source.OpenTrades);
        target.ClosedTrades.Clear();
        target.ClosedTrades.AddRange(source.ClosedTrades);
        target.Audits.Clear();
        target.Audits.AddRange(source.Audits);
        target.Decisions.Clear();
        target.Decisions.AddRange(source.Decisions);
        target.SpotHistory.Clear();
        target.SpotHistory.AddRange(source.SpotHistory);
        target.Weights.Clear();
        foreach (var (key, value) in source.Weights)
            target.Weights[key] = value;
    }

    private BotState LoadOrCreateState()
    {
        try
        {
            BotStateRecord? record = _repository.Load(PortfolioKey);
            if (record is null || string.IsNullOrWhiteSpace(record.StateJson))
                return CreateDefaultState();

            PersistedBotState? persisted = JsonSerializer.Deserialize<PersistedBotState>(record.StateJson, _jsonOptions);
            if (persisted is null)
                return CreateDefaultState();

            BotState state = CreateDefaultState();
            state.StateVersion = Math.Max(0, record.StateVersion);
            state.LastPersistedAt = record.LastPersistedAt;
            state.LastCycleStatus = string.IsNullOrWhiteSpace(record.LastCycleStatus) ? "cold" : record.LastCycleStatus;
            state.LastCycleDurationMs = Math.Max(0, record.LastCycleDurationMs);
            state.Config = ApplyConfigRequest(DefaultConfig(), new ExperimentalBotConfigRequest(
                Enabled: persisted.Config.Enabled,
                AutoTrade: persisted.Config.AutoTrade,
                AutoTune: persisted.Config.AutoTune,
                EvaluationIntervalSec: persisted.Config.EvaluationIntervalSec,
                BasePositionSize: persisted.Config.BasePositionSize,
                MinConfidence: persisted.Config.MinConfidence,
                StopLossPct: persisted.Config.StopLossPct,
                TakeProfitPct: persisted.Config.TakeProfitPct,
                MaxHoldingHours: persisted.Config.MaxHoldingHours,
                AuditTargetTrades: persisted.Config.AuditTargetTrades,
                StartingCapitalUsd: persisted.Config.StartingCapitalUsd,
                PortfolioRiskBudgetPct: persisted.Config.PortfolioRiskBudgetPct,
                MaxAssetRiskPct: persisted.Config.MaxAssetRiskPct,
                MaxTradeRiskPct: persisted.Config.MaxTradeRiskPct,
                MaxNewTradesPerCycle: persisted.Config.MaxNewTradesPerCycle));
            state.StartedAt = persisted.StartedAt == default ? DateTimeOffset.UtcNow : persisted.StartedAt;
            state.LastEvaluationAt = persisted.LastEvaluationAt;
            state.LastSignal = persisted.LastSignal;
            state.NeuralSignals.AddRange(persisted.NeuralSignals);
            state.OpenTrades.AddRange(persisted.OpenTrades);
            state.ClosedTrades.AddRange(persisted.ClosedTrades);
            state.Decisions.AddRange(persisted.Decisions);
            state.Audits.AddRange(persisted.Audits);
            state.SpotHistory.AddRange(persisted.SpotHistory.Where(p => p.Spot > 0));
            state.PeakEquity = Math.Max(persisted.PeakEquity, state.Config.StartingCapitalUsd);
            state.MaxDrawdown = Math.Max(0, persisted.MaxDrawdown);

            state.Weights.Clear();
            foreach (var (key, value) in DefaultWeights)
                state.Weights[key] = value;
            foreach (var (key, value) in persisted.Weights)
                state.Weights[key] = MathUtils.Clamp(value, -3, 3);

            TrimState(state);
            RefreshDrawdownAnchors(state);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load shared experimental bot state, starting fresh");
            return CreateDefaultState();
        }
    }

    private static BotState CreateDefaultState()
    {
        var config = DefaultConfig();
        var state = new BotState
        {
            Config = config,
            StartedAt = DateTimeOffset.UtcNow,
            StateVersion = 0,
            LastPersistedAt = DateTimeOffset.MinValue,
            LastCycleStatus = "cold",
            LastCycleDurationMs = 0,
            PeakEquity = config.StartingCapitalUsd,
            MaxDrawdown = 0
        };
        foreach (var (key, value) in DefaultWeights)
            state.Weights[key] = value;
        return state;
    }

    private void PersistStateNoLock(BotState state)
    {
        try
        {
            var persisted = new PersistedBotState
            {
                Config = state.Config with { ManagedAssets = ManagedAssets },
                StartedAt = state.StartedAt,
                LastEvaluationAt = state.LastEvaluationAt,
                LastSignal = state.LastSignal,
                NeuralSignals = state.NeuralSignals.ToList(),
                OpenTrades = state.OpenTrades.ToList(),
                ClosedTrades = state.ClosedTrades.ToList(),
                Decisions = state.Decisions.ToList(),
                Audits = state.Audits.ToList(),
                SpotHistory = state.SpotHistory.ToList(),
                Weights = new Dictionary<string, double>(state.Weights, StringComparer.OrdinalIgnoreCase),
                PeakEquity = state.PeakEquity,
                MaxDrawdown = state.MaxDrawdown
            };

            BotStateRecord saved = _repository.Save(new BotStateSaveRequest(
                BotKey: PortfolioKey,
                StateJson: JsonSerializer.Serialize(persisted, _jsonOptions),
                ExpectedStateVersion: state.StateVersion,
                LastEvaluationAt: state.LastEvaluationAt == DateTimeOffset.MinValue ? null : state.LastEvaluationAt,
                LastCycleStatus: state.LastCycleStatus,
                LastCycleDurationMs: state.LastCycleDurationMs));

            state.StateVersion = saved.StateVersion;
            state.LastPersistedAt = saved.LastPersistedAt;
            state.LastCycleStatus = saved.LastCycleStatus;
            state.LastCycleDurationMs = saved.LastCycleDurationMs;
        }
        catch (BotStateConflictException ex)
        {
            _logger.LogWarning(ex, "Bot runtime state conflict detected, reloading latest repository state");
            ReloadStateFromRepositoryNoLock();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist shared experimental bot state");
            _monitoring.PublishAlert(
                "experimental-bot",
                NotificationSeverity.Warning,
                $"Shared portfolio persistence failure: {ex.GetType().Name}");
            throw;
        }
    }

    private void ReloadStateFromRepositoryNoLock()
    {
        BotStateRecord? record = _repository.Load(PortfolioKey);
        if (record is null || string.IsNullOrWhiteSpace(record.StateJson))
            return;

        if (_state.StateVersion > 0 && record.StateVersion <= _state.StateVersion)
            return;

        PersistedBotState? persisted = JsonSerializer.Deserialize<PersistedBotState>(record.StateJson, _jsonOptions);
        if (persisted is null)
            return;

        BotState fresh = CreateDefaultState();
        fresh.StateVersion = Math.Max(0, record.StateVersion);
        fresh.LastPersistedAt = record.LastPersistedAt;
        fresh.LastCycleStatus = string.IsNullOrWhiteSpace(record.LastCycleStatus) ? "cold" : record.LastCycleStatus;
        fresh.LastCycleDurationMs = Math.Max(0, record.LastCycleDurationMs);
        fresh.Config = ApplyConfigRequest(DefaultConfig(), new ExperimentalBotConfigRequest(
            Enabled: persisted.Config.Enabled,
            AutoTrade: persisted.Config.AutoTrade,
            AutoTune: persisted.Config.AutoTune,
            EvaluationIntervalSec: persisted.Config.EvaluationIntervalSec,
            BasePositionSize: persisted.Config.BasePositionSize,
            MinConfidence: persisted.Config.MinConfidence,
            StopLossPct: persisted.Config.StopLossPct,
            TakeProfitPct: persisted.Config.TakeProfitPct,
            MaxHoldingHours: persisted.Config.MaxHoldingHours,
            AuditTargetTrades: persisted.Config.AuditTargetTrades,
            StartingCapitalUsd: persisted.Config.StartingCapitalUsd,
            PortfolioRiskBudgetPct: persisted.Config.PortfolioRiskBudgetPct,
            MaxAssetRiskPct: persisted.Config.MaxAssetRiskPct,
            MaxTradeRiskPct: persisted.Config.MaxTradeRiskPct,
            MaxNewTradesPerCycle: persisted.Config.MaxNewTradesPerCycle));
        fresh.StartedAt = persisted.StartedAt == default ? DateTimeOffset.UtcNow : persisted.StartedAt;
        fresh.LastEvaluationAt = persisted.LastEvaluationAt;
        fresh.LastSignal = persisted.LastSignal;
        fresh.NeuralSignals.AddRange(persisted.NeuralSignals);
        fresh.OpenTrades.AddRange(persisted.OpenTrades);
        fresh.ClosedTrades.AddRange(persisted.ClosedTrades);
        fresh.Decisions.AddRange(persisted.Decisions);
        fresh.Audits.AddRange(persisted.Audits);
        fresh.SpotHistory.AddRange(persisted.SpotHistory.Where(p => p.Spot > 0));
        fresh.PeakEquity = Math.Max(persisted.PeakEquity, fresh.Config.StartingCapitalUsd);
        fresh.MaxDrawdown = Math.Max(0, persisted.MaxDrawdown);
        fresh.Weights.Clear();
        foreach (var (key, value) in DefaultWeights)
            fresh.Weights[key] = value;
        foreach (var (key, value) in persisted.Weights)
            fresh.Weights[key] = MathUtils.Clamp(value, -3, 3);

        TrimState(fresh);
        RefreshDrawdownAnchors(fresh);
        ReplaceState(_state, fresh);
    }

    private async Task RefreshFromRepositoryThreadSafeAsync(CancellationToken ct)
    {
        await _state.Gate.WaitAsync(ct);
        try
        {
            ReloadStateFromRepositoryNoLock();
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private void EnsureMutationAllowed()
    {
        if (_runtime.CanRunBotLoop)
            return;

        throw new InvalidOperationException(
            $"Bot mutations are disabled for runtime role {_runtime.Role}. Use bot-worker or all mode.");
    }

    private void PublishPortfolioMetrics(BotState state)
    {
        ExperimentalBotStats stats = BuildStats(state);
        ExperimentalBotPortfolioOverview portfolio = BuildPortfolio(state, stats);
        _monitoring.RecordGauge("experimental.bot.multi.net_pnl", stats.NetPnl, "usd");
        _monitoring.RecordGauge("experimental.bot.multi.win_rate", stats.WinRate, "ratio");
        _monitoring.RecordGauge("experimental.bot.multi.drawdown_pct", portfolio.DrawdownPct, "ratio");
        _monitoring.RecordGauge("experimental.bot.multi.equity", portfolio.EquityUsd, "usd");
        _monitoring.RecordGauge("experimental.bot.multi.open_risk", portfolio.OpenRiskNotionalUsd, "usd");
    }

    private static double ComputeOpenRiskNotional(BotState state) =>
        state.OpenTrades.Sum(trade => Math.Abs(trade.MaxLoss));

    private static double ComputeGrossExposure(BotState state) =>
        state.OpenTrades.Sum(trade => trade.Legs.Sum(leg => Math.Abs(leg.MarkPrice * leg.Quantity)));

    private static void AppendSpotHistory(BotState state, string asset, DateTimeOffset now, double spot)
    {
        state.SpotHistory.Add(new SpotPoint(asset, now, spot));
        while (state.SpotHistory.Count > 0 && now - state.SpotHistory[0].Time > TimeSpan.FromDays(10))
            state.SpotHistory.RemoveAt(0);
        while (state.SpotHistory.Count > 3200)
            state.SpotHistory.RemoveAt(0);
    }

    private static (ExperimentalBotFeatureVector Features, IReadOnlyList<string> Drivers, double LiquidityScore) ComputeFeatureVector(
        IReadOnlyList<LiveOptionQuote> chain,
        double spot,
        IReadOnlyList<SpotPoint> spotHistory)
    {
        var valid = chain
            .Where(q => q.Strike > 0 && q.Expiry > DateTimeOffset.UtcNow)
            .ToList();

        var calls = valid.Where(q => q.Right == OptionRight.Call).ToList();
        var puts = valid.Where(q => q.Right == OptionRight.Put).ToList();
        double callTurnover = calls.Sum(q => Math.Max(0, q.Turnover24h));
        double putTurnover = puts.Sum(q => Math.Max(0, q.Turnover24h));
        double flowImbalance = MathUtils.Clamp((callTurnover - putTurnover) / Math.Max(callTurnover + putTurnover, 1e-9), -1, 1);

        var call25 = calls
            .Where(q => q.Delta > 0 && q.MarkIv > 0)
            .OrderBy(q => Math.Abs(q.Delta - 0.25))
            .ThenByDescending(q => q.Turnover24h)
            .FirstOrDefault();
        var put25 = puts
            .Where(q => q.Delta < 0 && q.MarkIv > 0)
            .OrderBy(q => Math.Abs(q.Delta + 0.25))
            .ThenByDescending(q => q.Turnover24h)
            .FirstOrDefault();

        double atmIv30 = ComputeAtmForTargetDays(valid, spot, 30);
        double atmIv90 = ComputeAtmForTargetDays(valid, spot, 90);
        double termSlopeRaw = atmIv90 - atmIv30;
        double termSlope = MathUtils.Clamp(termSlopeRaw * 12, -1, 1);
        double skewRaw = (call25?.MarkIv ?? atmIv30) - (put25?.MarkIv ?? atmIv30);
        double skewSignal = MathUtils.Clamp(skewRaw / Math.Max(atmIv30, 0.08), -1, 1);
        double vixProxy = MathUtils.Clamp((0.70 - atmIv30) / 0.50, -1, 1);
        double volRegime = MathUtils.Clamp((0.62 - atmIv30) * 1.8 + termSlopeRaw * 3.5, -1, 1);
        double orderbookPressure = ComputeOrderbookPressure(valid);
        double callOi = calls.Sum(q => Math.Max(0, q.OpenInterest));
        double putOi = puts.Sum(q => Math.Max(0, q.OpenInterest));
        double restingPressure = MathUtils.Clamp((callOi - putOi) / Math.Max(callOi + putOi, 1e-9), -1, 1);
        double momentum5m = ComputeMomentum(spotHistory, TimeSpan.FromMinutes(5));
        double momentum1h = ComputeMomentum(spotHistory, TimeSpan.FromHours(1));
        double futuresMomentum = MathUtils.Clamp(momentum5m * 2.2 + momentum1h * 1.2, -1, 1);

        double avgSpread = valid
            .Select(q =>
            {
                double mid = EffectiveMid(q);
                if (mid <= 0 || q.Bid <= 0 || q.Ask <= 0) return 1.0;
                return MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 2.0);
            })
            .DefaultIfEmpty(1.0)
            .Average();
        double totalTurnover = valid.Sum(q => Math.Max(0, q.Turnover24h));
        double totalOi = valid.Sum(q => Math.Max(0, q.OpenInterest));
        double liquidityScore = MathUtils.Clamp(Math.Log(1 + totalTurnover + totalOi * Math.Max(spot * 0.004, 1)) - avgSpread * 3.0, -8, 20);

        var features = new ExperimentalBotFeatureVector(
            FlowImbalance: flowImbalance,
            SkewSignal: skewSignal,
            VixProxy: vixProxy,
            FuturesMomentum: futuresMomentum,
            OrderbookPressure: orderbookPressure,
            RestingPressure: restingPressure,
            VolRegime: volRegime,
            TermSlope: termSlope);

        return (features, BuildDriverText(features), liquidityScore);
    }

    private static IReadOnlyList<string> BuildDriverText(ExperimentalBotFeatureVector features)
    {
        var drivers = new List<(string Label, double Value)>
        {
            ("Flow imbalance", features.FlowImbalance),
            ("Skew signal", features.SkewSignal),
            ("VIX proxy", features.VixProxy),
            ("Futures momentum", features.FuturesMomentum),
            ("Orderbook pressure", features.OrderbookPressure),
            ("Resting pressure", features.RestingPressure),
            ("Vol regime", features.VolRegime),
            ("Term slope", features.TermSlope)
        };

        return drivers
            .OrderByDescending(x => Math.Abs(x.Value))
            .Take(4)
            .Select(x => $"{x.Label}: {(x.Value >= 0 ? "+" : string.Empty)}{x.Value:F2}")
            .ToList();
    }

    private static ExperimentalBotSignal BuildSignal(
        string asset,
        IReadOnlyDictionary<string, double> weights,
        ExperimentalBotFeatureVector features,
        IReadOnlyList<string> drivers,
        double liquidityScore,
        DateTimeOffset now)
    {
        var map = ExtractFeatureMap(features);
        double raw = 0;
        foreach (var (key, value) in map)
        {
            double weight = weights.TryGetValue(key, out var configured) ? configured : 1;
            raw += weight * value;
        }

        double score = MathUtils.Clamp(raw * 20, -100, 100);
        string bias = score switch
        {
            >= 12 => "Upside Convexity",
            <= -12 => "Downside Convexity",
            _ when features.VixProxy > 0.20 => "Long Vol",
            _ when features.VixProxy < -0.10 => "Carry",
            _ => "Neutral"
        };

        double confidence = MathUtils.Clamp(
            36 + Math.Abs(score) * 0.48 + MathUtils.Clamp(liquidityScore * 2.0, -8, 24),
            5,
            99);

        string strategyTemplate = bias switch
        {
            "Upside Convexity" => "Call spread / calendar",
            "Downside Convexity" => "Put spread / put butterfly",
            "Long Vol" => "Calendar / debit spread",
            "Carry" => "Defined-risk premium harvest",
            _ => "No trade"
        };

        return new ExperimentalBotSignal(
            Bias: bias,
            Score: score,
            Confidence: confidence,
            StrategyTemplate: strategyTemplate,
            Summary: $"{asset} regime {bias} ({score:+0.0;-0.0}) | conf {confidence:F1} | drivers: {string.Join(", ", drivers.Take(3))}.",
            Drivers: drivers,
            Features: features,
            Timestamp: now);
    }

    private static Dictionary<string, double> ExtractFeatureMap(ExperimentalBotFeatureVector features)
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["flow_imbalance"] = features.FlowImbalance,
            ["skew_signal"] = features.SkewSignal,
            ["vix_proxy"] = features.VixProxy,
            ["futures_momentum"] = features.FuturesMomentum,
            ["orderbook_pressure"] = features.OrderbookPressure,
            ["resting_pressure"] = features.RestingPressure,
            ["vol_regime"] = features.VolRegime,
            ["term_slope"] = features.TermSlope
        };
    }

    private static double ComputeAtmForTargetDays(IReadOnlyList<LiveOptionQuote> quotes, double spot, int targetDays)
    {
        var candidates = quotes
            .Where(q => q.MarkIv > 0)
            .Select(q => new
            {
                Quote = q,
                Dte = Math.Max(0.0, (q.Expiry - DateTimeOffset.UtcNow).TotalDays)
            })
            .OrderBy(x => Math.Abs(x.Dte - targetDays) * 0.8 + Math.Abs(x.Quote.Strike - spot) / Math.Max(spot, 1e-9) * 100.0)
            .Take(4)
            .ToList();

        return candidates.Count > 0 ? candidates.Average(x => x.Quote.MarkIv) : 0;
    }

    private static double ComputeOrderbookPressure(IReadOnlyList<LiveOptionQuote> quotes)
    {
        var samples = quotes
            .Select(q =>
            {
                double mid = EffectiveMid(q);
                if (mid <= 0 || q.Bid <= 0 || q.Ask <= 0) return 0;
                double spread = MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 2);
                return MathUtils.Clamp((0.40 - spread) * 2.2, -1, 1);
            })
            .ToList();
        return samples.Count > 0 ? samples.Average() : 0;
    }

    private static double ComputeMomentum(IReadOnlyList<SpotPoint> history, TimeSpan lookback)
    {
        if (history.Count < 2)
            return 0;

        DateTimeOffset latest = history[^1].Time;
        double currentSpot = history[^1].Spot;
        SpotPoint? previous = history
            .Where(point => latest - point.Time >= lookback)
            .OrderByDescending(point => point.Time)
            .FirstOrDefault();
        if (previous is null || previous.Spot <= 0 || currentSpot <= 0)
            return 0;
        return MathUtils.Clamp((currentSpot - previous.Spot) / previous.Spot, -1, 1);
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
