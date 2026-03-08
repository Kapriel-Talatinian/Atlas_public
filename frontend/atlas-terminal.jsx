import { useState, useMemo } from "react";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend,
  ReferenceLine,
  Bar,
  BarChart,
  ScatterChart,
  Scatter,
  Cell,
  RadarChart,
  Radar,
  PolarGrid,
  PolarAngleAxis,
  PolarRadiusAxis,
  ZAxis,
} from "recharts";

/* ═══════════════════════════════════════════════════════════
   MATH ENGINE
   ═══════════════════════════════════════════════════════════ */
const NC = (x) => {
  const a1 = 0.254829592,
    a2 = -0.284496736,
    a3 = 1.421413741,
    a4 = -1.453152027,
    a5 = 1.061405429,
    p = 0.3275911;
  const s = x < 0 ? -1 : 1;
  const ax = Math.abs(x) / 1.4142135;
  const t = 1 / (1 + p * ax);
  return (
    0.5 *
    (1 +
      s *
        (1 -
          (((((a5 * t + a4) * t + a3) * t + a2) * t + a1) *
            t *
            Math.exp(-ax * ax))))
  );
};

const NP = (x) => Math.exp(-0.5 * x * x) / 2.5066282;
const d1_ = (S, K, r, v, T) =>
  (Math.log(S / K) + (r + 0.5 * v * v) * T) / (v * Math.sqrt(T));
const d2_ = (S, K, r, v, T) => d1_(S, K, r, v, T) - v * Math.sqrt(T);

const bsC = (S, K, v, T, r) =>
  T <= 1e-10
    ? Math.max(S - K, 0)
    : S * NC(d1_(S, K, r, v, T)) -
      K * Math.exp(-r * T) * NC(d2_(S, K, r, v, T));

const bsP = (S, K, v, T, r) =>
  T <= 1e-10
    ? Math.max(K - S, 0)
    : K * Math.exp(-r * T) * NC(-d2_(S, K, r, v, T)) -
      S * NC(-d1_(S, K, r, v, T));

const bsPrice = (S, K, v, T, r, t) => (t === "call" ? bsC(S, K, v, T, r) : bsP(S, K, v, T, r));

const delta_ = (S, K, r, v, T, t) =>
  T <= 1e-10
    ? t === "call"
      ? S > K
        ? 1
        : 0
      : S < K
      ? -1
      : 0
    : t === "call"
    ? NC(d1_(S, K, r, v, T))
    : NC(d1_(S, K, r, v, T)) - 1;

const gamma_ = (S, K, r, v, T) =>
  T <= 1e-10 ? 0 : NP(d1_(S, K, r, v, T)) / (S * v * Math.sqrt(T));

const vega_ = (S, K, r, v, T) =>
  T <= 1e-10 ? 0 : (S * NP(d1_(S, K, r, v, T)) * Math.sqrt(T)) / 100;

const theta_ = (S, K, r, v, T, t) => {
  if (T <= 1e-10) return 0;
  const D1 = d1_(S, K, r, v, T);
  const D2 = d2_(S, K, r, v, T);
  const t1 = -(S * NP(D1) * v) / (2 * Math.sqrt(T));
  return (
    (t === "call"
      ? t1 - r * K * Math.exp(-r * T) * NC(D2)
      : t1 + r * K * Math.exp(-r * T) * NC(-D2)) / 365.25
  );
};

const smileV = (bv, m, T, sk = -0.14, cv = 0.06) => {
  const k = Math.log(m);
  return Math.max(0.05, bv + sk * k + (cv * k * k) / Math.max(T, 0.01));
};

// Heston approx
const heston = (
  S,
  K,
  r,
  v0,
  T,
  t,
  p = { kappa: 3, theta: 0.4, xi: 0.8, rho: -0.65 }
) => {
  const ev =
    v0 * v0 * Math.exp(-p.kappa * T) +
    p.theta * p.theta * (1 - Math.exp(-p.kappa * T));
  const vv = (p.xi * p.xi * T) / 12;
  const ca = (p.rho * p.xi * v0 * T) / 4;
  let av = Math.sqrt(Math.max(ev * (1 + vv) + ca, 0.01));
  const k = Math.log(K / S);
  av = Math.max(av + (p.rho * p.xi * k * 0.3) / (2 * av), 0.05);
  return bsPrice(S, K, av, T, r, t);
};

// MC simplified
const mc = (S, K, r, v, T, t, n = 1200) => {
  if (T <= 1e-10) return t === "call" ? Math.max(S - K, 0) : Math.max(K - S, 0);
  const steps = 30;
  const dt = T / steps;
  const dr = (r - 0.5 * v * v) * dt;
  const df = v * Math.sqrt(dt);
  let sum = 0;

  for (let i = 0; i < n; i++) {
    let s1 = S;
    let s2 = S;
    for (let j = 0; j < steps; j++) {
      const z =
        Math.sqrt(-2 * Math.log(Math.random())) * Math.cos(2 * Math.PI * Math.random());
      s1 *= Math.exp(dr + df * z);
      s2 *= Math.exp(dr - df * z);
    }
    const p1 = t === "call" ? Math.max(s1 - K, 0) : Math.max(K - s1, 0);
    const p2 = t === "call" ? Math.max(s2 - K, 0) : Math.max(K - s2, 0);
    sum += (p1 + p2) / 2;
  }
  return (Math.exp(-r * T) * sum) / n;
};

// Binomial
const binom = (S, K, r, v, T, t, N = 80) => {
  if (T <= 1e-10) return t === "call" ? Math.max(S - K, 0) : Math.max(K - S, 0);

  const crr = (n) => {
    const dt = T / n;
    const u = Math.exp(v * Math.sqrt(dt));
    const d = 1 / u;
    const p = (Math.exp(r * dt) - d) / (u - d);
    const dc = Math.exp(-r * dt);

    const pr = Array(n + 1);
    for (let i = 0; i <= n; i++) {
      const sT = S * Math.pow(u, n - i) * Math.pow(d, i);
      pr[i] = t === "call" ? Math.max(sT - K, 0) : Math.max(K - sT, 0);
    }
    for (let j = n - 1; j >= 0; j--) {
      for (let i = 0; i <= j; i++) {
        pr[i] = dc * (p * pr[i] + (1 - p) * pr[i + 1]);
      }
    }
    return pr[0];
  };

  return 2 * crr(N) - crr((N / 2) | 0);
};

