using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// SABR stochastic volatility model.
/// σ_B(K,F) parameterized by α (vol level), β (CEV exponent), ρ (correlation), ν (vol-of-vol).
///
/// Uses Hagan et al. (2002) asymptotic expansion for implied vol.
/// β typically fixed at 0.5 for crypto; calibrate α, ρ, ν to market quotes.
/// SABR is an interpolation tool, NOT an extrapolation tool — use within observed strike range.
/// </summary>
public sealed record SabrParams(
    double Alpha = 0.40,   // Vol level
    double Beta = 0.50,    // CEV exponent (0.5 typical for crypto)
    double Rho = -0.30,    // Correlation (negative = put skew)
    double Nu = 0.60)      // Vol of vol
{
    public static readonly SabrParams CryptoBtc = new(0.40, 0.5, -0.35, 0.55);
    public static readonly SabrParams CryptoEth = new(0.50, 0.5, -0.30, 0.65);
}

public static class SabrModel
{
    /// <summary>
    /// Compute SABR implied vol using Hagan's formula.
    /// </summary>
    /// <param name="F">Forward price.</param>
    /// <param name="K">Strike.</param>
    /// <param name="T">Time to expiry.</param>
    /// <param name="p">SABR parameters.</param>
    public static double ImpliedVol(double F, double K, double T, SabrParams? p = null)
    {
        p ??= SabrParams.CryptoBtc;
        double alpha = p.Alpha, beta = p.Beta, rho = p.Rho, nu = p.Nu;

        if (T <= 1e-10) return alpha;

        // ATM case (F ≈ K)
        if (Math.Abs(F - K) < F * 1e-6)
        {
            double Fbeta = Math.Pow(F, 1 - beta);
            double t1 = alpha / Fbeta;
            double t2 = (1 - beta) * (1 - beta) * alpha * alpha / (24 * Fbeta * Fbeta);
            double t3 = rho * beta * nu * alpha / (4 * Fbeta);
            double t4 = (2 - 3 * rho * rho) * nu * nu / 24;
            return t1 * (1 + (t2 + t3 + t4) * T);
        }

        // General case
        double FK = F * K;
        double FKbeta = Math.Pow(FK, (1 - beta) / 2);
        double logFK = Math.Log(F / K);

        double z = (nu * FKbeta / alpha) * logFK;
        double x = Math.Log((Math.Sqrt(1 - 2 * rho * z + z * z) + z - rho) / (1 - rho));

        if (Math.Abs(x) < 1e-10)
            return alpha / FKbeta;

        double b2 = (1 - beta) * (1 - beta);
        double prefix = alpha / (FKbeta * (1 + b2 * logFK * logFK / 24 + b2 * b2 * Math.Pow(logFK, 4) / 1920));
        double zOverX = z / x;
        double correction = 1 + (b2 * alpha * alpha / (24 * FKbeta * FKbeta)
                              + rho * beta * nu * alpha / (4 * FKbeta)
                              + (2 - 3 * rho * rho) * nu * nu / 24) * T;

        return MathUtils.Clamp(prefix * zOverX * correction, 0.01, 5.0);
    }

    /// <summary>Price option using SABR implied vol fed into BS.</summary>
    public static double Price(double S, double K, double r, double sigma, double T,
        OptionType type, SabrParams? p = null)
    {
        p ??= SabrParams.CryptoBtc;
        double F = S * Math.Exp(r * T);
        // Use sigma as initial alpha estimate
        var sabrP = p with { Alpha = sigma * 0.8 };
        double iv = ImpliedVol(F, K, T, sabrP);
        return BlackScholes.Price(S, K, iv, T, r, type);
    }
}
