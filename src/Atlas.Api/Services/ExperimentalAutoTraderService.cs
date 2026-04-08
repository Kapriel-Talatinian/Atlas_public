using System.Collections.Concurrent;
using System.Text.Json;
using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface IExperimentalAutoTraderService
{
    Task<ExperimentalBotSnapshot> GetSnapshotAsync(string asset, CancellationToken ct = default);
    Task<ExperimentalBotModelExplainSnapshot> GetModelExplainAsync(string asset, CancellationToken ct = default);
    Task<ExperimentalBotSnapshot> ConfigureAsync(string asset, ExperimentalBotConfigRequest request, CancellationToken ct = default);
    Task<ExperimentalBotSnapshot> RunCycleAsync(string asset, int cycles = 1, CancellationToken ct = default);
    Task<ExperimentalBotSnapshot> ResetAsync(string asset, CancellationToken ct = default);
    Task RunAutopilotAsync(CancellationToken ct = default);
}

public sealed class ExperimentalAutoTraderService : IExperimentalAutoTraderService
{
    private sealed class InternalTrade
    {
        public string TradeId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public TradeDirection Side { get; set; } = TradeDirection.Buy;
        public double Quantity { get; set; }
        public double EntryPrice { get; set; }
        public DateTimeOffset EntryTime { get; set; }
        public string StrategyTemplate { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public double EntrySignalScore { get; set; }
        public Dictionary<string, double> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsOpen { get; set; } = true;
        public double MarkPrice { get; set; }
        public double UnrealizedPnl { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public DateTimeOffset? ExitTime { get; set; }
        public double ExitPrice { get; set; }
        public double RealizedPnl { get; set; }
        public string ExitReason { get; set; } = "open";
    }

    private sealed class PersistedTrade
    {
        public string TradeId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public TradeDirection Side { get; set; } = TradeDirection.Buy;
        public double Quantity { get; set; }
        public double EntryPrice { get; set; }
        public DateTimeOffset EntryTime { get; set; }
        public string StrategyTemplate { get; set; } = string.Empty;
        public string Rationale { get; set; } = string.Empty;
        public double EntrySignalScore { get; set; }
        public Dictionary<string, double> Features { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsOpen { get; set; }
        public double MarkPrice { get; set; }
        public double UnrealizedPnl { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public DateTimeOffset? ExitTime { get; set; }
        public double ExitPrice { get; set; }
        public double RealizedPnl { get; set; }
        public string ExitReason { get; set; } = "open";
    }

    private sealed record SpotPoint(DateTimeOffset Time, double Spot);

    private sealed class PersistedBotState
    {
        public ExperimentalBotConfig Config { get; set; } = new();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public ExperimentalBotSignal? LastSignal { get; set; }
        public List<PersistedTrade> OpenTrades { get; set; } = [];
        public List<PersistedTrade> ClosedTrades { get; set; } = [];
        public List<ExperimentalBotDecision> Decisions { get; set; } = [];
        public List<SpotPoint> SpotHistory { get; set; } = [];
        public Dictionary<string, double> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ExperimentalBotAuditEntry> Audits { get; set; } = [];
        public double PeakEquity { get; set; }
        public double MaxDrawdown { get; set; }
    }

    private sealed class BotState
    {
        public ExperimentalBotConfig Config { get; set; } = new();
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public ExperimentalBotSignal? LastSignal { get; set; }
        public List<InternalTrade> OpenTrades { get; } = [];
        public List<InternalTrade> ClosedTrades { get; } = [];
        public List<ExperimentalBotAuditEntry> Audits { get; } = [];
        public List<ExperimentalBotDecision> Decisions { get; } = [];
        public List<(DateTimeOffset Time, double Spot)> SpotHistory { get; } = [];
        public Dictionary<string, double> Weights { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public double PeakEquity { get; set; }
        public double MaxDrawdown { get; set; }
    }

    private static readonly IReadOnlyDictionary<string, double> DefaultWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["flow_imbalance"] = 1.20,
        ["skew_signal"] = 0.92,
        ["vix_proxy"] = 0.78,
        ["futures_momentum"] = 1.05,
        ["orderbook_pressure"] = 0.88,
        ["resting_pressure"] = 0.72,
        ["vol_regime"] = 0.66,
        ["term_slope"] = 0.58
    };

    private const double LearningRate = 0.035;
    private readonly ConcurrentDictionary<string, BotState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMarketDataService _marketData;
    private readonly ISystemMonitoringService _monitoring;
    private readonly ILogger<ExperimentalAutoTraderService> _logger;
    private readonly string _stateDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExperimentalAutoTraderService(
        IOptionsMarketDataService marketData,
        ISystemMonitoringService monitoring,
        IHostEnvironment hostEnvironment,
        ILogger<ExperimentalAutoTraderService> logger)
    {
        _marketData = marketData;
        _monitoring = monitoring;
        _logger = logger;

        string configured = Environment.GetEnvironmentVariable("EXPERIMENTAL_BOT_STATE_DIR") ?? string.Empty;
        _stateDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(hostEnvironment.ContentRootPath, "data", "experimental-bot")
            : configured.Trim();

        Directory.CreateDirectory(_stateDirectory);
    }

    public async Task<ExperimentalBotSnapshot> GetSnapshotAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        BotState state = GetOrCreateState(normalizedAsset);
        await EvaluateIfDueAsync(normalizedAsset, state, force: false, cycles: 1, ct);
        return await BuildSnapshotThreadSafeAsync(normalizedAsset, state, ct);
    }

    public async Task<ExperimentalBotModelExplainSnapshot> GetModelExplainAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        BotState state = GetOrCreateState(normalizedAsset);
        await EvaluateIfDueAsync(normalizedAsset, state, force: false, cycles: 1, ct);

        await state.Gate.WaitAsync(ct);
        try
        {
            ExperimentalBotSignal signal = state.LastSignal ?? new ExperimentalBotSignal(
                Bias: "Neutral",
                Score: 0,
                Confidence: 0,
                StrategyTemplate: "N/A",
                Summary: "No active signal.",
                Drivers: [],
                Features: new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0),
                Timestamp: DateTimeOffset.UtcNow);

            Dictionary<string, double> featureMap = ExtractFeatureMap(signal.Features);
            var contributions = featureMap
                .Select(kv =>
                {
                    double weight = state.Weights.TryGetValue(kv.Key, out var w) ? w : 1;
                    double contribution = weight * kv.Value;
                    return new ExperimentalBotFeatureContribution(
                        Feature: kv.Key,
                        Weight: weight,
                        FeatureValue: kv.Value,
                        Contribution: contribution);
                })
                .OrderByDescending(c => Math.Abs(c.Contribution))
                .ToList();

            var positives = contributions
                .Where(c => c.Contribution >= 0)
                .OrderByDescending(c => c.Contribution)
                .Take(4)
                .ToList();
            var negatives = contributions
                .Where(c => c.Contribution < 0)
                .OrderBy(c => c.Contribution)
                .Take(4)
                .ToList();

            var audits = state.Audits
                .OrderByDescending(a => a.Timestamp)
                .Take(20)
                .ToList();

            string narrative = BuildExplainNarrative(signal, positives, negatives, audits);
            return new ExperimentalBotModelExplainSnapshot(
                Asset: normalizedAsset,
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
            state.Gate.Release();
        }
    }

    public async Task<ExperimentalBotSnapshot> ConfigureAsync(string asset, ExperimentalBotConfigRequest request, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        BotState state = GetOrCreateState(normalizedAsset);

        await state.Gate.WaitAsync(ct);
        try
        {
            ExperimentalBotConfig previous = state.Config;
            state.Config = ApplyConfigRequest(state.Config, request);
            RefreshDrawdownAnchors(state);

            RecordDecision(
                state,
                bias: state.LastSignal?.Bias ?? "Neutral",
                score: state.LastSignal?.Score ?? 0,
                confidence: state.LastSignal?.Confidence ?? 0,
                action: state.Config.Enabled
                    ? state.Config.AutoTrade ? "Bot enabled with auto-trading" : "Bot enabled (manual mode)"
                    : "Bot disabled",
                reason:
                    $"interval={state.Config.EvaluationIntervalSec}s, minConf={state.Config.MinConfidence:F0}, " +
                    $"autotune={(state.Config.AutoTune ? "on" : "off")}, " +
                    $"capital={state.Config.StartingCapitalUsd:F0}, " +
                    $"auditTarget={state.Config.AuditTargetTrades}");

            if (Math.Abs(previous.StartingCapitalUsd - state.Config.StartingCapitalUsd) > 1e-9)
            {
                _monitoring.PublishAlert(
                    "experimental-bot",
                    NotificationSeverity.Info,
                    $"{normalizedAsset} bot capital base updated to {state.Config.StartingCapitalUsd:F0} USD");
            }

            PersistStateNoLock(normalizedAsset, state);
        }
        finally
        {
            state.Gate.Release();
        }

        await EvaluateIfDueAsync(normalizedAsset, state, force: true, cycles: 1, ct);
        return await BuildSnapshotThreadSafeAsync(normalizedAsset, state, ct);
    }

    public async Task<ExperimentalBotSnapshot> RunCycleAsync(string asset, int cycles = 1, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        BotState state = GetOrCreateState(normalizedAsset);
        await EvaluateIfDueAsync(normalizedAsset, state, force: true, cycles: cycles, ct);
        return await BuildSnapshotThreadSafeAsync(normalizedAsset, state, ct);
    }

    public async Task<ExperimentalBotSnapshot> ResetAsync(string asset, CancellationToken ct = default)
    {
        string normalizedAsset = NormalizeAsset(asset);
        BotState state = CreateDefaultState();
        _states[normalizedAsset] = state;

        await EvaluateIfDueAsync(normalizedAsset, state, force: true, cycles: 1, ct);

        await state.Gate.WaitAsync(ct);
        try
        {
            PersistStateNoLock(normalizedAsset, state);
        }
        finally
        {
            state.Gate.Release();
        }

        return await BuildSnapshotThreadSafeAsync(normalizedAsset, state, ct);
    }

    public async Task RunAutopilotAsync(CancellationToken ct = default)
    {
        var states = _states.ToArray();
        if (states.Length == 0)
            return;

        foreach (var kv in states)
        {
            string asset = kv.Key;
            BotState state = kv.Value;

            bool shouldRun;
            await state.Gate.WaitAsync(ct);
            try
            {
                shouldRun = state.Config.Enabled || state.Config.AutoTrade || state.OpenTrades.Count > 0;
            }
            finally
            {
                state.Gate.Release();
            }

            if (!shouldRun)
                continue;

            try
            {
                await EvaluateIfDueAsync(asset, state, force: false, cycles: 1, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Experimental bot autopilot failed for {Asset}", asset);
                _monitoring.PublishAlert(
                    "experimental-bot",
                    NotificationSeverity.Warning,
                    $"Autopilot cycle failed on {asset}: {ex.GetType().Name}");
            }
        }
    }

    private static ExperimentalBotConfig ApplyConfigRequest(ExperimentalBotConfig current, ExperimentalBotConfigRequest request)
    {
        return new ExperimentalBotConfig(
            Enabled: request.Enabled ?? current.Enabled,
            AutoTrade: request.AutoTrade ?? current.AutoTrade,
            AutoTune: request.AutoTune ?? current.AutoTune,
            EvaluationIntervalSec: Math.Clamp(request.EvaluationIntervalSec ?? current.EvaluationIntervalSec, 5, 120),
            BasePositionSize: MathUtils.Clamp(request.BasePositionSize ?? current.BasePositionSize, 0.05, 25),
            MaxOpenTrades: Math.Clamp(request.MaxOpenTrades ?? current.MaxOpenTrades, 1, 24),
            MinConfidence: MathUtils.Clamp(request.MinConfidence ?? current.MinConfidence, 35, 95),
            StopLossPct: MathUtils.Clamp(request.StopLossPct ?? current.StopLossPct, 0.05, 0.95),
            TakeProfitPct: MathUtils.Clamp(request.TakeProfitPct ?? current.TakeProfitPct, 0.05, 3.0),
            MaxHoldingHours: Math.Clamp(request.MaxHoldingHours ?? current.MaxHoldingHours, 2, 720),
            AuditTargetTrades: Math.Clamp(request.AuditTargetTrades ?? current.AuditTargetTrades, 25, 3000),
            StartingCapitalUsd: MathUtils.Clamp(request.StartingCapitalUsd ?? current.StartingCapitalUsd, 100, 2_500_000));
    }

    private BotState GetOrCreateState(string asset)
    {
        return _states.GetOrAdd(asset, LoadOrCreateState);
    }

    private BotState LoadOrCreateState(string asset)
    {
        string path = StateFilePath(asset);
        if (!File.Exists(path))
            return CreateDefaultState();

        try
        {
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return CreateDefaultState();

            PersistedBotState? persisted = JsonSerializer.Deserialize<PersistedBotState>(json, _jsonOptions);
            if (persisted is null)
                return CreateDefaultState();

            BotState state = CreateDefaultState();
            state.Config = ApplyConfigRequest(new ExperimentalBotConfig(), new ExperimentalBotConfigRequest(
                Enabled: persisted.Config.Enabled,
                AutoTrade: persisted.Config.AutoTrade,
                AutoTune: persisted.Config.AutoTune,
                EvaluationIntervalSec: persisted.Config.EvaluationIntervalSec,
                BasePositionSize: persisted.Config.BasePositionSize,
                MaxOpenTrades: persisted.Config.MaxOpenTrades,
                MinConfidence: persisted.Config.MinConfidence,
                StopLossPct: persisted.Config.StopLossPct,
                TakeProfitPct: persisted.Config.TakeProfitPct,
                MaxHoldingHours: persisted.Config.MaxHoldingHours,
                AuditTargetTrades: persisted.Config.AuditTargetTrades,
                StartingCapitalUsd: persisted.Config.StartingCapitalUsd));

            state.StartedAt = persisted.StartedAt == default ? DateTimeOffset.UtcNow : persisted.StartedAt;
            state.LastEvaluationAt = persisted.LastEvaluationAt;
            state.LastSignal = persisted.LastSignal;

            state.OpenTrades.Clear();
            state.OpenTrades.AddRange(persisted.OpenTrades.Select(ToInternalTrade));
            state.ClosedTrades.Clear();
            state.ClosedTrades.AddRange(persisted.ClosedTrades.Select(ToInternalTrade));
            state.Decisions.Clear();
            state.Decisions.AddRange(persisted.Decisions);

            state.SpotHistory.Clear();
            state.SpotHistory.AddRange(
                persisted.SpotHistory
                    .Where(point => point.Spot > 0)
                    .Select(point => (point.Time, point.Spot)));

            state.Weights.Clear();
            foreach (var (key, value) in DefaultWeights)
                state.Weights[key] = value;
            foreach (var (key, value) in persisted.Weights)
                state.Weights[key] = MathUtils.Clamp(value, -3.0, 3.0);

            state.Audits.Clear();
            state.Audits.AddRange(persisted.Audits.OrderBy(a => a.Timestamp));
            state.PeakEquity = Math.Max(persisted.PeakEquity, state.Config.StartingCapitalUsd);
            state.MaxDrawdown = Math.Max(0, persisted.MaxDrawdown);

            TrimState(state);
            RefreshDrawdownAnchors(state);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load experimental bot state for {Asset}, starting fresh", asset);
            _monitoring.PublishAlert(
                "experimental-bot",
                NotificationSeverity.Warning,
                $"State load failed for {asset}, fallback to fresh state");
            return CreateDefaultState();
        }
    }

    private static BotState CreateDefaultState()
    {
        var config = new ExperimentalBotConfig();
        var state = new BotState
        {
            Config = config,
            StartedAt = DateTimeOffset.UtcNow,
            PeakEquity = config.StartingCapitalUsd,
            MaxDrawdown = 0
        };
        foreach (var (key, value) in DefaultWeights)
            state.Weights[key] = value;
        return state;
    }

    private async Task<ExperimentalBotSnapshot> BuildSnapshotThreadSafeAsync(string asset, BotState state, CancellationToken ct)
    {
        await state.Gate.WaitAsync(ct);
        try
        {
            return BuildSnapshot(asset, state);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task EvaluateIfDueAsync(
        string asset,
        BotState state,
        bool force,
        int cycles,
        CancellationToken ct)
    {
        int safeCycles = Math.Clamp(cycles, 1, 12);
        await state.Gate.WaitAsync(ct);
        try
        {
            bool dirty = false;
            for (int i = 0; i < safeCycles; i++)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (!force && state.LastEvaluationAt != DateTimeOffset.MinValue)
                {
                    double elapsed = (now - state.LastEvaluationAt).TotalSeconds;
                    if (elapsed < state.Config.EvaluationIntervalSec)
                        break;
                }

                var chain = await _marketData.GetOptionChainAsync(asset, ct);
                if (chain.Count == 0)
                {
                    state.LastEvaluationAt = now;
                    state.LastSignal = new ExperimentalBotSignal(
                        Bias: "Neutral",
                        Score: 0,
                        Confidence: 10,
                        StrategyTemplate: "No market data",
                        Summary: $"No executable chain on {asset}.",
                        Drivers: ["Waiting for market data feed"],
                        Features: new ExperimentalBotFeatureVector(0, 0, 0, 0, 0, 0, 0, 0),
                        Timestamp: now);
                    RecordDecision(state, "Neutral", 0, 10, "Hold", "No chain data available");
                    _monitoring.PublishAlert("experimental-bot", NotificationSeverity.Warning, $"No chain data for {asset}");
                    dirty = true;
                    continue;
                }

                ExecuteCycle(asset, state, chain, now);
                state.LastEvaluationAt = now;
                dirty = true;
                _monitoring.IncrementCounter("experimental.bot.cycles");
            }

            if (dirty)
                PersistStateNoLock(asset, state);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private void ExecuteCycle(string asset, BotState state, IReadOnlyList<LiveOptionQuote> chain, DateTimeOffset now)
    {
        double spot = ReferenceSpot(chain);
        if (spot <= 0)
        {
            spot = chain.Select(q => q.UnderlyingPrice).FirstOrDefault(s => s > 0);
            if (spot <= 0) spot = 1;
        }

        state.SpotHistory.Add((now, spot));
        while (state.SpotHistory.Count > 900 || (state.SpotHistory.Count > 0 && now - state.SpotHistory[0].Time > TimeSpan.FromDays(7)))
            state.SpotHistory.RemoveAt(0);

        var (features, drivers, liquidityScore) = ComputeFeatureVector(chain, spot, state.SpotHistory);
        ExperimentalBotSignal signal = BuildSignal(asset, state.Weights, features, drivers, liquidityScore, now);
        state.LastSignal = signal;

        var bySymbol = chain
            .GroupBy(q => q.Symbol, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(q => q.Timestamp).First())
            .ToDictionary(q => q.Symbol, q => q, StringComparer.OrdinalIgnoreCase);

        var closedThisCycle = UpdateOpenTrades(state, bySymbol, signal, now);
        foreach (var closed in closedThisCycle)
        {
            LearnFromClosedTrade(state, closed);
            var audit = CreateAuditEntry(state, closed, now);
            state.Audits.Add(audit);

            if (state.Config.AutoTune)
            {
                string? tuningComment = TryAutoTuneConfig(state);
                if (!string.IsNullOrWhiteSpace(tuningComment))
                {
                    RecordDecision(
                        state,
                        signal.Bias,
                        signal.Score,
                        signal.Confidence,
                        "Auto tune",
                        tuningComment);
                }
            }

            _monitoring.IncrementCounter("experimental.bot.trades.closed");
            if (closed.RealizedPnl >= 0)
                _monitoring.IncrementCounter("experimental.bot.trades.win");
            else
                _monitoring.IncrementCounter("experimental.bot.trades.loss");
        }

        string action = "Hold";
        string reason = signal.Summary;

        if (state.Config.Enabled && state.Config.AutoTrade &&
            signal.Confidence >= state.Config.MinConfidence &&
            !string.Equals(signal.Bias, "Neutral", StringComparison.OrdinalIgnoreCase) &&
            state.OpenTrades.Count < state.Config.MaxOpenTrades &&
            TrySelectTradeCandidate(chain, spot, signal.Bias, out var candidate))
        {
            bool alreadyOpen = state.OpenTrades.Any(t => t.Symbol.Equals(candidate.Symbol, StringComparison.OrdinalIgnoreCase));
            if (!alreadyOpen)
            {
                double sizeBoost = MathUtils.Clamp(signal.Confidence / 70.0, 0.6, 2.2);
                double qty = MathUtils.Clamp(state.Config.BasePositionSize * sizeBoost, 0.01, 75);
                double entryPrice = EffectiveMid(candidate);

                if (entryPrice > 0)
                {
                    ExperimentalBotStats preOpenStats = BuildStats(state);
                    ExperimentalBotPortfolioOverview portfolio = BuildPortfolio(state, preOpenStats);
                    double ticketCost = entryPrice * qty;
                    double maxTicket = Math.Max(35, portfolio.EquityUsd * 0.22);

                    if (ticketCost <= maxTicket && portfolio.AvailableCapitalUsd >= ticketCost * 0.75)
                    {
                        var trade = new InternalTrade
                        {
                            TradeId = $"BOT-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
                            Symbol = candidate.Symbol,
                            Side = TradeDirection.Buy,
                            Quantity = qty,
                            EntryPrice = entryPrice,
                            EntryTime = now,
                            StrategyTemplate = signal.StrategyTemplate,
                            Rationale = string.Join("; ", signal.Drivers.Take(3)),
                            EntrySignalScore = signal.Score,
                            Features = ExtractFeatureMap(features),
                            MarkPrice = entryPrice,
                            UnrealizedPnl = 0,
                            UnrealizedPnlPct = 0,
                            IsOpen = true,
                            ExitReason = "open"
                        };

                        state.OpenTrades.Add(trade);
                        action = $"Open {candidate.Symbol}";
                        reason = $"{signal.Bias} setup with conf {signal.Confidence:F0}";
                        _monitoring.IncrementCounter("experimental.bot.trades.opened");
                    }
                    else
                    {
                        action = "Hold";
                        reason = "Risk gate blocked ticket: insufficient capital buffer";
                    }
                }
            }
            else
            {
                reason = "Signal active but symbol already in open trades";
            }
        }
        else if (state.Config.Enabled && !state.Config.AutoTrade)
        {
            action = "Manual mode";
            reason = "Bot computes signals but auto-trading is disabled";
        }

        RecordDecision(state, signal.Bias, signal.Score, signal.Confidence, action, reason);
        TrimState(state);
        RefreshDrawdownAnchors(state);

        ExperimentalBotStats stats = BuildStats(state);
        ExperimentalBotPortfolioOverview portfolioSummary = BuildPortfolio(state, stats);
        _monitoring.RecordGauge($"experimental.bot.{asset.ToLowerInvariant()}.net_pnl", stats.NetPnl, "usd");
        _monitoring.RecordGauge($"experimental.bot.{asset.ToLowerInvariant()}.win_rate", stats.WinRate, "ratio");
        _monitoring.RecordGauge($"experimental.bot.{asset.ToLowerInvariant()}.drawdown_pct", portfolioSummary.DrawdownPct, "ratio");
        _monitoring.RecordGauge($"experimental.bot.{asset.ToLowerInvariant()}.equity", portfolioSummary.EquityUsd, "usd");
    }

    private static (ExperimentalBotFeatureVector Features, IReadOnlyList<string> Drivers, double LiquidityScore) ComputeFeatureVector(
        IReadOnlyList<LiveOptionQuote> chain,
        double spot,
        IReadOnlyList<(DateTimeOffset Time, double Spot)> spotHistory)
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

        double vixProxy = MathUtils.Clamp((0.68 - atmIv30) / 0.45, -1, 1);
        double volRegime = MathUtils.Clamp((0.60 - atmIv30) * 1.9 + termSlopeRaw * 3.8, -1, 1);

        double orderbookPressure = ComputeOrderbookPressure(valid);
        double callOi = calls.Sum(q => Math.Max(0, q.OpenInterest));
        double putOi = puts.Sum(q => Math.Max(0, q.OpenInterest));
        double restingPressure = MathUtils.Clamp((callOi - putOi) / Math.Max(callOi + putOi, 1e-9), -1, 1);

        double momentum5m = ComputeMomentum(spotHistory, TimeSpan.FromMinutes(5));
        double momentum1h = ComputeMomentum(spotHistory, TimeSpan.FromHours(1));
        double futuresMomentum = MathUtils.Clamp(momentum5m * 2.4 + momentum1h * 1.3, -1, 1);

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
        double liquidityScore = MathUtils.Clamp(Math.Log(1 + totalTurnover + totalOi * Math.Max(spot * 0.004, 1)) - avgSpread * 3.2, -8, 20);

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

        double score = MathUtils.Clamp(raw * 21, -100, 100);
        string bias = score switch
        {
            >= 10 => "Bullish",
            <= -10 => "Bearish",
            _ => "Neutral"
        };

        double confidence = MathUtils.Clamp(
            34 + Math.Abs(score) * 0.50 + MathUtils.Clamp(liquidityScore * 2.3, -8, 24),
            5,
            99);

        string strategyTemplate = bias switch
        {
            "Bullish" when score >= 32 => "Bull call spread + put hedge overlay",
            "Bullish" => "Directional call spread",
            "Bearish" when score <= -32 => "Put backspread convex hedge",
            "Bearish" => "Bear put spread",
            _ when features.VixProxy >= 0 => "Iron condor carry",
            _ => "Gamma scalp / short-term straddle"
        };

        string summary =
            $"{bias} {asset} signal ({(score >= 0 ? "+" : string.Empty)}{score:F1}) " +
            $"| conf {confidence:F1} | template: {strategyTemplate}.";

        return new ExperimentalBotSignal(
            Bias: bias,
            Score: score,
            Confidence: confidence,
            StrategyTemplate: strategyTemplate,
            Summary: summary,
            Drivers: drivers,
            Features: features,
            Timestamp: now);
    }

    private List<InternalTrade> UpdateOpenTrades(
        BotState state,
        IReadOnlyDictionary<string, LiveOptionQuote> bySymbol,
        ExperimentalBotSignal signal,
        DateTimeOffset now)
    {
        var closed = new List<InternalTrade>();
        foreach (var trade in state.OpenTrades)
        {
            if (!trade.IsOpen) continue;

            if (!bySymbol.TryGetValue(trade.Symbol, out var quote))
            {
                trade.MarkPrice = trade.EntryPrice;
                trade.UnrealizedPnl = 0;
                trade.UnrealizedPnlPct = 0;
                continue;
            }

            double mark = EffectiveMid(quote);
            if (mark <= 0) mark = trade.EntryPrice;
            trade.MarkPrice = mark;

            double signedDiff = trade.Side == TradeDirection.Buy
                ? mark - trade.EntryPrice
                : trade.EntryPrice - mark;
            trade.UnrealizedPnl = signedDiff * trade.Quantity;
            trade.UnrealizedPnlPct = trade.EntryPrice > 0 ? signedDiff / trade.EntryPrice : 0;

            bool hitStop = trade.UnrealizedPnlPct <= -state.Config.StopLossPct;
            bool hitTarget = trade.UnrealizedPnlPct >= state.Config.TakeProfitPct;
            bool exceededHolding = now - trade.EntryTime >= TimeSpan.FromHours(state.Config.MaxHoldingHours);
            bool flipRisk = signal.Confidence >= 65 && signal.Score * trade.EntrySignalScore < -220;

            if (hitStop || hitTarget || exceededHolding || flipRisk)
            {
                trade.IsOpen = false;
                trade.ExitTime = now;
                trade.ExitPrice = mark;
                trade.RealizedPnl = trade.UnrealizedPnl;
                trade.ExitReason = hitStop
                    ? "stop-loss"
                    : hitTarget
                        ? "take-profit"
                        : exceededHolding
                            ? "time-stop"
                            : "signal-flip";
                closed.Add(trade);
            }
        }

        if (closed.Count > 0)
        {
            foreach (var trade in closed)
                state.ClosedTrades.Add(trade);
            state.OpenTrades.RemoveAll(t => !t.IsOpen);
        }

        RefreshDrawdownAnchors(state);
        return closed;
    }

    private static void LearnFromClosedTrade(BotState state, InternalTrade trade)
    {
        if (trade.Features.Count == 0) return;

        double outcome = trade.RealizedPnl >= 0 ? 1 : -1;
        foreach (var (key, featureValue) in trade.Features)
        {
            double current = state.Weights.TryGetValue(key, out var value) ? value : 1;
            double updated = MathUtils.Clamp(current + LearningRate * outcome * featureValue, -3.0, 3.0);
            if (DefaultWeights.TryGetValue(key, out var anchor))
                updated = updated * 0.996 + anchor * 0.004;
            state.Weights[key] = updated;
        }
    }

    private ExperimentalBotAuditEntry CreateAuditEntry(BotState state, InternalTrade trade, DateTimeOffset now)
    {
        var rolling = ComputeRollingStats(state.ClosedTrades, state.Config.StartingCapitalUsd, 100);
        double baseNotional = Math.Max(Math.Abs(trade.EntryPrice * trade.Quantity), 1e-9);
        double realizedPct = trade.RealizedPnl / baseNotional;
        bool win = trade.RealizedPnl >= 0;

        string learningComment;
        if (win)
        {
            learningComment =
                $"Green exit ({trade.ExitReason}). Reinforcing factors: {SummarizeTopFeatures(trade.Features, positiveOnly: true)}.";
        }
        else
        {
            learningComment =
                $"Red exit ({trade.ExitReason}). Penalizing factors: {SummarizeTopFeatures(trade.Features, positiveOnly: false)}.";
        }

        return new ExperimentalBotAuditEntry(
            Timestamp: now,
            TradeId: trade.TradeId,
            Symbol: trade.Symbol,
            RealizedPnl: trade.RealizedPnl,
            RealizedPnlPct: realizedPct,
            Win: win,
            ExitReason: trade.ExitReason,
            RollingWinRate: rolling.WinRate,
            RollingProfitFactor: rolling.ProfitFactor,
            RollingDrawdownPct: rolling.DrawdownPct,
            LearningComment: learningComment);
    }

    private static string? TryAutoTuneConfig(BotState state)
    {
        if (state.ClosedTrades.Count < 5)
            return null;

        var rolling = ComputeRollingStats(state.ClosedTrades, state.Config.StartingCapitalUsd, 100);
        ExperimentalBotConfig current = state.Config;

        double nextBase = current.BasePositionSize;
        double nextMinConf = current.MinConfidence;
        int nextMaxOpen = current.MaxOpenTrades;
        double nextStop = current.StopLossPct;
        double nextTake = current.TakeProfitPct;

        if (rolling.DrawdownPct > 0.16 || rolling.ProfitFactor < 0.90)
        {
            nextBase *= 0.90;
            nextMinConf += 1.2;
            nextMaxOpen -= 1;
            nextStop *= 0.95;
        }
        else if (rolling.WinRate > 0.64 && rolling.ProfitFactor > 1.35 && rolling.DrawdownPct < 0.08)
        {
            nextBase *= 1.04;
            nextMinConf -= 0.5;
            nextMaxOpen += rolling.WinRate > 0.70 ? 1 : 0;
            nextTake *= 1.03;
        }
        else if (rolling.WinRate < 0.50)
        {
            nextBase *= 0.96;
            nextMinConf += 0.8;
        }
        else
        {
            return null;
        }

        ExperimentalBotConfig next = current with
        {
            BasePositionSize = MathUtils.Clamp(nextBase, 0.05, 25),
            MinConfidence = MathUtils.Clamp(nextMinConf, 35, 95),
            MaxOpenTrades = Math.Clamp(nextMaxOpen, 1, 24),
            StopLossPct = MathUtils.Clamp(nextStop, 0.05, 0.95),
            TakeProfitPct = MathUtils.Clamp(nextTake, 0.05, 3.0)
        };

        bool changed =
            Math.Abs(next.BasePositionSize - current.BasePositionSize) > 1e-9 ||
            Math.Abs(next.MinConfidence - current.MinConfidence) > 1e-9 ||
            next.MaxOpenTrades != current.MaxOpenTrades ||
            Math.Abs(next.StopLossPct - current.StopLossPct) > 1e-9 ||
            Math.Abs(next.TakeProfitPct - current.TakeProfitPct) > 1e-9;

        if (!changed)
            return null;

        state.Config = next;
        return $"rolling win={rolling.WinRate:P1}, pf={rolling.ProfitFactor:F2}, dd={rolling.DrawdownPct:P1} -> " +
               $"base={next.BasePositionSize:F2}, minConf={next.MinConfidence:F0}, maxOpen={next.MaxOpenTrades}";
    }

    private static string SummarizeTopFeatures(IReadOnlyDictionary<string, double> features, bool positiveOnly)
    {
        if (features.Count == 0)
            return "insufficient feature trace";

        var ordered = features
            .Where(kv => positiveOnly ? kv.Value >= 0 : kv.Value < 0)
            .OrderByDescending(kv => Math.Abs(kv.Value))
            .Take(2)
            .ToList();

        if (ordered.Count == 0)
            ordered = features.OrderByDescending(kv => Math.Abs(kv.Value)).Take(2).ToList();

        return string.Join(", ", ordered.Select(kv => $"{kv.Key}={(kv.Value >= 0 ? "+" : string.Empty)}{kv.Value:F2}"));
    }

    private static string BuildExplainNarrative(
        ExperimentalBotSignal signal,
        IReadOnlyList<ExperimentalBotFeatureContribution> positives,
        IReadOnlyList<ExperimentalBotFeatureContribution> negatives,
        IReadOnlyList<ExperimentalBotAuditEntry> audits)
    {
        string pos = positives.Count > 0
            ? string.Join(", ", positives.Select(p => $"{p.Feature}({p.Contribution:+0.00;-0.00})"))
            : "none";
        string neg = negatives.Count > 0
            ? string.Join(", ", negatives.Select(p => $"{p.Feature}({p.Contribution:+0.00;-0.00})"))
            : "none";

        int wins = audits.Count(a => a.Win);
        int losses = audits.Count(a => !a.Win);
        double wr = audits.Count > 0 ? wins / (double)audits.Count : 0;

        return
            $"Bias {signal.Bias} | score {signal.Score:F1} | conf {signal.Confidence:F1}. " +
            $"Top positive drivers: {pos}. " +
            $"Top negative drivers: {neg}. " +
            $"Recent audited trades={audits.Count} (wins={wins}, losses={losses}, wr={wr:P1}).";
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

    private static bool TrySelectTradeCandidate(
        IReadOnlyList<LiveOptionQuote> chain,
        double spot,
        string bias,
        out LiveOptionQuote selected)
    {
        selected = default!;
        bool bullish = string.Equals(bias, "Bullish", StringComparison.OrdinalIgnoreCase);
        bool bearish = string.Equals(bias, "Bearish", StringComparison.OrdinalIgnoreCase);
        if (!bullish && !bearish) return false;

        OptionRight right = bullish ? OptionRight.Call : OptionRight.Put;
        double strikeTarget = bullish ? spot * 1.03 : spot * 0.97;

        var candidates = chain
            .Where(q => q.Right == right && q.Strike > 0 && q.Expiry > DateTimeOffset.UtcNow)
            .Select(q =>
            {
                double dte = Math.Max((q.Expiry - DateTimeOffset.UtcNow).TotalDays, 0);
                double mid = EffectiveMid(q);
                double spread = q.Bid > 0 && q.Ask > 0 && mid > 0
                    ? MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 2)
                    : 1.2;
                double liquidity = Math.Log(1 + Math.Max(0, q.OpenInterest) + Math.Max(0, q.Volume24h) + Math.Max(0, q.Turnover24h));
                double score = Math.Abs(q.Strike - strikeTarget) / Math.Max(spot, 1e-9) * 2.2
                    + Math.Abs(dte - 30) / 35.0
                    + spread * 0.9
                    - liquidity * 0.25;
                return new { Quote = q, Score = score };
            })
            .Where(x => EffectiveMid(x.Quote) > 0)
            .OrderBy(x => x.Score)
            .Take(1)
            .ToList();

        if (candidates.Count == 0) return false;
        selected = candidates[0].Quote;
        return true;
    }

    private static void RecordDecision(
        BotState state,
        string bias,
        double score,
        double confidence,
        string action,
        string reason)
    {
        state.Decisions.Add(new ExperimentalBotDecision(
            Timestamp: DateTimeOffset.UtcNow,
            Bias: bias,
            Score: score,
            Confidence: confidence,
            Action: action,
            Reason: reason));
    }

    private static void TrimState(BotState state)
    {
        const int maxClosed = 1800;
        const int maxDecisions = 1200;
        const int maxAudits = 2200;

        if (state.ClosedTrades.Count > maxClosed)
            state.ClosedTrades.RemoveRange(0, state.ClosedTrades.Count - maxClosed);
        if (state.Decisions.Count > maxDecisions)
            state.Decisions.RemoveRange(0, state.Decisions.Count - maxDecisions);
        if (state.Audits.Count > maxAudits)
            state.Audits.RemoveRange(0, state.Audits.Count - maxAudits);
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

        int winCount = sample.Count(t => t.RealizedPnl > 0);
        double winRate = sample.Count > 0 ? winCount / (double)sample.Count : 0;

        double grossProfit = sample.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
        double grossLoss = sample.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl);
        double profitFactor = grossLoss < 0
            ? grossProfit / Math.Abs(grossLoss)
            : grossProfit > 0 ? 99 : 0;

        double equity = Math.Max(startingCapital, 1e-9);
        double peak = equity;
        double maxDd = 0;
        foreach (var trade in sample)
        {
            equity += trade.RealizedPnl;
            if (equity > peak)
                peak = equity;
            double dd = peak - equity;
            if (dd > maxDd)
                maxDd = dd;
        }

        double ddPct = peak > 0 ? maxDd / peak : 0;
        return (winRate, profitFactor, MathUtils.Clamp(ddPct, 0, 1));
    }

    private static ExperimentalBotStats BuildStats(BotState state)
    {
        var closed = state.ClosedTrades;
        var open = state.OpenTrades;

        int winning = closed.Count(t => t.RealizedPnl > 0);
        int losing = closed.Count(t => t.RealizedPnl < 0);
        double realized = closed.Sum(t => t.RealizedPnl);
        double unrealized = open.Sum(t => t.UnrealizedPnl);
        double grossProfit = closed.Where(t => t.RealizedPnl > 0).Sum(t => t.RealizedPnl);
        double grossLoss = closed.Where(t => t.RealizedPnl < 0).Sum(t => t.RealizedPnl);

        double winRate = closed.Count > 0 ? winning / (double)closed.Count : 0;
        double profitFactor = grossLoss < 0
            ? grossProfit / Math.Abs(grossLoss)
            : grossProfit > 0 ? 99 : 0;
        double avgTradePnl = closed.Count > 0 ? realized / closed.Count : 0;
        double netPnl = realized + unrealized;

        var returns = closed
            .Select(t =>
            {
                double baseNotional = Math.Max(Math.Abs(t.EntryPrice * t.Quantity), 1e-9);
                return t.RealizedPnl / baseNotional;
            })
            .ToList();

        double sharpeLike = 0;
        if (returns.Count >= 3)
        {
            double mean = returns.Average();
            double variance = returns.Sum(r => Math.Pow(r - mean, 2)) / returns.Count;
            double std = Math.Sqrt(Math.Max(variance, 0));
            if (std > 1e-9)
                sharpeLike = mean / std * Math.Sqrt(Math.Min(returns.Count, 252));
        }

        var rolling = ComputeRollingStats(closed, state.Config.StartingCapitalUsd, 100);

        return new ExperimentalBotStats(
            TotalTrades: closed.Count + open.Count,
            ClosedTrades: closed.Count,
            WinningTrades: winning,
            LosingTrades: losing,
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
            RollingDrawdownPct100: rolling.DrawdownPct);
    }

    private static ExperimentalBotSnapshot BuildSnapshot(string asset, BotState state)
    {
        var open = state.OpenTrades
            .OrderByDescending(t => t.EntryTime)
            .Select(ToDto)
            .ToList();
        var closed = state.ClosedTrades
            .OrderByDescending(t => t.ExitTime)
            .Take(60)
            .Select(ToDto)
            .ToList();
        var audits = state.Audits
            .OrderByDescending(a => a.Timestamp)
            .Take(80)
            .ToList();
        var decisions = state.Decisions
            .OrderByDescending(d => d.Timestamp)
            .Take(60)
            .ToList();
        var weights = state.Weights
            .OrderByDescending(w => Math.Abs(w.Value))
            .Select(w => new ExperimentalBotModelWeight(w.Key, w.Value))
            .ToList();

        ExperimentalBotStats computedStats = BuildStats(state);
        var stats = computedStats with { MaxDrawdown = Math.Max(state.MaxDrawdown, computedStats.MaxDrawdown) };

        ExperimentalBotPortfolioOverview portfolio = BuildPortfolio(state, stats);
        ExperimentalBotAuditSnapshot audit = BuildAuditSnapshot(state, stats);

        return new ExperimentalBotSnapshot(
            Asset: asset,
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
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static ExperimentalBotPortfolioOverview BuildPortfolio(BotState state, ExperimentalBotStats stats)
    {
        double startingCapital = state.Config.StartingCapitalUsd;
        double equity = Math.Max(0, startingCapital + stats.NetPnl);
        double peak = Math.Max(state.PeakEquity, Math.Max(startingCapital, equity));
        double drawdownUsd = Math.Max(0, peak - equity);
        double drawdownPct = peak > 0 ? drawdownUsd / peak : 0;
        double openRisk = ComputeOpenRiskNotional(state);

        double reservedCapital = openRisk * 0.65;
        double available = Math.Max(0, equity - reservedCapital);

        return new ExperimentalBotPortfolioOverview(
            StartingCapitalUsd: startingCapital,
            EquityUsd: equity,
            PeakEquityUsd: peak,
            AvailableCapitalUsd: available,
            OpenRiskNotionalUsd: openRisk,
            DrawdownUsd: drawdownUsd,
            DrawdownPct: drawdownPct);
    }

    private static ExperimentalBotAuditSnapshot BuildAuditSnapshot(BotState state, ExperimentalBotStats stats)
    {
        int target = Math.Max(1, state.Config.AuditTargetTrades);
        int audited = state.ClosedTrades.Count;
        double completion = MathUtils.Clamp(audited / (double)target, 0, 1);

        string status = audited < target
            ? $"Learning {audited}/{target}"
            : stats.RollingWinRate100 >= 0.70 && stats.RollingProfitFactor100 >= 1.40 && stats.RollingDrawdownPct100 <= 0.10
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

    private static double ComputeOpenRiskNotional(BotState state)
    {
        return state.OpenTrades.Sum(t =>
        {
            double px = t.MarkPrice > 0 ? t.MarkPrice : t.EntryPrice;
            return Math.Max(0, px) * Math.Max(0, t.Quantity);
        });
    }

    private static ExperimentalBotTrade ToDto(InternalTrade trade)
    {
        return new ExperimentalBotTrade(
            TradeId: trade.TradeId,
            Symbol: trade.Symbol,
            Side: trade.Side,
            Quantity: trade.Quantity,
            EntryPrice: trade.EntryPrice,
            MarkPrice: trade.MarkPrice,
            UnrealizedPnl: trade.UnrealizedPnl,
            UnrealizedPnlPct: trade.UnrealizedPnlPct,
            EntryTime: trade.EntryTime,
            StrategyTemplate: trade.StrategyTemplate,
            Rationale: trade.Rationale,
            IsOpen: trade.IsOpen,
            ExitTime: trade.ExitTime,
            ExitPrice: trade.ExitPrice,
            RealizedPnl: trade.RealizedPnl);
    }

    private static PersistedTrade ToPersistedTrade(InternalTrade trade)
    {
        return new PersistedTrade
        {
            TradeId = trade.TradeId,
            Symbol = trade.Symbol,
            Side = trade.Side,
            Quantity = trade.Quantity,
            EntryPrice = trade.EntryPrice,
            EntryTime = trade.EntryTime,
            StrategyTemplate = trade.StrategyTemplate,
            Rationale = trade.Rationale,
            EntrySignalScore = trade.EntrySignalScore,
            Features = new Dictionary<string, double>(trade.Features, StringComparer.OrdinalIgnoreCase),
            IsOpen = trade.IsOpen,
            MarkPrice = trade.MarkPrice,
            UnrealizedPnl = trade.UnrealizedPnl,
            UnrealizedPnlPct = trade.UnrealizedPnlPct,
            ExitTime = trade.ExitTime,
            ExitPrice = trade.ExitPrice,
            RealizedPnl = trade.RealizedPnl,
            ExitReason = trade.ExitReason
        };
    }

    private static InternalTrade ToInternalTrade(PersistedTrade trade)
    {
        return new InternalTrade
        {
            TradeId = trade.TradeId,
            Symbol = trade.Symbol,
            Side = trade.Side,
            Quantity = trade.Quantity,
            EntryPrice = trade.EntryPrice,
            EntryTime = trade.EntryTime,
            StrategyTemplate = trade.StrategyTemplate,
            Rationale = trade.Rationale,
            EntrySignalScore = trade.EntrySignalScore,
            Features = new Dictionary<string, double>(trade.Features, StringComparer.OrdinalIgnoreCase),
            IsOpen = trade.IsOpen,
            MarkPrice = trade.MarkPrice,
            UnrealizedPnl = trade.UnrealizedPnl,
            UnrealizedPnlPct = trade.UnrealizedPnlPct,
            ExitTime = trade.ExitTime,
            ExitPrice = trade.ExitPrice,
            RealizedPnl = trade.RealizedPnl,
            ExitReason = string.IsNullOrWhiteSpace(trade.ExitReason) ? "closed" : trade.ExitReason
        };
    }

    private void PersistStateNoLock(string asset, BotState state)
    {
        try
        {
            var payload = new PersistedBotState
            {
                Config = state.Config,
                StartedAt = state.StartedAt,
                LastEvaluationAt = state.LastEvaluationAt,
                LastSignal = state.LastSignal,
                OpenTrades = state.OpenTrades.Select(ToPersistedTrade).ToList(),
                ClosedTrades = state.ClosedTrades.Select(ToPersistedTrade).ToList(),
                Decisions = state.Decisions.ToList(),
                SpotHistory = state.SpotHistory
                    .Where(point => point.Spot > 0)
                    .Select(point => new SpotPoint(point.Time, point.Spot))
                    .ToList(),
                Weights = new Dictionary<string, double>(state.Weights, StringComparer.OrdinalIgnoreCase),
                Audits = state.Audits.ToList(),
                PeakEquity = state.PeakEquity,
                MaxDrawdown = state.MaxDrawdown
            };

            string serialized = JsonSerializer.Serialize(payload, _jsonOptions);
            string filePath = StateFilePath(asset);
            string tmpPath = filePath + ".tmp";

            File.WriteAllText(tmpPath, serialized);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist experimental bot state for {Asset}", asset);
            _monitoring.PublishAlert(
                "experimental-bot",
                NotificationSeverity.Warning,
                $"State persist failed for {asset}: {ex.GetType().Name}");
        }
    }

    private string StateFilePath(string asset)
    {
        return Path.Combine(_stateDirectory, $"{asset.ToLowerInvariant()}.json");
    }

    private static string NormalizeAsset(string asset)
    {
        string normalized = (asset ?? string.Empty).Trim().ToUpperInvariant();
        return normalized switch
        {
            "ETH" => "ETH",
            "SOL" => "SOL",
            "WTI" => "WTI",
            _ => "BTC"
        };
    }

    private static double ComputeOrderbookPressure(IReadOnlyList<LiveOptionQuote> quotes)
    {
        double signed = 0;
        double weight = 0;
        foreach (var q in quotes)
        {
            double mid = EffectiveMid(q);
            if (mid <= 0) continue;
            double spread = q.Bid > 0 && q.Ask > 0
                ? MathUtils.Clamp((q.Ask - q.Bid) / mid, 0, 2)
                : 1.2;
            double tilt = q.Mark > 0 ? MathUtils.Clamp((mid - q.Mark) / q.Mark, -1, 1) : 0;
            double liq = Math.Log(1 + Math.Max(0, q.OpenInterest) + Math.Max(0, q.Volume24h) + Math.Max(0, q.Turnover24h));
            double signal = ((0.85 - spread) * 0.6 + tilt * 1.3) * liq;
            signed += (q.Right == OptionRight.Call ? 1 : -1) * signal;
            weight += Math.Abs(signal);
        }

        if (weight <= 1e-9) return 0;
        return MathUtils.Clamp(signed / weight, -1, 1);
    }

    private static double ComputeAtmForTargetDays(IReadOnlyList<LiveOptionQuote> chain, double spot, double targetDays)
    {
        var byExpiry = chain
            .GroupBy(q => q.Expiry.Date)
            .Select(g =>
            {
                var list = g.ToList();
                DateTimeOffset expiry = list[0].Expiry;
                double days = Math.Max(0, (expiry - DateTimeOffset.UtcNow).TotalDays);
                double atmIv = ComputeAtmIv(list, spot);
                return new { days, atmIv };
            })
            .Where(x => x.atmIv > 0)
            .OrderBy(x => Math.Abs(x.days - targetDays))
            .ToList();

        if (byExpiry.Count == 0) return 0.55;
        return byExpiry[0].atmIv;
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

        var atm = ivQuotes.Where(q => Math.Abs(q.Strike - atmStrike) < 1e-9).ToList();
        return atm.Count > 0 ? atm.Average(q => q.MarkIv) : ivQuotes[0].MarkIv;
    }

    private static double ComputeMomentum(
        IReadOnlyList<(DateTimeOffset Time, double Spot)> spotHistory,
        TimeSpan lookback)
    {
        if (spotHistory.Count < 2) return 0;

        var latest = spotHistory[^1];
        DateTimeOffset targetTime = latest.Time - lookback;
        var anchor = spotHistory
            .Where(x => x.Time <= targetTime)
            .OrderByDescending(x => x.Time)
            .FirstOrDefault();

        if (anchor.Time == default || anchor.Spot <= 0 || latest.Spot <= 0) return 0;
        return MathUtils.Clamp((latest.Spot - anchor.Spot) / anchor.Spot, -1, 1);
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

    private static double EffectiveMid(LiveOptionQuote quote)
    {
        if (quote.Mid > 0) return quote.Mid;
        if (quote.Bid > 0 && quote.Ask > 0) return (quote.Bid + quote.Ask) / 2.0;
        if (quote.Mark > 0) return quote.Mark;
        if (quote.Bid > 0) return quote.Bid;
        return quote.Ask;
    }
}
