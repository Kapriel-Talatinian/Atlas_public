using System.Diagnostics;
using Atlas.Core.Common;
using Atlas.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace Atlas.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PricingController : ControllerBase
{
    private const double DaysPerYear = 365.25;
    private const int MinMcPaths = 500;
    private const int MaxMcPaths = 500_000;
    private const int MinBinomialSteps = 10;
    private const int MaxBinomialSteps = 5_000;

    /// <summary>Price an option using all 5 models simultaneously.</summary>
    [HttpGet("compare")]
    public ActionResult<List<PricingResult>> CompareModels(
        double spot = 87250, double strike = 87000, double vol = 0.58,
        double tte = 30, double rate = 0.048, string type = "call",
        int mcPaths = 5000, int binSteps = 80)
    {
        if (!TryNormalizePricingInputs(spot, strike, tte, rate, type, out var optType, out var T, out var error, vol))
            return error;
        if (mcPaths is < MinMcPaths or > MaxMcPaths)
            return BadRequest($"mcPaths must be in [{MinMcPaths}, {MaxMcPaths}].");
        if (binSteps is < MinBinomialSteps or > MaxBinomialSteps)
            return BadRequest($"binSteps must be in [{MinBinomialSteps}, {MaxBinomialSteps}].");

        var results = new List<PricingResult>();
        var sw = Stopwatch.StartNew();

        void AddResult(string model, double price, double impliedVol, GreeksResult greeks, Dictionary<string, object> modelParams) =>
            results.Add(new PricingResult(model, price, impliedVol, greeks, DateTimeOffset.UtcNow, sw.Elapsed, modelParams));

        // 1. Black-Scholes
        sw.Restart();
        double bsPrice = BlackScholes.Price(spot, strike, vol, T, rate, optType);
        var bsGreeks = BlackScholes.AllGreeks(spot, strike, rate, vol, T, optType);
        AddResult("Black-Scholes", bsPrice, vol, bsGreeks,
            new() { ["method"] = "Closed-form N(d1), N(d2)" });

        // 2. Heston
        sw.Restart();
        double hPrice = HestonModel.Price(spot, strike, rate, Math.Sqrt(vol), T, optType);
        double hIv = ImpliedVolSolver.Solve(hPrice, spot, strike, rate, T, optType);
        AddResult("Heston SV", hPrice, hIv,
            HestonModel.Greeks(spot, strike, rate, Math.Sqrt(vol), T, optType),
            new() { ["kappa"] = 3.0, ["theta"] = 0.40, ["xi"] = 0.80, ["rho"] = -0.65 });

        // 3. Monte Carlo
        sw.Restart();
        double mcPrice = MonteCarlo.Price(spot, strike, rate, vol, T, optType, mcPaths);
        double mcIv = ImpliedVolSolver.Solve(mcPrice, spot, strike, rate, T, optType);
        AddResult("Monte Carlo", mcPrice, mcIv, bsGreeks,
            new() { ["paths"] = mcPaths, ["method"] = "GBM + antithetic" });

        // 4. Binomial
        sw.Restart();
        double binPrice = BinomialTree.PriceRichardson(spot, strike, rate, vol, T, optType, binSteps);
        double binIv = ImpliedVolSolver.Solve(binPrice, spot, strike, rate, T, optType);
        AddResult("Binomial CRR", binPrice, binIv, bsGreeks,
            new() { ["steps"] = binSteps, ["method"] = "CRR + Richardson" });

        // 5. SABR
        sw.Restart();
        double sabrPrice = SabrModel.Price(spot, strike, rate, vol, T, optType);
        double sabrIv = ImpliedVolSolver.Solve(sabrPrice, spot, strike, rate, T, optType);
        AddResult("SABR", sabrPrice, sabrIv, bsGreeks,
            new() { ["alpha"] = vol * 0.8, ["beta"] = 0.5, ["rho"] = -0.35, ["nu"] = 0.55 });

        return Ok(results);
    }

    /// <summary>Compute Greeks for a single option.</summary>
    [HttpGet("greeks")]
    public ActionResult<GreeksResult> GetGreeks(
        double spot = 87250, double strike = 87000, double vol = 0.58,
        double tte = 30, double rate = 0.048, string type = "call")
    {
        if (!TryNormalizePricingInputs(spot, strike, tte, rate, type, out var optType, out var T, out var error, vol))
            return error;
        return Ok(BlackScholes.AllGreeks(spot, strike, rate, vol, T, optType));
    }

    /// <summary>Extract implied vol from a market price.</summary>
    [HttpGet("implied-vol")]
    public ActionResult<double> GetImpliedVol(
        double price, double spot = 87250, double strike = 87000,
        double tte = 30, double rate = 0.048, string type = "call")
    {
        if (!double.IsFinite(price) || price <= 0)
            return BadRequest("price must be > 0.");
        if (!TryNormalizePricingInputs(spot, strike, tte, rate, type, out var optType, out var T, out var error))
            return error;

        return Ok(ImpliedVolSolver.Solve(price, spot, strike, rate, T, optType));
    }

    private bool TryNormalizePricingInputs(
        double spot,
        double strike,
        double tte,
        double rate,
        string type,
        out OptionType optType,
        out double maturity,
        out ActionResult error,
        double? vol = null)
    {
        optType = OptionType.Call;
        maturity = 0;
        error = Ok();

        if (!double.IsFinite(spot) || spot <= 0)
        {
            error = BadRequest("spot must be > 0.");
            return false;
        }

        if (!double.IsFinite(strike) || strike <= 0)
        {
            error = BadRequest("strike must be > 0.");
            return false;
        }

        if (!double.IsFinite(tte) || tte <= 0)
        {
            error = BadRequest("tte (days to expiry) must be > 0.");
            return false;
        }

        if (vol.HasValue && (!double.IsFinite(vol.Value) || vol.Value <= 0 || vol.Value > 5))
        {
            error = BadRequest("vol must be in (0, 5].");
            return false;
        }

        if (!double.IsFinite(rate) || rate < -1 || rate > 1)
        {
            error = BadRequest("rate must be in [-1, 1].");
            return false;
        }

        if (type.Equals("call", StringComparison.OrdinalIgnoreCase))
        {
            optType = OptionType.Call;
        }
        else if (type.Equals("put", StringComparison.OrdinalIgnoreCase))
        {
            optType = OptionType.Put;
        }
        else
        {
            error = BadRequest("type must be 'call' or 'put'.");
            return false;
        }

        maturity = tte / DaysPerYear;
        return true;
    }
}
