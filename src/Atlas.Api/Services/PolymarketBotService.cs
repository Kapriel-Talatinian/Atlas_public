using System.Globalization;
using System.Text.Json;
using Atlas.Api.Models;
using Atlas.Core.Common;

namespace Atlas.Api.Services;

public interface IPolymarketBotService
{
    Task<PolymarketLiveSnapshot> GetSnapshotAsync(int lookaheadMinutes = 24 * 60, int maxMarkets = 24, CancellationToken ct = default);
    Task<PolymarketLiveSnapshot> GetCachedSnapshotAsync(CancellationToken ct = default);
    Task<PolymarketLiveSnapshot> RunAutopilotAsync(CancellationToken ct = default);
    Task<bool> SetPausedAsync(bool paused, CancellationToken ct = default);
    bool IsPaused { get; }
}

public sealed class PolymarketBotService : IPolymarketBotService
{
    private const string BotKey = "POLYMARKET-LIVE";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class InternalPosition
    {
        public string PositionId { get; set; } = string.Empty;
        public string MarketId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string Asset { get; set; } = string.Empty;
        public string Question { get; set; } = string.Empty;
        public string DisplayLabel { get; set; } = string.Empty;
        public string SignalCategory { get; set; } = string.Empty;
        public string PrimaryOutcomeLabel { get; set; } = "Yes";
        public string SecondaryOutcomeLabel { get; set; } = "No";
        public string Side { get; set; } = string.Empty;
        public double StakeUsd { get; set; }
        public double Quantity { get; set; }
        public double EntryPrice { get; set; }
        public double CurrentPrice { get; set; }
        public double CurrentValueUsd { get; set; }
        public double UnrealizedPnlUsd { get; set; }
        public double UnrealizedPnlPct { get; set; }
        public double MaxLossUsd { get; set; }
        public double MaxPayoutUsd { get; set; }
        public double MaxProfitUsd { get; set; }
        public double ExpectedValueUsd { get; set; }
        public double RiskRewardRatio { get; set; }
        public double FairProbability { get; set; }
        public double MarketProbability { get; set; }
        public double EdgePct { get; set; }
        public double QualityScore { get; set; }
        public double ConvictionScore { get; set; }
        public string Thesis { get; set; } = string.Empty;
        public string MacroReasoning { get; set; } = string.Empty;
        public string MicroReasoning { get; set; } = string.Empty;
        public string MathReasoning { get; set; } = string.Empty;
        public string RiskPlan { get; set; } = string.Empty;
        public DateTimeOffset EntryTime { get; set; }
        public DateTimeOffset Expiry { get; set; }
        public DateTimeOffset TimeStopAt { get; set; }
        public PolymarketThresholdRelation ThresholdRelation { get; set; }
        public double LowerStrike { get; set; }
        public double? UpperStrike { get; set; }
        public bool IsOpen { get; set; } = true;
        public DateTimeOffset? ExitTime { get; set; }
        public double ExitPrice { get; set; }
        public double RealizedPnlUsd { get; set; }
        public string ExitReason { get; set; } = "open";
    }

