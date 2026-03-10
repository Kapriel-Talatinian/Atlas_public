import { formatSigned } from "../../quant";

function tone(bias) {
  if (String(bias).includes("Bullish")) return "status-good";
  if (String(bias).includes("Bearish")) return "status-bad";
  return "status-warn";
}

function tier(score) {
  if (!Number.isFinite(score)) return "Unknown";
  if (score >= 28) return "Strong Bullish";
  if (score >= 8) return "Bullish";
  if (score <= -28) return "Strong Bearish";
  if (score <= -8) return "Bearish";
  return "Neutral";
}

function action(score) {
  if (!Number.isFinite(score)) return "Action: wait for valid live feed.";
  if (score >= 28) return "Action: directional upside (long call spread / risk reversal).";
  if (score >= 8) return "Action: mild upside bias, avoid paying rich vol.";
  if (score <= -28) return "Action: directional downside hedge (long put spread).";
  if (score <= -8) return "Action: mild downside bias, keep convex protection.";
  return "Action: neutral tape, focus carry and relative value.";
}

function number(value, digits = 1) {
  if (!Number.isFinite(value)) return "-";
  return value.toFixed(digits);
}

export default function LiveBiasPanel({ snapshot, loading }) {
  if (loading && !snapshot) return <div className="status-chip">Computing live bias...</div>;
  if (!snapshot) return <div className="status-chip">No live bias yet.</div>;
  const score = snapshot.biasScore || 0;
  const signal = tier(score);

  return (
    <div className="macro-wrap">
      <div className="inline-controls">
        <span className={`status-chip ${tone(signal)}`}>Live Signal: {signal}</span>
        <span className="status-chip">Score: {formatSigned(score, 1)}</span>
        <span className="status-chip">Confidence: {number(snapshot.confidenceScore || 0, 1)}</span>
        <span className="status-chip">Horizon: {snapshot.horizonDays}D</span>
        {loading && <span className="status-chip">Refreshing...</span>}
      </div>

      <div className="summary-card">
        <div className="summary-label">Direct Action</div>
        <div className="summary-value" style={{ fontFamily: "Space Grotesk, sans-serif", fontSize: "0.8rem" }}>
          {action(score)}
        </div>
      </div>

      <div className="macro-drivers">
        {(snapshot.drivers || []).slice(0, 4).map((driver) => (
          <div className="macro-driver" key={driver.name}>
            <div>
              <div className="summary-label">{driver.name}</div>
              <div className="subtle">{driver.effect}</div>
            </div>
            <div className="summary-value">{formatSigned(driver.contributionScore || 0, 1)}</div>
          </div>
        ))}
      </div>
    </div>
  );
}
