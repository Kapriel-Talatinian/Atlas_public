using Atlas.Api.Middleware;
using Atlas.Api.Services;
using Atlas.ToxicFlow;
using Microsoft.AspNetCore.HttpOverrides;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
var corsOrigins = ParseCorsOrigins(builder.Configuration["CORS_ALLOWED_ORIGINS"]);
var port = Environment.GetEnvironmentVariable("PORT");

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
builder.Services.AddSingleton<ISystemMonitoringService, SystemMonitoringService>();
builder.Services.AddSingleton<IOptionsMarketDataService, ResilientOptionsMarketDataService>();
builder.Services.AddScoped<IOptionsAnalyticsService, OptionsAnalyticsService>();
builder.Services.AddSingleton<IPaperTradingService, PaperTradingService>();
builder.Services.AddSingleton<IExperimentalAutoTraderService, ExperimentalAutoTraderService>();
builder.Services.AddHostedService<ExperimentalBotHostedService>();

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
