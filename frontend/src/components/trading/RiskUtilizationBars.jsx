import { formatSigned, formatUsd } from "../../quant";

function pctAbs(value, limit) {
  if (!Number.isFinite(value) || !Number.isFinite(limit) || limit <= 0) return 0;
  return Math.min(1, Math.abs(value) / limit);
}

function formatRowValue(key, value) {
  if (key === "Gross") return formatUsd(value || 0, 0);
  if (key === "DailyLoss") return formatUsd(value || 0, 0);
  return formatSigned(value || 0, 3);
}

function colorClass(value) {
  if (value >= 0.9) return "util-fill util-danger";
  if (value >= 0.7) return "util-fill util-warn";
  return "util-fill util-good";
}

function UtilRow({ label, value, limit }) {
  const ratio = pctAbs(value, limit);
  return (
    <div className="util-row">
      <div className="util-head">
        <span>{label}</span>
        <span>
          {formatRowValue(label, value)} / {formatRowValue(label, limit)}
        </span>
      </div>
      <div className="util-track">
        <div className={colorClass(ratio)} style={{ width: `${ratio === 0 ? 0 : Math.max(5, ratio * 100)}%` }} />
      </div>
    </div>
  );
}

export default function RiskUtilizationBars({ risk, limits }) {
  const dailyLoss = Math.max(0, -(risk?.dailyPnl || 0));
  const rows = [
    { label: "Gross", value: risk?.grossNotional || 0, limit: limits?.maxGrossNotional || 0 },
    { label: "Delta", value: risk?.netDelta || 0, limit: limits?.maxNetDelta || 0 },
    { label: "Gamma", value: risk?.netGamma || 0, limit: limits?.maxNetGamma || 0 },
    { label: "Vega", value: risk?.netVega || 0, limit: limits?.maxNetVega || 0 },
    { label: "Theta", value: risk?.netTheta || 0, limit: limits?.maxNetThetaAbs || 0 },
    { label: "DailyLoss", value: dailyLoss, limit: limits?.maxDailyLoss || 0 },
  ];

  return (
    <div className="util-grid">
      {rows.map((row) => (
        <UtilRow key={row.label} label={row.label} value={row.value} limit={row.limit} />
      ))}
    </div>
  );
}
