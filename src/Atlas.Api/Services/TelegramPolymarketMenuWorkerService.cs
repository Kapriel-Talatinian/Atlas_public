using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Atlas.Api.Models;

namespace Atlas.Api.Services;

public static class TelegramPolymarketMenuFormatter
{
    public static string BuildMenu(bool isPaused = false) => string.Join('\n',
        "ATLAS CONTROL CENTER",
        isPaused ? "STATE: PAUSED (no new entries)" : "STATE: ACTIVE",
        "",
        "Tap a button or send /help for the full list.",
        "",
        "PORTFOLIO",
        "/pnl /metrics /stats /today /week /month",
        "",
        "POSITIONS",
        "/positions /history /recent /best /worst",
        "",
        "RISK & EXITS",
        "/risk /exposure /exits /streak",
        "",
        "SCANNER",
        "/scanner /ready /blocked /spot",
        "",
        "PER ASSET",
        "/btc /eth /sol /hours",
        "",
        "CONTROL",
        "/pause /resume /status /config /uptime /ping /errors /journal",
        "",
        "/menu /help - always come back here");

    public static string BuildToday(PolymarketLiveSnapshot snapshot, DateTimeOffset now)
    {
        var todayClosed = snapshot.RecentClosedPositions
            .Where(p => p.ExitTime.HasValue && p.ExitTime.Value.UtcDateTime.Date == now.UtcDateTime.Date)
            .ToList();
        double pnl = todayClosed.Sum(p => p.RealizedPnlUsd);
        int wins = todayClosed.Count(p => p.RealizedPnlUsd > 0);
        int losses = todayClosed.Count(p => p.RealizedPnlUsd < 0);
        double winRate = todayClosed.Count > 0 ? (double)wins / todayClosed.Count : 0;
        double bestTrade = todayClosed.DefaultIfEmpty().Max(p => p?.RealizedPnlUsd ?? 0);
        double worstTrade = todayClosed.DefaultIfEmpty().Min(p => p?.RealizedPnlUsd ?? 0);

        return string.Join('\n',
            "TODAY",
            $"Realized PnL: {pnl:+0.00;-0.00}$",
            $"Trades: {todayClosed.Count} ({wins}W / {losses}L)",
            $"Win rate: {winRate:P1}",
            $"Best: {bestTrade:+0.00;-0.00}$",
            $"Worst: {worstTrade:+0.00;-0.00}$",
            $"Still open: {snapshot.OpenPositions.Count}");
    }

    public static string BuildWeek(PolymarketLiveSnapshot snapshot, DateTimeOffset now)
    {
        DateTime cutoff = now.UtcDateTime.AddDays(-7);
        var weekClosed = snapshot.RecentClosedPositions
            .Where(p => p.ExitTime.HasValue && p.ExitTime.Value.UtcDateTime >= cutoff)
            .ToList();
        double pnl = weekClosed.Sum(p => p.RealizedPnlUsd);
        int wins = weekClosed.Count(p => p.RealizedPnlUsd > 0);
        int losses = weekClosed.Count(p => p.RealizedPnlUsd < 0);
        double winRate = weekClosed.Count > 0 ? (double)wins / weekClosed.Count : 0;
        double avgWin = wins > 0 ? weekClosed.Where(p => p.RealizedPnlUsd > 0).Average(p => p.RealizedPnlUsd) : 0;
        double avgLoss = losses > 0 ? weekClosed.Where(p => p.RealizedPnlUsd < 0).Average(p => p.RealizedPnlUsd) : 0;

        return string.Join('\n',
            "LAST 7 DAYS",
            $"Realized PnL: {pnl:+0.00;-0.00}$",
            $"Trades: {weekClosed.Count} ({wins}W / {losses}L)",
            $"Win rate: {winRate:P1}",
            $"Avg winner: {avgWin:+0.00;-0.00}$",
            $"Avg loser: {avgLoss:+0.00;-0.00}$");
    }

    public static string BuildByAsset(PolymarketLiveSnapshot snapshot, string asset)
    {
        string upper = asset.ToUpperInvariant();
        var openForAsset = snapshot.OpenPositions.Where(p => p.Asset.Equals(upper, StringComparison.OrdinalIgnoreCase)).ToList();
        var closedForAsset = snapshot.RecentClosedPositions.Where(p => p.Asset.Equals(upper, StringComparison.OrdinalIgnoreCase)).ToList();
        double realized = closedForAsset.Sum(p => p.RealizedPnlUsd);
        int wins = closedForAsset.Count(p => p.RealizedPnlUsd > 0);
        int losses = closedForAsset.Count(p => p.RealizedPnlUsd < 0);
        double winRate = closedForAsset.Count > 0 ? (double)wins / closedForAsset.Count : 0;
        double openExposure = openForAsset.Sum(p => p.StakeUsd);
        double unrealized = openForAsset.Sum(p => p.UnrealizedPnlUsd);

        return string.Join('\n',
            $"{upper} BREAKDOWN",
            $"Open: {openForAsset.Count} positions | exposure {openExposure:0.00}$ | uPnL {unrealized:+0.00;-0.00}$",
            $"Closed: {closedForAsset.Count} trades | realized {realized:+0.00;-0.00}$",
            $"Win rate: {winRate:P1} ({wins}W / {losses}L)");
    }

