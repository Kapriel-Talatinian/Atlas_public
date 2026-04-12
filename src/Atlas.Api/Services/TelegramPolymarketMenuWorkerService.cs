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
        "Use the buttons below or type a command:",
        "",
        "PORTFOLIO",
        "/pnl - equity, cash, daily, monthly",
        "/metrics - win rate, drawdown, exposure",
        "/today - today PnL & trades",
        "/week - last 7 days summary",
        "",
        "POSITIONS",
        "/positions - open tickets",
        "/history - closed trades",
        "/best - top winners",
        "/worst - biggest losers",
        "",
        "PER ASSET",
        "/btc  /eth  /sol",
        "",
        "CONTROL",
        "/pause - block new entries",
        "/resume - unblock entries",
        "/status - runtime diagnostics",
        "/journal - decision log",
        "/menu - show this menu again");

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
                new TelegramBotCommand("menu", "Atlas command center"),
                new TelegramBotCommand("status", "Runtime and scanner status"),
                new TelegramBotCommand("pnl", "Equity, cash and performance"),
                new TelegramBotCommand("metrics", "Win rate, drawdown, exposure"),
                new TelegramBotCommand("today", "Today PnL and trades"),
                new TelegramBotCommand("week", "Last 7 days summary"),
                new TelegramBotCommand("positions", "Open positions"),
                new TelegramBotCommand("history", "Recent closed trades"),
                new TelegramBotCommand("best", "Top winning trades"),
                new TelegramBotCommand("worst", "Biggest losing trades"),
                new TelegramBotCommand("btc", "BTC breakdown"),
                new TelegramBotCommand("eth", "ETH breakdown"),
                new TelegramBotCommand("sol", "SOL breakdown"),
                new TelegramBotCommand("pause", "Block new entries"),
                new TelegramBotCommand("resume", "Unblock entries"),
                new TelegramBotCommand("journal", "Recent decision log")
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

        PolymarketLiveSnapshot snapshot = await _polymarketBotService.GetCachedSnapshotAsync(ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string response = command switch
        {
            "status" => TelegramPolymarketMenuFormatter.BuildStatus(snapshot),
            "pnl" => TelegramPolymarketMenuFormatter.BuildPnl(snapshot),
            "metrics" => TelegramPolymarketMenuFormatter.BuildMetrics(snapshot),
            "today" => TelegramPolymarketMenuFormatter.BuildToday(snapshot, now),
            "week" => TelegramPolymarketMenuFormatter.BuildWeek(snapshot, now),
            "positions" => TelegramPolymarketMenuFormatter.BuildPositions(snapshot, now),
            "history" => TelegramPolymarketMenuFormatter.BuildHistory(snapshot),
            "best" => TelegramPolymarketMenuFormatter.BuildBest(snapshot),
            "worst" => TelegramPolymarketMenuFormatter.BuildWorst(snapshot),
            "btc" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "BTC"),
            "eth" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "ETH"),
            "sol" => TelegramPolymarketMenuFormatter.BuildByAsset(snapshot, "SOL"),
            "journal" => TelegramPolymarketMenuFormatter.BuildJournal(snapshot),
            _ => "Unknown command. Use /menu."
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