// SABR
const sabr = (S, K, r, v, T, t) => {
  const F = S * Math.exp(r * T),
    al = v * 0.8,
    be = 0.5,
    rho = -0.35,
    nu = 0.55;
  let iv;

  if (Math.abs(F - K) < F * 1e-6) {
    const Fb = Math.pow(F, 1 - be);
    iv =
      (al / Fb) *
      (1 +
        (((1 - be) * (1 - be) * al * al) / (24 * Fb * Fb) +
          (rho * be * nu * al) / (4 * Fb) +
          ((2 - 3 * rho * rho) * nu * nu) / 24) *
          T);
  } else {
    const FK = F * K;
    const FKb = Math.pow(FK, (1 - be) / 2);
    const lFK = Math.log(F / K);
    const z = (nu * FKb * lFK) / al;
    const x = Math.log((Math.sqrt(1 - 2 * rho * z + z * z) + z - rho) / (1 - rho));
    iv =
      Math.abs(x) < 1e-10
        ? al / FKb
        : (al / (FKb * (1 + (((1 - be) * (1 - be) * lFK * lFK) / 24)))) *
          (z / x) *
          (1 +
            (((1 - be) * (1 - be) * al * al) / (24 * FKb * FKb) +
              (rho * be * nu * al) / (4 * FKb) +
              ((2 - 3 * rho * rho) * nu * nu) / 24) *
              T);
  }

  iv = Math.max(0.05, Math.min(iv || v, 5));
  return bsPrice(S, K, iv, T, r, t);
};

/* ═══════════════════════════════════════════════════════════
   TOXIC FLOW DEMO DATA GENERATOR
   ═══════════════════════════════════════════════════════════ */
const CLUSTER_TYPES = {
  BENIGN: { label: "Benign", color: "#4a5a80", icon: "○" },
  STALE_SNIPER: { label: "Stale Quote Sniper", color: "#ff5252", icon: "⚡" },
  INFORMED: { label: "Informed Directional", color: "#ff4da6", icon: "🎯" },
  VOL_INFORMED: { label: "Vol-Informed", color: "#b16cff", icon: "📊" },
  MOMENTUM: { label: "Momentum Chaser", color: "#ffb020", icon: "🔥" },
  PKG_LEGGER: { label: "Package Legger", color: "#ff8844", icon: "📦" },
  GAMMA_SCALP: { label: "Gamma Scalper", color: "#00d4ff", icon: "Γ" },
  EXPIRY_MANIP: { label: "Expiry Manipulator", color: "#ff2222", icon: "⏰" },
  SUSPICIOUS: { label: "Suspicious", color: "#ff6b6b", icon: "⚠" },
};

function seedRng(seed) {
  let s = seed;
  return () => {
    s = (s * 16807) % 2147483647;
    return (s - 1) / 2147483646;
  };
}

