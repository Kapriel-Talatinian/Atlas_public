export function formatUsd(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    maximumFractionDigits: digits,
    minimumFractionDigits: digits,
  }).format(value);
}

export function formatCompactUsd(value) {
  if (!Number.isFinite(value)) return "-";
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    notation: "compact",
    maximumFractionDigits: 2,
  }).format(value);
}

export function formatSigned(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  const prefix = value > 0 ? "+" : "";
  return `${prefix}${value.toFixed(digits)}`;
}

export function formatPct(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return `${(value * 100).toFixed(digits)}%`;
}

export function formatRatio(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toFixed(digits);
}

export function buildSmileData(chain) {
  const grouped = new Map();
  chain.forEach((quote) => {
    if (!Number.isFinite(quote.markIv) || quote.markIv <= 0) return;
    const key = String(quote.strike);
    const current = grouped.get(key) || { strike: quote.strike, callIv: null, putIv: null };
    if (quote.right === "Call") current.callIv = quote.markIv;
    if (quote.right === "Put") current.putIv = quote.markIv;
    grouped.set(key, current);
  });

  return Array.from(grouped.values())
    .sort((a, b) => a.strike - b.strike)
    .map((row) => ({
      ...row,
      avgIv:
        Number.isFinite(row.callIv) && Number.isFinite(row.putIv)
          ? (row.callIv + row.putIv) / 2
          : Number.isFinite(row.callIv)
          ? row.callIv
          : row.putIv,
    }));
}

export function buildSurfaceSlice(surface, maxExpiries = 6) {
  const expiries = [...new Set(surface.map((p) => p.expiry))].slice(0, maxExpiries);
  return surface
    .filter((p) => expiries.includes(p.expiry))
    .filter((p) => p.markIv > 0 && p.moneyness > 0)
    .map((p) => ({
      dte: p.daysToExpiry,
      moneyness: p.moneyness,
      iv: p.markIv * 100,
      right: p.right,
    }));
}

export function formatTimeAgo(date) {
  if (!date) return "-";
  const diffSec = Math.max(0, Math.round((Date.now() - new Date(date).getTime()) / 1000));
  if (diffSec < 60) return `${diffSec}s`;
  const mins = Math.floor(diffSec / 60);
  if (mins < 60) return `${mins}m`;
  const hours = Math.floor(mins / 60);
  return `${hours}h`;
}

