using Atlas.Api.Models;

namespace Atlas.Api.Services;

public sealed class PolymarketLiveBotWorkerService : BackgroundService
{
    private const string BotKey = "POLYMARKET-LIVE";

    private readonly IPolymarketBotService _botService;
    private readonly IBotLeaderElectionService _leaderElection;
    private readonly AtlasRuntimeContext _runtime;
    private readonly ILogger<PolymarketLiveBotWorkerService> _logger;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _leaseDuration;

    public PolymarketLiveBotWorkerService(
        IPolymarketBotService botService,
        IBotLeaderElectionService leaderElection,
        AtlasRuntimeContext runtime,
        ILogger<PolymarketLiveBotWorkerService> logger)
    {
        _botService = botService;
        _leaderElection = leaderElection;
        _runtime = runtime;
        _logger = logger;

        int tickSeconds = Math.Clamp(ParseIntEnv("POLYMARKET_WORKER_HEARTBEAT_SECONDS", 4), 1, 30);
        int leaseSeconds = Math.Max(tickSeconds + 2, Math.Clamp(ParseIntEnv("POLYMARKET_WORKER_LEASE_SECONDS", 16), 5, 120));
        _tick = TimeSpan.FromSeconds(tickSeconds);
        _leaseDuration = TimeSpan.FromSeconds(leaseSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.CanRunBotLoop)
        {
            _logger.LogInformation("Polymarket live worker skipped because runtime role is {Role}", _runtime.Role);
            return;
        }

        if (!string.Equals(Environment.GetEnvironmentVariable("POLYMARKET_BOT_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Polymarket live worker is disabled by POLYMARKET_BOT_ENABLED");
            return;
        }

        _logger.LogInformation(
            "Polymarket live worker starting with role {Role}, instance {InstanceId}, leader backend {Backend}",
            _runtime.Role,
            _runtime.InstanceId,
            _leaderElection.BackendName);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                BotLeaderLeaseSnapshot lease = _leaderElection.AcquireOrRenew(BotKey, _runtime, _leaseDuration);
                if (lease.IsLeader)
                    await _botService.RunAutopilotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polymarket live worker cycle failed");
            }

            try
            {
                await Task.Delay(_tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runtime.CanRunBotLoop)
            _leaderElection.Release(BotKey, _runtime);
        return base.StopAsync(cancellationToken);
    }

    private static int ParseIntEnv(string name, int fallback)
    {
        string raw = Environment.GetEnvironmentVariable(name) ?? string.Empty;
        return int.TryParse(raw, out int parsed) ? parsed : fallback;
    }
}
