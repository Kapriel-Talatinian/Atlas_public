# Quant Notes

## 1. Intent and scope

Atlas is intentionally positioned as an **institutional-grade options desk simulator and analytics platform**, not as a claim of production-alpha pricing superiority. The quantitative layer is designed to demonstrate that the platform makes coherent modelling choices, exposes the right diagnostics, and fails in understandable ways.

The current perimeter is therefore:

- consistent pricing and Greeks primitives,
- smile / skew / term-structure diagnostics for crypto options,
- cross-model comparison for trader intuition,
- resilient market-data ingestion and normalization,
- auditability of pricing and execution decisions.

The goal is not to beat a production sell-side pricer. The goal is to show a system that thinks like a desk: model hierarchy, calibration heuristics, market sanity checks, and explicit operational trade-offs.

## 2. Model stack and why it is structured this way

Atlas deliberately keeps **multiple models alive at the same time** instead of pretending one model solves the whole problem.

### 2.1 Black-Scholes

Black-Scholes is the baseline reference model in Atlas.

Why it stays in the stack:

- closed-form and stable,
- ideal anchor for sanity checks and implied-vol inversion,
- analytical Greeks are available and fast,
- useful control variate / benchmark for Monte Carlo and tree methods.

Implementation notes:

- calendar basis uses `365.25` days to match 24/7 crypto markets,
- no dividend yield is modelled explicitly,
- higher-order Greeks exposed: `vanna`, `volga`, `charm`, `rho`,
- deterministic limits are handled for near-zero maturity and near-zero volatility.

### 2.1.1 Why Brenner-Subrahmanyam for implied-vol initialization

Atlas uses the **Brenner-Subrahmanyam** style initialization for implied volatility extraction because it is fast, simple, and very effective in the regime that matters most operationally: near-ATM options where most quoting, surface anchoring, and sanity checks happen.

The practical reason for using it is not elegance but convergence behavior:

- for ATM and near-ATM options it places Newton-Raphson close to the root,
- the first few Newton steps are therefore informative rather than chaotic,
- in practice this tends to converge in roughly `3-5` iterations for liquid ATM-style inputs,
- the implementation cost is essentially zero compared with a more elaborate heuristic seed.

This is exactly the kind of heuristic that belongs in a desk platform: cheap, robust in the common case, and easy to explain.

### 2.2 Heston

Heston is included because crypto options require a structural view of **stochastic volatility and skew**, not just a flat-vol surface.

Why Heston is useful here:

- it introduces spot/vol correlation through `rho`,
- it produces a more realistic desk conversation around skew convexity than plain BS,
- it is a credible structural layer for comparing “surface-implied” vs “model-implied” value.

Important implementation detail:

- the repository currently uses a **moment-matching / effective-volatility approximation**, not a full characteristic-function integration or Carr-Madan FFT engine,
- Greeks are obtained by bump-and-revalue around the Heston approximation,
- this is intentional for speed, readability, and portfolio-demo robustness.

That means the Heston component should be read as a **research-grade structural approximation**, not as a production-calibrated library pricer.

### 2.2.1 What a "real" Heston path would require

If Atlas were pushed from portfolio-grade analytics to production-grade stochastic-volatility pricing, the Heston layer would need to move away from moment matching and toward a proper characteristic-function implementation.

The natural next step would be:

- **Carr-Madan FFT** pricing with a damping parameter such as `alpha = 1.5`,
- or direct characteristic-function integration using **Gauss-Laguerre quadrature**,
- plus a calibration routine with regularization and parameter guards.

Why that is not already in the repository:

- the current platform goal is to expose coherent cross-model analytics, not to win a calibration benchmark,
- FFT/integration code is materially more complex to audit and explain,
- the moment-matching approximation is materially more stable for an interactive demo platform with broad feature scope.

### 2.3 SABR

SABR is present because it remains extremely practical for **smile interpolation and desk intuition**, especially when discussing wing behaviour and local smile shape.

In Atlas:

- SABR uses the **Hagan et al. (2002)** asymptotic implied-vol formula,
- `beta` is kept as a desk-style structural choice rather than overfit at every refresh,
- the model is used as an interpolation layer, not as an extrapolation promise.

Why SABR sits next to Heston instead of replacing it:

- Heston gives a structural stochastic-volatility narrative,
- SABR gives a compact and trader-friendly smile parameterization,
- seeing both helps highlight model dispersion rather than hide it.

