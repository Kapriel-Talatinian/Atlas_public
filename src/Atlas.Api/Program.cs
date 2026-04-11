using Atlas.Api.Middleware;
using Atlas.Api.Models;
using Atlas.Api.Services;
using Atlas.ToxicFlow;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Json.Serialization;

LoadLocalEnvFiles();

var builder = WebApplication.CreateBuilder(args);
var corsOrigins = ParseCorsOrigins(builder.Configuration["CORS_ALLOWED_ORIGINS"]);
var port = Environment.GetEnvironmentVariable("PORT");
var runtimeContext = AtlasRuntimeContext.FromEnvironment();

if (!string.IsNullOrWhiteSpace(port) &&
    string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(runtimeContext);

// ┌─────────────────────────────────────────────────────┐
// │  DI REGISTRATION                                    │
// │  Swap DemoDataService → live data service           │
// │  to go from demo to production                      │
// └─────────────────────────────────────────────────────┘

builder.Services.AddSingleton<IMarketDataProvider, DemoDataService>();
builder.Services.AddSingleton<FlowClusterEngine>();
builder.Services.AddHttpClient("bybit-options", client =>
{
    client.BaseAddress = new Uri("https://api.bybit.com");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddHttpClient("bytick-options", client =>
{
    client.BaseAddress = new Uri("https://api.bytick.com");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddHttpClient("deribit-options", client =>
{
    client.BaseAddress = new Uri("https://www.deribit.com");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddHttpClient("polymarket-gamma", client =>
{
    client.BaseAddress = new Uri("https://gamma-api.polymarket.com");
    client.Timeout = TimeSpan.FromSeconds(12);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddHttpClient("telegram-bot", client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org");
    client.Timeout = TimeSpan.FromSeconds(35);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddSingleton<ISystemMonitoringService, SystemMonitoringService>();
builder.Services.AddSingleton<ITradingPersistenceService, SqliteTradingPersistenceService>();
if (PostgresConnectionResolver.HasPostgresConfiguration())
{
    builder.Services.AddSingleton<IBotStateRepository, PostgresBotStateRepository>();
    builder.Services.AddSingleton<IBotLeaderElectionService, PostgresBotLeaderElectionService>();
}
else
{
    builder.Services.AddSingleton<IBotStateRepository, FileBotStateRepository>();
    builder.Services.AddSingleton<IBotLeaderElectionService, SingleNodeBotLeaderElectionService>();
}
builder.Services.AddSingleton<IOptionsMarketDataService, ResilientOptionsMarketDataService>();
builder.Services.AddSingleton<IOptionsAnalyticsService, OptionsAnalyticsService>();
builder.Services.AddHttpClient("polymarket-clob", client =>
{
    client.BaseAddress = new Uri("https://clob.polymarket.com");
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Atlas.Api/1.0 (+https://github.com/Kapriel-Talatinian/Atlas_public)");
});
builder.Services.AddSingleton<IPolymarketLiveService, PolymarketLiveService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["POLYMARKET_PRIVATE_KEY"]))
{
    builder.Services.AddSingleton<IPolymarketSigningService, PolymarketSigningService>();
    builder.Services.AddSingleton<IPolymarketClobClient, PolymarketClobClient>();
}
else
{
    builder.Services.AddSingleton<IPolymarketSigningService, NoopPolymarketSigningService>();
    builder.Services.AddSingleton<IPolymarketClobClient, NoopPolymarketClobClient>();
}
builder.Services.AddSingleton<IPolymarketReconciliationService, PolymarketReconciliationService>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["TELEGRAM_BOT_TOKEN"]) &&
    !string.IsNullOrWhiteSpace(builder.Configuration["TELEGRAM_CHAT_ID"]))
{
    builder.Services.AddSingleton<ITelegramSignalService, TelegramSignalService>();
}
else
{
    builder.Services.AddSingleton<ITelegramSignalService, NoopTelegramSignalService>();
}
builder.Services.AddSingleton<IPolymarketBotService, PolymarketBotService>();
builder.Services.AddSingleton<INeuralTradingBrainService, NeuralTradingBrainService>();
builder.Services.AddSingleton<IPaperTradingService, PaperTradingService>();
builder.Services.AddSingleton<IExperimentalAutoTraderService, SharedPortfolioExperimentalAutoTraderService>();
builder.Services.AddSingleton<IIncidentRecoveryService, IncidentRecoveryService>();
if (runtimeContext.CanRunBotLoop)
{
    builder.Services.AddHostedService<ExperimentalBotWorkerService>();
    builder.Services.AddHostedService<PolymarketLiveBotWorkerService>();
    builder.Services.AddHostedService<TelegramPolymarketMenuWorkerService>();
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (corsOrigins.Length == 0 || corsOrigins.Contains("*"))
        {
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            return;
        }

        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();
var stateRepository = app.Services.GetRequiredService<IBotStateRepository>();
var leaderElection = app.Services.GetRequiredService<IBotLeaderElectionService>();
var forwardHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardHeaders.KnownNetworks.Clear();
forwardHeaders.KnownProxies.Clear();

app.UseForwardedHeaders(forwardHeaders);
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseMiddleware<RequestObservabilityMiddleware>();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { ok = true, timestamp = DateTimeOffset.UtcNow }));

app.Logger.LogInformation(
    "Atlas runtime booted with role {Role}, instance {InstanceId}, host {HostName}, botLoop={CanRunBotLoop}, stateBackend={StateBackend}, leaderBackend={LeaderBackend}",
    runtimeContext.Role,
    runtimeContext.InstanceId,
    runtimeContext.HostName,
    runtimeContext.CanRunBotLoop,
    stateRepository.BackendName,
    leaderElection.BackendName);

app.Run();

static string[] ParseCorsOrigins(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Array.Empty<string>();
    }

    return raw
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static void LoadLocalEnvFiles()
{
    var candidates = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".env.local"),
        Path.Combine(Directory.GetCurrentDirectory(), "src", "Atlas.Api", ".env.local"),
        Path.Combine(AppContext.BaseDirectory, ".env.local")
    }
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .Where(File.Exists)
    .ToList();

    foreach (string path in candidates)
    {
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            if (string.IsNullOrWhiteSpace(key) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
                continue;

            string value = line[(separator + 1)..].Trim().Trim('"');
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
