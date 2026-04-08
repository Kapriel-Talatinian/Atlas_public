import { formatPct, formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits,
  });
}

function statusTone(value) {
  if (String(value).toLowerCase().includes("validated")) return "status-good";
  if (String(value).toLowerCase().includes("need")) return "status-bad";
  return "status-warn";
}

function tradeTone(value) {
  if (value > 0) return "tone-good";
  if (value < 0) return "tone-bad";
  return "";
}

function renderLegs(legs = []) {
  if (!legs.length) return "-";
  return legs
    .map((leg) => `${leg.direction} ${fmt(leg.quantity, 2)} ${leg.symbol}`)
    .join(" | ");
}

function OpenTradeCard({ trade }) {
  return (
    <div className={`reco-card ${tradeTone(trade.unrealizedPnl)}`.trim()}>
      <div className="reco-top">
        <h4>{trade.asset} • {trade.strategyTemplate}</h4>
        <span className="status-chip">{trade.bias || "Structured"}</span>
      </div>
      <div className="reco-metrics">
        <div>Trade: {trade.tradeId}</div>
        <div>Score: {fmt(trade.entryScore, 1)}</div>
        <div>Conf: {fmt(trade.confidence, 1)}</div>
        <div>Entry: {formatUsd(trade.entryNetPremium || 0, 2)}</div>
        <div>Liq now: {formatUsd(trade.currentLiquidationValue || 0, 2)}</div>
        <div>UPnL: {formatUsd(trade.unrealizedPnl || 0, 2)}</div>
        <div>UPnL/Risk: {formatPct(trade.unrealizedPnlPct || 0, 1)}</div>
        <div>MaxP: {formatUsd(trade.maxProfit || 0, 2)}</div>
        <div>MaxL: {formatUsd(trade.maxLoss || 0, 2)}</div>
        <div>R/R: {fmt(trade.rewardRiskRatio || 0, 2)}</div>
        <div>PoP: {formatPct(trade.probabilityOfProfitApprox || 0, 1)}</div>
        <div>EV: {formatUsd(trade.expectedValue || 0, 2)}</div>
        <div>Risk share: {formatPct(trade.riskBudgetPct || 0, 1)}</div>
      </div>
      <div className="subtle" style={{ marginTop: 8 }}>
        <strong>Why:</strong> {trade.rationale || trade.thesis || "No rationale."}
      </div>
      <div className="subtle" style={{ marginTop: 6 }}>
        <strong>Math:</strong> {trade.mathSummary || "-"}
      </div>
      <div className="subtle" style={{ marginTop: 6 }}>
        <strong>Legs:</strong> {renderLegs(trade.legs)}
      </div>
    </div>
  );
}

