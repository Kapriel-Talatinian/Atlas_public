namespace Atlas.Core.Common;

/// <summary>
/// High-performance math utilities for options pricing.
/// Normal CDF uses the Abramowitz & Stegun rational approximation (|error| &lt; 7.5e-8).
/// </summary>
public static class MathUtils
{
    private const double Sqrt2 = 1.4142135623730951;
    private const double SqrtTwoPi = 2.5066282746310002;
    private const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>Standard normal CDF Φ(x).</summary>
    public static double NormalCdf(double x)
    {
        const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741;
        const double a4 = -1.453152027, a5 = 1.061405429, p = 0.3275911;

        double sign = x < 0 ? -1.0 : 1.0;
        double ax = Math.Abs(x) / Sqrt2;
        double t = 1.0 / (1.0 + p * ax);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-ax * ax);
        return 0.5 * (1.0 + sign * y);
    }

    /// <summary>Standard normal PDF φ(x).</summary>
    public static double NormalPdf(double x) =>
        Math.Exp(-0.5 * x * x) / SqrtTwoPi;

    /// <summary>
    /// Optimal bump size for numerical differentiation.
    /// h = max(|x| * sqrt(ε), sqrt(ε))
    /// </summary>
    public static double OptimalBump(double x) =>
        Math.Max(Math.Abs(x) * Math.Sqrt(MachineEpsilon), Math.Sqrt(MachineEpsilon));

    /// <summary>Box-Muller transform for normal random variates.</summary>
    public static (double z1, double z2) BoxMuller(Random rng)
    {
        double u1, u2;
        do { u1 = rng.NextDouble(); } while (u1 == 0);
        do { u2 = rng.NextDouble(); } while (u2 == 0);
        double r = Math.Sqrt(-2.0 * Math.Log(u1));
        double theta = 2.0 * Math.PI * u2;
        return (r * Math.Cos(theta), r * Math.Sin(theta));
    }

    /// <summary>Clamp value between min and max.</summary>
    public static double Clamp(double val, double min, double max) =>
        val < min ? min : val > max ? max : val;
}
