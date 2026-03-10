namespace Atlas.Api.Services;

public sealed class ExperimentalBotHostedService : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    private readonly IExperimentalAutoTraderService _autoTrader;
    private readonly ILogger<ExperimentalBotHostedService> _logger;

    public ExperimentalBotHostedService(
        IExperimentalAutoTraderService autoTrader,
        ILogger<ExperimentalBotHostedService> logger)
    {
        _autoTrader = autoTrader;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                await _autoTrader.RunAutopilotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Experimental bot background cycle failed");
            }

            try
            {
                await Task.Delay(Tick, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