### 2.4 Monte Carlo

Monte Carlo is not used because it is fashionable; it is used as a **numerical cross-check**.

Current implementation:

- GBM dynamics,
- antithetic variates,
- optional control-variate style comparison against Black-Scholes,
- reproducible seeded runs in tests.

This is useful for:

- convergence sanity checks,
- validating that the pricing baseline is coherent,
- demonstrating numerical discipline.

It is not currently a stochastic-volatility Monte Carlo engine. In particular, a production Heston path engine would require a robust scheme such as QE rather than naive Euler.

### 2.5 Binomial tree

The CRR tree exists for two reasons:

- independent convergence validation against Black-Scholes,
- explicit exercise-boundary tooling for American-style intuition, even though the main platform focus remains European crypto options.

Atlas uses Richardson extrapolation to improve convergence and keep the implementation simple and explainable.

## 3. Calibration approach actually used in Atlas

The calibration layer in Atlas is intentionally **heuristic, transparent, and desk-readable**.

It is not a heavyweight nonlinear optimizer over the full chain on every tick. That is a deliberate trade-off: for this platform, explainability and stability matter more than squeezing every basis point of fit error.

### 3.1 Inputs extracted from the chain

The current calibration pipeline derives a compact set of market descriptors from the cleaned option chain:

- `ATM IV 30D`,
- `ATM IV 90D`,
- `term slope 30D -> 90D`,
- front-expiry `25D risk reversal` proxy / skew measure,
- turnover-weighted model fit sample.

These descriptors are computed after market-data normalization and filtering, not directly from raw venue payloads.

### 3.2 Heston parameter shaping

Atlas starts from asset-specific prior parameters, then shapes them using observable surface features:

- `kappa` increases with the absolute term slope,
- `theta` is linked to ATM volatility level and slope,
- `xi` reacts to skew magnitude and elevated ATM volatility,
- `rho` is adjusted from the observed skew sign and magnitude.

This means calibration is **surface-feature-driven** rather than a global optimizer over the characteristic function. That choice is honest, fast, and stable for a platform whose job is also to stay interactive.

### 3.3 SABR parameter shaping

SABR is calibrated with the same philosophy:

- `alpha` is anchored to ATM volatility,
- `beta` is treated as a structural choice,
- `rho` follows observed skew,
- `nu` increases with skew intensity and term-structure stress.

Again, the emphasis is on consistency and interpretability over aggressive curve fitting.

### 3.4 Fit metrics

Atlas then computes ex-post fit diagnostics on a turnover-prioritized quote sample:

- mean absolute error in percent,
- RMSE in percent,
- sample count.

These fit metrics are returned explicitly by the API so the user can see whether a model is being used in a regime where it is still believable.

### 3.5 Root-finding architecture for implied volatility

The implied-vol solver follows a deliberate two-stage design:

- **Newton-Raphson first** for speed,
- **Brent second** for guaranteed bracketed convergence.

This is the right split for a desk application because:

- Newton is very fast when vega is informative and the initial guess is decent,
- Newton becomes fragile in deep OTM / ITM regimes or near arbitrage bounds,
- Brent is slower but reliable once the root is bracketed.

The key design choice is therefore not “Newton or Brent”, but **Newton with Brent as the safety net**. That mirrors how production solvers are often structured: optimistic fast path, conservative guaranteed fallback.

## 4. Data hygiene and market realism

The quant layer is only as credible as the incoming data. For that reason, Atlas does not directly trust one feed.

The platform uses:

- multi-source public market data,
- normalization to a common quote schema,
- stale detection,
- source health scoring,
- controlled fallback logic when one venue degrades or blocks access.

This matters because options analytics degrade very quickly when:

- timestamps drift,
- stale quotes survive too long,
- crossed or empty markets are not filtered,
- synthetic fallbacks are not clearly separated from direct venue quotes.

The practical design principle is simple: **clean first, compute second**.

## 5. Edge cases explicitly handled

Several quantitative edge cases are treated as first-class concerns.

### 5.1 Near-zero maturity / zero volatility

The BS layer has deterministic-limit handling for:

- `T -> 0`,
- `sigma -> 0`.

This avoids nonsensical divisions and makes prices converge to discounted intrinsic value when appropriate.