export default function ExperimentalBotPanel({
  snapshot,
  loading,
  onRefresh,
}) {
  if (loading && !snapshot) return <div className="status-chip">Loading autopilot portfolio...</div>;
  if (!snapshot) return <div className="status-chip">No autopilot snapshot available.</div>;

  const signal = snapshot.signal || {};
  const stats = snapshot.stats || {};
  const portfolio = snapshot.portfolio || {};
  const audit = snapshot.audit || {};
  const assets = snapshot.assets || ["BTC", "ETH", "SOL"];
  const allocations = portfolio.assetAllocations || [];
  const neuralSignals = snapshot.neuralSignals || [];

  return (
    <div className="bot-lab">
      <div className="inline-controls">
        <span className={`status-chip ${snapshot.running ? "status-good" : "status-warn"}`}>
          Autopilot: {snapshot.running ? "ON" : "OFF"}
        </span>
        <span className="status-chip">Universe: {assets.join(" / ")}</span>
        <span className="status-chip">Signal: {signal.bias || "Neutral"}</span>
        <span className="status-chip">Score: {formatSigned(signal.score || 0, 1)}</span>
        <span className="status-chip">Conf: {fmt(signal.confidence || 0, 1)}</span>
        <span className={`status-chip ${statusTone(audit.status)}`}>Audit: {audit.status || "N/A"}</span>
        <button className="btn btn-secondary" onClick={onRefresh}>Refresh</button>
      </div>

      <div className="summary-card" style={{ marginTop: 10 }}>
        <div className="summary-label">Autopilot Thesis</div>
        <div className="summary-value" style={{ fontSize: "0.84rem", fontFamily: "Space Grotesk, sans-serif" }}>
          {snapshot.engineSummary || signal.summary || "No portfolio thesis yet."}
        </div>
        {(signal.drivers || []).length > 0 && (
          <div className="inline-controls" style={{ marginTop: 8 }}>
            {signal.drivers.slice(0, 5).map((driver) => (
              <span key={driver} className="status-chip">{driver}</span>
            ))}
          </div>
        )}
      </div>

      <div className="summary-grid" style={{ marginTop: 10 }}>
        <div className="summary-card">
          <div className="summary-label">Net PnL</div>
          <div className="summary-value">{formatUsd(stats.netPnl || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Equity</div>
          <div className="summary-value">{formatUsd(portfolio.equityUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Available</div>
          <div className="summary-value">{formatUsd(portfolio.availableCapitalUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Gross Exposure</div>
          <div className="summary-value">{formatUsd(portfolio.grossExposureUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Open Risk</div>
          <div className="summary-value">{formatUsd(portfolio.openRiskNotionalUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Open Trades</div>
          <div className="summary-value">{fmt(portfolio.openTradesCount || stats.openTrades || 0, 0)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Win Rate</div>
          <div className="summary-value">{formatPct(stats.winRate || 0, 1)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Profit Factor</div>
          <div className="summary-value">{fmt(stats.profitFactor || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Drawdown</div>
          <div className="summary-value">
            {formatUsd(portfolio.drawdownUsd || 0, 2)} ({formatPct(portfolio.drawdownPct || 0, 1)})
          </div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Audit Progress</div>
          <div className="summary-value">
            {fmt(audit.auditedTrades || 0, 0)} / {fmt(audit.targetTrades || 0, 0)}
          </div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Rolling WR(100)</div>
          <div className="summary-value">{formatPct(stats.rollingWinRate100 || 0, 1)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Rolling PF(100)</div>
          <div className="summary-value">{fmt(stats.rollingProfitFactor100 || 0, 2)}</div>
        </div>
      </div>

      <div className="split" style={{ marginTop: 12 }}>
        <div className="panel">
          <div className="section-subhead">Portfolio Allocation</div>
          <div className="chain-table-wrap" style={{ maxHeight: 220 }}>
            <table className="chain-table">
              <thead>
                <tr>
                  <th>Asset</th>
                  <th>Open</th>
                  <th>Gross</th>
                  <th>Risk</th>
                  <th>Net PnL</th>
                  <th>Weight</th>
                </tr>
              </thead>
              <tbody>
                {allocations.map((row) => (
                  <tr key={row.asset}>
                    <td>{row.asset}</td>
                    <td>{fmt(row.openTrades || 0, 0)}</td>
                    <td>{formatUsd(row.grossExposureUsd || 0, 2)}</td>
                    <td>{formatUsd(row.openRiskUsd || 0, 2)}</td>
                    <td>{formatUsd(row.netPnlUsd || 0, 2)}</td>
                    <td>{formatPct(row.weightPct || 0, 1)}</td>
                  </tr>
                ))}
                {allocations.length === 0 && (
                  <tr>
                    <td colSpan={6}>No active or realized allocation yet.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>

        <div className="panel">
          <div className="section-subhead">Decision Journal</div>
          <div className="decision-feed">
            {(snapshot.recentDecisions || []).slice(0, 16).map((decision) => (
              <div key={`${decision.timestamp}-${decision.action}`} className="decision-item">
                <div>
                  <strong>{decision.action}</strong>
                  <div className="subtle">{decision.reason}</div>
                </div>
                <div className="subtle">
                  {new Date(decision.timestamp).toLocaleTimeString()} | {decision.bias} | {formatSigned(decision.score || 0, 1)}
                </div>
              </div>
            ))}
            {(snapshot.recentDecisions || []).length === 0 && (
              <div className="status-chip">No decisions yet.</div>
            )}
          </div>
        </div>
      </div>

      <div style={{ marginTop: 12 }}>
        <div className="section-subhead" style={{ marginBottom: 6 }}>Neural Trading Brain</div>
        {!neuralSignals.length ? (
          <div className="status-chip">No neural signal snapshot available.</div>
        ) : (
          <div className="reco-list">
            {neuralSignals.map((brain) => (
              <div className={`reco-card ${tradeTone(brain.score)}`.trim()} key={brain.asset}>
                <div className="reco-top">
                  <h4>{brain.asset} • {brain.recommendedStructure}</h4>
                  <span className="status-chip">{brain.bias} / {brain.volatilityBias}</span>
                </div>
                <div className="reco-metrics">
                  <div>Score: {formatSigned(brain.score || 0, 1)}</div>
                  <div>Conf: {fmt(brain.confidence || 0, 1)}</div>
                  <div>Tensor: {brain.sequenceLength} x {brain.channelCount}</div>
                  <div>Entry: {brain.entryPlan}</div>
                  <div>Exit: {brain.exitPlan}</div>
                  <div>Risk: {brain.riskPlan}</div>
                </div>
                <div className="subtle" style={{ marginTop: 8 }}>
                  <strong>Summary:</strong> {brain.summary}
                </div>
                <div className="subtle" style={{ marginTop: 6 }}>
                  <strong>Macro:</strong> {brain.macroReasoning}
                </div>
                <div className="subtle" style={{ marginTop: 6 }}>
                  <strong>Micro:</strong> {brain.microReasoning}
                </div>
                <div className="subtle" style={{ marginTop: 6 }}>
                  <strong>Math:</strong> {brain.mathReasoning}
                </div>
                {(brain.candidates || []).length > 0 && (
                  <div className="subtle" style={{ marginTop: 6 }}>
                    <strong>Top candidates:</strong>{" "}
                    {brain.candidates
                      .slice(0, 3)
                      .map((candidate) => `${candidate.name} (${formatSigned(candidate.score || 0, 1)})`)
                      .join(" | ")}
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>

      <div style={{ marginTop: 12 }}>
        <div className="section-subhead" style={{ marginBottom: 6 }}>Open Structures</div>
        {!snapshot.openTrades?.length ? (
          <div className="status-chip">No open structured option package right now.</div>
        ) : (
          <div className="reco-list">
            {snapshot.openTrades.map((trade) => (
              <OpenTradeCard key={trade.tradeId} trade={trade} />
            ))}
          </div>
        )}
      </div>

      <div className="panel" style={{ marginTop: 12 }}>
        <div className="section-subhead">Recent Closures</div>
        <div className="chain-table-wrap" style={{ maxHeight: 320 }}>
          <table className="chain-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Asset</th>
                <th>Strategy</th>
                <th>PnL</th>
                <th>Max Loss</th>
                <th>RR</th>
                <th>Why</th>
                <th>Math</th>
              </tr>
            </thead>
            <tbody>
              {(snapshot.recentClosedTrades || []).slice(0, 24).map((trade) => (
                <tr key={trade.tradeId}>
                  <td>{trade.exitTime ? new Date(trade.exitTime).toLocaleString() : "-"}</td>
                  <td>{trade.asset}</td>
                  <td>{trade.strategyTemplate}</td>
                  <td>{formatUsd(trade.realizedPnl || 0, 2)}</td>
                  <td>{formatUsd(trade.maxLoss || 0, 2)}</td>
                  <td>{fmt(trade.rewardRiskRatio || 0, 2)}</td>
                  <td>{trade.rationale || trade.thesis}</td>
                  <td>{trade.mathSummary}</td>
                </tr>
              ))}
              {(snapshot.recentClosedTrades || []).length === 0 && (
                <tr>
                  <td colSpan={8}>No closed structures yet.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="panel" style={{ marginTop: 12 }}>
        <div className="section-subhead">Audit Trail</div>
        <div className="chain-table-wrap" style={{ maxHeight: 320 }}>
          <table className="chain-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Asset</th>
                <th>Strategy</th>
                <th>PnL</th>
                <th>Exit</th>
                <th>Rolling</th>
                <th>Comment</th>
              </tr>
            </thead>
            <tbody>
              {(snapshot.recentAudits || []).slice(0, 24).map((entry) => (
                <tr key={`${entry.tradeId}-${entry.timestamp}`}>
                  <td>{new Date(entry.timestamp).toLocaleString()}</td>
                  <td>{entry.asset}</td>
                  <td>{entry.strategyTemplate}</td>
                  <td>{formatUsd(entry.realizedPnl || 0, 2)}</td>
                  <td>{entry.exitReason}</td>
                  <td>
                    WR {formatPct(entry.rollingWinRate || 0, 1)} | PF {fmt(entry.rollingProfitFactor || 0, 2)} | DD {formatPct(entry.rollingDrawdownPct || 0, 1)}
                  </td>
                  <td>{entry.learningComment}</td>
                </tr>
              ))}
              {(snapshot.recentAudits || []).length === 0 && (
                <tr>
                  <td colSpan={7}>No audit rows yet.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
