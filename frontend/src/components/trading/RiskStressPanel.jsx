import { formatSigned, formatUsd } from "../../quant";

function fmtPct(value) {
  if (!Number.isFinite(value)) return "-";
  return `${(value * 100).toFixed(1)}%`;
}

export default function RiskStressPanel({ preview, stress, loadingPreview, loadingStress, onRunStress }) {
  return (
    <div className="risk-stress-wrap">
      <div className="section-subhead">Pre-Trade Preview</div>
      {loadingPreview ? (
        <div className="status-chip">Calculating preview...</div>
      ) : preview ? (
        <div className="summary-grid">
          <div className="summary-card">
            <div className="summary-label">Status</div>
            <div className="summary-value">{preview.accepted ? "Accepted" : preview.rejectReason || "Rejected"}</div>
          </div>
          <div className="summary-card">
            <div className="summary-label">Projected Gross</div>
            <div className="summary-value">{formatUsd(preview.projectedRisk?.grossNotional || 0, 0)}</div>
          </div>
          <div className="summary-card">
            <div className="summary-label">Projected Delta</div>
            <div className="summary-value">{formatSigned(preview.projectedRisk?.netDelta || 0, 3)}</div>
          </div>
          <div className="summary-card">
            <div className="summary-label">Est. Fees</div>
            <div className="summary-value">{formatUsd(preview.estimatedFees || 0, 4)}</div>
          </div>
          <div className="summary-card">
            <div className="summary-label">Initial Margin</div>
            <div className="summary-value">{formatUsd(preview.estimatedInitialMargin || 0, 0)}</div>
          </div>
          <div className="summary-card">
            <div className="summary-label">Maintenance Margin</div>
            <div className="summary-value">{formatUsd(preview.estimatedMaintenanceMargin || 0, 0)}</div>
          </div>
        </div>
      ) : (
        <div className="status-chip">No preview yet.</div>
      )}

      <div className="inline-controls" style={{ marginTop: 10 }}>
        <div className="section-subhead" style={{ marginBottom: 0 }}>Stress Test</div>
        <button className="btn btn-secondary" onClick={onRunStress} disabled={loadingStress}>
          {loadingStress ? "Running stress..." : "Run Stress Scenarios"}
        </button>
      </div>

      {stress?.scenarios?.length > 0 && (
        <div className="chain-table-wrap" style={{ marginTop: 8, maxHeight: 260 }}>
          <table className="chain-table">
            <thead>
              <tr>
                <th>Scenario</th>
                <th>Spot Shock</th>
                <th>IV Shock</th>
                <th>Est PnL</th>
                <th>Net Delta</th>
                <th>Net Vega</th>
              </tr>
            </thead>
            <tbody>
              {stress.scenarios.map((scenario) => (
                <tr key={scenario.name}>
                  <td>{scenario.name}</td>
                  <td>{fmtPct(scenario.underlyingShockPct)}</td>
                  <td>{fmtPct(scenario.ivShockPct)}</td>
                  <td>{formatUsd(scenario.estimatedPnl, 2)}</td>
                  <td>{formatSigned(scenario.estimatedNetDelta, 3)}</td>
                  <td>{formatSigned(scenario.estimatedNetVega, 2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {stress && (
        <div className="inline-controls" style={{ marginTop: 8 }}>
          <span className="status-chip">Worst: {stress.worstScenarioName} ({formatUsd(stress.worstScenarioPnl, 2)})</span>
          <span className="status-chip">Best: {stress.bestScenarioName} ({formatUsd(stress.bestScenarioPnl, 2)})</span>
        </div>
      )}
    </div>
  );
}
