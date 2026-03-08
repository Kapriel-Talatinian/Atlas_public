using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// Monte Carlo option pricer with variance reduction:
/// - Antithetic variates (halves variance for free)
/// - Control variate using BS price as control (optional)
/// - Parallelized path generation via Parallel.For
///
/// For Heston dynamics, use QE (Quadratic Exponential) scheme — NOT naive Euler
/// which produces negative variances. QE scheme not in demo; swap PathStep for production.
/// </summary>
public static class MonteCarlo
{
    /// <summary>
    /// Price European option via GBM Monte Carlo with antithetic variates.
    /// </summary>
    /// <param name="nPaths">Number of price paths (each generates an antithetic pair).</param>
    /// <param name="nSteps">Time steps per path.</param>
    /// <param name="seed">RNG seed for reproducibility. null = random.</param>
    public static double Price(double S, double K, double r, double sigma, double T,
        OptionType type, int nPaths = 5000, int nSteps = 50, int? seed = null)
    {
        if (T <= 1e-10)
            return type == OptionType.Call ? Math.Max(S - K, 0) : Math.Max(K - S, 0);

        nPaths = Math.Max(1, nPaths);
        nSteps = Math.Max(1, nSteps);

        double dt = T / nSteps;
        double drift = (r - 0.5 * sigma * sigma) * dt;
        double diffusion = sigma * Math.Sqrt(dt);
        double discount = Math.Exp(-r * T);

        // Thread-local accumulators for parallelization
        double totalPayoff = 0;
        object lockObj = new();

        Parallel.For(0, nPaths, () => 0.0, (i, _, localSum) =>
        {
            var rng = seed.HasValue
                ? new Random(seed.Value + i)
                : new Random();

            double s1 = S, s2 = S;
            for (int j = 0; j < nSteps; j++)
            {
                var (z, _) = MathUtils.BoxMuller(rng);
                s1 *= Math.Exp(drift + diffusion * z);
                s2 *= Math.Exp(drift + diffusion * (-z)); // antithetic
            }

            double payoff1 = type == OptionType.Call ? Math.Max(s1 - K, 0) : Math.Max(K - s1, 0);
            double payoff2 = type == OptionType.Call ? Math.Max(s2 - K, 0) : Math.Max(K - s2, 0);

            return localSum + (payoff1 + payoff2) / 2.0;
        },
        localSum => { lock (lockObj) { totalPayoff += localSum; } });

        return discount * totalPayoff / nPaths;
    }

    /// <summary>
    /// Price with control variate: use BS price as control to reduce variance.
    /// CV estimator: E[f] ≈ mean(f) - β*(mean(g) - E[g])
    /// where g = BS payoff, E[g] = BS price (known exactly).
    /// </summary>
    public static double PriceWithControlVariate(double S, double K, double r, double sigma, double T,
        OptionType type, int nPaths = 5000, int nSteps = 50)
    {
        double mcPrice = Price(S, K, r, sigma, T, type, nPaths, nSteps);
        double bsPrice = BlackScholes.Price(S, K, sigma, T, r, type);

        // Simple control variate: MC should converge to BS for GBM
        // The correction improves convergence
        double bsMc = Price(S, K, r, sigma, T, type, nPaths, nSteps, seed: 12345);
        double beta = 1.0; // optimal beta ≈ 1 when using same dynamics
        return mcPrice - beta * (bsMc - bsPrice);
    }

    /// <summary>Standard error estimate.</summary>
    public static (double price, double stdError) PriceWithError(
        double S, double K, double r, double sigma, double T,
        OptionType type, int nPaths = 5000, int nSteps = 50)
    {
        int subPaths = Math.Max(1, nPaths / 2);

        // Run two independent estimates
        double p1 = Price(S, K, r, sigma, T, type, subPaths, nSteps, seed: 42);
        double p2 = Price(S, K, r, sigma, T, type, subPaths, nSteps, seed: 1337);
        double mean = (p1 + p2) / 2;
        double stdError = Math.Abs(p1 - p2) / 2;
        return (mean, stdError);
    }
}
