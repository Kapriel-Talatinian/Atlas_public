using Atlas.Api.Models;

namespace Atlas.Api.Services;

public sealed class ExperimentalBotWorkerService : BackgroundService
{
    private const string BotKey = "MULTI";

    private readonly IExperimentalAutoTraderService _autoTrader;
    private readonly IBotLeaderElectionService _leaderElection;
    private readonly AtlasRuntimeContext _runtime;
    private readonly ILogger<ExperimentalBotWorkerService> _logger;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _leaseDuration;

    public ExperimentalBotWorkerService(
        IExperimentalAutoTraderService autoTrader,
        IBotLeaderElectionService leaderElection,
        AtlasRuntimeContext runtime,
        ILogger<ExperimentalBotWorkerService> logger)
    {
        _autoTrader = autoTrader;
        _leaderElection = leaderElection;
        _runtime = runtime;
        _logger = logger;

        int tickSeconds = Math.Clamp(ParseIntEnv("BOT_HEARTBEAT_SECONDS", 3), 1, 30);
        int leaseSeconds = Math.Max(tickSeconds + 2, Math.Clamp(ParseIntEnv("BOT_LEASE_SECONDS", 15), 5, 120));
        _tick = TimeSpan.FromSeconds(tickSeconds);
        _leaseDuration = TimeSpan.FromSeconds(leaseSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_runtime.CanRunBotLoop)
        {
            _logger.LogInformation("Experimental bot worker skipped because runtime role is {Role}", _runtime.Role);
            return;
        }

        _logger.LogInformation(
            "Experimental bot worker starting with role {Role}, instance {InstanceId}, leader backend {Backend}",
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
                {
                    _logger.LogDebug(
                        "Bot leader lease held by {InstanceId} with token {Token} until {LeaseUntil}",
                        _runtime.InstanceId,
                        lease.FencingToken,
                        lease.LeaseUntil);
                    await _autoTrader.RunAutopilotAsync(stoppingToken);
                }
                else
                {
                    _logger.LogDebug(
                        "Bot worker standby. Current leader {LeaderInstanceId}, lease until {LeaseUntil}",
                        lease.OwnerInstanceId,
                        lease.LeaseUntil);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Experimental bot worker cycle failed");
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