function generateToxicFlowData() {
  const rng = seedRng(42);
  const gauss = () => {
    const u1 = rng();
    const u2 = rng();
    return Math.sqrt(-2 * Math.log(u1 || 0.001)) * Math.cos(2 * Math.PI * u2);
  };

  const cps = [
    "MM-Alpha",
    "MM-Beta",
    "MM-Gamma",
    "MM-Delta",
    "HF-Citrine",
    "HF-Obsidian",
    "PROP-Volt",
    "PROP-Apex",
    "ARB-Flash",
    "ARB-Quantum",
    "ARB-Nexus",
    "RETAIL-Pool",
    "WHALE-001",
    "WHALE-002",
  ];
  const strats = [
    "Long Call",
    "Straddle",
    "Risk Reversal",
    "Iron Condor",
    "Calendar",
    "Butterfly",
    "Strangle",
    "Bull Spread",
  ];

  const trades = [];
  let spot = 87250;

  for (let i = 0; i < 400; i++) {
    spot *= Math.exp((rng() - 0.498) * 0.001);
    const cp = cps[Math.floor(rng() * cps.length)];
    const isToxic = cp.startsWith("ARB-") || cp === "WHALE-001";
    const strike = Math.round((spot * (1 + (rng() - 0.5) * 0.2)) / 500) * 500;
    const optType = rng() > 0.45 ? "call" : "put";
    const side = isToxic ? (rng() > 0.3 ? "buy" : "sell") : rng() > 0.5 ? "buy" : "sell";
    const tte = [1, 7, 14, 30, 60, 90][Math.floor(rng() * 6)];
    const T = tte / 365.25;
    const iv = 0.55 + (rng() - 0.5) * 0.2;
    const price = bsPrice(spot, strike, iv, T, 0.048, optType);
    const qty = isToxic ? Math.floor(rng() * 40 + 10) : Math.floor(rng() * 20 + 1);
    const latencyMs = isToxic && cp.startsWith("ARB-") ? rng() * 5 : rng() * 500 + 20;
    const d = delta_(spot, strike, 0.048, iv, T, optType);

    const sign = side === "buy" ? 1 : -1;
    const drift = (rng() - 0.48) * 0.001;
    const toxDrift = isToxic ? sign * Math.abs(drift) * 3 : drift;
    const latBoost = latencyMs < 5 && rng() < 0.3 ? sign * 0.002 : 0;
    const vl = 0.0005;
    const m1s = (toxDrift + latBoost * 2 + gauss() * vl) * spot;
    const m5s = (toxDrift * 5 + latBoost * 0.5 + gauss() * vl * 2.2) * spot;
    const m5m = (toxDrift * 300 + gauss() * vl * 17) * spot;
    const m30m = (toxDrift * 1800 + gauss() * vl * 42) * spot;

    const markout5s = sign * d * m5s * qty;
    const markout5m = sign * d * m5m * qty;

    let cluster = "BENIGN",
      flags = [],
      toxScore = 0;
    const absM5s = Math.abs(markout5s),
      absM5m = Math.abs(markout5m);

    if (latencyMs < 5 && Math.abs(m1s) > Math.abs(m5m) * 2) {
      cluster = "STALE_SNIPER";
      flags = ["FAST_FILL", "MEAN_REVERT"];
      toxScore = 0.6 + rng() * 0.3;
    } else if (markout5s > 0 && markout5m > 0 && m30m > 0) {
      cluster = "INFORMED";
      flags = ["CONSISTENT_WINNER"];
      toxScore = 0.5 + rng() * 0.4;
    } else if (Math.abs((rng() - 0.5) * 0.02) > 0.01) {
      cluster = "VOL_INFORMED";
      flags = ["PRE_VOL_MOVE"];
      toxScore = 0.4 + rng() * 0.3;
    } else if (latencyMs < 10 && qty > 30) {
      cluster = "EXPIRY_MANIP";
      flags = ["LARGE_NEAR_EXPIRY"];
      toxScore = 0.7 + rng() * 0.2;
    } else if (rng() < 0.15) {
      cluster = "MOMENTUM";
      flags = ["TREND_FOLLOWING"];
      toxScore = 0.3 + rng() * 0.3;
    } else if (rng() < 0.1 && Math.abs(d) < 0.1) {
      cluster = "GAMMA_SCALP";
      toxScore = 0.2 + rng() * 0.2;
    } else if (absM5s > 500 || absM5m > 2000) {
      cluster = "SUSPICIOUS";
      flags = ["ELEVATED_MARKOUT"];
      toxScore = 0.35 + rng() * 0.3;
    } else {
      toxScore = rng() * 0.15;
    }

    const level =
      toxScore < 0.15
        ? "SAFE"
        : toxScore < 0.3
        ? "LOW"
        : toxScore < 0.5
        ? "MEDIUM"
        : toxScore < 0.75
        ? "HIGH"
        : "CRITICAL";

    const ts = new Date(
      2026,
      2,
      7,
      6 + Math.floor(i / 50),
      Math.floor(rng() * 60),
      Math.floor(rng() * 60)
    );

    trades.push({
      id: `ATL-${(10000 + i).toString().padStart(5, "0")}`,
      ts: ts.toISOString().replace("T", " ").slice(0, 19),
      cp,
      instrument: `BTC-${tte}D-${strike}-${optType === "call" ? "C" : "P"}`,
      optType,
      side,
      strike,
      qty,
      price,
      iv,
      spot,
      delta: d,
      latencyMs: Math.round(latencyMs * 10) / 10,
      markout5s: Math.round(markout5s),
      markout5m: Math.round(markout5m),
      markout30m: Math.round(sign * d * m30m * qty),
      cluster,
      clusterInfo: CLUSTER_TYPES[cluster],
      toxScore: Math.round(toxScore * 100) / 100,
      level,
      flags,
      strategy: rng() < 0.35 ? strats[Math.floor(rng() * strats.length)] : null,
      venue: ["CLOB", "RFQ", "Block"][Math.floor(rng() * 3)],
      model: ["BS", "Heston", "SABR", "MC"][Math.floor(rng() * 4)],
    });
  }

  return trades;
}

/* ═══════════════════════════════════════════════════════════
   UI SYSTEM
   ═══════════════════════════════════════════════════════════ */
const C = {
  bg: "#060810",
  bg2: "#0a0e18",
  pn: "#0e1420",
  cd: "#131b2b",
  cd2: "#1a2338",
  bd: "#1a2540",
  bd2: "#243050",
  ac: "#00e8a2",
  a2: "#4d8df7",
  a3: "#b16cff",
  wr: "#ff5252",
  am: "#ffb020",
  cy: "#00d4ff",
  pk: "#ff4da6",
  t1: "#e8edf4",
  t2: "#8a9cc0",
  t3: "#4a5a80",
  t4: "#2a3555",
  gn: "#00e8a2",
  rd: "#ff5252",
};

const F = "'IBM Plex Mono', monospace";

const Tag = ({ children, color = C.ac, glow }) => (
  <span
    style={{
      display: "inline-flex",
      alignItems: "center",
      gap: 3,
      background: `${color}12`,
      color,
      border: `1px solid ${color}30`,
      borderRadius: 3,
      padding: "1px 6px",
      fontSize: 9,
      fontFamily: F,
      letterSpacing: 0.7,
      boxShadow: glow ? `0 0 10px ${color}25` : "none",
      whiteSpace: "nowrap",
    }}
  >
    {children}
  </span>
);

const Dot = ({ color = C.ac, pulse, size = 6 }) => (
  <span
    style={{
      width: size,
      height: size,
      borderRadius: "50%",
      background: color,
      display: "inline-block",
      boxShadow: `0 0 8px ${color}`,
      animation: pulse ? "pulse 2s infinite" : "none",
    }}
  />
);

const Btn = ({ children, active, onClick, color = C.ac, small }) => (
  <button
    onClick={onClick}
    style={{
      background: active ? `${color}15` : "transparent",
      border: `1px solid ${active ? color : C.bd}`,
      color: active ? color : C.t3,
      fontFamily: F,
      fontSize: small ? 9 : 10,
      padding: small ? "3px 8px" : "5px 12px",
      borderRadius: 4,
      cursor: "pointer",
      letterSpacing: 0.5,
      transition: "all .15s",
      whiteSpace: "nowrap",
    }}
  >
    {children}
  </button>
);

const Panel = ({ children, style = {}, ...props }) => (
  <div
    {...props}
    style={{
      background: C.pn,
      border: `1px solid ${C.bd}`,
      borderRadius: 6,
      padding: 12,
      position: "relative",
      overflow: "hidden",
      ...style,
    }}
  >
    {children}
  </div>
);

const Head = ({ title, sub, right, color = C.ac }) => (
  <div
    style={{
      display: "flex",
      alignItems: "center",
      justifyContent: "space-between",
      marginBottom: 10,
      gap: 8,
    }}
  >
    <div style={{ display: "flex", alignItems: "center", gap: 7 }}>
      <Dot color={color} />
      <span
        style={{
          fontFamily: F,
          fontSize: 10,
          color: C.t2,
          letterSpacing: 1.5,
          textTransform: "uppercase",
        }}
      >
        {title}
      </span>
      {sub && <Tag color={C.t3}>{sub}</Tag>}
    </div>
    {right && <div style={{ display: "flex", gap: 4, alignItems: "center" }}>{right}</div>}
  </div>
);

