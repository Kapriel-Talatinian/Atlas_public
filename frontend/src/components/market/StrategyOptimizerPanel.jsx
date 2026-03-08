import { formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

function updateTarget(onTargetsChange, key, raw) {
  const numeric = Number(raw);
  onTargetsChange({
    [key]: Number.isFinite(numeric) ? numeric : 0,
  });
}

export default function StrategyOptimizerPanel({
  board,
  loading,
  targets,
  onTargetsChange,
  onLoad,
}) {
  return (
    <div className="optimizer-wrap">
      <div className="inline-controls">
        <span className="section-subhead" style={{ marginBottom: 0 }}>Target Greeks</span>
        <input
          className="input"
          style={{ width: 90 }}
          type="number"
          step="0.1"
          value={targets.targetDelta}
          onChange={(event) => updateTarget(onTargetsChange, "targetDelta", event.target.value)}
          title="Target Delta"
        />
        <input
          className="input"
          style={{ width: 90 }}
          type="number"
          step="1"
          value={targets.targetVega}
          onChange={(event) => updateTarget(onTargetsChange, "targetVega", event.target.value)}
          title="Target Vega"
        />
        <input
          className="input"
          style={{ width: 90 }}
          type="number"
          step="0.5"
          value={targets.targetTheta}
          onChange={(event) => updateTarget(onTargetsChange, "targetTheta", event.target.value)}
          title="Target Theta"
        />
      </div>

      {loading ? (
        <div className="status-chip">Optimizing strategies vs target greeks...</div>
      ) : !board?.entries?.length ? (
        <div className="status-chip">No optimizer output for current market/profile.</div>
      ) : (
        <div className="reco-list">
          {board.entries.map((entry) => (
            <div className="reco-card" key={`${entry.name}-${entry.suggestedSizeMultiplier}`}>
              <div className="reco-top">
                <h4>{entry.name}</h4>
                <button className="btn btn-secondary" onClick={() => onLoad(entry.analysis)}>
                  Load
                </button>
              </div>
              <div className="reco-metrics">
                <div>Score: {fmt(entry.score, 1)}</div>
                <div>Distance: {fmt(entry.distanceScore, 1)}</div>
                <div>Size x: {fmt(entry.suggestedSizeMultiplier, 2)}</div>
                <div>Risk: {entry.riskLabel}</div>
                <div>Edge: {formatSigned(entry.edgeScorePct || 0, 2)}%</div>
                <div>Conf: {fmt(entry.confidenceScore, 1)}</div>
                <div>Delta*: {formatSigned(entry.projectedGreeks?.delta || 0, 3)}</div>
                <div>Vega*: {formatSigned(entry.projectedGreeks?.vega || 0, 3)}</div>
                <div>Theta*: {formatSigned(entry.projectedGreeks?.theta || 0, 3)}</div>
                <div>Premium: {formatUsd(entry.analysis?.netPremium || 0, 2)}</div>
              </div>
              <div className="subtle" style={{ marginTop: 6 }}>{entry.thesis}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
