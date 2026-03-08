import { formatPct, formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function VolRegimePanel({ regime, loading }) {
  if (loading) return <div className="status-chip">Computing regime...</div>;
  if (!regime) return <div className="status-chip">No regime snapshot yet.</div>;

  return (
    <div className="regime-wrap">
      <div className="inline-controls">
        <span className="status-chip">Regime: {regime.regime}</span>
        <span className="status-chip">Signal: {regime.signal}</span>
        <span className="status-chip">Confidence: {fmt(regime.confidenceScore, 1)}</span>
      </div>

      <div className="summary-grid" style={{ marginTop: 8 }}>
        <div className="summary-card">
          <div className="summary-label">Spot</div>
          <div className="summary-value">{formatUsd(regime.spot || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">ATM IV Front</div>
          <div className="summary-value">{formatPct(regime.atmIvFront || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">ATM IV 30D</div>
          <div className="summary-value">{formatPct(regime.atmIv30D || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">ATM IV 90D</div>
          <div className="summary-value">{formatPct(regime.atmIv90D || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Term Slope 30 to 90</div>
          <div className="summary-value">{formatSigned((regime.termSlope30To90 || 0) * 100, 2)}%</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Skew 25D</div>
          <div className="summary-value">{formatSigned((regime.skew25D || 0) * 100, 2)}%</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">PCR OI</div>
          <div className="summary-value">{fmt(regime.putCallOpenInterestRatio || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Expected Move 7D</div>
          <div className="summary-value">{formatUsd(regime.expectedMove7DAbs || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Expected Move 30D</div>
          <div className="summary-value">{formatUsd(regime.expectedMove30DAbs || 0, 2)}</div>
        </div>
      </div>
    </div>
  );
}