    public static string BuildBest(PolymarketLiveSnapshot snapshot)
    {
        var top = snapshot.RecentClosedPositions
            .Where(p => p.RealizedPnlUsd > 0)
            .OrderByDescending(p => p.RealizedPnlUsd)
            .Take(5)
            .ToList();

        if (top.Count == 0)
            return "TOP WINNERS\nNo winning trade yet.";

        var lines = new List<string> { "TOP WINNERS" };
        foreach (PolymarketPosition p in top)
        {
            string outcome = p.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            lines.Add($"+{p.RealizedPnlUsd:0.00}$ | {p.DisplayLabel} {outcome} | stake {p.StakeUsd:0.00}$ | {p.ExitReason}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildWorst(PolymarketLiveSnapshot snapshot)
    {
        var bottom = snapshot.RecentClosedPositions
            .Where(p => p.RealizedPnlUsd < 0)
            .OrderBy(p => p.RealizedPnlUsd)
            .Take(5)
            .ToList();

        if (bottom.Count == 0)
            return "BIGGEST LOSERS\nNo losing trade yet.";

        var lines = new List<string> { "BIGGEST LOSERS" };
        foreach (PolymarketPosition p in bottom)
        {
            string outcome = p.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            lines.Add($"{p.RealizedPnlUsd:0.00}$ | {p.DisplayLabel} {outcome} | stake {p.StakeUsd:0.00}$ | {p.ExitReason}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildScanner(PolymarketLiveSnapshot snapshot)
    {
        var opps = snapshot.Opportunities
            .OrderByDescending(o => o.ConvictionScore)
            .Take(8)
            .ToList();

        if (opps.Count == 0)
            return "SCANNER\nNo markets in view.";

        var lines = new List<string>
        {
            $"SCANNER ({snapshot.Stats.ActionableSignals} ready / {snapshot.Stats.ScannerSignals} signals / {snapshot.Stats.TradeableMarkets} tradeable)"
        };
        foreach (PolymarketMarketSignal s in opps)
        {
            double edge = Math.Max(s.EdgeYesPct, s.EdgeNoPct);
            string side = s.RecommendedSide.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            string tag = s.BotEligible ? "READY" : "blocked";
            lines.Add($"- {s.DisplayLabel} | {side} | edge {(edge * 100):+0.0;-0.0}% | conv {s.ConvictionScore:0} | {tag}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildReady(PolymarketLiveSnapshot snapshot)
    {
        var ready = snapshot.Opportunities
            .Where(o => o.BotEligible)
            .OrderByDescending(o => o.ConvictionScore)
            .Take(10)
            .ToList();

        if (ready.Count == 0)
            return "BOT-READY\nNo markets clear all gates right now.";

        var lines = new List<string> { $"BOT-READY ({ready.Count})" };
        foreach (PolymarketMarketSignal s in ready)
        {
            double edge = Math.Max(s.EdgeYesPct, s.EdgeNoPct);
            string side = s.RecommendedSide.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            double minutes = s.MinutesToExpiry;
            lines.Add($"- {s.DisplayLabel} | {side} | edge {(edge * 100):+0.0}% | conv {s.ConvictionScore:0} | T-{FormatMinutes(minutes)}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildBlocked(PolymarketLiveSnapshot snapshot)
    {
        var blocked = snapshot.Opportunities
            .Where(o => !string.Equals(o.RecommendedSide, "Pass", StringComparison.OrdinalIgnoreCase) && !o.BotEligible)
            .OrderByDescending(o => o.ConvictionScore)
            .Take(8)
            .ToList();

        if (blocked.Count == 0)
            return "BLOCKED\nNo scanner signal currently blocked.";

        var lines = new List<string> { $"BLOCKED ({blocked.Count})" };
        foreach (PolymarketMarketSignal s in blocked)
        {
            string reason = s.BotEligibilityReason.Length > 60
                ? s.BotEligibilityReason[..60] + "..."
                : s.BotEligibilityReason;
            lines.Add($"- {s.DisplayLabel}: {reason}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildRisk(PolymarketLiveSnapshot snapshot)
    {
        PolymarketBotPortfolioSnapshot p = snapshot.Portfolio;
        PolymarketRuntimeStatus r = snapshot.Runtime;
        var openByAsset = snapshot.OpenPositions
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(x => x.StakeUsd))
            .ToList();

        double exposurePct = p.EquityUsd > 0 ? p.GrossExposureUsd / p.EquityUsd : 0;

        var lines = new List<string>
        {
            "RISK SNAPSHOT",
            $"Daily PnL: {p.DailyPnlUsd:+0.00;-0.00}$ / limit -{r.DailyLossLimitUsd:0.00}$",
            $"Daily lock: {(r.DailyLossLockActive ? "ON" : "off")}",
            $"Drawdown: {p.DrawdownUsd:0.00}$ ({p.DrawdownPct:P1})",
            $"Peak equity: {p.PeakEquityUsd:0.00}$",
            $"Gross exposure: {p.GrossExposureUsd:0.00}$ ({exposurePct:P1} of equity)",
            $"Max per trade: {r.MaxTradeUsd:0.00}$",
            $"Open risk: {p.MaxTradeRiskUsd:0.00}$"
        };
        if (openByAsset.Count > 0)
        {
            lines.Add("By asset:");
            foreach (var g in openByAsset)
            {
                double expo = g.Sum(x => x.StakeUsd);
                double upl = g.Sum(x => x.UnrealizedPnlUsd);
                lines.Add($"- {g.Key}: {g.Count()} open | {expo:0.00}$ exposure | uPnL {upl:+0.00;-0.00}$");
            }
        }
        return string.Join('\n', lines);
    }

    public static string BuildExposure(PolymarketLiveSnapshot snapshot)
    {
        var rows = snapshot.OpenPositions
            .GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Asset = g.Key,
                Count = g.Count(),
                Exposure = g.Sum(x => x.StakeUsd),
                Unrealized = g.Sum(x => x.UnrealizedPnlUsd)
            })
            .OrderByDescending(x => x.Exposure)
            .ToList();

        if (rows.Count == 0)
            return "EXPOSURE\nFlat.";

        double equity = snapshot.Portfolio.EquityUsd;
        var lines = new List<string> { "EXPOSURE BY ASSET" };
        foreach (var r in rows)
        {
            double pct = equity > 0 ? r.Exposure / equity : 0;
            lines.Add($"- {r.Asset}: {r.Count} open | {r.Exposure:0.00}$ ({pct:P1}) | uPnL {r.Unrealized:+0.00;-0.00}$");
        }
        lines.Add($"Total: {rows.Sum(r => r.Exposure):0.00}$ ({rows.Sum(r => r.Exposure) / Math.Max(equity, 1):P1} of equity)");
        return string.Join('\n', lines);
    }

    public static string BuildExits(PolymarketLiveSnapshot snapshot)
    {
        var closed = snapshot.RecentClosedPositions;
        if (closed.Count == 0)
            return "EXIT BREAKDOWN\nNo closed trade yet.";

        var groups = closed
            .GroupBy(p => string.IsNullOrWhiteSpace(p.ExitReason) ? "unknown" : p.ExitReason.ToLowerInvariant())
            .Select(g => new
            {
                Reason = g.Key,
                Count = g.Count(),
                TotalPnl = g.Sum(p => p.RealizedPnlUsd),
                AvgPnl = g.Average(p => p.RealizedPnlUsd)
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        double total = closed.Count;
        var lines = new List<string> { $"EXIT BREAKDOWN (last {closed.Count})" };
        foreach (var g in groups)
        {
            double share = g.Count / total;
            lines.Add($"- {g.Reason}: {g.Count} ({share:P0}) | avg {g.AvgPnl:+0.00;-0.00}$ | total {g.TotalPnl:+0.00;-0.00}$");
        }
        return string.Join('\n', lines);
    }

    public static string BuildStreak(PolymarketLiveSnapshot snapshot)
    {
        var ordered = snapshot.RecentClosedPositions
            .Where(p => p.RealizedPnlUsd != 0)
            .OrderByDescending(p => p.ExitTime)
            .ToList();

        if (ordered.Count == 0)
            return "STREAK\nNo closed trade yet.";

        int currentStreak = 0;
        bool currentWin = ordered[0].RealizedPnlUsd > 0;
        double currentPnl = 0;
        foreach (var p in ordered)
        {
            bool isWin = p.RealizedPnlUsd > 0;
            if (isWin == currentWin)
            {
                currentStreak++;
                currentPnl += p.RealizedPnlUsd;
            }
            else break;
        }

        int bestWinStreak = 0, bestLossStreak = 0, run = 0;
        bool? prev = null;
        foreach (var p in ordered.OrderBy(x => x.ExitTime))
        {
            bool win = p.RealizedPnlUsd > 0;
            if (prev is null || prev.Value != win)
            {
                run = 1;
            }
            else run++;
            if (win) bestWinStreak = Math.Max(bestWinStreak, run);
            else bestLossStreak = Math.Max(bestLossStreak, run);
            prev = win;
        }

        string last10 = string.Join(" ",
            ordered.Take(10).Select(p => p.RealizedPnlUsd > 0 ? "W" : "L"));

        return string.Join('\n',
            "STREAK",
            $"Current: {currentStreak} {(currentWin ? "wins" : "losses")} ({currentPnl:+0.00;-0.00}$)",
            $"Best win streak: {bestWinStreak}",
            $"Worst loss streak: {bestLossStreak}",
            $"Last 10: {last10}");
    }

    public static string BuildSpot(PolymarketLiveSnapshot snapshot)
    {
        if (snapshot.Assets.Count == 0)
            return "SPOT\nNo asset data yet.";

        var lines = new List<string> { "SPOT + VOL REGIME" };
        foreach (var a in snapshot.Assets.OrderBy(x => x.Asset))
        {
            string spotTxt = a.Spot >= 1000 ? $"${a.Spot:N0}" : $"${a.Spot:0.00}";
            lines.Add($"{a.Asset}: {spotTxt}");
            lines.Add($"  ATM IV {(a.AtmIv * 100):0.0}% | {a.Regime} | {a.LiveBiasLabel} ({a.LiveBiasScore:+0.0;-0.0})");
        }
        return string.Join('\n', lines);
    }

    public static string BuildStats(PolymarketLiveSnapshot snapshot)
    {
        var closed = snapshot.RecentClosedPositions;
        PolymarketBotPortfolioSnapshot p = snapshot.Portfolio;
        if (closed.Count == 0)
            return "STATS\nNo closed trade yet.";

        int total = closed.Count;
        int wins = closed.Count(x => x.RealizedPnlUsd > 0);
        int losses = closed.Count(x => x.RealizedPnlUsd < 0);
        double winRate = total > 0 ? (double)wins / total : 0;
        double grossWin = closed.Where(x => x.RealizedPnlUsd > 0).Sum(x => x.RealizedPnlUsd);
        double grossLoss = Math.Abs(closed.Where(x => x.RealizedPnlUsd < 0).Sum(x => x.RealizedPnlUsd));
        double profitFactor = grossLoss > 0 ? grossWin / grossLoss : 0;
        double avgTrade = closed.Average(x => x.RealizedPnlUsd);
        double best = closed.Max(x => x.RealizedPnlUsd);
        double worst = closed.Min(x => x.RealizedPnlUsd);
        double startingBalance = p.StartingBalanceUsd > 0 ? p.StartingBalanceUsd : 100;
        double roi = startingBalance > 0 ? p.NetPnlUsd / startingBalance : 0;

        return string.Join('\n',
            "TOTAL STATS",
            $"Trades: {total} ({wins}W / {losses}L)",
            $"Win rate: {winRate:P1}",
            $"Profit factor: {profitFactor:0.00}",
            $"Net PnL: {p.NetPnlUsd:+0.00;-0.00}$ ({(roi * 100):+0.0;-0.0}%)",
            $"Avg trade: {avgTrade:+0.00;-0.00}$",
            $"Best: {best:+0.00;-0.00}$",
            $"Worst: {worst:+0.00;-0.00}$");
    }

    public static string BuildMonth(PolymarketLiveSnapshot snapshot, DateTimeOffset now)
    {
        DateTime startOfMonth = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime startOfPrev = startOfMonth.AddMonths(-1);

        var monthTrades = snapshot.RecentClosedPositions
            .Where(p => p.ExitTime.HasValue && p.ExitTime.Value.UtcDateTime >= startOfMonth)
            .ToList();
        var prevMonthTrades = snapshot.RecentClosedPositions
            .Where(p => p.ExitTime.HasValue
                && p.ExitTime.Value.UtcDateTime >= startOfPrev
                && p.ExitTime.Value.UtcDateTime < startOfMonth)
            .ToList();

        double pnl = monthTrades.Sum(p => p.RealizedPnlUsd);
        double prevPnl = prevMonthTrades.Sum(p => p.RealizedPnlUsd);
        int wins = monthTrades.Count(p => p.RealizedPnlUsd > 0);
        int losses = monthTrades.Count(p => p.RealizedPnlUsd < 0);
        double winRate = monthTrades.Count > 0 ? (double)wins / monthTrades.Count : 0;

        return string.Join('\n',
            $"MONTH {now.UtcDateTime:MMMM yyyy}",
            $"Realized PnL: {pnl:+0.00;-0.00}$",
            $"Trades: {monthTrades.Count} ({wins}W / {losses}L)",
            $"Win rate: {winRate:P1}",
            $"Prev month: {prevPnl:+0.00;-0.00}$ ({prevMonthTrades.Count} trades)");
    }

    public static string BuildRecent(PolymarketLiveSnapshot snapshot)
    {
        var recent = snapshot.RecentClosedPositions.Take(15).ToList();
        if (recent.Count == 0) return "RECENT\nNo closed trade yet.";

        var lines = new List<string> { $"RECENT ({recent.Count})" };
        foreach (PolymarketPosition p in recent)
        {
            string side = p.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            string whenTxt = p.ExitTime.HasValue
                ? p.ExitTime.Value.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture)
                : "--:--";
            lines.Add($"{whenTxt} {p.Asset} {side} | {p.RealizedPnlUsd:+0.00;-0.00}$ | {p.ExitReason}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildHours(PolymarketLiveSnapshot snapshot)
    {
        var withTime = snapshot.RecentClosedPositions
            .Where(p => p.ExitTime.HasValue)
            .ToList();
        if (withTime.Count == 0) return "HOURS\nNo closed trade yet.";

        var buckets = withTime
            .GroupBy(p => p.ExitTime!.Value.UtcDateTime.Hour)
            .Select(g => new
            {
                Hour = g.Key,
                Count = g.Count(),
                Pnl = g.Sum(x => x.RealizedPnlUsd),
                WinRate = (double)g.Count(x => x.RealizedPnlUsd > 0) / g.Count()
            })
            .OrderByDescending(x => x.Pnl)
            .ToList();

        var best = buckets.Take(3);
        var worst = buckets.OrderBy(x => x.Pnl).Take(3);

        var lines = new List<string> { "BEST HOURS (UTC)" };
        foreach (var b in best)
            lines.Add($"{b.Hour:00}h: {b.Pnl:+0.00;-0.00}$ | {b.Count} trades | WR {b.WinRate:P0}");
        lines.Add("WORST HOURS (UTC)");
        foreach (var b in worst)
            lines.Add($"{b.Hour:00}h: {b.Pnl:+0.00;-0.00}$ | {b.Count} trades | WR {b.WinRate:P0}");
        return string.Join('\n', lines);
    }

    public static string BuildConfig()
    {
        string Env(string key, string fallback = "unset") =>
            Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } v ? v : fallback;

        return string.Join('\n',
            "CONFIG",
            $"Execution mode: {Env("POLYMARKET_EXECUTION_MODE", "paper")}",
            $"Trading enabled: {Env("POLYMARKET_TRADING_ENABLED", "false")}",
            $"Max per trade: {Env("POLYMARKET_MAX_TRADE_USD", "2")}$",
            $"Daily loss limit: {Env("POLYMARKET_DAILY_LOSS_LIMIT_USD", "5")}$",
            $"Max new trades/cycle: {Env("POLYMARKET_MAX_NEW_TRADES_PER_CYCLE", "2")}",
            $"Scan interval: {Env("POLYMARKET_BOT_EVALUATION_SECONDS", "5")}s",
            $"Lookahead: {Env("POLYMARKET_LOOKAHEAD_MINUTES", "1440")} min",
            $"Max markets: {Env("POLYMARKET_MAX_MARKETS", "24")}",
            "Gates: edge >= 1.5% | conv >= 55 | qual >= 46",
            "       liq >= 5000$ | spread <= 5% | strike dist <= 5%",
            "Sizing: quarter-Kelly | stop-loss 35% stake",
            "Concentration: max 2 pos/asset | max 60% expo/asset");
    }

    public static string BuildUptime(PolymarketLiveSnapshot snapshot)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan sinceSnapshot = now - snapshot.Timestamp;
        var lastEntry = snapshot.Journal.FirstOrDefault(j => j.Type == "entry");
        var lastExit = snapshot.Journal.FirstOrDefault(j => j.Type == "exit");
        string lastEntryTxt = lastEntry != null
            ? $"{(now - lastEntry.Timestamp).TotalMinutes:0} min ago"
            : "never";
        string lastExitTxt = lastExit != null
            ? $"{(now - lastExit.Timestamp).TotalMinutes:0} min ago"
            : "never";

        return string.Join('\n',
            "UPTIME",
            $"Now (UTC): {now:yyyy-MM-dd HH:mm:ss}",
            $"Last scan: {sinceSnapshot.TotalSeconds:0}s ago",
            $"Last entry: {lastEntryTxt}",
            $"Last exit: {lastExitTxt}",
            $"Scanner state: {snapshot.Status}");
    }

    public static string BuildPing(PolymarketLiveSnapshot snapshot) =>
        $"PONG\n{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\nLast scan {(DateTimeOffset.UtcNow - snapshot.Timestamp).TotalSeconds:0}s ago";

    public static string BuildErrors(PolymarketLiveSnapshot snapshot)
    {
        var errors = snapshot.Journal
            .Where(j => j.Type is "clob-reject" or "clob-sell-fail" or "risk")
            .Take(10)
            .ToList();

        if (errors.Count == 0)
            return "ERRORS / ALERTS\nNo recent error.";

        var lines = new List<string> { $"ERRORS / ALERTS ({errors.Count})" };
        foreach (PolymarketJournalEntry e in errors)
            lines.Add($"[{e.Timestamp.UtcDateTime:HH:mm}] {e.Type}: {e.Headline} — {e.Detail}");
        return string.Join('\n', lines);
    }

    public static string BuildHelp() => string.Join('\n',
        "ATLAS COMMAND REFERENCE",
        "",
        "PORTFOLIO",
        "/pnl /metrics /stats /today /week /month /recent",
        "",
        "POSITIONS",
        "/positions /history /best /worst /exits /streak",
        "",
        "RISK",
        "/risk /exposure /limits",
        "",
        "SCANNER / MARKETS",
        "/scanner /ready /blocked /spot",
        "",
        "PER ASSET",
        "/btc /eth /sol /hours",
        "",
        "OPERATIONS",
        "/status /config /uptime /ping /journal /errors",
        "",
        "CONTROL",
        "/pause /resume",
        "",
        "/menu - main control center",
        "/help - this reference");

    public static TelegramInlineKeyboard BuildMenuKeyboard(string? dashboardUrl = null)
    {
        var rows = new List<IReadOnlyList<TelegramInlineButton>>
        {
            new List<TelegramInlineButton>
            {
                new("PnL", Callback: "/pnl"),
                new("Metrics", Callback: "/metrics"),
                new("Today", Callback: "/today")
            },
            new List<TelegramInlineButton>
            {
                new("Positions", Callback: "/positions"),
                new("History", Callback: "/history"),
                new("Journal", Callback: "/journal")
            },
            new List<TelegramInlineButton>
            {
                new("BTC", Callback: "/btc"),
                new("ETH", Callback: "/eth"),
                new("SOL", Callback: "/sol")
            },
            new List<TelegramInlineButton>
            {
                new("Best", Callback: "/best"),
                new("Worst", Callback: "/worst"),
                new("Status", Callback: "/status")
            },
            new List<TelegramInlineButton>
            {
                new("Pause", Callback: "/pause"),
                new("Resume", Callback: "/resume")
            }
        };

        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            rows.Add(new List<TelegramInlineButton>
            {
                new("Open Dashboard", WebApp: dashboardUrl)
            });
        }

        return new TelegramInlineKeyboard(rows);
    }

    public static string BuildStatus(PolymarketLiveSnapshot snapshot)
    {
        return string.Join('\n',
            "ATLAS STATUS",
            $"Status: {snapshot.Status}",
            $"Runtime: {snapshot.Runtime.RuntimeMode}",
            $"Trading: {(snapshot.Runtime.TradingEnabled ? "on" : "off")}",
            $"Risk lock: {(snapshot.Runtime.DailyLossLockActive ? "on" : "off")}",
            $"Scanner: {snapshot.Stats.ScannerSignals}",
            $"Bot-ready: {snapshot.Stats.ActionableSignals}",
            $"Open: {snapshot.Portfolio.OpenPositionsCount}",
            $"Closed: {snapshot.Portfolio.ClosedPositionsCount}",
            snapshot.Summary);
    }

    public static string BuildPnl(PolymarketLiveSnapshot snapshot)
    {
        PolymarketBotPortfolioSnapshot p = snapshot.Portfolio;
        return string.Join('\n',
            "ATLAS PNL",
            $"Starting: {p.StartingBalanceUsd:0.00}$",
            $"Cash: {p.CashBalanceUsd:0.00}$",
            $"Equity: {p.EquityUsd:0.00}$",
            $"Net: {p.NetPnlUsd:+0.00;-0.00}$",
            $"Daily: {p.DailyPnlUsd:+0.00;-0.00}$",
            $"Monthly: {p.MonthlyPnlUsd:+0.00;-0.00}$",
            $"Drawdown: {p.DrawdownUsd:0.00}$ ({p.DrawdownPct:P1})",
            $"Win rate: {p.WinRate:P1}");
    }

    public static string BuildMetrics(PolymarketLiveSnapshot snapshot)
    {
        PolymarketBotPortfolioSnapshot p = snapshot.Portfolio;
        return string.Join('\n',
            "ATLAS METRICS",
            $"Win rate: {p.WinRate:P1}",
            $"Avg winner: {p.AvgWinnerUsd:+0.00;-0.00}$",
            $"Avg loser: {p.AvgLoserUsd:+0.00;-0.00}$",
            $"Drawdown: {p.DrawdownUsd:0.00}$ ({p.DrawdownPct:P1})",
            $"Gross exposure: {p.GrossExposureUsd:0.00}$",
            $"Open positions: {p.OpenPositionsCount}",
            $"Closed positions: {p.ClosedPositionsCount}");
    }

    public static string BuildPositions(PolymarketLiveSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.OpenPositions.Count == 0)
            return "OPEN POSITIONS\nFlat right now.";

        var lines = new List<string> { $"OPEN POSITIONS ({snapshot.OpenPositions.Count})" };
        foreach (PolymarketPosition position in snapshot.OpenPositions.Take(5))
        {
            double minutesLeft = Math.Max(0, (position.Expiry - now).TotalMinutes);
            string outcome = position.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            lines.Add($"- {position.DisplayLabel} | {outcome}");
            lines.Add($"  stake {position.StakeUsd:0.00}$ | entry {position.EntryPrice:P1} | mark {position.CurrentPrice:P1} | uPnL {position.UnrealizedPnlUsd:+0.00;-0.00}$ | T-left {FormatMinutes(minutesLeft)}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildHistory(PolymarketLiveSnapshot snapshot)
    {
        if (snapshot.RecentClosedPositions.Count == 0)
            return "RECENT CLOSED\nNo closed trades yet.";

        var lines = new List<string> { $"RECENT CLOSED ({snapshot.RecentClosedPositions.Count})" };
        foreach (PolymarketPosition position in snapshot.RecentClosedPositions.Take(5))
        {
            string outcome = position.Side.Replace("Buy ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().ToUpperInvariant();
            lines.Add($"- {position.DisplayLabel} | {outcome} | {position.RealizedPnlUsd:+0.00;-0.00}$ | {position.ExitReason}");
        }
        return string.Join('\n', lines);
    }

    public static string BuildJournal(PolymarketLiveSnapshot snapshot)
    {
        if (snapshot.Journal.Count == 0)
            return "DECISION JOURNAL\nNo journal entry yet.";

        var lines = new List<string> { "DECISION JOURNAL" };
        foreach (PolymarketJournalEntry entry in snapshot.Journal.Take(6))
            lines.Add($"- {entry.Type}: {entry.Headline} | {entry.Detail}");
        return string.Join('\n', lines);
    }

    public static string NormalizeCommand(string raw)
    {
        string trimmed = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        string token = trimmed.Split(['\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        token = token.TrimStart('/');
        int atIndex = token.IndexOf('@');
        if (atIndex >= 0)
            token = token[..atIndex];
        return token.Trim().ToLowerInvariant();
    }

    private static string FormatMinutes(double minutes)
    {
        if (minutes < 60)
            return $"{minutes:0.#}m";
        double hours = minutes / 60.0;
        if (hours < 24)
            return $"{hours:0.#}h";
        return $"{hours / 24.0:0.#}d";
    }
}

public sealed class TelegramPolymarketMenuWorkerService : BackgroundService
{
    private const string BotKey = "POLYMARKET-LIVE";
    private sealed record TelegramApiEnvelope<T>(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("error_code")] int? ErrorCode,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record TelegramWebhookInfo(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("pending_update_count")] int PendingUpdateCount,
        [property: JsonPropertyName("last_error_message")] string? LastErrorMessage);

    private sealed record TelegramGetUpdatesResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("result")] List<TelegramUpdate>? Result);

    private sealed record TelegramUpdate(
        [property: JsonPropertyName("update_id")] long UpdateId,
        [property: JsonPropertyName("message")] TelegramMessage? Message,
        [property: JsonPropertyName("callback_query")] TelegramCallbackQuery? CallbackQuery);

    private sealed record TelegramMessage(
        [property: JsonPropertyName("message_id")] long MessageId,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("chat")] TelegramChat? Chat);

    private sealed record TelegramChat(
        [property: JsonPropertyName("id")] long Id);

    private sealed record TelegramCallbackQuery(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("data")] string? Data,
        [property: JsonPropertyName("message")] TelegramMessage? Message,
        [property: JsonPropertyName("from")] TelegramCallbackUser? From);

    private sealed record TelegramCallbackUser(
        [property: JsonPropertyName("id")] long Id);

    private sealed record TelegramSetCommandsRequest(
        [property: JsonPropertyName("commands")] IReadOnlyList<TelegramBotCommand> Commands);

    private sealed record TelegramBotCommand(
        [property: JsonPropertyName("command")] string Command,
        [property: JsonPropertyName("description")] string Description);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPolymarketBotService _polymarketBotService;
    private readonly ITelegramSignalService _telegram;
    private readonly IBotLeaderElectionService _leaderElection;
    private readonly AtlasRuntimeContext _runtime;
    private readonly ILogger<TelegramPolymarketMenuWorkerService> _logger;
    private readonly string _token;
    private readonly string _chatId;
    private readonly string? _dashboardUrl;
    private long _offset;

    public TelegramPolymarketMenuWorkerService(
        IHttpClientFactory httpClientFactory,
        IPolymarketBotService polymarketBotService,
        ITelegramSignalService telegram,
        IBotLeaderElectionService leaderElection,
        AtlasRuntimeContext runtime,
        ILogger<TelegramPolymarketMenuWorkerService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _polymarketBotService = polymarketBotService;
        _telegram = telegram;
        _leaderElection = leaderElection;
        _runtime = runtime;
        _logger = logger;
        _token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? string.Empty;
        _chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")?.Trim() ?? string.Empty;
        string rawDashboard = Environment.GetEnvironmentVariable("ATLAS_DASHBOARD_URL")?.Trim() ?? string.Empty;
        _dashboardUrl = rawDashboard.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? rawDashboard : null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.CanRunBotLoop)
        {
            _logger.LogInformation("Telegram command worker skipped because runtime role is {Role}", _runtime.Role);
            return;
        }

        if (!_telegram.IsConfigured || string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_chatId))
        {
            _logger.LogInformation("Telegram command worker is disabled because bot token or chat id is missing.");
            return;
        }

        _logger.LogInformation(
            "Telegram command worker starting on instance {InstanceId}, chatId={ChatId}, leaderBackend={LeaderBackend}",
            _runtime.InstanceId, _chatId, _leaderElection.BackendName);
        await WarmupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                BotLeaderLeaseSnapshot lease = _leaderElection.AcquireOrRenew(BotKey, _runtime, TimeSpan.FromSeconds(30));
                if (!lease.IsLeader)
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                    continue;
                }

                IReadOnlyList<TelegramUpdate> updates = await GetUpdatesAsync(stoppingToken);
                if (updates.Count > 0)
                    _logger.LogInformation("Telegram command worker received {Count} update(s), offset={Offset}", updates.Count, _offset);

                foreach (TelegramUpdate update in updates)
                {
                    _offset = Math.Max(_offset, update.UpdateId + 1);
                    await HandleUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram command worker poll failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime.CanRunBotLoop)
            _leaderElection.Release(BotKey, _runtime);
        return base.StopAsync(cancellationToken);
    }

    private async Task WarmupAsync(CancellationToken ct)
    {
        try
        {
            await EnsurePollingModeAsync(ct);
            await RegisterCommandsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram command worker warmup failed; continuing with polling loop");
        }
    }

    private async Task RegisterCommandsAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("telegram-bot");
            var payload = new TelegramSetCommandsRequest(new[]
            {
                new TelegramBotCommand("menu", "Control center"),
                new TelegramBotCommand("pnl", "Equity, cash, PnL"),
                new TelegramBotCommand("stats", "Total stats (WR, PF, best, worst)"),
                new TelegramBotCommand("today", "Today performance"),
                new TelegramBotCommand("week", "Last 7 days"),
                new TelegramBotCommand("month", "Current month vs previous"),
                new TelegramBotCommand("positions", "Open positions"),
                new TelegramBotCommand("history", "Recent closed"),
                new TelegramBotCommand("recent", "Last 15 trades"),
                new TelegramBotCommand("best", "Top winners"),
                new TelegramBotCommand("worst", "Biggest losers"),
                new TelegramBotCommand("risk", "Risk snapshot"),
                new TelegramBotCommand("exposure", "Exposure by asset"),
                new TelegramBotCommand("exits", "Exit reason breakdown"),
                new TelegramBotCommand("streak", "Win/loss streak"),
                new TelegramBotCommand("scanner", "Top ranked markets"),
                new TelegramBotCommand("ready", "Bot-ready markets"),
                new TelegramBotCommand("blocked", "Blocked signals + reason"),
                new TelegramBotCommand("spot", "Spot prices + vol regime"),
                new TelegramBotCommand("btc", "BTC breakdown"),
                new TelegramBotCommand("eth", "ETH breakdown"),
                new TelegramBotCommand("sol", "SOL breakdown"),
                new TelegramBotCommand("hours", "Best/worst trading hours"),
                new TelegramBotCommand("status", "Runtime status"),
                new TelegramBotCommand("config", "Bot configuration"),
                new TelegramBotCommand("uptime", "Last scan and activity"),
                new TelegramBotCommand("ping", "Health check"),
                new TelegramBotCommand("errors", "Recent errors and alerts"),
                new TelegramBotCommand("journal", "Decision log"),
                new TelegramBotCommand("pause", "Block new entries"),
                new TelegramBotCommand("resume", "Unblock entries"),
                new TelegramBotCommand("metrics", "Quick metrics summary"),
                new TelegramBotCommand("help", "Full command reference")
            });
            using HttpResponseMessage response = await client.PostAsJsonAsync($"/bot{_token}/setMyCommands", payload, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register Telegram bot commands");
        }
    }

    private async Task EnsurePollingModeAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("telegram-bot");
        TelegramApiEnvelope<TelegramWebhookInfo>? webhook = await client.GetFromJsonAsync<TelegramApiEnvelope<TelegramWebhookInfo>>(
            $"/bot{_token}/getWebhookInfo",
            ct);

        if (webhook?.Result is null)
            return;

        if (!string.IsNullOrWhiteSpace(webhook.Result.Url))
        {
            _logger.LogInformation(
                "Telegram command worker detected webhook mode; clearing webhook before long polling. pendingUpdates={Pending}",
                webhook.Result.PendingUpdateCount);

            using HttpResponseMessage response = await client.PostAsync(
                $"/bot{_token}/deleteWebhook?drop_pending_updates=false",
                content: null,
                ct);
            response.EnsureSuccessStatusCode();
        }
        else if (webhook.Result.PendingUpdateCount > 0)
        {
            _logger.LogInformation(
                "Telegram command worker found {Pending} pending Telegram update(s) ready to process",
                webhook.Result.PendingUpdateCount);
        }
    }

    private async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(CancellationToken ct, int timeoutSeconds = 20)
    {
        var client = _httpClientFactory.CreateClient("telegram-bot");
        string path = $"/bot{_token}/getUpdates?timeout={timeoutSeconds}&offset={_offset}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D";
        using HttpResponseMessage response = await client.GetAsync(path, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Telegram getUpdates 409 conflict — clearing webhook and retrying. body={Body}",
                body);
            await EnsurePollingModeAsync(ct);
            return [];
        }

        response.EnsureSuccessStatusCode();

        TelegramGetUpdatesResponse? payload = await response.Content.ReadFromJsonAsync<TelegramGetUpdatesResponse>(cancellationToken: ct);
        return payload?.Result ?? [];
    }

    private async Task HandleUpdateAsync(TelegramUpdate update, CancellationToken ct)
    {
        // Callback query (button press)
        if (update.CallbackQuery is not null)
        {
            string? callbackData = update.CallbackQuery.Data;
            long? fromId = update.CallbackQuery.From?.Id;
            if (fromId.HasValue && !string.Equals(fromId.Value.ToString(CultureInfo.InvariantCulture), _chatId, StringComparison.Ordinal))
            {
                _logger.LogWarning("Telegram callback from user {UserId} ignored (expected {ExpectedChatId})", fromId, _chatId);
                return;
            }
            await AnswerCallbackAsync(update.CallbackQuery.Id, ct);
            if (!string.IsNullOrWhiteSpace(callbackData))
                await DispatchCommandAsync(TelegramPolymarketMenuFormatter.NormalizeCommand(callbackData), ct);
            return;
        }

        // Regular message
        string text = update.Message?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || update.Message?.Chat is null)
        {
            _logger.LogDebug("Telegram update {UpdateId} skipped: no text or no chat", update.UpdateId);
            return;
        }

        string incomingChatId = update.Message.Chat.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.Equals(incomingChatId, _chatId, StringComparison.Ordinal))
        {
            _logger.LogWarning("Telegram message from chat {IncomingChatId} ignored (expected {ExpectedChatId})", incomingChatId, _chatId);
            return;
        }

        string command = TelegramPolymarketMenuFormatter.NormalizeCommand(text);
        if (string.IsNullOrWhiteSpace(command))
            return;

        await DispatchCommandAsync(command, ct);
    }

    private async Task DispatchCommandAsync(string command, CancellationToken ct)
    {
        _logger.LogInformation("Telegram command worker accepted command /{Command} from chat {ChatId}", command, _chatId);

        if (command is "menu" or "start" or "help")
        {
            bool paused = _polymarketBotService.IsPaused;
            string menu = TelegramPolymarketMenuFormatter.BuildMenu(paused);
            TelegramInlineKeyboard keyboard = TelegramPolymarketMenuFormatter.BuildMenuKeyboard(_dashboardUrl);
            await _telegram.SendWithKeyboardAsync(menu, keyboard, ct);
            return;
        }

        if (command is "pause")
        {
            await _polymarketBotService.SetPausedAsync(true, ct);
            await _telegram.SendAsync("Bot paused. No new entries will be opened. Existing positions continue to be managed.", ct);
            return;
        }

        if (command is "resume")
        {
            await _polymarketBotService.SetPausedAsync(false, ct);
            await _telegram.SendAsync("Bot resumed. New entries allowed.", ct);
            return;
        }

        if (command is "config")
        {
            await _telegram.SendAsync(TelegramPolymarketMenuFormatter.BuildConfig(), ct);
            return;
        }
        if (command is "help")
        {
            await _telegram.SendAsync(TelegramPolymarketMenuFormatter.BuildHelp(), ct);
            return;
        }

        PolymarketLiveSnapshot snapshot = await _polymarketBotService.GetCachedSnapshotAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string response = command switch
        {
            "status" => TelegramPolymarketMenuFormatter.BuildStatus(snapshot),
            "pnl" => TelegramPolymarketMenuFormatter.BuildPnl(snapshot),
            "metrics" => TelegramPolymarketMenuFormatter.BuildMetrics(snapshot),
            "stats" => TelegramPolymarketMenuFormatter.BuildStats(snapshot),
            "today" => TelegramPolymarketMenuFormatter.BuildToday(snapshot, now),
            "week" => TelegramPolymarketMenuFormatter.BuildWeek(snapshot, now),
            "month" => TelegramPolymarketMenuFormatter.BuildMonth(snapshot, now),
            "positions" => TelegramPolymarketMenuFormatter.BuildPositions(snapshot, now),
            "history" => TelegramPolymarketMenuFormatter.BuildHistory(snapshot),
            "recent" => TelegramPolymarketMenuFormatter.BuildRecent(snapshot),
            "best" => TelegramPolymarketMenuFormatter.BuildBest(snapshot),
            "worst" => TelegramPolymarketMenuFormatter.BuildWorst(snapshot),
            "risk" => TelegramPolymarketMenuFormatter.BuildRisk(snapshot),
            "exposure" or "expo" => TelegramPolymarketMenuFormatter.BuildExposure(snapshot),
            "exits" => TelegramPolymarketMenuFormatter.BuildExits(snapshot),
            "streak" => TelegramPolymarketMenuFormatter.BuildStreak(snapshot),
            "scanner" or "scan" => TelegramPolymarketMenuFormatter.BuildScanner(snapshot),
            "ready" => TelegramPolymarketMenuFormatter.BuildReady(snapshot),
            "blocked" => TelegramPolymarketMenuFormatter.BuildBlocked(snapshot),
            "spot" or "prices" => TelegramPolymarketMenuFormatter.BuildSpot(snapshot),
            "btc" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "BTC"),
            "eth" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "ETH"),
            "sol" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "SOL"),
            "hours" => TelegramPolymarketMenuFormatter.BuildHours(snapshot),
            "journal" => TelegramPolymarketMenuFormatter.BuildJournal(snapshot),
            "errors" or "alerts" => TelegramPolymarketMenuFormatter.BuildErrors(snapshot),
            "uptime" => TelegramPolymarketMenuFormatter.BuildUptime(snapshot),
            "ping" => TelegramPolymarketMenuFormatter.BuildPing(snapshot),
            _ => "Unknown command. Use /menu or /help."
        };

        await _telegram.SendAsync(response, ct);
    }

    private async Task AnswerCallbackAsync(string callbackId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("telegram-bot");
            var payload = new { callback_query_id = callbackId };
            using HttpResponseMessage response = await client.PostAsJsonAsync($"/bot{_token}/answerCallbackQuery", payload, ct);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to answer Telegram callback");
        }
    }
}