const TT = ({ active, payload, label }) => {
  if (!active || !payload?.length) return null;
  return (
    <div
      style={{
        background: `${C.cd}f0`,
        border: `1px solid ${C.bd2}`,
        borderRadius: 4,
        padding: "5px 9px",
        fontFamily: F,
        fontSize: 9.5,
        backdropFilter: "blur(8px)",
      }}
    >
      <div style={{ color: C.t3, marginBottom: 2 }}>{label}</div>
      {payload.map((p, i) => (
        <div key={i} style={{ color: p.color || C.t1, lineHeight: 1.5 }}>
          {p.name}: <b>{typeof p.value === "number" ? p.value.toFixed(2) : p.value}</b>
        </div>
      ))}
    </div>
  );
};

const LEVEL_COLORS = {
  SAFE: C.gn,
  LOW: "#66bb6a",
  MEDIUM: C.am,
  HIGH: "#ff6b35",
  CRITICAL: C.rd,
};

/* ═══════════════════════════════════════════════════════════
   MAIN APP
   ═══════════════════════════════════════════════════════════ */
export default function Atlas() {
  const [tab, setTab] = useState("toxic");
  const [spot] = useState(87250);
  const [baseVol] = useState(0.58);
  const [r] = useState(0.048);
  const [tte, setTte] = useState(30);
  const [strike, setStrike] = useState(87000);
  const [optType, setOptType] = useState("call");
  const [selectedCp, setSelectedCp] = useState(null);
  const [selectedCluster, setSelectedCluster] = useState(null);

  const T = tte / 365.25;
  const allTrades = useMemo(() => generateToxicFlowData(), []);

  const clusters = useMemo(() => {
    const grouped = {};
    allTrades.forEach((t) => {
      if (!grouped[t.cluster]) {
        grouped[t.cluster] = {
          type: t.cluster,
          info: t.clusterInfo,
          trades: [],
          totalNotional: 0,
          totalMarkout5m: 0,
        };
      }
      grouped[t.cluster].trades.push(t);
      grouped[t.cluster].totalNotional += Math.abs(t.price * t.qty);
      grouped[t.cluster].totalMarkout5m += t.markout5m;
    });

    return Object.values(grouped)
      .map((g) => ({
        ...g,
        count: g.trades.length,
        avgToxScore: g.trades.reduce((a, t) => a + t.toxScore, 0) / g.trades.length,
        avgMarkout5s: g.trades.reduce((a, t) => a + t.markout5s, 0) / g.trades.length,
      }))
      .sort((a, b) => b.avgToxScore - a.avgToxScore);
  }, [allTrades]);

  const cpProfiles = useMemo(() => {
    const grouped = {};
    allTrades.forEach((t) => {
      if (!grouped[t.cp]) grouped[t.cp] = { cp: t.cp, trades: [], totalMarkout5s: 0, totalMarkout5m: 0 };
      grouped[t.cp].trades.push(t);
      grouped[t.cp].totalMarkout5s += t.markout5s;
      grouped[t.cp].totalMarkout5m += t.markout5m;
    });

    return Object.values(grouped)
      .map((g) => {
        const avgTox = g.trades.reduce((a, t) => a + t.toxScore, 0) / g.trades.length;
        const topClusterEntry = Object.entries(
          g.trades.reduce((acc, t) => {
            acc[t.cluster] = (acc[t.cluster] || 0) + 1;
            return acc;
          }, {})
        ).sort((a, b) => b[1] - a[1])[0];

        return {
          cp: g.cp,
          count: g.trades.length,
          avgToxScore: avgTox,
          avgMarkout5s: Math.round(g.totalMarkout5s / g.trades.length),
          avgMarkout5m: Math.round(g.totalMarkout5m / g.trades.length),
          avgLatency: Math.round(g.trades.reduce((a, t) => a + t.latencyMs, 0) / g.trades.length),
          topCluster: topClusterEntry ? topClusterEntry[0] : "BENIGN",
          level: avgTox > 0.5 ? "HIGH" : avgTox > 0.3 ? "MEDIUM" : "LOW",
        };
      })
      .sort((a, b) => b.avgToxScore - a.avgToxScore);
  }, [allTrades]);

  const toxicPct = Math.round(
    (allTrades.filter((t) => t.toxScore > 0.3).length / allTrades.length) * 100
  );
  const totalAdverseCost = Math.abs(
    allTrades.filter((t) => t.toxScore > 0.3).reduce((a, t) => a + t.markout5m, 0)
  );

  const modelPrices = useMemo(() => {
    const iv = smileV(baseVol, spot / strike, T);
    return [
      {
        model: "Black-Scholes",
        tag: "BS",
        price: bsPrice(spot, strike, iv, T, r, optType),
        color: C.ac,
        iv: iv * 100,
      },
      {
        model: "Heston SV",
        tag: "HST",
        price: heston(spot, strike, r, Math.sqrt(iv), T, optType),
        color: C.a2,
        iv: 0,
      },
      {
        model: "Monte Carlo",
        tag: "MC",
        price: mc(spot, strike, r, iv, T, optType),
        color: C.a3,
        iv: 0,
      },
      {
        model: "Binomial CRR",
        tag: "BIN",
        price: binom(spot, strike, r, iv, T, optType),
        color: C.am,
        iv: 0,
      },
      {
        model: "SABR",
        tag: "SABR",
        price: sabr(spot, strike, r, iv, T, optType),
        color: C.cy,
        iv: 0,
      },
    ];
  }, [spot, strike, baseVol, r, T, optType]);

  const scatterData = useMemo(
    () =>
      allTrades.map((t) => ({
        x: t.markout5s,
        y: t.markout5m,
        z: Math.min(Math.max(Math.abs(t.price * t.qty) / 500, 20), 250),
        toxScore: t.toxScore,
        cluster: t.cluster,
        cp: t.cp,
        color: t.clusterInfo.color,
        id: t.id,
      })),
    [allTrades]
  );

  const filteredTrades = useMemo(() => {
    let t = allTrades;
    if (selectedCp) t = t.filter((tr) => tr.cp === selectedCp);
    if (selectedCluster) t = t.filter((tr) => tr.cluster === selectedCluster);
    return t.slice(0, 100);
  }, [allTrades, selectedCp, selectedCluster]);

  const tabs = [
    { id: "toxic", label: "Toxic Flow", color: C.rd },
    { id: "models", label: "Model Pricing", color: C.ac },
    { id: "history", label: "History & Audit", color: C.pk },
    { id: "cluster", label: "Portfolio", color: C.am },
  ];

  const bsIv = smileV(baseVol, spot / strike, T);
  const bsPrc = bsPrice(spot, strike, bsIv, T, r, optType);

  return (
    <div style={{ background: C.bg, minHeight: "100vh", color: C.t1, fontFamily: F, fontSize: 11 }}>
      <div
        style={{
          background: C.bg2,
          borderBottom: `1px solid ${C.bd}`,
          padding: "0 16px",
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          height: 42,
        }}
      >
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <div
            style={{
              width: 24,
              height: 24,
              borderRadius: 5,
              background: `linear-gradient(135deg,${C.ac},${C.a2})`,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontSize: 14,
              fontWeight: 900,
              color: C.bg,
            }}
          >
            Σ
          </div>
          <span style={{ fontFamily: "'Outfit',sans-serif", fontSize: 14, fontWeight: 700, letterSpacing: 3 }}>
            ATLAS
          </span>
          <Tag color={C.ac} glow>
            LIVE
          </Tag>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 16 }}>
          <span style={{ fontSize: 10, color: C.t3 }}>BTC</span>
          <span style={{ fontSize: 15, fontWeight: 700, color: C.ac }}>${spot.toLocaleString()}</span>
          <Tag color={C.gn}>+3.1%</Tag>
          <div style={{ width: 1, height: 18, background: C.bd }} />
          <span style={{ fontSize: 10, color: C.t3 }}>IV</span>
          <span style={{ fontSize: 12, fontWeight: 600, color: C.a3 }}>{(baseVol * 100).toFixed(0)}%</span>
          <Dot color={C.ac} pulse />
        </div>
      </div>

      <div
        style={{
          background: C.pn,
          borderBottom: `1px solid ${C.bd}`,
          padding: "0 16px",
          display: "flex",
          height: 34,
        }}
      >
        {tabs.map((t) => (
          <button
            key={t.id}
            onClick={() => setTab(t.id)}
            style={{
              background: tab === t.id ? `${t.color}10` : "transparent",
              border: "none",
              borderBottom: tab === t.id ? `2px solid ${t.color}` : "2px solid transparent",
              color: tab === t.id ? t.color : C.t3,
              fontFamily: F,
              fontSize: 10,
              padding: "8px 14px",
              cursor: "pointer",
              letterSpacing: 0.8,
            }}
          >
            {t.label.toUpperCase()}
          </button>
        ))}
      </div>

      <div
        style={{
          padding: 12,
          display: "flex",
          flexDirection: "column",
          gap: 12,
          maxHeight: "calc(100vh - 76px)",
          overflowY: "auto",
        }}
      >
        {tab === "toxic" && (
          <>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(5,1fr)", gap: 8 }}>
              {[
                { l: "TOTAL TRADES", v: allTrades.length, c: C.t1 },
                { l: "TOXIC TRADES", v: allTrades.filter((t) => t.toxScore > 0.3).length, c: C.rd },
                { l: "TOXIC %", v: `${toxicPct}%`, c: toxicPct > 25 ? C.rd : C.am },
                {
                  l: "ADV. SELECTION COST",
                  v: `$${Math.round(totalAdverseCost).toLocaleString()}`,
                  c: C.rd,
                },
                { l: "ACTIVE CLUSTERS", v: clusters.filter((c) => c.type !== "BENIGN").length, c: C.am },
              ].map((m, i) => (
                <Panel key={i} style={{ borderTop: `2px solid ${m.c}` }}>
                  <div style={{ fontSize: 8, color: C.t3, letterSpacing: 1.5, marginBottom: 4 }}>{m.l}</div>
                  <div style={{ fontSize: 20, fontWeight: 800, color: m.c }}>{m.v}</div>
                </Panel>
              ))}
            </div>

            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill,minmax(180px,1fr))", gap: 8 }}>
              {clusters
                .filter((c) => c.type !== "BENIGN")
                .map((cl, i) => (
                  <Panel
                    key={i}
                    style={{
                      borderLeft: `3px solid ${cl.info.color}`,
                      cursor: "pointer",
                      background: selectedCluster === cl.type ? `${cl.info.color}10` : C.pn,
                    }}
                    onClick={() => setSelectedCluster(selectedCluster === cl.type ? null : cl.type)}
                  >
                    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: 6 }}>
                      <span style={{ fontSize: 14 }}>{cl.info.icon}</span>
                      <Tag color={cl.info.color}>{cl.count}</Tag>
                    </div>
                    <div style={{ fontSize: 10, fontWeight: 600, color: cl.info.color, marginBottom: 2 }}>
                      {cl.info.label}
                    </div>
                    <div style={{ fontSize: 9, color: C.t3 }}>Avg toxicity: {(cl.avgToxScore * 100).toFixed(0)}%</div>
                    <div style={{ fontSize: 9, color: cl.avgMarkout5s > 0 ? C.rd : C.gn }}>
                      Avg markout 5s: ${Math.round(cl.avgMarkout5s)}
                    </div>
                  </Panel>
                ))}
            </div>

            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <Panel>
                <Head title="Flow Toxicity Map" sub="MARKOUT 5s vs 5m" color={C.rd} />
                <ResponsiveContainer width="100%" height={300}>
                  <ScatterChart>
                    <CartesianGrid strokeDasharray="3 3" stroke={C.bd} />
                    <XAxis
                      dataKey="x"
                      type="number"
                      tick={{ fontSize: 8, fill: C.t3 }}
                      name="Markout 5s"
                      tickFormatter={(v) => `$${v}`}
                    />
                    <YAxis
                      dataKey="y"
                      type="number"
                      tick={{ fontSize: 8, fill: C.t3 }}
                      name="Markout 5m"
                      tickFormatter={(v) => `$${v}`}
                    />
                    <ZAxis dataKey="z" range={[30, 250]} />
                    <Tooltip
                      content={({ active, payload }) => {
                        if (!active || !payload?.length) return null;
                        const d = payload[0]?.payload;
                        return (
                          <div
                            style={{
                              background: `${C.cd}f0`,
                              border: `1px solid ${C.bd2}`,
                              borderRadius: 4,
                              padding: "6px 10px",
                              fontFamily: F,
                              fontSize: 9,
                            }}
                          >
                            <div style={{ color: d?.color, fontWeight: 600 }}>
                              {CLUSTER_TYPES[d?.cluster]?.label}
                            </div>
                            <div style={{ color: C.t2 }}>CP: {d?.cp}</div>
                            <div style={{ color: C.t2 }}>
                              5s: ${d?.x?.toFixed(0)} · 5m: ${d?.y?.toFixed(0)}
                            </div>
                            <div style={{ color: C.t2 }}>
                              Tox: {((d?.toxScore || 0) * 100).toFixed(0)}%
                            </div>
                          </div>
                        );
                      }}
                    />
                    <ReferenceLine x={0} stroke={C.t4} />
                    <ReferenceLine y={0} stroke={C.t4} />
                    <Scatter data={scatterData}>
                      {scatterData.map((d, i) => (
                        <Cell key={i} fill={d.color} fillOpacity={0.65} />
                      ))}
                    </Scatter>
                  </ScatterChart>
                </ResponsiveContainer>
              </Panel>

              <Panel>
                <Head
                  title="Counterparty Toxicity"
                  color={C.pk}
                  right={
                    selectedCp && (
                      <Btn small color={C.t3} onClick={() => setSelectedCp(null)}>
                        CLEAR
                      </Btn>
                    )
                  }
                />
                <div style={{ maxHeight: 300, overflowY: "auto" }}>
                  <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 10 }}>
                    <thead>
                      <tr>
                        {["Counterparty", "Trades", "Avg Tox", "M5s", "M5m", "Latency", "Cluster"].map((h, i) => (
                          <th
                            key={i}
                            style={{
                              padding: "5px 6px",
                              color: C.t3,
                              fontWeight: 500,
                              fontSize: 8,
                              letterSpacing: 1,
                              textAlign: "left",
                              borderBottom: `1px solid ${C.bd}`,
                              position: "sticky",
                              top: 0,
                              background: C.pn,
                              zIndex: 1,
                            }}
                          >
                            {h}
                          </th>
                        ))}
                      </tr>
                    </thead>
                    <tbody>
                      {cpProfiles.map((cp, i) => {
                        const info = CLUSTER_TYPES[cp.topCluster];
                        return (
                          <tr
                            key={i}
                            style={{
                              borderBottom: `1px solid ${C.bd}10`,
                              cursor: "pointer",
                              background: selectedCp === cp.cp ? `${C.pk}10` : "transparent",
                            }}
                            onClick={() => setSelectedCp(selectedCp === cp.cp ? null : cp.cp)}
                            onMouseEnter={(e) => {
                              if (selectedCp !== cp.cp) e.currentTarget.style.background = `${C.a2}08`;
                            }}
                            onMouseLeave={(e) => {
                              if (selectedCp !== cp.cp) e.currentTarget.style.background = "transparent";
                            }}
                          >
                            <td style={{ padding: "5px 6px", fontWeight: 500 }}>{cp.cp}</td>
                            <td style={{ padding: "5px 6px", color: C.t2 }}>{cp.count}</td>
                            <td style={{ padding: "5px 6px" }}>
                              <Tag color={LEVEL_COLORS[cp.level] || C.t3}>
                                {(cp.avgToxScore * 100).toFixed(0)}%
                              </Tag>
                            </td>
                            <td style={{ padding: "5px 6px", color: cp.avgMarkout5s > 0 ? C.rd : C.gn }}>
                              ${cp.avgMarkout5s}
                            </td>
                            <td style={{ padding: "5px 6px", color: cp.avgMarkout5m > 0 ? C.rd : C.gn }}>
                              ${cp.avgMarkout5m}
                            </td>
                            <td style={{ padding: "5px 6px", color: cp.avgLatency < 20 ? C.rd : C.t2 }}>
                              {cp.avgLatency}ms
                            </td>
                            <td style={{ padding: "5px 6px" }}>
                              <Tag color={info?.color || C.t3}>{info?.label || "Unknown"}</Tag>
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              </Panel>
            </div>

            <Panel>
              <Head title="Markout Distribution by Cluster" sub="5-MINUTE HORIZON" color={C.a3} />
              <ResponsiveContainer width="100%" height={200}>
                <BarChart
                  data={clusters
                    .filter((c) => c.type !== "BENIGN")
                    .map((c) => ({
                      name: c.info.label.split(" ").slice(0, 2).join(" "),
                      avg5s: Math.round(c.avgMarkout5s),
                      pnl: Math.round(c.totalMarkout5m),
                      count: c.count,
                      color: c.info.color,
                    }))}
                >
                  <CartesianGrid strokeDasharray="3 3" stroke={C.bd} />
                  <XAxis dataKey="name" tick={{ fontSize: 8, fill: C.t3 }} />
                  <YAxis tick={{ fontSize: 8, fill: C.t3 }} tickFormatter={(v) => `$${v}`} />
                  <Tooltip content={<TT />} />
                  <Bar dataKey="pnl" name="Total P&L Impact" radius={[3, 3, 0, 0]}>
                    {clusters
                      .filter((c) => c.type !== "BENIGN")
                      .map((c, i) => (
                        <Cell key={i} fill={c.info.color} fillOpacity={0.7} />
                      ))}
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            </Panel>
          </>
        )}

        {tab === "models" && (
          <>
            <Panel>
              <Head
                title="Pricing Parameters"
                color={C.ac}
                right={
                  <>
                    <Btn small active={optType === "call"} onClick={() => setOptType("call")} color={C.gn}>
                      CALL
                    </Btn>
                    <Btn small active={optType === "put"} onClick={() => setOptType("put")} color={C.rd}>
                      PUT
                    </Btn>
                  </>
                }
              />
              <div style={{ display: "flex", gap: 16, flexWrap: "wrap", alignItems: "center" }}>
                <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
                  <span style={{ fontSize: 9, color: C.t3 }}>K</span>
                  <input
                    value={strike}
                    onChange={(e) => setStrike(parseInt(e.target.value, 10) || 0)}
                    style={{
                      background: C.bg2,
                      border: `1px solid ${C.bd}`,
                      borderRadius: 3,
                      color: C.t1,
                      fontFamily: F,
                      fontSize: 11,
                      padding: "3px 6px",
                      width: 75,
                      outline: "none",
                    }}
                  />
                </div>
                <div style={{ display: "flex", alignItems: "center", gap: 4 }}>
                  <span style={{ fontSize: 9, color: C.t3 }}>DTE</span>
                  <input
                    type="range"
                    min={1}
                    max={365}
                    value={tte}
                    onChange={(e) => setTte(parseInt(e.target.value, 10))}
                    style={{ width: 90, accentColor: C.am }}
                  />
                  <span style={{ fontSize: 10, color: C.am, fontWeight: 600 }}>{tte}d</span>
                </div>
              </div>
            </Panel>

            <div style={{ display: "grid", gridTemplateColumns: "repeat(5,1fr)", gap: 8 }}>
              {modelPrices.map((m, i) => (
                <Panel key={i} style={{ borderTop: `2px solid ${m.color}` }}>
                  <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 4 }}>
                    <Tag color={m.color}>{m.tag}</Tag>
                    <span style={{ fontSize: 8, color: C.t3 }}>{m.model}</span>
                  </div>
                  <div style={{ fontSize: 20, fontWeight: 800, color: m.color }}>${m.price.toFixed(2)}</div>
                  {i > 0 && (
                    <div
                      style={{
                        fontSize: 9,
                        color: Math.abs(m.price - modelPrices[0].price) < 1 ? C.gn : C.am,
                        marginTop: 4,
                      }}
                    >
                      Δ: {(m.price - modelPrices[0].price) >= 0 ? "+" : ""}
                      {(m.price - modelPrices[0].price).toFixed(2)}
                    </div>
                  )}
                </Panel>
              ))}
            </div>

            <Panel>
              <Head title="Greeks" sub={`K=${strike.toLocaleString()}`} color={C.cy} />
              <div style={{ display: "grid", gridTemplateColumns: "repeat(7,1fr)", gap: 6 }}>
                {[
                  { l: "Δ", v: delta_(spot, strike, r, bsIv, T, optType), dp: 4, c: C.ac },
                  { l: "Γ", v: gamma_(spot, strike, r, bsIv, T), dp: 6, c: C.a2 },
                  { l: "ν", v: vega_(spot, strike, r, bsIv, T), dp: 2, c: C.a3 },
                  { l: "Θ", v: theta_(spot, strike, r, bsIv, T, optType), dp: 2, c: C.wr },
                  { l: "IV", v: bsIv * 100, dp: 1, c: C.t1, s: "%" },
                  { l: "Price", v: bsPrc, dp: 2, c: C.ac, s: "$" },
                  { l: "Moneyness", v: (spot / strike - 1) * 100, dp: 2, c: C.t1, s: "%" },
                ].map((g, i) => (
                  <div
                    key={i}
                    style={{
                      background: C.cd,
                      borderRadius: 3,
                      padding: "5px 7px",
                      borderLeft: `2px solid ${g.c}`,
                    }}
                  >
                    <div style={{ fontSize: 8, color: C.t3, letterSpacing: 1 }}>{g.l}</div>
                    <div style={{ fontSize: 13, fontWeight: 700, color: g.c }}>
                      {g.s === "$" ? "$" : ""}
                      {g.v.toFixed(g.dp)}
                      {g.s && g.s !== "$" ? g.s : ""}
                    </div>
                  </div>
                ))}
              </div>
            </Panel>
          </>
        )}

        {tab === "history" && (
          <Panel>
            <Head
              title="Trade History — Full Audit Trail"
              color={C.pk}
              sub={`${filteredTrades.length} TRADES`}
              right={
                <>
                  {selectedCp && <Tag color={C.pk}>CP: {selectedCp}</Tag>}
                  {selectedCluster && (
                    <Tag color={CLUSTER_TYPES[selectedCluster]?.color}>
                      {CLUSTER_TYPES[selectedCluster]?.label}
                    </Tag>
                  )}
                  {(selectedCp || selectedCluster) && (
                    <Btn
                      small
                      color={C.t3}
                      onClick={() => {
                        setSelectedCp(null);
                        setSelectedCluster(null);
                      }}
                    >
                      CLEAR
                    </Btn>
                  )}
                </>
              }
            />
            <div style={{ overflowX: "auto", maxHeight: 600 }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 9.5 }}>
                <thead>
                  <tr>
                    {[
                      "ID",
                      "Time",
                      "Type",
                      "Instrument",
                      "Side",
                      "Qty",
                      "Price",
                      "Model",
                      "Toxicity",
                      "Cluster",
                      "Markout 5s",
                      "Markout 5m",
                      "Latency",
                      "CP",
                      "Flags",
                    ].map((h, i) => (
                      <th
                        key={i}
                        style={{
                          padding: "5px 6px",
                          color: C.t3,
                          fontWeight: 500,
                          fontSize: 7.5,
                          letterSpacing: 1,
                          textAlign: "left",
                          borderBottom: `1px solid ${C.bd}`,
                          position: "sticky",
                          top: 0,
                          background: C.pn,
                          zIndex: 1,
                          whiteSpace: "nowrap",
                        }}
                      >
                        {h}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {filteredTrades.map((t, i) => (
                    <tr
                      key={i}
                      style={{ borderBottom: `1px solid ${C.bd}10`, transition: "background .1s" }}
                      onMouseEnter={(e) => (e.currentTarget.style.background = `${C.pk}08`)}
                      onMouseLeave={(e) => (e.currentTarget.style.background = "transparent")}
                    >
                      <td style={{ padding: "4px 6px", color: C.pk, fontWeight: 500 }}>{t.id}</td>
                      <td style={{ padding: "4px 6px", color: C.t3, whiteSpace: "nowrap", fontSize: 9 }}>{t.ts}</td>
                      <td style={{ padding: "4px 6px" }}>
                        <Tag color={t.venue === "RFQ" ? C.am : t.venue === "Block" ? C.a2 : C.t3}>{t.venue}</Tag>
                      </td>
                      <td style={{ padding: "4px 6px", color: C.t2, fontSize: 9 }}>{t.instrument}</td>
                      <td style={{ padding: "4px 6px", color: t.side === "buy" ? C.gn : C.rd, fontWeight: 600 }}>
                        {t.side.toUpperCase()}
                      </td>
                      <td style={{ padding: "4px 6px", color: C.t2 }}>{t.qty}</td>
                      <td style={{ padding: "4px 6px", color: C.t1 }}>${t.price.toFixed(0)}</td>
                      <td style={{ padding: "4px 6px" }}>
                        <Tag color={t.model === "BS" ? C.ac : t.model === "Heston" ? C.a2 : t.model === "MC" ? C.a3 : C.cy}>
                          {t.model}
                        </Tag>
                      </td>
                      <td style={{ padding: "4px 6px" }}>
                        <Tag color={LEVEL_COLORS[t.level]}>{(t.toxScore * 100).toFixed(0)}%</Tag>
                      </td>
                      <td style={{ padding: "4px 6px" }}>
                        <Tag color={t.clusterInfo.color}>{t.clusterInfo.label.split(" ")[0]}</Tag>
                      </td>
                      <td style={{ padding: "4px 6px", color: t.markout5s > 0 ? C.rd : C.gn, fontWeight: 500 }}>
                        ${t.markout5s}
                      </td>
                      <td style={{ padding: "4px 6px", color: t.markout5m > 0 ? C.rd : C.gn, fontWeight: 500 }}>
                        ${t.markout5m}
                      </td>
                      <td style={{ padding: "4px 6px", color: t.latencyMs < 10 ? C.rd : C.t3 }}>{t.latencyMs}ms</td>
                      <td style={{ padding: "4px 6px", color: C.t2, cursor: "pointer" }} onClick={() => setSelectedCp(t.cp)}>
                        {t.cp}
                      </td>
                      <td style={{ padding: "4px 6px" }}>
                        <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
                          {t.flags.map((f, j) => (
                            <Tag key={j} color={C.wr}>
                              {f}
                            </Tag>
                          ))}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Panel>
        )}

        {tab === "cluster" && (
          <Panel>
            <Head title="Cluster Radar — Exposure Analysis" color={C.am} />
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: 12 }}>
              <ResponsiveContainer width="100%" height={300}>
                <RadarChart
                  data={clusters
                    .filter((c) => c.type !== "BENIGN")
                    .map((c) => ({
                      axis: c.info.label.split(" ").slice(0, 2).join("\n"),
                      count: c.count,
                      toxScore: c.avgToxScore * 100,
                      notional: c.totalNotional / 10000,
                    }))}
                >
                  <PolarGrid stroke={C.bd} />
                  <PolarAngleAxis dataKey="axis" tick={{ fontSize: 8, fill: C.t2 }} />
                  <PolarRadiusAxis tick={false} />
                  <Radar name="Trade Count" dataKey="count" stroke={C.a2} fill={C.a2} fillOpacity={0.15} strokeWidth={2} />
                  <Radar name="Toxicity %" dataKey="toxScore" stroke={C.rd} fill={C.rd} fillOpacity={0.1} strokeWidth={2} />
                  <Legend wrapperStyle={{ fontSize: 9 }} />
                </RadarChart>
              </ResponsiveContainer>

              <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                <div style={{ fontSize: 10, color: C.t3, letterSpacing: 1.5, marginBottom: 4 }}>CLUSTER BREAKDOWN</div>
                {clusters.map((cl, i) => (
                  <div
                    key={i}
                    style={{
                      display: "flex",
                      alignItems: "center",
                      gap: 8,
                      padding: "6px 8px",
                      background: C.cd,
                      borderRadius: 4,
                      borderLeft: `3px solid ${cl.info.color}`,
                    }}
                  >
                    <span style={{ fontSize: 12 }}>{cl.info.icon}</span>
                    <div style={{ flex: 1 }}>
                      <div style={{ fontSize: 10, color: cl.info.color, fontWeight: 600 }}>{cl.info.label}</div>
                      <div style={{ fontSize: 9, color: C.t3 }}>
                        {cl.count} trades · ${Math.round(cl.totalNotional).toLocaleString()} notional
                      </div>
                    </div>
                    <div style={{ textAlign: "right" }}>
                      <div
                        style={{
                          fontSize: 11,
                          fontWeight: 700,
                          color: LEVEL_COLORS[cl.avgToxScore > 0.5 ? "HIGH" : cl.avgToxScore > 0.3 ? "MEDIUM" : "LOW"],
                        }}
                      >
                        {(cl.avgToxScore * 100).toFixed(0)}%
                      </div>
                      <div style={{ fontSize: 8, color: C.t4 }}>avg tox</div>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          </Panel>
        )}
      </div>

      <style>{`
        @import url('https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;600;700;800&family=Outfit:wght@400;500;600;700;800&display=swap');
        @keyframes pulse { 0%,100% { opacity:1 } 50% { opacity:.3 } }
        ::-webkit-scrollbar { width:4px; height:4px }
        ::-webkit-scrollbar-track { background:${C.bg} }
        ::-webkit-scrollbar-thumb { background:${C.bd}; border-radius:2px }
        * { box-sizing:border-box }
        input[type=range] { -webkit-appearance:none; background:${C.bd}; height:3px; border-radius:2px; outline:none }
        input[type=range]::-webkit-slider-thumb { -webkit-appearance:none; width:12px; height:12px; border-radius:50%; cursor:pointer; background:${C.am} }
      `}</style>
    </div>
  );
}