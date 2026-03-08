import { formatPct } from "../../quant";

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function ModelCalibrationPanel({ calibration, loading }) {
  if (loading) return <div className="status-chip">Calibrating model stack...</div>;
  if (!calibration) return <div className="status-chip">No calibration snapshot yet.</div>;

  return (
    <div className="calibration-wrap">
      <div className="inline-controls">
        <span className="status-chip">Asset: {calibration.asset}</span>
        <span className="status-chip">Confidence: {fmt(calibration.confidenceScore, 1)}</span>
      </div>

      <div className="summary-grid" style={{ marginTop: 8 }}>
        <div className="summary-card">
          <div className="summary-label">ATM IV 30D</div>
          <div className="summary-value">{formatPct(calibration.atmIv30D || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Skew 25D</div>
          <div className="summary-value">{formatPct(calibration.skew25D || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Term Slope 30 to 90</div>
          <div className="summary-value">{formatPct(calibration.termSlope30To90 || 0, 2)}</div>
        </div>
      </div>

      <div className="chain-table-wrap" style={{ marginTop: 8, maxHeight: 220 }}>
        <table className="chain-table">
          <thead>
            <tr>
              <th>Model</th>
              <th>MAE</th>
              <th>RMSE</th>
              <th>Samples</th>
              <th>Parameters</th>
            </tr>
          </thead>
          <tbody>
            {(calibration.fitMetrics || []).map((row) => {
              const model = String(row.model || "");
              let params = "-";
              if (model === "Heston") {
                params = `k:${fmt(calibration.hestonKappa, 2)} θ:${fmt(calibration.hestonTheta, 2)} ξ:${fmt(calibration.hestonXi, 2)} ρ:${fmt(calibration.hestonRho, 2)}`;
              } else if (model === "SABR") {
                params = `α:${fmt(calibration.sabrAlpha, 2)} β:${fmt(calibration.sabrBeta, 2)} ρ:${fmt(calibration.sabrRho, 2)} ν:${fmt(calibration.sabrNu, 2)}`;
              }
              return (
                <tr key={model}>
                  <td>{model}</td>
                  <td>{fmt(row.meanAbsErrorPct || 0, 2)}%</td>
                  <td>{fmt(row.rootMeanSquareErrorPct || 0, 2)}%</td>
                  <td>{fmt(row.sampleCount || 0, 0)}</td>
                  <td>{params}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}
