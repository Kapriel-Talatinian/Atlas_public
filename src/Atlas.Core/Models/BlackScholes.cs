using Atlas.Core.Common;

namespace Atlas.Core.Models;

/// <summary>
/// Black-Scholes pricer adapted for crypto markets.
/// - 365.25 continuous days (24/7 markets, no business day conventions)
/// - No dividend yield (funding rate can be added via r adjustment)
/// - Full analytical Greeks including higher-order: Vanna, Volga, Charm
/// </summary>
public static class BlackScholes
{
    private const double MinTime = 1e-10;
    private const double MinVol = 1e-10;

    private static double DiscountedStrike(double K, double r, double T) => K * Math.Exp(-r * T);

    /// <summary>d₁ = [ln(S/K) + (r + σ²/2)T] / (σ√T)</summary>
    public static double D1(double S, double K, double r, double sigma, double T) =>
        (Math.Log(S / K) + (r + 0.5 * sigma * sigma) * T)
        / (Math.Max(sigma, MinVol) * Math.Sqrt(Math.Max(T, MinTime)));

    /// <summary>d₂ = d₁ - σ√T</summary>
    public static double D2(double S, double K, double r, double sigma, double T) =>
        D1(S, K, r, sigma, T) - Math.Max(sigma, MinVol) * Math.Sqrt(Math.Max(T, MinTime));

    /// <summary>European call price: C = S·N(d₁) - K·e⁻ʳᵀ·N(d₂)</summary>
    public static double CallPrice(double S, double K, double sigma, double T, double r)
    {
        if (T <= MinTime) return Math.Max(S - K, 0);
        if (sigma <= MinVol) return Math.Max(S - DiscountedStrike(K, r, T), 0);
        double d1 = D1(S, K, r, sigma, T);
        double d2 = D2(S, K, r, sigma, T);
        return S * MathUtils.NormalCdf(d1) - K * Math.Exp(-r * T) * MathUtils.NormalCdf(d2);
    }

    /// <summary>European put price via put-call parity: P = K·e⁻ʳᵀ·N(-d₂) - S·N(-d₁)</summary>
    public static double PutPrice(double S, double K, double sigma, double T, double r)
    {
        if (T <= MinTime) return Math.Max(K - S, 0);
        if (sigma <= MinVol) return Math.Max(DiscountedStrike(K, r, T) - S, 0);
        double d1 = D1(S, K, r, sigma, T);
        double d2 = D2(S, K, r, sigma, T);
        return K * Math.Exp(-r * T) * MathUtils.NormalCdf(-d2) - S * MathUtils.NormalCdf(-d1);
    }

    public static double Price(double S, double K, double sigma, double T, double r, OptionType type) =>
        type == OptionType.Call ? CallPrice(S, K, sigma, T, r) : PutPrice(S, K, sigma, T, r);

    // ─── Analytical Greeks ────────────────────────────────────

    /// <summary>Δ = ∂C/∂S. Call: N(d₁), Put: N(d₁)-1</summary>
    public static double Delta(double S, double K, double r, double sigma, double T, OptionType type)
    {
        if (T <= MinTime) return type == OptionType.Call ? (S > K ? 1 : 0) : (S < K ? -1 : 0);
        if (sigma <= MinVol)
        {
            double boundary = DiscountedStrike(K, r, T);
            return type == OptionType.Call ? (S > boundary ? 1 : 0) : (S < boundary ? -1 : 0);
        }
        double d1 = D1(S, K, r, sigma, T);
        return type == OptionType.Call ? MathUtils.NormalCdf(d1) : MathUtils.NormalCdf(d1) - 1.0;
    }

    /// <summary>Γ = ∂²C/∂S² = φ(d₁) / (S·σ·√T)</summary>
    public static double Gamma(double S, double K, double r, double sigma, double T)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        return MathUtils.NormalPdf(D1(S, K, r, sigma, T)) / (S * sigma * Math.Sqrt(T));
    }

    /// <summary>ν = ∂C/∂σ = S·φ(d₁)·√T. Returned per 1% vol move (÷100).</summary>
    public static double Vega(double S, double K, double r, double sigma, double T)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        return S * MathUtils.NormalPdf(D1(S, K, r, sigma, T)) * Math.Sqrt(T) / 100.0;
    }

    /// <summary>Θ = time decay per calendar day (÷365.25 for crypto 24/7).</summary>
    public static double Theta(double S, double K, double r, double sigma, double T, OptionType type)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        double d1 = D1(S, K, r, sigma, T), d2 = D2(S, K, r, sigma, T);
        double term1 = -(S * MathUtils.NormalPdf(d1) * sigma) / (2 * Math.Sqrt(T));
        return type == OptionType.Call
            ? (term1 - r * K * Math.Exp(-r * T) * MathUtils.NormalCdf(d2)) / 365.25
            : (term1 + r * K * Math.Exp(-r * T) * MathUtils.NormalCdf(-d2)) / 365.25;
    }

    /// <summary>Vanna = ∂²C/∂S∂σ = -φ(d₁)·d₂/σ. Critical for delta-vol correlation.</summary>
    public static double Vanna(double S, double K, double r, double sigma, double T)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        double d1 = D1(S, K, r, sigma, T), d2 = D2(S, K, r, sigma, T);
        return -MathUtils.NormalPdf(d1) * d2 / sigma;
    }

    /// <summary>Volga (Vomma) = ∂²C/∂σ² = S·φ(d₁)·√T·d₁·d₂/σ. Vol-of-vol exposure.</summary>
    public static double Volga(double S, double K, double r, double sigma, double T)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        double d1 = D1(S, K, r, sigma, T), d2 = D2(S, K, r, sigma, T);
        return S * MathUtils.NormalPdf(d1) * Math.Sqrt(T) * d1 * d2 / sigma;
    }

    /// <summary>Charm = ∂Δ/∂t. Measures delta bleed over time.</summary>
    public static double Charm(double S, double K, double r, double sigma, double T)
    {
        if (T <= MinTime || sigma <= MinVol) return 0;
        double d1 = D1(S, K, r, sigma, T), d2 = D2(S, K, r, sigma, T);
        return -MathUtils.NormalPdf(d1) * (2 * r * T - d2 * sigma * Math.Sqrt(T))
               / (2 * T * sigma * Math.Sqrt(T));
    }

    /// <summary>ρ = ∂C/∂r. Less relevant in crypto but still computed.</summary>
    public static double Rho(double S, double K, double r, double sigma, double T, OptionType type)
    {
        if (T <= MinTime) return 0;
        if (sigma <= MinVol) return 0;
        double d2 = D2(S, K, r, sigma, T);
        return type == OptionType.Call
            ? K * T * Math.Exp(-r * T) * MathUtils.NormalCdf(d2) / 100.0
            : -K * T * Math.Exp(-r * T) * MathUtils.NormalCdf(-d2) / 100.0;
    }

    /// <summary>Full Greeks bundle.</summary>
    public static GreeksResult AllGreeks(double S, double K, double r, double sigma, double T, OptionType type) =>
        new(
            Delta: Delta(S, K, r, sigma, T, type),
            Gamma: Gamma(S, K, r, sigma, T),
            Vega: Vega(S, K, r, sigma, T),
            Theta: Theta(S, K, r, sigma, T, type),
            Vanna: Vanna(S, K, r, sigma, T),
            Volga: Volga(S, K, r, sigma, T),
            Charm: Charm(S, K, r, sigma, T),
            Rho: Rho(S, K, r, sigma, T, type));
}
