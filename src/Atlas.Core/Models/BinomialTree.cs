using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// Cox-Ross-Rubinstein (CRR) binomial tree pricer.
/// u = e^(σ√Δt), d = 1/u, p = (e^(rΔt) - d) / (u - d)
///
/// Uses Richardson extrapolation: 2·P(2N) - P(N) for accelerated convergence.
/// Supports American exercise detection (early exercise boundary).
/// </summary>
public static class BinomialTree
{
    /// <summary>
    /// Price European option using CRR binomial tree.
    /// </summary>
    /// <param name="N">Number of time steps. 80-100 gives good accuracy for European.</param>
    /// <param name="american">If true, checks early exercise at each node.</param>
    public static double Price(double S, double K, double r, double sigma, double T,
        OptionType type, int N = 80, bool american = false)
    {
        if (T <= 1e-10)
            return type == OptionType.Call ? Math.Max(S - K, 0) : Math.Max(K - S, 0);

        N = Math.Max(2, N);
        double price = CrrPrice(S, K, r, sigma, T, type, N, american);
        return price;
    }

    /// <summary>
    /// Price with Richardson extrapolation: 2·P(2N) - P(N).
    /// Accelerates convergence from O(1/N) to O(1/N²).
    /// </summary>
    public static double PriceRichardson(double S, double K, double r, double sigma, double T,
        OptionType type, int N = 80, bool american = false)
    {
        if (T <= 1e-10)
            return type == OptionType.Call ? Math.Max(S - K, 0) : Math.Max(K - S, 0);

        N = Math.Max(2, N);
        int coarseSteps = Math.Max(2, N / 2);
        double p1 = CrrPrice(S, K, r, sigma, T, type, N, american);
        double p2 = CrrPrice(S, K, r, sigma, T, type, coarseSteps, american);
        return 2 * p1 - p2;
    }

    private static double CrrPrice(double S, double K, double r, double sigma, double T,
        OptionType type, int N, bool american)
    {
        N = Math.Max(2, N);
        double dt = T / N;
        double u = Math.Exp(sigma * Math.Sqrt(dt));
        double d = 1.0 / u;
        double p = MathUtils.Clamp((Math.Exp(r * dt) - d) / (u - d), 0, 1);
        double disc = Math.Exp(-r * dt);

        // Terminal payoffs
        var prices = new double[N + 1];
        for (int i = 0; i <= N; i++)
        {
            double sT = S * Math.Pow(u, N - i) * Math.Pow(d, i);
            prices[i] = type == OptionType.Call
                ? Math.Max(sT - K, 0)
                : Math.Max(K - sT, 0);
        }

        // Backward induction
        for (int j = N - 1; j >= 0; j--)
        {
            for (int i = 0; i <= j; i++)
            {
                double holdValue = disc * (p * prices[i] + (1 - p) * prices[i + 1]);

                if (american)
                {
                    double sNode = S * Math.Pow(u, j - i) * Math.Pow(d, i);
                    double exerciseValue = type == OptionType.Call
                        ? Math.Max(sNode - K, 0)
                        : Math.Max(K - sNode, 0);
                    prices[i] = Math.Max(holdValue, exerciseValue);
                }
                else
                {
                    prices[i] = holdValue;
                }
            }
        }

        return prices[0];
    }

    /// <summary>
    /// Detect early exercise boundary for American options.
    /// Returns the critical spot price at each time step where exercise is optimal.
    /// </summary>
    public static double[] EarlyExerciseBoundary(double S, double K, double r, double sigma, double T,
        OptionType type, int N = 100)
    {
        N = Math.Max(2, N);
        double dt = T / N;
        double u = Math.Exp(sigma * Math.Sqrt(dt));
        double d = 1.0 / u;
        double p = MathUtils.Clamp((Math.Exp(r * dt) - d) / (u - d), 0, 1);
        double disc = Math.Exp(-r * dt);

        var boundary = new double[N];
        var prices = new double[N + 1];

        for (int i = 0; i <= N; i++)
        {
            double sT = S * Math.Pow(u, N - i) * Math.Pow(d, i);
            prices[i] = type == OptionType.Call ? Math.Max(sT - K, 0) : Math.Max(K - sT, 0);
        }

        for (int j = N - 1; j >= 0; j--)
        {
            double criticalSpot = 0;
            for (int i = 0; i <= j; i++)
            {
                double holdValue = disc * (p * prices[i] + (1 - p) * prices[i + 1]);
                double sNode = S * Math.Pow(u, j - i) * Math.Pow(d, i);
                double exerciseValue = type == OptionType.Call
                    ? Math.Max(sNode - K, 0) : Math.Max(K - sNode, 0);

                if (exerciseValue > holdValue && sNode > criticalSpot)
                    criticalSpot = sNode;

                prices[i] = Math.Max(holdValue, exerciseValue);
            }
            boundary[j] = criticalSpot;
        }

        return boundary;
    }
}
