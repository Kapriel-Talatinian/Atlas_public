using Atlas.Api.Models;
using Atlas.Api.Services;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/options")]
public class OptionsController : ControllerBase
{
    private static readonly JsonSerializerOptions StreamJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOptionsAnalyticsService _analytics;
    private readonly IOptionsMarketDataService _marketData;

    public OptionsController(IOptionsAnalyticsService analytics, IOptionsMarketDataService marketData)
    {
        _analytics = analytics;
        _marketData = marketData;
    }

    [HttpGet("assets")]
    public async Task<ActionResult<IReadOnlyList<AssetMarketOverview>>> GetAssetOverview(
        [FromQuery] string assets = "BTC,ETH,SOL,WTI",
        CancellationToken ct = default)
    {
        var selectedAssets = NormalizeAssets(assets);
        var tasks = selectedAssets.Select(asset => _analytics.GetOverviewAsync(asset, ct));
        var overview = await Task.WhenAll(tasks);
        return Ok(overview.OrderBy(o => o.Asset).ToList());
    }

    [HttpGet("expiries")]
    public async Task<ActionResult<IReadOnlyList<string>>> GetExpiries(
        [FromQuery] string asset = "BTC",
        CancellationToken ct = default)
    {
        var expiries = await _analytics.GetExpiriesAsync(asset, ct);
        return Ok(expiries.Select(e => e.ToString("yyyy-MM-dd")).ToList());
    }

