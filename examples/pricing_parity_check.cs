using Atlas.Core.Common;
using Atlas.Core.Models;

const double tolerance = 0.01;
const int pathCount = 200_000;

var scenarios = BuildScenarios();
var failures = new List<string>();

Console.WriteLine("Atlas pricing parity check");
Console.WriteLine($"Scenarios: {scenarios.Count}, tolerance: {tolerance:F4}, MC paths: {pathCount}");
Console.WriteLine();
Console.WriteLine($"{"#",2} {"Type",4} {"S",10} {"K",10} {"Vol",8} {"T",8} {"BS",12} {"Bin",12} {"MC+CV",12} {"|Bin-BS|",12} {"|MC-BS|",12} {"OK",4}");

for (int i = 0; i < scenarios.Count; i++)
{
    var s = scenarios[i];
    double bs = BlackScholes.Price(s.Spot, s.Strike, s.Vol, s.TteYears, s.Rate, s.Type);
    double bin = BinomialTree.PriceRichardson(s.Spot, s.Strike, s.Rate, s.Vol, s.TteYears, s.Type, 500);
    double mc = MonteCarloPriceWithControlVariate(s.Spot, s.Strike, s.Rate, s.Vol, s.TteYears, s.Type, pathCount, seed: 42 + i);

    double binError = Math.Abs(bin - bs);
    double mcError = Math.Abs(mc - bs);
    bool ok = binError <= tolerance && mcError <= tolerance;

    Console.WriteLine(
        $"{i + 1,2} {ShortType(s.Type),4} {s.Spot,10:F2} {s.Strike,10:F2} {s.Vol,8:P2} {s.TteYears,8:F4} {bs,12:F4} {bin,12:F4} {mc,12:F4} {binError,12:F4} {mcError,12:F4} {(ok ? "yes" : "no"),4}");

    if (!ok)
        failures.Add($"Scenario {i + 1}: type={s.Type}, spot={s.Spot}, strike={s.Strike}, vol={s.Vol}, T={s.TteYears}, binErr={binError:F6}, mcErr={mcError:F6}");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("All pricing checks passed.");
    return 0;
}

Console.WriteLine("Pricing check failures:");
foreach (string failure in failures)
    Console.WriteLine($"- {failure}");

return 1;

static string ShortType(OptionType type) => type == OptionType.Call ? "CALL" : "PUT";

static List<Scenario> BuildScenarios()
{
    var scenarios = new List<Scenario>(20);
    double[] spots = [100, 100, 100, 100, 100];
    double[] strikes = [90, 95, 100, 105, 110];
    double[] vols = [0.18, 0.24];
    double[] maturities = [30.0 / 365.25, 90.0 / 365.25];
    double rate = 0.03;

    foreach (OptionType type in new[] { OptionType.Call, OptionType.Put })
    {
        foreach (double vol in vols)
        {
            foreach (double maturity in maturities)
            {
                foreach (int idx in Enumerable.Range(0, strikes.Length))
                {
                    scenarios.Add(new Scenario(spots[idx], strikes[idx], vol, maturity, rate, type));
                    if (scenarios.Count == 20)
                        return scenarios;
                }
            }
        }
    }

    return scenarios;
}

static double MonteCarloPriceWithControlVariate(
    double spot,
    double strike,
    double rate,
    double sigma,
    double t,
    OptionType type,
    int paths,
    int seed)
{
    if (t <= 1e-10)
        return type == OptionType.Call ? Math.Max(spot - strike, 0) : Math.Max(strike - spot, 0);

    var rng = new Random(seed);
    double drift = (rate - 0.5 * sigma * sigma) * t;
    double diffusion = sigma * Math.Sqrt(t);
    double discount = Math.Exp(-rate * t);

    double payoffSum = 0;
    double controlSum = 0;
    double payoffControlSum = 0;
    double controlSquaredSum = 0;

    for (int i = 0; i < paths; i++)
    {
        var (z, _) = MathUtils.BoxMuller(rng);
        double st1 = spot * Math.Exp(drift + diffusion * z);
        double st2 = spot * Math.Exp(drift - diffusion * z);

        double payoff1 = type == OptionType.Call ? Math.Max(st1 - strike, 0) : Math.Max(strike - st1, 0);
        double payoff2 = type == OptionType.Call ? Math.Max(st2 - strike, 0) : Math.Max(strike - st2, 0);
        double discountedPayoff = discount * (payoff1 + payoff2) / 2.0;

        // Control variate: discounted terminal spot has known expectation S0.
        double control = discount * (st1 + st2) / 2.0 - spot;

        payoffSum += discountedPayoff;
        controlSum += control;
        payoffControlSum += discountedPayoff * control;
        controlSquaredSum += control * control;
    }

    double meanPayoff = payoffSum / paths;
    double meanControl = controlSum / paths;
    double covariance = payoffControlSum / paths - meanPayoff * meanControl;
    double controlVariance = controlSquaredSum / paths - meanControl * meanControl;
    double beta = controlVariance > 1e-12 ? covariance / controlVariance : 0;

    return meanPayoff - beta * meanControl;
}

internal sealed record Scenario(
    double Spot,
    double Strike,
    double Vol,
    double TteYears,
    double Rate,
    OptionType Type);
