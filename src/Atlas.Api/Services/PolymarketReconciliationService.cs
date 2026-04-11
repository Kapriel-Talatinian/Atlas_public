using Atlas.Api.Models;

namespace Atlas.Api.Services;

public interface IPolymarketReconciliationService
{
    Task<ReconciliationReport> ReconcileAsync(CancellationToken ct = default);
}

public sealed record ReconciliationReport(
    bool Healthy,
    double ClobBalanceUsdc,
    int ClobOpenOrders,
    int BotOpenPositions,
    IReadOnlyList<ReconciliationMismatch> Mismatches,
    DateTimeOffset Timestamp);

public sealed record ReconciliationMismatch(
    string Type,
    string MarketId,
    string Detail);

public sealed class PolymarketReconciliationService : IPolymarketReconciliationService
{
    private readonly IPolymarketClobClient _clob;
    private readonly IPolymarketBotService _bot;
    private readonly ITelegramSignalService _telegram;
    private readonly ILogger<PolymarketReconciliationService> _logger;

    public PolymarketReconciliationService(
        IPolymarketClobClient clob,
        IPolymarketBotService bot,
        ITelegramSignalService telegram,
        ILogger<PolymarketReconciliationService> logger)
    {
        _clob = clob;
        _bot = bot;
        _telegram = telegram;
        _logger = logger;
    }

    public async Task<ReconciliationReport> ReconcileAsync(CancellationToken ct = default)
    {
        var mismatches = new List<ReconciliationMismatch>();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        ClobBalanceSnapshot balance = await _clob.GetBalanceAsync(ct);
        IReadOnlyList<ClobOpenOrder> clobOrders = await _clob.GetOpenOrdersAsync(ct);
        PolymarketLiveSnapshot botSnapshot = await _bot.GetCachedSnapshotAsync(ct);

        // Check: orders on CLOB that bot doesn't know about
        var botMarketIds = botSnapshot.OpenPositions
            .Select(p => p.MarketId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (ClobOpenOrder order in clobOrders)
        {
            if (!botMarketIds.Contains(order.MarketId))
            {
                mismatches.Add(new ReconciliationMismatch(
                    "orphan-clob-order",
                    order.MarketId,
                    $"CLOB has live order {order.Id} ({order.Side} {order.OriginalSize}@{order.Price}) but bot has no matching position"));
            }
        }

        // Check: bot positions with no corresponding CLOB activity
        var clobMarketIds = clobOrders
            .Select(o => o.MarketId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fetch recent trades to see if positions were filled
        IReadOnlyList<ClobTradeRecord> recentTrades = await _clob.GetTradesAsync(limit: 200, ct: ct);
        var tradedMarketIds = recentTrades
            .Select(t => t.MarketId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var position in botSnapshot.OpenPositions)
        {
            bool hasClobOrder = clobMarketIds.Contains(position.MarketId);
            bool hasRecentTrade = tradedMarketIds.Contains(position.MarketId);

            if (!hasClobOrder && !hasRecentTrade)
            {
                mismatches.Add(new ReconciliationMismatch(
                    "phantom-bot-position",
                    position.MarketId,
                    $"Bot has open position {position.PositionId} ({position.Side} {position.StakeUsd:0.00}$) but no CLOB order or trade found"));
            }
        }

        // Check: balance sanity
        double botCash = botSnapshot.Portfolio.CashBalanceUsd;
        if (balance.TotalUsdc > 0 && Math.Abs(botCash - balance.AvailableUsdc) > balance.TotalUsdc * 0.20)
        {
            mismatches.Add(new ReconciliationMismatch(
                "balance-drift",
                "PORTFOLIO",
                $"Bot cash {botCash:0.00}$ vs CLOB available {balance.AvailableUsdc:0.00}$ — drift exceeds 20%"));
        }

        bool healthy = mismatches.Count == 0;
        var report = new ReconciliationReport(
            Healthy: healthy,
            ClobBalanceUsdc: balance.TotalUsdc,
            ClobOpenOrders: clobOrders.Count,
            BotOpenPositions: botSnapshot.OpenPositions.Count,
            Mismatches: mismatches,
            Timestamp: now);

        if (!healthy)
        {
            _logger.LogWarning(
                "Polymarket reconciliation found {Count} mismatch(es): {Details}",
                mismatches.Count,
                string.Join("; ", mismatches.Select(m => $"[{m.Type}] {m.MarketId}: {m.Detail}")));

            string alert = string.Join('\n',
                "ATLAS RECONCILIATION ALERT",
                $"Mismatches: {mismatches.Count}",
                $"CLOB balance: {balance.TotalUsdc:0.00}$",
                $"CLOB orders: {clobOrders.Count}",
                $"Bot positions: {botSnapshot.OpenPositions.Count}",
                string.Join('\n', mismatches.Select(m => $"- [{m.Type}] {m.Detail}")));
            _ = _telegram.SendAsync(alert, ct);
        }
        else
        {
            _logger.LogDebug(
                "Polymarket reconciliation healthy: balance={Balance:0.00}$, clobOrders={Orders}, botPositions={Positions}",
                balance.TotalUsdc, clobOrders.Count, botSnapshot.OpenPositions.Count);
        }

        return report;
    }
}