    [HttpGet("chain")]
    public async Task<ActionResult<IReadOnlyList<LiveOptionQuote>>> GetChain(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] string type = "all",
        [FromQuery] int limit = 220,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var chain = await _analytics.GetChainAsync(asset, parsedExpiry, type, limit, ct);
        return Ok(chain);
    }

    [HttpGet("surface")]
    public async Task<ActionResult<IReadOnlyList<VolSurfacePoint>>> GetVolSurface(
        [FromQuery] string asset = "BTC",
        [FromQuery] int limit = 600,
        CancellationToken ct = default)
    {
        var surface = await _analytics.GetSurfaceAsync(asset, limit, ct);
        return Ok(surface);
    }

    [HttpGet("models")]
    public async Task<ActionResult<OptionModelSnapshot>> GetOptionModels(
        [FromQuery] string symbol,
        CancellationToken ct = default)
    {
        try
        {
            var snapshot = await _analytics.GetModelSnapshotAsync(symbol, ct);
            return Ok(snapshot);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("calibration")]
    public async Task<ActionResult<ModelCalibrationSnapshot>> GetCalibration(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var calibration = await _analytics.GetModelCalibrationAsync(asset, parsedExpiry, ct);
        return Ok(calibration);
    }

    [HttpGet("signals")]
    public async Task<ActionResult<OptionSignalBoard>> GetSignals(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] string type = "all",
        [FromQuery] int limit = 140,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var board = await _analytics.GetSignalBoardAsync(asset, parsedExpiry, type, limit, ct);
        return Ok(board);
    }

    [HttpGet("regime")]
    public async Task<ActionResult<VolRegimeSnapshot>> GetRegime(
        [FromQuery] string asset = "BTC",
        CancellationToken ct = default)
    {
        var regime = await _analytics.GetRegimeAsync(asset, ct);
        return Ok(regime);
    }

    [HttpGet("macro-bias")]
    public async Task<ActionResult<MacroBiasSnapshot>> GetMacroBias(
        [FromQuery] string asset = "BTC",
        [FromQuery] int horizonDays = 30,
        [FromQuery] double growthMomentum = 0,
        [FromQuery] double inflationShock = 0,
        [FromQuery] double policyTightening = 0,
        [FromQuery] double usdStrength = 0,
        [FromQuery] double liquidityStress = 0,
        [FromQuery] double supplyShock = 0,
        [FromQuery] double riskAversion = 0,
        CancellationToken ct = default)
    {
        var request = new MacroBiasRequest(
            Asset: asset,
            HorizonDays: horizonDays,
            GrowthMomentum: growthMomentum,
            InflationShock: inflationShock,
            PolicyTightening: policyTightening,
            UsdStrength: usdStrength,
            LiquidityStress: liquidityStress,
            SupplyShock: supplyShock,
            RiskAversion: riskAversion);
        var snapshot = await _analytics.GetMacroBiasAsync(asset, request, ct);
        return Ok(snapshot);
    }

    [HttpGet("live-bias")]
    public async Task<ActionResult<MacroBiasSnapshot>> GetLiveBias(
        [FromQuery] string asset = "BTC",
        [FromQuery] int horizonDays = 30,
        CancellationToken ct = default)
    {
        var snapshot = await _analytics.GetLiveBiasAsync(asset, horizonDays, ct);
        return Ok(snapshot);
    }

    [HttpGet("recommendations")]
    public async Task<ActionResult<StrategyRecommendationBoard>> GetRecommendations(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] double size = 1,
        [FromQuery] string riskProfile = "balanced",
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var board = await _analytics.GetRecommendationsAsync(asset, parsedExpiry, size, riskProfile, ct);
        return Ok(board);
    }

    [HttpGet("optimizer")]
    public async Task<ActionResult<StrategyOptimizationBoard>> GetOptimizer(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] double size = 1,
        [FromQuery] string riskProfile = "balanced",
        [FromQuery] double targetDelta = 0,
        [FromQuery] double targetVega = 0,
        [FromQuery] double targetTheta = 0,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var board = await _analytics.OptimizeStrategiesAsync(
            asset,
            parsedExpiry,
            size,
            riskProfile,
            targetDelta,
            targetVega,
            targetTheta,
            ct);
        return Ok(board);
    }

    [HttpGet("exposure-grid")]
    public async Task<ActionResult<GreeksExposureGrid>> GetExposureGrid(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] int maxExpiries = 6,
        [FromQuery] int maxStrikes = 24,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var board = await _analytics.GetGreeksExposureGridAsync(asset, parsedExpiry, maxExpiries, maxStrikes, ct);
        return Ok(board);
    }

    [HttpGet("arbitrage")]
    public async Task<ActionResult<ArbitrageScanResult>> GetArbitrage(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] int limit = 120,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var scan = await _analytics.GetArbitrageScanAsync(asset, parsedExpiry, limit, ct);
        return Ok(scan);
    }

    [HttpGet("strategies/presets")]
    public async Task<ActionResult<IReadOnlyList<StrategyAnalysisResult>>> GetPresetStrategies(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] double size = 1,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
            return BadRequest("expiry must be in yyyy-MM-dd format");
        var presets = await _analytics.BuildPresetStrategiesAsync(asset, parsedExpiry, size, ct);
        return Ok(presets);
    }

    [HttpGet("stream")]
    public async Task StreamMarket(
        [FromQuery] string asset = "BTC",
        [FromQuery] string? expiry = null,
        [FromQuery] int chainLimit = 80,
        CancellationToken ct = default)
    {
        if (!TryParseExpiry(expiry, out var parsedExpiry))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("expiry must be in yyyy-MM-dd format", ct);
            return;
        }

        int safeLimit = Math.Clamp(chainLimit, 10, 300);
        Response.StatusCode = StatusCodes.Status200OK;
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");
        Response.Headers.ContentType = "text/event-stream";
        await Response.WriteAsync("retry: 1500\n\n", ct);
        await Response.Body.FlushAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var overviewTask = _analytics.GetOverviewAsync(asset, ct);
                var chainTask = _analytics.GetChainAsync(asset, parsedExpiry, "all", safeLimit, ct);
                await Task.WhenAll(overviewTask, chainTask);

                string payload = JsonSerializer.Serialize(new
                {
                    overview = overviewTask.Result,
                    chain = chainTask.Result,
                    timestamp = DateTimeOffset.UtcNow
                }, StreamJsonOptions);

                await Response.WriteAsync("event: market\n", ct);
                await Response.WriteAsync($"data: {payload}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested || HttpContext.RequestAborted.IsCancellationRequested)
                    break;

                string errorPayload = JsonSerializer.Serialize(new
                {
                    asset,
                    message = ex.Message,
                    timestamp = DateTimeOffset.UtcNow
                }, StreamJsonOptions);
                try
                {
                    await Response.WriteAsync("event: status\n", ct);
                    await Response.WriteAsync($"data: {errorPayload}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    [HttpPost("strategies/analyze")]
    public async Task<ActionResult<StrategyAnalysisResult>> AnalyzeStrategy(
        [FromBody] StrategyAnalyzeRequest request,
        CancellationToken ct = default)
    {
        if (request.Legs.Count == 0) return BadRequest("At least one leg is required.");
        var result = await _analytics.AnalyzeAsync(request, ct);
        return Ok(result);
    }

    private List<string> NormalizeAssets(string assets)
    {
        var requested = assets
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selected = _marketData.SupportedAssets
            .Where(requested.Contains)
            .ToList();
        if (selected.Count == 0)
            selected = _marketData.SupportedAssets.ToList();
        return selected;
    }

    private static bool TryParseExpiry(string? expiry, out DateTimeOffset? parsedExpiry)
    {
        parsedExpiry = null;
        if (string.IsNullOrWhiteSpace(expiry)) return true;
        if (!DateOnly.TryParseExact(expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return false;

        parsedExpiry = new DateTimeOffset(dateOnly.Year, dateOnly.Month, dateOnly.Day, 8, 0, 0, TimeSpan.Zero);
        return true;
    }
}
