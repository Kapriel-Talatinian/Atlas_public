import { formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function StrategyRecommendationPanel({
  board,
  loading,
  riskProfile,
  onRiskProfileChange,
  onLoad,
}) {
  return (
    <div className="reco-wrap">
      <div className="inline-controls">
        <span className="section-subhead" style={{ marginBottom: 0 }}>Risk Profile</span>
        <select className="select" value={riskProfile} onChange={(event) => onRiskProfileChange(event.target.value)}>
          <option value="conservative">Conservative</option>
          <option value="balanced">Balanced</option>
          <option value="aggressive">Aggressive</option>
        </select>
      </div>

      {loading ? (
        <div className="status-chip">Building strategy recommendations...</div>
      ) : !board?.recommendations?.length ? (
        <div className="status-chip">No recommendation available for this profile.</div>
      ) : (
        <div className="reco-list">
          {board.recommendations.map((rec) => (
            <div className="reco-card" key={rec.name}>
              <div className="reco-top">
                <h4>{rec.name}</h4>
                <button className="btn btn-secondary" onClick={() => onLoad(rec.analysis)}>
                  Load
                </button>
              </div>
              <div className="reco-metrics">
                <div>Score: {fmt(rec.score, 1)}</div>
                <div>Edge: {formatSigned(rec.edgeScorePct || 0, 2)}%</div>
                <div>Conf: {fmt(rec.confidenceScore, 1)}</div>
                <div>Fit: {fmt(rec.regimeFitScore, 1)}</div>
                <div>Risk: {rec.riskLabel}</div>
                <div>Premium: {formatUsd(rec.analysis?.netPremium || 0, 2)}</div>
                <div>MaxP: {formatUsd(rec.analysis?.maxProfit || 0, 2)}</div>
                <div>MaxL: {formatUsd(rec.analysis?.maxLoss || 0, 2)}</div>
                <div>R/R: {fmt(rec.analysis?.rewardRiskRatio || 0, 2)}</div>
                <div>PoP: {fmt((rec.analysis?.probabilityOfProfitApprox || 0) * 100, 1)}%</div>
                <div>EV: {formatUsd(rec.analysis?.expectedValue || 0, 2)}</div>
              </div>
              <div className="subtle" style={{ marginTop: 6 }}>{rec.thesis}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
