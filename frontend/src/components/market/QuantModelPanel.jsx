import { formatPct, formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function QuantModelPanel({ snapshot, loading }) {
  if (loading) {
    return <div className="status-chip">Computing model snapshot...</div>;
  }

  if (!snapshot) {
    return <div className="status-chip">Select an option to run model snapshot.</div>;
  }

  return (
    <div className="quant-model-wrap">
      <div className="inline-controls">
        <span className="status-chip">Signal: {snapshot.signal}</span>
        <span className="status-chip">Confidence: {fmt(snapshot.confidenceScore, 1)}</span>
        <span className="status-chip">Prob ITM: {formatPct(snapshot.probItm || 0, 1)}</span>
      </div>

      <div className="summary-grid" style={{ marginTop: 8 }}>
        <div className="summary-card">
          <div className="summary-label">Market Mid</div>
          <div className="summary-value">{formatUsd(snapshot.mid || 0, 4)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Fair Composite</div>
          <div className="summary-value">{formatUsd(snapshot.fairComposite || 0, 4)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Edge vs Mid</div>
          <div className="summary-value">{formatSigned((snapshot.edgeVsMidPct || 0) * 100, 2)}%</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Expected Move</div>
          <div className="summary-value">{formatUsd(snapshot.impliedMoveAbs || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Model Dispersion</div>
          <div className="summary-value">{formatPct(snapshot.modelDispersionPct || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Liquidity Score</div>
          <div className="summary-value">{fmt(snapshot.liquidityScore || 0, 2)}</div>
        </div>
      </div>

      <div className="chain-table-wrap" style={{ marginTop: 8, maxHeight: 220 }}>
        <table className="chain-table">
          <thead>
            <tr>
              <th>Model</th>
              <th>Theo Price</th>
              <th>Delta</th>
              <th>Gamma</th>
              <th>Vega</th>
              <th>Theta</th>
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>Black-Scholes</td>
              <td>{formatUsd(snapshot.fairBs || 0, 4)}</td>
              <td>{formatSigned(snapshot.greeks?.delta || 0, 4)}</td>
              <td>{formatSigned(snapshot.greeks?.gamma || 0, 6)}</td>
              <td>{formatSigned(snapshot.greeks?.vega || 0, 4)}</td>
              <td>{formatSigned(snapshot.greeks?.theta || 0, 4)}</td>
            </tr>
            <tr>
              <td>Heston</td>
              <td>{formatUsd(snapshot.fairHeston || 0, 4)}</td>
              <td>-</td>
              <td>-</td>
              <td>-</td>
              <td>-</td>
            </tr>
            <tr>
              <td>SABR</td>
              <td>{formatUsd(snapshot.fairSabr || 0, 4)}</td>
              <td>-</td>
              <td>-</td>
              <td>-</td>
              <td>-</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}
