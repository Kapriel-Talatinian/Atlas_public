using Atlas.Api.Models;
using Atlas.Api.Services;
using Atlas.Core.Common;
using Atlas.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Tests;

public class SloMonitoringTests
{
    [Fact]
    public void HealthyTraffic_DoesNotBreachSlo()
    {
        var monitoring = new SystemMonitoringService();
        for (int i = 0; i < 120; i++)
            monitoring.ObserveRequest("/api/system/health", 200, 80);

        SloReport report = monitoring.GetSloReport();

        Assert.False(report.Breached);
        Assert.All(report.Windows, window =>
        {
            Assert.True(window.AvailabilityRatio >= 0.995);
            Assert.True(window.P95LatencyMs <= 450);
        });
    }

    [Fact]
    public void ErrorAndLatencySpike_BreachesSlo()
    {
        var monitoring = new SystemMonitoringService();

        for (int i = 0; i < 80; i++)
            monitoring.ObserveRequest("/api/options/chain", 200, 850);
        for (int i = 0; i < 20; i++)
            monitoring.ObserveRequest("/api/options/chain", 500, 1400);

        SloReport report = monitoring.GetSloReport();

        Assert.True(report.Breached);
        Assert.Contains(report.Windows, w => w.AvailabilityBreached || w.LatencyBreached);
        Assert.True(report.Flags.Count > 0);
    }
}

public class TradingPersistenceTests
{
    [Fact]
    public void PositionEvents_ArePersistedAndQueryable()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"atlas-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string dbPath = Path.Combine(tempRoot, "trading.db");

        string? previousDb = Environment.GetEnvironmentVariable("TRADING_DB_PATH");
        Environment.SetEnvironmentVariable("TRADING_DB_PATH", dbPath);

        try
        {
            var env = new FakeHostEnvironment(tempRoot);
            var persistence = new SqliteTradingPersistenceService(env, NullLogger<SqliteTradingPersistenceService>.Instance);

            var positions = new List<TradingPosition>
            {
                new(
                    Symbol: "BTC-28MAR26-90000-C-USDT",
                    Asset: "BTC",
                    NetQuantity: 2,
                    AvgEntryPrice: 1500,
                    MarkPrice: 1600,
                    Notional: 3200,
                    UnrealizedPnl: 200,
                    RealizedPnl: 0,
                    Greeks: GreeksResult.Zero,
                    UpdatedAt: DateTimeOffset.UtcNow,
                    InitialMarginRequirement: 180,
                    MaintenanceMarginRequirement: 130,
                    MarginMode: "Portfolio")
            };

            persistence.AppendPositionSnapshot(positions, "test-suite");
            IReadOnlyList<PersistedPositionEvent> fetched = persistence.GetPositionEvents(10);

            Assert.NotEmpty(fetched);
            Assert.Contains(fetched, evt =>
                evt.Source == "test-suite" &&
                evt.Positions.Count == 1 &&
                evt.Positions[0].Symbol.StartsWith("BTC-", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRADING_DB_PATH", previousDb);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ApplicationName = "Atlas.Tests";
            EnvironmentName = "Development";
            ContentRootFileProvider = null!;
        }

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; }
        public string ContentRootPath { get; set; }
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
    }
}
