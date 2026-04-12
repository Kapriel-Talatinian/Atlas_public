using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Api.Services;

public interface ITelegramSignalService
{
    bool IsConfigured { get; }
    Task SendAsync(string message, CancellationToken ct = default);
    Task SendWithKeyboardAsync(string message, TelegramInlineKeyboard keyboard, CancellationToken ct = default);
}

public sealed record TelegramInlineKeyboard(IReadOnlyList<IReadOnlyList<TelegramInlineButton>> Rows);

public sealed record TelegramInlineButton(string Text, string? Callback = null, string? Url = null, string? WebApp = null);

public sealed class NoopTelegramSignalService : ITelegramSignalService
{
    public bool IsConfigured => false;

    public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
    public Task SendWithKeyboardAsync(string message, TelegramInlineKeyboard keyboard, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class TelegramSignalService : ITelegramSignalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelegramSignalService> _logger;
    private readonly string _token;
    private readonly string _chatId;

    public TelegramSignalService(IHttpClientFactory httpClientFactory, ILogger<TelegramSignalService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")?.Trim() ?? string.Empty;
        _chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID")?.Trim() ?? string.Empty;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token) && !string.IsNullOrWhiteSpace(_chatId);

    public Task SendAsync(string message, CancellationToken ct = default) =>
        SendCoreAsync(message, replyMarkup: null, ct);

    public Task SendWithKeyboardAsync(string message, TelegramInlineKeyboard keyboard, CancellationToken ct = default) =>
        SendCoreAsync(message, BuildReplyMarkup(keyboard), ct);

    private async Task SendCoreAsync(string message, object? replyMarkup, CancellationToken ct)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            var client = _httpClientFactory.CreateClient("telegram-bot");
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = _chatId,
                ["text"] = message.Trim(),
                ["disable_web_page_preview"] = true
            };
            if (replyMarkup is not null)
                payload["reply_markup"] = replyMarkup;

            string body = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync($"/bot{_token}/sendMessage", content, ct);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram signal delivery failed");
        }
    }

    private static object BuildReplyMarkup(TelegramInlineKeyboard keyboard)
    {
        var rows = new List<List<Dictionary<string, object>>>();
        foreach (var row in keyboard.Rows)
        {
            var cols = new List<Dictionary<string, object>>();
            foreach (var btn in row)
            {
                var dict = new Dictionary<string, object> { ["text"] = btn.Text };
                if (!string.IsNullOrWhiteSpace(btn.Callback))
                    dict["callback_data"] = btn.Callback;
                if (!string.IsNullOrWhiteSpace(btn.Url))
                    dict["url"] = btn.Url;
                if (!string.IsNullOrWhiteSpace(btn.WebApp))
                    dict["web_app"] = new Dictionary<string, string> { ["url"] = btn.WebApp };
                cols.Add(dict);
            }
            rows.Add(cols);
        }
        return new Dictionary<string, object> { ["inline_keyboard"] = rows };
    }
}
