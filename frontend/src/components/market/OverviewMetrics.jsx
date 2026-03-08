import { formatCompactUsd, formatPct, formatSigned, formatUsd } from "../../quant";

function metric(label, value) {
  return (
    <div className="metric-card" key={label}>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value}</div>
    </div>
  );
}

export default function OverviewMetrics({ overview }) {
  return (
    <div className="metric-grid">
      {metric("Underlying", formatUsd(overview?.underlyingPrice || 0, 2))}
      {metric("ATM IV", formatPct(overview?.atmIv || 0, 2))}
      {metric("25D RR", `${formatSigned((overview?.riskReversal25D || 0) * 100, 2)}%`)}
      {metric("Open Interest", (overview?.openInterest || 0).toLocaleString("en-US", { maximumFractionDigits: 0 }))}
      {metric("24h Turnover", formatCompactUsd(overview?.turnover24h || 0))}
      {metric("P/C OI Ratio", (overview?.putCallOpenInterestRatio || 0).toFixed(2))}
    </div>
  );
}