    private sealed class PersistedState
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public double StartingBalanceUsd { get; set; }
        public double CashBalanceUsd { get; set; }
        public double PeakEquityUsd { get; set; }
        public double MaxDrawdownUsd { get; set; }
        public bool DailyLossLockActive { get; set; }
        public string? DailyLossLockDateKey { get; set; }
        public string? LastDailySummaryDateKey { get; set; }
        public string? LastMonthlySummaryKey { get; set; }
        public string? LastEntryDecisionDigest { get; set; }
        public DateTimeOffset LastEntryDecisionAt { get; set; } = DateTimeOffset.MinValue;
        public bool Paused { get; set; }
        public List<InternalPosition> OpenPositions { get; set; } = [];
        public List<InternalPosition> ClosedPositions { get; set; } = [];
        public List<PolymarketJournalEntry> Journal { get; set; } = [];
        public PolymarketLiveSnapshot? LastAnalysisSnapshot { get; set; }
    }

    private sealed class RuntimeState
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEvaluationAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastPersistedAt { get; set; } = DateTimeOffset.MinValue;
        public string LastCycleStatus { get; set; } = "cold";
        public int LastCycleDurationMs { get; set; }
        public long StateVersion { get; set; }
        public double StartingBalanceUsd { get; set; }
        public double CashBalanceUsd { get; set; }
        public double PeakEquityUsd { get; set; }
        public double MaxDrawdownUsd { get; set; }
        public bool DailyLossLockActive { get; set; }
        public string? DailyLossLockDateKey { get; set; }
        public string? LastDailySummaryDateKey { get; set; }
        public string? LastMonthlySummaryKey { get; set; }
        public string? LastEntryDecisionDigest { get; set; }
        public DateTimeOffset LastEntryDecisionAt { get; set; } = DateTimeOffset.MinValue;
        public bool Paused { get; set; }
        public List<InternalPosition> OpenPositions { get; } = [];
        public List<InternalPosition> ClosedPositions { get; } = [];
        public List<PolymarketJournalEntry> Journal { get; } = [];
        public PolymarketLiveSnapshot? LastAnalysisSnapshot { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed record BotConfig(
        bool Enabled,
        string ExecutionMode,
        int EvaluationIntervalSec,
        int LookaheadMinutes,
        int MaxMarkets,
        int MaxNewTradesPerCycle,
        double MaxTradeUsd,
        double StartingBalanceUsd,
        double DailyLossLimitUsd,
        string ReportTimeZone);

    private readonly IPolymarketLiveService _liveService;
    private readonly IBotStateRepository _repository;
    private readonly AtlasRuntimeContext _runtime;
    private readonly ITelegramSignalService _telegram;
    private readonly IPolymarketClobClient _clob;
    private readonly ILogger<PolymarketBotService> _logger;
    private readonly RuntimeState _state;

    public PolymarketBotService(
        IPolymarketLiveService liveService,
        IBotStateRepository repository,
        AtlasRuntimeContext runtime,
        ITelegramSignalService telegram,
        IPolymarketClobClient clob,
        ILogger<PolymarketBotService> logger)
    {
        _liveService = liveService;
        _repository = repository;
        _runtime = runtime;
        _telegram = telegram;
        _clob = clob;
        _logger = logger;
        _state = LoadOrCreateState();
    }

    public async Task<PolymarketLiveSnapshot> GetSnapshotAsync(int lookaheadMinutes = 24 * 60, int maxMarkets = 24, CancellationToken ct = default)
    {
        if (_runtime.CanRunBotLoop)
            await EvaluateIfDueAsync(force: false, lookaheadMinutes, maxMarkets, ct);
        else
            await RefreshFromRepositoryThreadSafeAsync(ct);

        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task<PolymarketLiveSnapshot> GetCachedSnapshotAsync(CancellationToken ct = default)
    {
        if (!_runtime.CanRunBotLoop)
            await RefreshFromRepositoryThreadSafeAsync(ct);

        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public async Task<PolymarketLiveSnapshot> RunAutopilotAsync(CancellationToken ct = default)
    {
        if (!_runtime.CanRunBotLoop)
            throw new InvalidOperationException("Polymarket live bot mutations are only allowed on a bot-worker runtime.");

        await EvaluateIfDueAsync(force: true, lookaheadMinutes: 24 * 60, maxMarkets: 24, ct);
        return await BuildSnapshotThreadSafeAsync(ct);
    }

    public bool IsPaused => _state.Paused;

    public async Task<bool> SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        await _state.Gate.WaitAsync(ct);
        try
        {
            _state.Paused = paused;
            AddJournal(_state, "control", paused ? "Bot paused" : "Bot resumed",
                paused ? "New entries blocked. Existing positions continue to be managed." : "New entries allowed again.",
                null, null, DateTimeOffset.UtcNow);
            PersistStateNoLock(_state);
            return _state.Paused;
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private async Task EvaluateIfDueAsync(bool force, int lookaheadMinutes, int maxMarkets, CancellationToken ct)
    {
        BotConfig config = LoadConfig() with
        {
            LookaheadMinutes = Math.Clamp(lookaheadMinutes, 5, 7 * 24 * 60),
            MaxMarkets = Math.Clamp(maxMarkets, 6, 60)
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await _state.Gate.WaitAsync(ct);
        try
        {
            bool due = force ||
                _state.LastEvaluationAt == DateTimeOffset.MinValue ||
                now - _state.LastEvaluationAt >= TimeSpan.FromSeconds(config.EvaluationIntervalSec);
            if (!due)
                return;

            DateTime start = DateTime.UtcNow;
            _state.LastCycleStatus = "scanning";

            PolymarketLiveSnapshot live = await _liveService.GetLiveSnapshotAsync(config.LookaheadMinutes, config.MaxMarkets, ct);
            _state.LastAnalysisSnapshot = live;
            _state.LastEvaluationAt = now;

            HandleSummaryBoundariesNoLock(_state, config, now, ct);
            UpdateOpenPositionsNoLock(_state, live, config, now);
            UpdateDailyLossLockNoLock(_state, config, now);

            if (config.Enabled && !_state.DailyLossLockActive)
                OpenNewPositionsNoLock(_state, live, config, now);

            RefreshEquityAnchorsNoLock(_state);
            TrimStateNoLock(_state);
            _state.LastCycleDurationMs = Math.Max(0, (int)(DateTime.UtcNow - start).TotalMilliseconds);
            _state.LastCycleStatus = _state.DailyLossLockActive ? "halted" : _state.OpenPositions.Count > 0 ? "trading" : "idle";
            PersistStateNoLock(_state);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private void HandleSummaryBoundariesNoLock(RuntimeState state, BotConfig config, DateTimeOffset now, CancellationToken ct)
    {
        TimeZoneInfo timeZone = ResolveTimeZone(config.ReportTimeZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        string todayKey = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string monthKey = localNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(state.LastDailySummaryDateKey) && !string.Equals(state.LastDailySummaryDateKey, todayKey, StringComparison.Ordinal))
        {
            if (state.DailyLossLockActive && !string.Equals(state.DailyLossLockDateKey, todayKey, StringComparison.Ordinal))
            {
                state.DailyLossLockActive = false;
                state.DailyLossLockDateKey = null;
                AddJournal(state, "risk", "Daily loss lock reset", $"New local day {todayKey} started. Entry lock released.", null, null, now);
            }
        }

        if (!string.IsNullOrWhiteSpace(state.LastMonthlySummaryKey) && !string.Equals(state.LastMonthlySummaryKey, monthKey, StringComparison.Ordinal))
        {
        }

        state.LastDailySummaryDateKey = todayKey;
        state.LastMonthlySummaryKey = monthKey;
    }

    private void UpdateOpenPositionsNoLock(RuntimeState state, PolymarketLiveSnapshot live, BotConfig config, DateTimeOffset now)
    {
        var currentSignals = live.Opportunities.ToDictionary(signal => signal.MarketId, StringComparer.OrdinalIgnoreCase);
        var referenceByAsset = live.Assets.ToDictionary(asset => asset.Asset, StringComparer.OrdinalIgnoreCase);
        var closed = new List<InternalPosition>();

        foreach (InternalPosition position in state.OpenPositions.Where(p => p.IsOpen))
        {
            if (currentSignals.TryGetValue(position.MarketId, out PolymarketMarketSignal? signal))
            {
                ApplyMark(position, CurrentProbabilityForSide(signal, position.Side));
                position.FairProbability = FairProbabilityForSide(signal, position.Side);
                position.MarketProbability = MarketProbabilityForSide(signal, position.Side);
                position.EdgePct = position.FairProbability - position.MarketProbability;
                position.QualityScore = signal.QualityScore;
                position.ConvictionScore = signal.ConvictionScore;
                position.DisplayLabel = signal.DisplayLabel;
                position.SignalCategory = signal.SignalCategory;
                position.PrimaryOutcomeLabel = signal.PrimaryOutcomeLabel;
                position.SecondaryOutcomeLabel = signal.SecondaryOutcomeLabel;
                position.MacroReasoning = signal.MacroReasoning;
                position.MicroReasoning = signal.MicroReasoning;
                position.MathReasoning = signal.MathReasoning;
                position.Thesis = signal.Summary;
                position.RiskPlan = signal.ExecutionPlan.RiskPlan;
            }
            else
            {
                ApplyMark(position, position.CurrentPrice);
            }

            bool shouldClose = false;
            string exitReason = position.ExitReason;
            double exitPrice = position.CurrentPrice;

            if (now >= position.Expiry)
            {
                if (referenceByAsset.TryGetValue(position.Asset, out PolymarketReferenceAssetSnapshot? reference))
                {
                    bool yesWins = EvaluateResolution(reference.Spot, position.ThresholdRelation, position.LowerStrike, position.UpperStrike);
                    exitPrice = ResolutionPrice(position.Side, yesWins);
                    shouldClose = true;
                    exitReason = "resolved";
                }
            }
            else if (position.UnrealizedPnlUsd >= TakeProfitUsd(position))
            {
                shouldClose = true;
                exitReason = "take-profit";
            }
            else if (position.UnrealizedPnlUsd <= -StopLossUsd(position))
            {
                shouldClose = true;
                exitReason = "stop-loss";
            }
            else if (now >= position.TimeStopAt)
            {
                shouldClose = true;
                exitReason = "time-stop";
            }
            else if (position.EdgePct < -0.04)
            {
                shouldClose = true;
                exitReason = "edge-flip";
            }

            if (!shouldClose)
                continue;

            // In live mode, place a sell order on the CLOB before closing
            if (IsLiveMode(config) && exitReason != "resolved")
            {
                ClobOrderResult sellResult = _clob.PlaceOrderAsync(new ClobPlaceOrderRequest(
                    TokenId: position.MarketId,
                    Price: exitPrice,
                    Size: position.Quantity,
                    Side: PolymarketOrderSide.Sell), CancellationToken.None).GetAwaiter().GetResult();

                if (!sellResult.Success)
                {
                    _logger.LogWarning("CLOB sell order failed for {PositionId}: {Error}", position.PositionId, sellResult.ErrorMessage);
                    AddJournal(state, "clob-sell-fail", $"{position.Asset} sell failed",
                        $"{position.Question} | reason={sellResult.ErrorMessage}", position.PositionId, position.MarketId, now);
                    continue; // keep position open, retry next cycle
                }
            }

            ClosePosition(position, exitPrice, exitReason, now);
            state.CashBalanceUsd += position.CurrentValueUsd;
            closed.Add(position);
            string exitModeTag = IsLiveMode(config) ? "LIVE" : "PAPER";
            AddJournal(state, "exit", $"[{exitModeTag}] {position.Asset} {position.Side} closed", $"{position.Question} | reason={exitReason} | pnl={position.RealizedPnlUsd:+0.00;-0.00}$", position.PositionId, position.MarketId, now);
            if (ShouldNotifyClose(exitReason))
                _ = _telegram.SendAsync(BuildCloseTelegramMessage(position, state, exitReason), CancellationToken.None);
        }

        if (closed.Count == 0)
            return;

        state.OpenPositions.RemoveAll(position => !position.IsOpen);
        state.ClosedPositions.AddRange(closed.OrderByDescending(position => position.ExitTime));
    }

    private bool IsLiveMode(BotConfig config) =>
        string.Equals(config.ExecutionMode, "live", StringComparison.OrdinalIgnoreCase) && _clob.IsConfigured;

    private void OpenNewPositionsNoLock(RuntimeState state, PolymarketLiveSnapshot live, BotConfig config, DateTimeOffset now)
    {
        bool isPaper = string.Equals(config.ExecutionMode, "paper", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config.ExecutionMode, "dry-run", StringComparison.OrdinalIgnoreCase);
        bool isLive = IsLiveMode(config);

        if (!isPaper && !isLive)
            return;

        if (state.Paused)
        {
            MaybeRecordNoTradeDecision(state, "No new trade opened",
                "Bot is paused via Telegram /pause. Existing positions remain managed.", now);
            return;
        }

        var scannerSignals = live.Opportunities
            .Where(signal => !string.Equals(signal.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var assessedSignals = scannerSignals
            .Select(signal => (Signal: signal, Assessment: PolymarketBotRuleEngine.AssessSignal(signal, config.LookaheadMinutes)))
            .ToList();

        var botReadySignals = assessedSignals
            .Where(item => item.Assessment.BotEligible)
            .ToList();

        // Per-asset concentration: max 2 open positions per asset, max 60% gross exposure
        double grossExposure = state.OpenPositions.Where(p => p.IsOpen).Sum(p => p.StakeUsd);
        var assetPositionCounts = state.OpenPositions.Where(p => p.IsOpen)
            .GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var assetExposure = state.OpenPositions.Where(p => p.IsOpen)
            .GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.StakeUsd), StringComparer.OrdinalIgnoreCase);
        double equityUsd = state.CashBalanceUsd + grossExposure;

        var candidates = botReadySignals
            .Where(item => !state.OpenPositions.Any(position => position.MarketId.Equals(item.Signal.MarketId, StringComparison.OrdinalIgnoreCase)))
            .Where(item =>
            {
                int count = assetPositionCounts.GetValueOrDefault(item.Signal.Asset, 0);
                if (count >= 2) return false; // max 2 positions per asset
                double exposure = assetExposure.GetValueOrDefault(item.Signal.Asset, 0);
                if (equityUsd > 0 && exposure / equityUsd >= 0.60) return false; // max 60% per asset
                return true;
            })
            .OrderByDescending(item => SignalPriority(item.Signal))
            .Take(Math.Max(1, config.MaxNewTradesPerCycle))
            .ToList();

        if (scannerSignals.Count == 0)
        {
            MaybeRecordNoTradeDecision(
                state,
                "No new trade opened",
                "No scanner signal currently recommends a YES/NO or UP/DOWN side strongly enough to enter.",
                now);
            return;
        }

        if (botReadySignals.Count == 0)
        {
            MaybeRecordNoTradeDecision(
                state,
                "No new trade opened",
                SummarizeBotGateBlockers(assessedSignals),
                now);
            return;
        }

        if (candidates.Count == 0)
        {
            MaybeRecordNoTradeDecision(
                state,
                "No new trade opened",
                $"All {botReadySignals.Count} bot-ready market(s) are already live in the portfolio. The bot is waiting for a fresh market id.",
                now);
            return;
        }

        if (state.CashBalanceUsd < 0.50)
        {
            MaybeRecordNoTradeDecision(
                state,
                "No new trade opened",
                $"Available cash {state.CashBalanceUsd:0.00}$ is below the 0.50$ minimum ticket size.",
                now);
            return;
        }

        bool openedAny = false;
        foreach ((PolymarketMarketSignal Signal, PolymarketBotSignalAssessment Assessment) candidate in candidates)
        {
            PolymarketMarketSignal signal = candidate.Signal;
            if (state.CashBalanceUsd + 1e-9 < 0.50)
                break;

            double entryPrice = candidate.Assessment.EntryPrice;
            entryPrice = MathUtils.Clamp(entryPrice, 0.01, 0.99);

            // Quarter-Kelly position sizing: scale stake by edge strength
            double edge = candidate.Assessment.SelectedEdgePct;
            double kellyFraction = edge > 0 ? MathUtils.Clamp(edge / (1.0 - entryPrice), 0.01, 0.25) : 0.01;
            double kellyStake = kellyFraction * 0.25 * state.CashBalanceUsd; // quarter-Kelly
            double stakeUsd = MathUtils.Clamp(kellyStake, 0.50, config.MaxTradeUsd);
            stakeUsd = Math.Min(stakeUsd, state.CashBalanceUsd);
            if (stakeUsd < 0.50)
                break;

            PolymarketParsedQuestion parsed = BuildParsedQuestion(signal);

            double quantity = stakeUsd / entryPrice;
            double fairProbability = FairProbabilityForSide(signal, signal.RecommendedSide);
            double marketProbability = MarketProbabilityForSide(signal, signal.RecommendedSide);
            double maxPayout = quantity;
            double maxProfit = maxPayout - stakeUsd;
            double expectedValue = maxPayout * fairProbability - stakeUsd;
            double riskReward = stakeUsd > 0 ? Math.Max(0, maxProfit) / stakeUsd : 0;

            var position = new InternalPosition
            {
                PositionId = $"POLY-{Guid.NewGuid():N}"[..17],
                MarketId = signal.MarketId,
                EventId = signal.EventId,
                Asset = signal.Asset,
                Question = signal.Question,
                DisplayLabel = signal.DisplayLabel,
                SignalCategory = signal.SignalCategory,
                PrimaryOutcomeLabel = signal.PrimaryOutcomeLabel,
                SecondaryOutcomeLabel = signal.SecondaryOutcomeLabel,
                Side = signal.RecommendedSide,
                StakeUsd = stakeUsd,
                Quantity = quantity,
                EntryPrice = entryPrice,
                CurrentPrice = entryPrice,
                CurrentValueUsd = stakeUsd,
                MaxLossUsd = stakeUsd,
                MaxPayoutUsd = maxPayout,
                MaxProfitUsd = maxProfit,
                ExpectedValueUsd = expectedValue,
                RiskRewardRatio = riskReward,
                FairProbability = fairProbability,
                MarketProbability = marketProbability,
                EdgePct = fairProbability - marketProbability,
                QualityScore = signal.QualityScore,
                ConvictionScore = signal.ConvictionScore,
                Thesis = signal.Summary,
                MacroReasoning = signal.MacroReasoning,
                MicroReasoning = signal.MicroReasoning,
                MathReasoning = signal.MathReasoning,
                RiskPlan = signal.ExecutionPlan.RiskPlan,
                EntryTime = now,
                Expiry = signal.Expiry,
                TimeStopAt = now.AddMinutes(Math.Max(2, signal.ExecutionPlan.TimeStopMinutes)),
                ThresholdRelation = parsed.Relation,
                LowerStrike = parsed.LowerStrike,
                UpperStrike = parsed.UpperStrike,
                IsOpen = true,
                ExitReason = "open"
            };
            ApplyMark(position, entryPrice);

            // In live mode, place the actual CLOB order before recording the position
            if (isLive)
            {
                ClobOrderResult orderResult = _clob.PlaceOrderAsync(new ClobPlaceOrderRequest(
                    TokenId: signal.MarketId,
                    Price: entryPrice,
                    Size: quantity,
                    Side: PolymarketBotRuleEngine.UsesPrimaryOutcome(signal.RecommendedSide)
                        ? PolymarketOrderSide.Buy
                        : PolymarketOrderSide.Buy), CancellationToken.None).GetAwaiter().GetResult();

                if (!orderResult.Success)
                {
                    _logger.LogWarning(
                        "CLOB order rejected for {MarketId}: {Error}",
                        signal.MarketId, orderResult.ErrorMessage);
                    AddJournal(state, "clob-reject", $"{signal.Asset} order rejected",
                        $"{signal.Question} | reason={orderResult.ErrorMessage}", position.PositionId, signal.MarketId, now);
                    continue; // skip this position, don't deduct cash
                }

                position.PositionId = $"CLOB-{orderResult.OrderId}";
                _logger.LogInformation("CLOB order placed: {OrderId} for {MarketId}", orderResult.OrderId, signal.MarketId);
            }

            state.CashBalanceUsd -= stakeUsd;
            state.OpenPositions.Add(position);
            string modeTag = isLive ? "LIVE" : "PAPER";
            AddJournal(state, "entry", $"[{modeTag}] {signal.Asset} {signal.RecommendedSide} opened", $"{signal.Question} | stake={stakeUsd:0.00}$ | edge={(position.EdgePct * 100):+0.00;-0.00}% | fair={position.FairProbability:P1} | market={position.MarketProbability:P1}", position.PositionId, position.MarketId, now);
            _ = _telegram.SendAsync(BuildOpenTelegramMessage(position), CancellationToken.None);
            openedAny = true;
        }

        if (openedAny)
        {
            state.LastEntryDecisionDigest = null;
            state.LastEntryDecisionAt = DateTimeOffset.MinValue;
        }
        else
        {
            MaybeRecordNoTradeDecision(
                state,
                "No new trade opened",
                $"Bot-ready setups existed, but available cash {state.CashBalanceUsd:0.00}$ was insufficient for another {config.MaxTradeUsd:0.00}$ ticket.",
                now);
        }
    }

    private static double SignalPriority(PolymarketMarketSignal signal)
    {
        double horizonBonus = signal.MinutesToExpiry switch
        {
            <= 30 => 25,  // strongly prefer near-expiry
            <= 60 => 15,
            <= 120 => 8,
            <= 180 => 2,
            _ => -6
        };

        return signal.ConvictionScore * 0.55
            + signal.QualityScore * 0.35
            + Math.Max(signal.EdgeYesPct, signal.EdgeNoPct) * 800
            - signal.Spread * 200  // penalize wide spreads harder
            - signal.DistanceToStrikePct * 150  // prefer strikes near spot
            + horizonBonus;
    }

    private static PolymarketParsedQuestion BuildParsedQuestion(PolymarketMarketSignal signal)
    {
        PolymarketThresholdRelation relation = signal.ThresholdType?.Trim().ToLowerInvariant() switch
        {
            "above" => PolymarketThresholdRelation.Above,
            "below" => PolymarketThresholdRelation.Below,
            "between" => PolymarketThresholdRelation.Between,
            "outside" => PolymarketThresholdRelation.Outside,
            _ => signal.SignalCategory == "directional"
                ? PolymarketThresholdRelation.Above
                : PolymarketThresholdRelation.Above
        };

        return new PolymarketParsedQuestion(
            Asset: signal.Asset,
            Relation: relation,
            LowerStrike: signal.StrikeLow,
            UpperStrike: signal.StrikeHigh,
            RawQuestion: signal.Question);
    }

    private static bool UsesFirstOutcome(string side) =>
        side.Equals("Buy Yes", StringComparison.OrdinalIgnoreCase) ||
        side.Equals("Buy Up", StringComparison.OrdinalIgnoreCase);

    private static double CurrentProbabilityForSide(PolymarketMarketSignal signal, string side) =>
        UsesFirstOutcome(side)
            ? MathUtils.Clamp(signal.MarketYesPrice, 0, 1)
            : MathUtils.Clamp(signal.MarketNoPrice, 0, 1);

    private static double FairProbabilityForSide(PolymarketMarketSignal signal, string side) =>
        UsesFirstOutcome(side)
            ? MathUtils.Clamp(signal.FairYesProbability, 0, 1)
            : MathUtils.Clamp(signal.FairNoProbability, 0, 1);

    private static double MarketProbabilityForSide(PolymarketMarketSignal signal, string side) =>
        UsesFirstOutcome(side)
            ? MathUtils.Clamp(signal.MarketYesPrice, 0, 1)
            : MathUtils.Clamp(signal.MarketNoPrice, 0, 1);

    private static void ApplyMark(InternalPosition position, double probability)
    {
        position.CurrentPrice = MathUtils.Clamp(probability, 0, 1);
        position.CurrentValueUsd = position.Quantity * position.CurrentPrice;
        position.UnrealizedPnlUsd = position.CurrentValueUsd - position.StakeUsd;
        position.UnrealizedPnlPct = position.StakeUsd > 0 ? position.UnrealizedPnlUsd / position.StakeUsd : 0;
    }

    private static void ClosePosition(InternalPosition position, double exitPrice, string exitReason, DateTimeOffset now)
    {
        position.ExitPrice = MathUtils.Clamp(exitPrice, 0, 1);
        position.CurrentPrice = position.ExitPrice;
        position.CurrentValueUsd = position.Quantity * position.ExitPrice;
        position.RealizedPnlUsd = position.CurrentValueUsd - position.StakeUsd;
        position.UnrealizedPnlUsd = 0;
        position.UnrealizedPnlPct = 0;
        position.ExitTime = now;
        position.ExitReason = exitReason;
        position.IsOpen = false;
    }

    private static bool EvaluateResolution(double spot, PolymarketThresholdRelation relation, double lowerStrike, double? upperStrike)
    {
        return relation switch
        {
            PolymarketThresholdRelation.Above => spot > lowerStrike,
            PolymarketThresholdRelation.Below => spot < lowerStrike,
            PolymarketThresholdRelation.Between when upperStrike.HasValue => spot >= lowerStrike && spot <= upperStrike.Value,
            PolymarketThresholdRelation.Outside when upperStrike.HasValue => spot < lowerStrike || spot > upperStrike.Value,
            _ => false
        };
    }

    private static double ResolutionPrice(string side, bool yesWins) =>
        UsesFirstOutcome(side)
            ? (yesWins ? 1.0 : 0.0)
            : (yesWins ? 0.0 : 1.0);

    private static double TakeProfitUsd(InternalPosition position)
    {
        // Scale TP with stake: 25-50% of stake depending on expected value
        double evBased = Math.Max(position.ExpectedValueUsd * 0.60, position.StakeUsd * 0.15);
        return MathUtils.Clamp(evBased, 0.10, position.StakeUsd * 0.50);
    }

    private static double StopLossUsd(InternalPosition position)
    {
        // Wider SL at 35% of stake to survive short-term noise
        return MathUtils.Clamp(position.StakeUsd * 0.35, 0.15, position.StakeUsd * 0.50);
    }

    private void UpdateDailyLossLockNoLock(RuntimeState state, BotConfig config, DateTimeOffset now)
    {
        TimeZoneInfo timeZone = ResolveTimeZone(config.ReportTimeZone);
        string todayKey = TimeZoneInfo.ConvertTime(now, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        double dailyRealized = ComputeRealizedPnlForDay(state, todayKey, timeZone);

        if (state.DailyLossLockActive && !string.Equals(state.DailyLossLockDateKey, todayKey, StringComparison.Ordinal))
        {
            state.DailyLossLockActive = false;
            state.DailyLossLockDateKey = null;
        }

        if (!state.DailyLossLockActive && dailyRealized <= -config.DailyLossLimitUsd)
        {
            state.DailyLossLockActive = true;
            state.DailyLossLockDateKey = todayKey;
            AddJournal(state, "risk", "Daily loss lock engaged", $"Daily realized PnL {dailyRealized:+0.00;-0.00}$ breached limit {-config.DailyLossLimitUsd:0.00}$.", null, null, now);
        }
    }

    private static double ComputeRealizedPnlForDay(RuntimeState state, string dayKey, TimeZoneInfo timeZone) =>
        state.ClosedPositions
            .Where(position => position.ExitTime is not null)
            .Where(position => TimeZoneInfo.ConvertTime(position.ExitTime!.Value, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == dayKey)
            .Sum(position => position.RealizedPnlUsd);

    private static double ComputeRealizedPnlForMonth(RuntimeState state, string monthKey, TimeZoneInfo timeZone) =>
        state.ClosedPositions
            .Where(position => position.ExitTime is not null)
            .Where(position => TimeZoneInfo.ConvertTime(position.ExitTime!.Value, timeZone).ToString("yyyy-MM", CultureInfo.InvariantCulture) == monthKey)
            .Sum(position => position.RealizedPnlUsd);

    private static double ComputeEquity(RuntimeState state) =>
        state.CashBalanceUsd + state.OpenPositions.Where(position => position.IsOpen).Sum(position => position.CurrentValueUsd);

    private static void RefreshEquityAnchorsNoLock(RuntimeState state)
    {
        double equity = ComputeEquity(state);
        state.PeakEquityUsd = Math.Max(state.PeakEquityUsd, equity);
        state.MaxDrawdownUsd = Math.Max(state.MaxDrawdownUsd, Math.Max(0, state.PeakEquityUsd - equity));
    }

    private static void AddJournal(RuntimeState state, string type, string headline, string detail, string? positionId, string? marketId, DateTimeOffset now)
    {
        state.Journal.Add(new PolymarketJournalEntry(now, type, headline, detail, positionId, marketId));
    }

    private static void MaybeRecordNoTradeDecision(RuntimeState state, string headline, string detail, DateTimeOffset now)
    {
        string digest = $"{headline}|{detail}";
        if (string.Equals(state.LastEntryDecisionDigest, digest, StringComparison.Ordinal) &&
            state.LastEntryDecisionAt != DateTimeOffset.MinValue &&
            now - state.LastEntryDecisionAt < TimeSpan.FromMinutes(15))
            return;

        AddJournal(state, "watch", headline, detail, null, null, now);
        state.LastEntryDecisionDigest = digest;
        state.LastEntryDecisionAt = now;
    }

    private static string SummarizeBotGateBlockers(IEnumerable<(PolymarketMarketSignal Signal, PolymarketBotSignalAssessment Assessment)> assessments)
    {
        var dominantReason = assessments
            .Where(item => !item.Assessment.BotEligible)
            .GroupBy(item => item.Assessment.BlockReason)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();

        if (dominantReason is null)
            return "Scanner found signals, but none cleared the bot entry gates.";

        int totalBlocked = assessments.Count(item => !item.Assessment.BotEligible);
        return $"{totalBlocked} scanner signal(s) were blocked by the bot gates. Dominant reason: {dominantReason.Key}.";
    }

    private static void TrimStateNoLock(RuntimeState state)
    {
        const int maxClosed = 400;
        const int maxJournal = 300;
        if (state.ClosedPositions.Count > maxClosed)
            state.ClosedPositions.RemoveRange(0, state.ClosedPositions.Count - maxClosed);
        if (state.Journal.Count > maxJournal)
            state.Journal.RemoveRange(0, state.Journal.Count - maxJournal);
    }

    private static string BuildOpenTelegramMessage(InternalPosition position)
    {
        string outcome = position.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
        string label = position.DisplayLabel?.ToUpperInvariant() ?? position.Question.ToUpperInvariant();
        return $"NEW ORDER | {label} | {outcome} | TP BRUT {position.MaxProfitUsd:0.00}$ | PERTE MAX {position.MaxLossUsd:0.00}$";
    }

    private static bool ShouldNotifyClose(string exitReason) =>
        string.Equals(exitReason, "take-profit", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(exitReason, "stop-loss", StringComparison.OrdinalIgnoreCase);

    private static string BuildCloseTelegramMessage(InternalPosition position, RuntimeState state, string exitReason)
    {
        string outcome = position.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
        string label = position.DisplayLabel?.ToUpperInvariant() ?? position.Question.ToUpperInvariant();
        string prefix = string.Equals(exitReason, "take-profit", StringComparison.OrdinalIgnoreCase) ? "TP" : "SL";
        double equity = ComputeEquity(state);
        double cash = state.CashBalanceUsd;
        return $"{prefix} | {label} | {outcome} | PNL {position.RealizedPnlUsd:+0.00;-0.00}$ | EQUITY {equity:0.00}$ | CASH {cash:0.00}$";
    }

    private static TimeZoneInfo ResolveTimeZone(string configured)
    {
        string[] ids = [configured, "Europe/Paris", "UTC"];
        foreach (string id in ids.Where(value => !string.IsNullOrWhiteSpace(value)))
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

    private BotConfig LoadConfig() => new(
        Enabled: string.Equals(Environment.GetEnvironmentVariable("POLYMARKET_BOT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase),
        ExecutionMode: (Environment.GetEnvironmentVariable("POLYMARKET_EXECUTION_MODE") ?? "analysis-only").Trim(),
        EvaluationIntervalSec: ParseIntEnv("POLYMARKET_BOT_EVALUATION_SECONDS", 5, 3, 60),
        LookaheadMinutes: ParseIntEnv("POLYMARKET_LOOKAHEAD_MINUTES", 24 * 60, 5, 7 * 24 * 60),
        MaxMarkets: ParseIntEnv("POLYMARKET_MAX_MARKETS", 24, 6, 60),
        MaxNewTradesPerCycle: ParseIntEnv("POLYMARKET_MAX_NEW_TRADES_PER_CYCLE", 2, 1, 8),
        MaxTradeUsd: ParseDoubleEnv("POLYMARKET_MAX_TRADE_USD", 2.0, 0.25, 25.0),
        StartingBalanceUsd: ParseDoubleEnv("POLYMARKET_STARTING_BALANCE_USD", 100.0, 5.0, 100_000),
        DailyLossLimitUsd: ParseDoubleEnv("POLYMARKET_DAILY_LOSS_LIMIT_USD", 5.0, 1.0, 500.0),
        ReportTimeZone: (Environment.GetEnvironmentVariable("POLYMARKET_REPORT_TIMEZONE") ?? "Europe/Paris").Trim());

    private static int ParseIntEnv(string name, int fallback, int min, int max)
    {
        string raw = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        if (!int.TryParse(raw, out int parsed))
            return fallback;

        return Math.Clamp(parsed, min, max);
    }

    private static double ParseDoubleEnv(string name, double fallback, double min, double max)
    {
        string raw = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        if (!double.TryParse(raw, out double parsed))
            return fallback;

        return MathUtils.Clamp(parsed, min, max);
    }

    private async Task<PolymarketLiveSnapshot> BuildSnapshotThreadSafeAsync(CancellationToken ct)
    {
        await _state.Gate.WaitAsync(ct);
        try
        {
            return BuildSnapshotNoLock(_state);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    private PolymarketLiveSnapshot BuildSnapshotNoLock(RuntimeState state)
    {
        PolymarketLiveSnapshot baseSnapshot = state.LastAnalysisSnapshot ?? new PolymarketLiveSnapshot(
            Status: "cold",
            Summary: "Polymarket bot has not produced a scan yet.",
            Runtime: new PolymarketRuntimeStatus(false, false, _telegram.IsConfigured, false, state.DailyLossLockActive, "analysis-only", "not-configured", 1.0, 5.0, "Waiting for first scan."),
            Assets: [],
            BotTiers: [],
            Opportunities: [],
            Stats: new PolymarketScanStats(0, 0, 0, 0, 0, 0, 0),
            Notes: ["No Polymarket scan captured yet."],
            Portfolio: new PolymarketBotPortfolioSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0, DateTimeOffset.UtcNow),
            OpenPositions: [],
            RecentClosedPositions: [],
            Journal: [],
            Timestamp: DateTimeOffset.UtcNow);

        BotConfig config = LoadConfig();
        double realized = state.ClosedPositions.Sum(position => position.RealizedPnlUsd);
        double unrealized = state.OpenPositions.Sum(position => position.UnrealizedPnlUsd);
        double equity = ComputeEquity(state);
        double grossExposure = state.OpenPositions.Where(position => position.IsOpen).Sum(position => position.CurrentValueUsd);
        double winRate = state.ClosedPositions.Count == 0 ? 0 : (double)state.ClosedPositions.Count(position => position.RealizedPnlUsd > 0) / state.ClosedPositions.Count;
        double avgWinner = state.ClosedPositions.Where(position => position.RealizedPnlUsd > 0).Select(position => position.RealizedPnlUsd).DefaultIfEmpty(0).Average();
        double avgLoser = state.ClosedPositions.Where(position => position.RealizedPnlUsd < 0).Select(position => position.RealizedPnlUsd).DefaultIfEmpty(0).Average();
        TimeZoneInfo timeZone = ResolveTimeZone(config.ReportTimeZone);
        string todayKey = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string monthKey = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        double dailyPnl = ComputeRealizedPnlForDay(state, todayKey, timeZone);
        double monthlyPnl = ComputeRealizedPnlForMonth(state, monthKey, timeZone);

        var portfolio = new PolymarketBotPortfolioSnapshot(
            StartingBalanceUsd: state.StartingBalanceUsd,
            CashBalanceUsd: state.CashBalanceUsd,
            EquityUsd: equity,
            AvailableBalanceUsd: state.CashBalanceUsd,
            GrossExposureUsd: grossExposure,
            RealizedPnlUsd: realized,
            UnrealizedPnlUsd: unrealized,
            NetPnlUsd: realized + unrealized,
            DailyPnlUsd: dailyPnl,
            MonthlyPnlUsd: monthlyPnl,
            PeakEquityUsd: state.PeakEquityUsd,
            DrawdownUsd: Math.Max(0, state.PeakEquityUsd - equity),
            DrawdownPct: state.PeakEquityUsd > 0 ? Math.Max(0, state.PeakEquityUsd - equity) / state.PeakEquityUsd : 0,
            OpenPositionsCount: state.OpenPositions.Count,
            ClosedPositionsCount: state.ClosedPositions.Count,
            WinRate: winRate,
            AvgWinnerUsd: avgWinner,
            AvgLoserUsd: avgLoser,
            MaxTradeRiskUsd: config.MaxTradeUsd,
            Timestamp: DateTimeOffset.UtcNow);

        bool isLive = string.Equals(config.ExecutionMode, "live", StringComparison.OrdinalIgnoreCase);
        bool liveArmed = isLive && _clob.IsConfigured;
        bool isPaper = string.Equals(config.ExecutionMode, "paper", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config.ExecutionMode, "dry-run", StringComparison.OrdinalIgnoreCase);

        PolymarketRuntimeStatus runtime = baseSnapshot.Runtime with
        {
            TradingEnabled = config.Enabled || baseSnapshot.Runtime.TradingEnabled,
            TelegramConfigured = _telegram.IsConfigured,
            ExecutionArmed = config.Enabled && (isPaper || liveArmed),
            RuntimeMode = config.Enabled
                ? (liveArmed ? "live-armed" : isLive ? "paper-live-ready" : config.ExecutionMode)
                : baseSnapshot.Runtime.RuntimeMode,
            DailyLossLockActive = state.DailyLossLockActive,
            MaxTradeUsd = config.MaxTradeUsd,
            DailyLossLimitUsd = config.DailyLossLimitUsd,
            Summary = state.DailyLossLockActive
                ? $"Daily loss lock is active. New entries are blocked until the next {config.ReportTimeZone} trading day."
                : liveArmed
                    ? $"Live CLOB routing armed. Max stake {config.MaxTradeUsd:0.00}$ per trade. Kill-switch via /pause on Telegram."
                : isLive
                    ? "Live mode requested but the CLOB signer is not configured. Set POLYMARKET_PRIVATE_KEY + API keys."
                : config.Enabled
                    ? $"Polymarket autopilot is running in {config.ExecutionMode} mode with {config.MaxTradeUsd:0.00}$ maximum stake per trade."
                    : baseSnapshot.Runtime.Summary
        };

        string status = state.DailyLossLockActive
            ? "risk-lock"
            : liveArmed
                ? state.OpenPositions.Count > 0 ? "live-trading" : "live-ready"
            : isLive
                ? "live-unarmed"
            : config.Enabled && !string.Equals(config.ExecutionMode, "analysis-only", StringComparison.OrdinalIgnoreCase)
                ? state.OpenPositions.Count > 0 ? "paper-trading" : "paper-ready"
                : baseSnapshot.Status;

        string summary = state.OpenPositions.Count > 0
            ? $"{state.OpenPositions.Count} Polymarket position(s) are live across BTC/ETH/SOL in {(liveArmed ? "LIVE" : config.ExecutionMode.ToUpperInvariant())} mode with Kelly-sized stakes (max {config.MaxTradeUsd:0.00}$) and automated risk exits."
            : liveArmed
                ? $"Live CLOB routing is armed and scanning. Max stake {config.MaxTradeUsd:0.00}$ per trade."
            : isLive
                ? "Live mode requested but the CLOB signer is not configured. Check POLYMARKET_PRIVATE_KEY and API keys."
            : config.Enabled && !string.Equals(config.ExecutionMode, "analysis-only", StringComparison.OrdinalIgnoreCase)
                ? $"Polymarket autopilot is armed in {config.ExecutionMode} mode and waiting for short-horizon crypto threshold edges."
                : baseSnapshot.Summary;

        return baseSnapshot with
        {
            Status = status,
            Summary = summary,
            Runtime = runtime,
            Portfolio = portfolio,
            OpenPositions = state.OpenPositions.OrderByDescending(position => position.EntryTime).Select(ToDto).ToList(),
            RecentClosedPositions = state.ClosedPositions.OrderByDescending(position => position.ExitTime ?? position.EntryTime).Take(80).Select(ToDto).ToList(),
            Journal = state.Journal.OrderByDescending(entry => entry.Timestamp).Take(120).ToList(),
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    private static PolymarketPosition ToDto(InternalPosition position) => new(
        PositionId: position.PositionId,
        MarketId: position.MarketId,
        Asset: position.Asset,
        Question: position.Question,
        DisplayLabel: position.DisplayLabel,
        SignalCategory: position.SignalCategory,
        PrimaryOutcomeLabel: position.PrimaryOutcomeLabel,
        SecondaryOutcomeLabel: position.SecondaryOutcomeLabel,
        Side: position.Side,
        StakeUsd: position.StakeUsd,
        Quantity: position.Quantity,
        EntryPrice: position.EntryPrice,
        CurrentPrice: position.CurrentPrice,
        CurrentValueUsd: position.CurrentValueUsd,
        UnrealizedPnlUsd: position.UnrealizedPnlUsd,
        UnrealizedPnlPct: position.UnrealizedPnlPct,
        MaxLossUsd: position.MaxLossUsd,
        MaxPayoutUsd: position.MaxPayoutUsd,
        MaxProfitUsd: position.MaxProfitUsd,
        ExpectedValueUsd: position.ExpectedValueUsd,
        RiskRewardRatio: position.RiskRewardRatio,
        FairProbability: position.FairProbability,
        MarketProbability: position.MarketProbability,
        EdgePct: position.EdgePct,
        QualityScore: position.QualityScore,
        ConvictionScore: position.ConvictionScore,
        Thesis: position.Thesis,
        MacroReasoning: position.MacroReasoning,
        MicroReasoning: position.MicroReasoning,
        MathReasoning: position.MathReasoning,
        RiskPlan: position.RiskPlan,
        EntryTime: position.EntryTime,
        Expiry: position.Expiry,
        TimeStopAt: position.TimeStopAt,
        IsOpen: position.IsOpen,
        ExitTime: position.ExitTime,
        ExitPrice: position.ExitPrice,
        RealizedPnlUsd: position.RealizedPnlUsd,
        ExitReason: position.ExitReason);

    private RuntimeState LoadOrCreateState()
    {
        BotStateRecord? record = _repository.Load(BotKey);
        if (record is null)
            return CreateDefaultState();

        try
        {
            PersistedState? persisted = JsonSerializer.Deserialize<PersistedState>(record.StateJson, JsonOptions);
            if (persisted is null)
                return CreateDefaultState();

            var state = CreateDefaultState();
            state.StartedAt = persisted.StartedAt;
            state.LastEvaluationAt = persisted.LastEvaluationAt;
            state.LastPersistedAt = record.LastPersistedAt;
            state.LastCycleStatus = record.LastCycleStatus;
            state.LastCycleDurationMs = record.LastCycleDurationMs;
            state.StateVersion = record.StateVersion;
            state.StartingBalanceUsd = persisted.StartingBalanceUsd > 0 ? persisted.StartingBalanceUsd : LoadConfig().StartingBalanceUsd;
            state.CashBalanceUsd = persisted.CashBalanceUsd > 0 ? persisted.CashBalanceUsd : state.StartingBalanceUsd;
            state.PeakEquityUsd = Math.Max(persisted.PeakEquityUsd, state.StartingBalanceUsd);
            state.MaxDrawdownUsd = Math.Max(0, persisted.MaxDrawdownUsd);
            state.DailyLossLockActive = persisted.DailyLossLockActive;
            state.DailyLossLockDateKey = persisted.DailyLossLockDateKey;
            state.LastDailySummaryDateKey = persisted.LastDailySummaryDateKey;
            state.LastMonthlySummaryKey = persisted.LastMonthlySummaryKey;
            state.LastEntryDecisionDigest = persisted.LastEntryDecisionDigest;
            state.LastEntryDecisionAt = persisted.LastEntryDecisionAt;
            state.Paused = persisted.Paused;
            state.OpenPositions.AddRange(persisted.OpenPositions);
            state.ClosedPositions.AddRange(persisted.ClosedPositions);
            state.Journal.AddRange(persisted.Journal);
            state.LastAnalysisSnapshot = persisted.LastAnalysisSnapshot;
            TrimStateNoLock(state);
            RefreshEquityAnchorsNoLock(state);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Polymarket bot state. Starting fresh.");
            return CreateDefaultState();
        }
    }

    private RuntimeState CreateDefaultState()
    {
        BotConfig config = LoadConfig();
        return new RuntimeState
        {
            StartedAt = DateTimeOffset.UtcNow,
            LastEvaluationAt = DateTimeOffset.MinValue,
            LastPersistedAt = DateTimeOffset.MinValue,
            LastCycleStatus = "cold",
            LastCycleDurationMs = 0,
            StateVersion = 0,
            StartingBalanceUsd = config.StartingBalanceUsd,
            CashBalanceUsd = config.StartingBalanceUsd,
            PeakEquityUsd = config.StartingBalanceUsd,
            MaxDrawdownUsd = 0,
            DailyLossLockActive = false,
            LastEntryDecisionAt = DateTimeOffset.MinValue
        };
    }

    private void PersistStateNoLock(RuntimeState state)
    {
        var persisted = new PersistedState
        {
            StartedAt = state.StartedAt,
            LastEvaluationAt = state.LastEvaluationAt,
            StartingBalanceUsd = state.StartingBalanceUsd,
            CashBalanceUsd = state.CashBalanceUsd,
            PeakEquityUsd = state.PeakEquityUsd,
            MaxDrawdownUsd = state.MaxDrawdownUsd,
            DailyLossLockActive = state.DailyLossLockActive,
            DailyLossLockDateKey = state.DailyLossLockDateKey,
            LastDailySummaryDateKey = state.LastDailySummaryDateKey,
            LastMonthlySummaryKey = state.LastMonthlySummaryKey,
            LastEntryDecisionDigest = state.LastEntryDecisionDigest,
            LastEntryDecisionAt = state.LastEntryDecisionAt,
            Paused = state.Paused,
            OpenPositions = state.OpenPositions.ToList(),
            ClosedPositions = state.ClosedPositions.ToList(),
            Journal = state.Journal.ToList(),
            LastAnalysisSnapshot = state.LastAnalysisSnapshot
        };

        BotStateRecord saved = _repository.Save(new BotStateSaveRequest(
            BotKey: BotKey,
            StateJson: JsonSerializer.Serialize(persisted, JsonOptions),
            ExpectedStateVersion: state.StateVersion,
            LastEvaluationAt: state.LastEvaluationAt == DateTimeOffset.MinValue ? null : state.LastEvaluationAt,
            LastCycleStatus: state.LastCycleStatus,
            LastCycleDurationMs: state.LastCycleDurationMs));

        state.StateVersion = saved.StateVersion;
        state.LastPersistedAt = saved.LastPersistedAt;
    }

    private async Task RefreshFromRepositoryThreadSafeAsync(CancellationToken ct)
    {
        await _state.Gate.WaitAsync(ct);
        try
        {
            BotStateRecord? record = _repository.Load(BotKey);
            if (record is null || record.StateVersion == _state.StateVersion)
                return;

            PersistedState? persisted = JsonSerializer.Deserialize<PersistedState>(record.StateJson, JsonOptions);
            if (persisted is null)
                return;

            _state.StartedAt = persisted.StartedAt;
            _state.LastEvaluationAt = persisted.LastEvaluationAt;
            _state.LastPersistedAt = record.LastPersistedAt;
            _state.LastCycleStatus = record.LastCycleStatus;
            _state.LastCycleDurationMs = record.LastCycleDurationMs;
            _state.StateVersion = record.StateVersion;
            _state.StartingBalanceUsd = persisted.StartingBalanceUsd;
            _state.CashBalanceUsd = persisted.CashBalanceUsd;
            _state.PeakEquityUsd = persisted.PeakEquityUsd;
            _state.MaxDrawdownUsd = persisted.MaxDrawdownUsd;
            _state.DailyLossLockActive = persisted.DailyLossLockActive;
            _state.DailyLossLockDateKey = persisted.DailyLossLockDateKey;
            _state.LastDailySummaryDateKey = persisted.LastDailySummaryDateKey;
            _state.LastMonthlySummaryKey = persisted.LastMonthlySummaryKey;
            _state.LastEntryDecisionDigest = persisted.LastEntryDecisionDigest;
            _state.LastEntryDecisionAt = persisted.LastEntryDecisionAt;
            _state.Paused = persisted.Paused;
            _state.OpenPositions.Clear();
            _state.OpenPositions.AddRange(persisted.OpenPositions);
            _state.ClosedPositions.Clear();
            _state.ClosedPositions.AddRange(persisted.ClosedPositions);
            _state.Journal.Clear();
            _state.Journal.AddRange(persisted.Journal);
            _state.LastAnalysisSnapshot = persisted.LastAnalysisSnapshot;
        }
        finally
        {
            _state.Gate.Release();
        }
    }
}
