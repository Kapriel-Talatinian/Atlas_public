using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Atlas.Api.Models;

namespace Atlas.Api.Services;

public static class TelegramPolymarketMenuFormatter
{
    public static string BuildMenu() => string.Join('\n',
        "ATLAS POLYMARKET MENU",
        "/status - runtime, scanner, bot-ready, risk lock",
        "/pnl - equity, cash, daily, monthly, net",
        "/metrics - win rate, avg winner, avg loser, drawdown, exposure",
        "/positions - open positions",
        "/history - recent closed trades",
        "/journal - recent decision log",
        "/menu - show this command center");

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
        [property: JsonPropertyName("message")] TelegramMessage? Message);

    private sealed record TelegramMessage(
        [property: JsonPropertyName("message_id")] long MessageId,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("chat")] TelegramChat? Chat);

    private sealed record TelegramChat(
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
                new TelegramBotCommand("menu", "Show Atlas Polymarket menu"),
                new TelegramBotCommand("status", "Runtime and scanner status"),
                new TelegramBotCommand("pnl", "Equity, cash and performance"),
                new TelegramBotCommand("metrics", "Win rate, averages, drawdown and exposure"),
                new TelegramBotCommand("positions", "Open positions"),
                new TelegramBotCommand("history", "Recent closed trades"),
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
        string path = $"/bot{_token}/getUpdates?timeout={timeoutSeconds}&offset={_offset}";
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

        _logger.LogInformation("Telegram command worker accepted command /{Command} from chat {ChatId}", command, _chatId);

        string response;
        if (command is "menu" or "start" or "help")
        {
            response = TelegramPolymarketMenuFormatter.BuildMenu();
        }
        else
        {
            PolymarketLiveSnapshot snapshot = await _polymarketBotService.GetCachedSnapshotAsync(ct);
            response = command switch
            {
                "status" => TelegramPolymarketMenuFormatter.BuildStatus(snapshot),
                "pnl" => TelegramPolymarketMenuFormatter.BuildPnl(snapshot),
                "metrics" => TelegramPolymarketMenuFormatter.BuildMetrics(snapshot),
                "positions" => TelegramPolymarketMenuFormatter.BuildPositions(snapshot, DateTimeOffset.UtcNow),
                "history" => TelegramPolymarketMenuFormatter.BuildHistory(snapshot),
                "journal" => TelegramPolymarketMenuFormatter.BuildJournal(snapshot),
                _ => "Unknown command. Use /menu."
            };
        }

        await _telegram.SendAsync(response, ct);
    }
}