### 5.2 Implied-vol inversion near arbitrage bounds

Implied volatility inversion uses:

- Newton-Raphson first,
- Brent fallback for robustness,
- explicit lower/upper no-arbitrage bounds.

This is especially important in crypto, where noisy quotes and extreme vols can otherwise destabilize the solver.

Brent matters here because it is **bracketed and guaranteed to converge** whenever the pricing function changes sign over the search interval. In practical terms, that means Atlas prefers a slightly slower answer over a numerically brittle one when the quote is awkward.

### 5.3 Greeks consistency

Analytical Black-Scholes Greeks are checked against finite-difference approximations in test coverage. This is a basic but important guardrail: risk numbers that are fast but inconsistent are worse than useless.

### 5.4 Model dispersion as a signal

Atlas does not hide the disagreement between models. It computes model dispersion explicitly and uses it as a confidence / edge-quality input. This is closer to how a real desk thinks: disagreement between BS, SABR, and Heston is itself information.

### 5.5 Empty or degraded chains

When the chain is sparse or missing, Atlas returns conservative fallbacks and low-confidence calibration rather than pretending precision exists where it does not.

## 6. Margin design

Atlas currently uses a **linear add-on portfolio margin approximation**:

- base rate on gross notional,
- add-ons for `|delta|`, `|gamma|`, `|vega|`, `|theta|`,
- simple initial / maintenance split.

Why this choice is reasonable at the current stage:

- it is transparent,
- it is monotone in obvious risk dimensions,
- it is easy to validate in tests,
- it exposes the shape of a margin engine without pretending to be exchange-exact.

### 6.1 What would be needed for SPAN-like margin

A production-grade risk stack would move toward a **scenario-based** engine closer to SPAN / portfolio margin.

At minimum, that would require:

- `16` stress scenarios per asset family,
- spot shocks, vol shocks, skew twists, and tenor shocks,
- cross-asset aggregation rules,
- concentration and liquidity add-ons,
- explicit short-option minimums and offset logic.

In other words, the current linear engine is a useful infrastructure placeholder, but a true production system would compute margin as the worst loss across a structured scenario cube, not as a weighted linear proxy.

## 7. Validation philosophy

The current validation strategy is deliberately practical.

It includes:

- closed-form Black-Scholes value checks,
- put-call parity checks,
- Greeks consistency checks,
- Monte Carlo convergence toward BS,
- binomial convergence toward BS,
- non-regression snapshots for BS / Heston / SABR,
- SLO / persistence reliability tests on the application side.

This does not prove production-grade model quality. It proves something more realistic for this project stage: the implementation is numerically coherent, regression-resistant, and operationally inspectable.

## 8. Known limitations

The limitations should be stated plainly.

- Heston is currently an approximation layer, not a full characteristic-function production engine.
- SABR is used as a smile interpolation tool; extrapolation risk remains.
- The calibration is heuristic and feature-driven, not a global optimizer with regularization.
- Funding, borrow, and venue-specific microstructure effects are simplified.
- American exercise handling exists in the tree tooling, but the live desk logic is centered on European crypto options.
- Public venue APIs can degrade or block traffic; synthetic fallback preserves platform continuity but is not a substitute for dedicated institutional market data.

## 9. Why these trade-offs are acceptable here

For Atlas, the most important outcome is not “the fanciest pricer on paper.” It is a system where:

- the models are explainable,
- the outputs are comparable,
- the limits are explicit,
- the platform remains interactive and resilient,
- execution and risk can consume the analytics coherently.

That is the right trade for a portfolio project that wants to signal both **quant judgment** and **platform engineering maturity**.

## 10. References

- Gatheral, Jim. *The Volatility Surface: A Practitioner's Guide*. Wiley, 2006.
- Hagan, Patrick S., Deep Kumar, Andrew S. Lesniewski, and Diana E. Woodward. “Managing Smile Risk.” *Wilmott Magazine*, 2002.
- Heston, Steven L. “A Closed-Form Solution for Options with Stochastic Volatility with Applications to Bond and Currency Options.” *The Review of Financial Studies*, 1993.
- Carr, Peter, and Dilip Madan. “Option Valuation Using the Fast Fourier Transform.” *Journal of Computational Finance*, 1999.
- Jäckel, Peter. *By Implication: A Computationally Efficient Implied Volatility Solver*. 2006.
