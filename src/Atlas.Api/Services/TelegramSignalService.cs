using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Atlas.Api.Services;

public interface ITelegramSignalService
{
    bool IsConfigured { get; }
    Task SendAsync(string message, CancellationToken ct = default);
}

public sealed class NoopTelegramSignalService : ITelegramSignalService
{
    public bool IsConfigured => false;

    public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class TelegramSignalService : ITelegramSignalService
{
    private sealed record TelegramSendMessageRequest(
        [property: JsonPropertyName("chat_id")] string ChatId,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("disable_web_page_preview")] bool DisableWebPagePreview);

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

    public async Task SendAsync(string message, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(message))
            return;

        try
        {
            var client = _httpClientFactory.CreateClient("telegram-bot");
            using HttpResponseMessage response = await client.PostAsJsonAsync(
                $"/bot{_token}/sendMessage",
                new TelegramSendMessageRequest(
                    ChatId: _chatId,
                    Text: message.Trim(),
                    DisableWebPagePreview: true),
                ct);

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram signal delivery failed");
        }
    }
}
