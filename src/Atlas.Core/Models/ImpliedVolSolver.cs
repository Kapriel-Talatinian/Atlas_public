using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// Implied volatility extraction from market prices.
/// Primary: Newton-Raphson with Brenner-Subrahmanyam initial guess σ₀ ≈ √(2π/T) × C/S
/// Fallback: Brent's method for guaranteed convergence
/// Bracket: [0.01, 5.0] — wider than trad-fi for crypto's extreme vol regime
/// </summary>
public static class ImpliedVolSolver
{
    private const int MaxNewtonIterations = 50;
    private const int MaxBrentIterations = 100;
    private const double Tolerance = 1e-10;
    private const double MinVol = 0.01;
    private const double MaxVol = 5.0;

    /// <summary>
    /// Extract implied vol using Newton-Raphson with automatic fallback to Brent.
    /// </summary>
    public static double Solve(double marketPrice, double S, double K, double r, double T, OptionType type)
    {
        if (!double.IsFinite(marketPrice) || !double.IsFinite(S) || !double.IsFinite(K) || !double.IsFinite(r) || !double.IsFinite(T))
            return 0;
        if (S <= 0 || K <= 0 || T <= 1e-10 || marketPrice <= 0) return 0;

        double discountedStrike = K * Math.Exp(-r * T);
        double lowerBound = type == OptionType.Call
            ? Math.Max(S - discountedStrike, 0)
            : Math.Max(discountedStrike - S, 0);
        double upperBound = type == OptionType.Call ? S : discountedStrike;

        if (marketPrice <= lowerBound + Tolerance) return MinVol;
        if (marketPrice >= upperBound - Tolerance) return MaxVol;

        // Brenner-Subrahmanyam initial guess
        double sigma = Math.Sqrt(2 * Math.PI / T) * marketPrice / S;
        sigma = MathUtils.Clamp(sigma, MinVol, MaxVol);

        // Newton-Raphson
        for (int i = 0; i < MaxNewtonIterations; i++)
        {
            double price = BlackScholes.Price(S, K, sigma, T, r, type);
            double vega = BlackScholes.Vega(S, K, r, sigma, T) * 100; // un-scale

            if (Math.Abs(vega) < 1e-14) break;

            double diff = price - marketPrice;
            if (Math.Abs(diff) < Tolerance) return sigma;

            sigma -= diff / vega;
            sigma = MathUtils.Clamp(sigma, MinVol, MaxVol);
        }

        // Fallback to Brent's method if Newton didn't converge
        return SolveBrent(marketPrice, S, K, r, T, type);
    }

    /// <summary>
    /// Brent's method — guaranteed convergence via bracketed root-finding.
    /// </summary>
    public static double SolveBrent(double marketPrice, double S, double K, double r, double T, OptionType type)
    {
        double a = MinVol, b = MaxVol;
        double fa = BlackScholes.Price(S, K, a, T, r, type) - marketPrice;
        double fb = BlackScholes.Price(S, K, b, T, r, type) - marketPrice;

        if (Math.Abs(fa) < Tolerance) return a;
        if (Math.Abs(fb) < Tolerance) return b;
        if (fa * fb > 0) return Math.Abs(fa) < Math.Abs(fb) ? a : b;

        double c = a, fc = fa;
        bool mflag = true;
        double s = 0, d = 0;

        for (int i = 0; i < MaxBrentIterations; i++)
        {
            if (Math.Abs(b - a) < Tolerance) return (a + b) / 2;

            if (Math.Abs(fa - fc) > Tolerance && Math.Abs(fb - fc) > Tolerance)
            {
                // Inverse quadratic interpolation
                s = a * fb * fc / ((fa - fb) * (fa - fc))
                  + b * fa * fc / ((fb - fa) * (fb - fc))
                  + c * fa * fb / ((fc - fa) * (fc - fb));
            }
            else
            {
                // Secant method
                double denom = fb - fa;
                s = Math.Abs(denom) < Tolerance ? (a + b) / 2 : b - fb * (b - a) / denom;
            }

            // Conditions for bisection
            double lower = Math.Min(a, b);
            double upper = Math.Max(a, b);
            bool cond1 = s < (3 * lower + upper) / 4 || s > upper;
            bool cond2 = mflag && Math.Abs(s - b) >= Math.Abs(b - c) / 2;
            bool cond3 = !mflag && Math.Abs(s - b) >= Math.Abs(c - d) / 2;

            if (cond1 || cond2 || cond3)
            {
                s = (a + b) / 2;
                mflag = true;
            }
            else
            {
                mflag = false;
            }

            double fs = BlackScholes.Price(S, K, s, T, r, type) - marketPrice;
            d = c; c = b; fc = fb;

            if (fa * fs < 0) { b = s; fb = fs; }
            else { a = s; fa = fs; }

            if (Math.Abs(fa) < Math.Abs(fb))
            {
                (a, b) = (b, a);
                (fa, fb) = (fb, fa);
            }
        }

        return (a + b) / 2;
    }
}
