using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// Heston stochastic volatility model for crypto options.
/// dS = r·S·dt + √v·S·dW₁
/// dv = κ(θ-v)dt + ξ√v·dW₂,  corr(dW₁,dW₂) = ρ
///
/// Uses moment-matching approximation for speed.
/// For production calibration, use Carr-Madan FFT or direct numerical integration.
/// </summary>
public sealed record HestonParams(
    double Kappa = 3.0,    // Mean reversion speed
    double Theta = 0.40,   // Long-run variance (vol² ~ 63% annualized)
    double Xi = 0.80,      // Vol of vol — high for crypto
    double Rho = -0.65)    // Spot-vol correlation — negative = crash fear
{
    /// <summary>Default crypto BTC parameters (calibrated to typical market).</summary>
    public static readonly HestonParams CryptoBtc = new(3.0, 0.40, 0.80, -0.65);
    public static readonly HestonParams CryptoEth = new(2.5, 0.55, 0.90, -0.60);
    public static readonly HestonParams CryptoSol = new(2.0, 0.80, 1.10, -0.50);
}

public static class HestonModel
{
    /// <summary>
    /// Price a European option under Heston dynamics using moment-matching.
    /// Computes effective vol from the Heston variance dynamics and applies
    /// skew correction via the ρ·ξ term.
    ///
    /// For full characteristic function pricing, see HestonFft (not implemented in demo).
    /// Swap <see cref="Price"/> with FFT-based pricer for production.
    /// </summary>
    public static double Price(double S, double K, double r, double v0, double T,
        OptionType type, HestonParams? p = null)
    {
        p ??= HestonParams.CryptoBtc;
        if (T <= 1e-10)
            return type == OptionType.Call ? Math.Max(S - K, 0) : Math.Max(K - S, 0);

        // Effective variance: E[v(T)] under mean-reversion
        double effVar = v0 * v0 * Math.Exp(-p.Kappa * T)
                      + p.Theta * p.Theta * (1 - Math.Exp(-p.Kappa * T));

        // Vol-of-vol correction (convexity adjustment)
        double volvolAdj = p.Xi * p.Xi * T / 12.0;

        // Correlation-induced skew
        double corrAdj = p.Rho * p.Xi * v0 * T / 4.0;

        double adjVol = Math.Sqrt(Math.Max(effVar * (1 + volvolAdj) + corrAdj, 0.01));

        // Log-moneyness skew adjustment
        double k = Math.Log(K / S);
        double skewAdj = p.Rho * p.Xi / (2.0 * adjVol) * k * 0.3;
        double finalVol = MathUtils.Clamp(adjVol + skewAdj, 0.05, 5.0);

        return BlackScholes.Price(S, K, finalVol, T, r, type);
    }

    /// <summary>
    /// Compute numerical Greeks via bump-and-revalue.
    /// Uses central differences with optimal bump sizes.
    /// </summary>
    public static GreeksResult Greeks(double S, double K, double r, double v0, double T,
        OptionType type, HestonParams? p = null)
    {
        double h_s = S * 0.001;
        double h_v = 0.001;
        double h_t = 1.0 / 365.25;

        double pBase = Price(S, K, r, v0, T, type, p);
        double pSup = Price(S + h_s, K, r, v0, T, type, p);
        double pSdn = Price(S - h_s, K, r, v0, T, type, p);
        double pVup = Price(S, K, r, v0 + h_v, T, type, p);
        double pVdn = Price(S, K, r, v0 - h_v, T, type, p);
        double pTdn = T > h_t ? Price(S, K, r, v0, T - h_t, type, p) : pBase;

        return new GreeksResult(
            Delta: (pSup - pSdn) / (2 * h_s),
            Gamma: (pSup - 2 * pBase + pSdn) / (h_s * h_s),
            Vega: (pVup - pVdn) / (2 * h_v) / 100.0,
            Theta: -(pTdn - pBase) / h_t / 365.25,
            Vanna: 0, Volga: 0, Charm: 0, Rho: 0);
    }
}
