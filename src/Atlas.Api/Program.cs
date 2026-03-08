using Atlas.Api.Middleware;
using Atlas.Api.Services;
using Atlas.ToxicFlow;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

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
});
builder.Services.AddHttpClient("deribit-options", client =>
{
    client.BaseAddress = new Uri("https://www.deribit.com");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<ISystemMonitoringService, SystemMonitoringService>();
builder.Services.AddSingleton<IOptionsMarketDataService, ResilientOptionsMarketDataService>();
builder.Services.AddScoped<IOptionsAnalyticsService, OptionsAnalyticsService>();
builder.Services.AddSingleton<IPaperTradingService, PaperTradingService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseMiddleware<RequestObservabilityMiddleware>();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { ok = true, timestamp = DateTimeOffset.UtcNow }));

app.Run();
