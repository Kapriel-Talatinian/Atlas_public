import { formatPct, formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits,
  });
}

function actionTone(bias) {
  if (String(bias).includes("Bullish")) return "status-good";
  if (String(bias).includes("Bearish")) return "status-bad";
  return "status-warn";
}

function auditTone(status) {
  if (String(status).toLowerCase().includes("validated")) return "status-good";
  if (String(status).toLowerCase().includes("need")) return "status-bad";
  return "status-warn";
}

function updateConfig(onChange, key, raw, type = "number") {
  if (type === "boolean") {
    onChange({ [key]: Boolean(raw) });
    return;
  }
  const numeric = Number(raw);
  onChange({ [key]: Number.isFinite(numeric) ? numeric : 0 });
}

export default function ExperimentalBotPanel({
  snapshot,
  asset,
  loading,
  configDraft,
  onConfigChange,
  onApplyConfig,
  applyingConfig,
  onRunCycle,
  runningCycle,
  onReset,
  resetting,
  onRefresh,
}) {
  if (loading && !snapshot) return <div className="status-chip">Loading experimental bot...</div>;
  if (!snapshot) return <div className="status-chip">No bot snapshot available.</div>;

  const signal = snapshot.signal;
  const stats = snapshot.stats || {};
  const config = configDraft || snapshot.config || {};
  const portfolio = snapshot.portfolio || {};
  const audit = snapshot.audit || {};

  return (
    <div className="bot-lab">
      <div className="inline-controls">
        <span className={`status-chip ${actionTone(signal?.bias)}`}>
          Signal: {signal?.bias || "Neutral"}
        </span>
        <span className="status-chip">Score: {formatSigned(signal?.score || 0, 1)}</span>
        <span className="status-chip">Conf: {fmt(signal?.confidence || 0, 1)}</span>
        <span className="status-chip">Asset: {asset}</span>
        <span className={`status-chip ${snapshot.running ? "status-good" : "status-warn"}`}>
          Bot: {snapshot.running ? "ON" : "OFF"}
        </span>
        <span className={`status-chip ${auditTone(audit?.status)}`}>
          Audit: {audit?.status || "N/A"}
        </span>
        <button className="btn btn-secondary" onClick={onRefresh}>
          Refresh
        </button>
        <button className="btn btn-secondary" onClick={onRunCycle} disabled={runningCycle}>
          {runningCycle ? "Running..." : "Run Cycle"}
        </button>
        <button className="btn btn-ghost" onClick={onReset} disabled={resetting}>
          {resetting ? "Resetting..." : "Reset Bot"}
        </button>
      </div>

      <div className="summary-card" style={{ marginTop: 10 }}>
        <div className="summary-label">Live Thesis</div>
        <div className="summary-value" style={{ fontSize: "0.84rem", fontFamily: "Space Grotesk, sans-serif" }}>
          {signal?.summary || "No live thesis."}
        </div>
        {(signal?.drivers || []).length > 0 && (
          <div className="inline-controls" style={{ marginTop: 8 }}>
            {signal.drivers.slice(0, 4).map((driver) => (
              <span key={driver} className="status-chip">{driver}</span>
            ))}
          </div>
        )}
      </div>

      <div className="summary-grid" style={{ marginTop: 10 }}>
        <div className="summary-card">
          <div className="summary-label">Net PnL</div>
          <div className="summary-value">{formatUsd(stats?.netPnl || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Equity</div>
          <div className="summary-value">{formatUsd(portfolio?.equityUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Available</div>
          <div className="summary-value">{formatUsd(portfolio?.availableCapitalUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Open Risk</div>
          <div className="summary-value">{formatUsd(portfolio?.openRiskNotionalUsd || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Drawdown</div>
          <div className="summary-value">
            {formatUsd(portfolio?.drawdownUsd || 0, 2)} ({formatPct(portfolio?.drawdownPct || 0, 1)})
          </div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Rolling WinRate(100)</div>
          <div className="summary-value">{formatPct(stats?.rollingWinRate100 || 0, 1)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Rolling PF(100)</div>
          <div className="summary-value">{fmt(stats?.rollingProfitFactor100 || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Rolling DD(100)</div>
          <div className="summary-value">{formatPct(stats?.rollingDrawdownPct100 || 0, 1)}</div>
        </div>
      </div>

      <div className="split" style={{ marginTop: 12 }}>
        <div className="panel">
          <div className="section-subhead">Bot Configuration</div>
          <div className="ticket-grid">
            <div>
              <label>Enabled</label>
              <select
                className="select"
                value={config.enabled ? "1" : "0"}
                onChange={(event) => updateConfig(onConfigChange, "enabled", event.target.value === "1", "boolean")}
              >
                <option value="1">On</option>
                <option value="0">Off</option>
              </select>
            </div>
            <div>
              <label>Auto Trade</label>
              <select
                className="select"
                value={config.autoTrade ? "1" : "0"}
                onChange={(event) => updateConfig(onConfigChange, "autoTrade", event.target.value === "1", "boolean")}
              >
                <option value="1">On</option>
                <option value="0">Off</option>
              </select>
            </div>
            <div>
              <label>Auto Tune</label>
              <select
                className="select"
                value={config.autoTune ? "1" : "0"}
                onChange={(event) => updateConfig(onConfigChange, "autoTune", event.target.value === "1", "boolean")}
              >
                <option value="1">On</option>
                <option value="0">Off</option>
              </select>
            </div>
            <div>
              <label>Interval (s)</label>
              <input
                className="input"
                type="number"
                min="5"
                max="120"
                value={config.evaluationIntervalSec}
                onChange={(event) => updateConfig(onConfigChange, "evaluationIntervalSec", event.target.value)}
              />
            </div>
            <div>
              <label>Base Position Size</label>
              <input
                className="input"
                type="number"
                min="0.05"
                step="0.05"
                value={config.basePositionSize}
                onChange={(event) => updateConfig(onConfigChange, "basePositionSize", event.target.value)}
              />
            </div>
            <div>
              <label>Start Capital (USD)</label>
              <input
                className="input"
                type="number"
                min="100"
                step="50"
                value={config.startingCapitalUsd}
                onChange={(event) => updateConfig(onConfigChange, "startingCapitalUsd", event.target.value)}
              />
            </div>
            <div>
              <label>Audit Target</label>
              <input
                className="input"
                type="number"
                min="25"
                max="3000"
                value={config.auditTargetTrades}
                onChange={(event) => updateConfig(onConfigChange, "auditTargetTrades", event.target.value)}
              />
            </div>
            <div>
              <label>Min Conf</label>
              <input
                className="input"
                type="number"
                min="35"
                max="95"
                value={config.minConfidence}
                onChange={(event) => updateConfig(onConfigChange, "minConfidence", event.target.value)}
              />
            </div>
            <div>
              <label>Max Open</label>
              <input
                className="input"
                type="number"
                min="1"
                max="24"
                value={config.maxOpenTrades}
                onChange={(event) => updateConfig(onConfigChange, "maxOpenTrades", event.target.value)}
              />
            </div>
            <div>
              <label>Stop Loss %</label>
              <input
                className="input"
                type="number"
                min="0.05"
                step="0.05"
                value={config.stopLossPct}
                onChange={(event) => updateConfig(onConfigChange, "stopLossPct", event.target.value)}
              />
            </div>
            <div>
              <label>Take Profit %</label>
              <input
                className="input"
                type="number"
                min="0.05"
                step="0.05"
                value={config.takeProfitPct}
                onChange={(event) => updateConfig(onConfigChange, "takeProfitPct", event.target.value)}
              />
            </div>
            <div>
              <label>Max Hold (h)</label>
              <input
                className="input"
                type="number"
                min="2"
                max="720"
                value={config.maxHoldingHours}
                onChange={(event) => updateConfig(onConfigChange, "maxHoldingHours", event.target.value)}
              />
            </div>
          </div>
          <div className="inline-controls" style={{ marginTop: 10 }}>
            <button className="btn btn-secondary" onClick={onApplyConfig} disabled={applyingConfig}>
              {applyingConfig ? "Applying..." : "Apply Config"}
            </button>
            <span className="status-chip">Base = taille nominale initiale par ticket</span>
            <span className="status-chip">Learning rate: {fmt(stats.learningRate || 0, 3)}</span>
            <span className="status-chip">Sharpe-like: {fmt(stats.sharpeLike || 0, 2)}</span>
          </div>
        </div>

        <div className="panel">
          <div className="section-subhead">Portfolio + Audit Control</div>
          <div className="summary-grid" style={{ marginTop: 6 }}>
            <div className="summary-card">
              <div className="summary-label">Start</div>
              <div className="summary-value">{formatUsd(portfolio?.startingCapitalUsd || 0, 2)}</div>
            </div>
            <div className="summary-card">
              <div className="summary-label">Peak Equity</div>
              <div className="summary-value">{formatUsd(portfolio?.peakEquityUsd || 0, 2)}</div>
            </div>
            <div className="summary-card">
              <div className="summary-label">Audited</div>
              <div className="summary-value">{fmt(audit?.auditedTrades || 0, 0)} / {fmt(audit?.targetTrades || 0, 0)}</div>
            </div>
            <div className="summary-card">
              <div className="summary-label">Completion</div>
              <div className="summary-value">{formatPct(audit?.completionPct || 0, 1)}</div>
            </div>
            <div className="summary-card">
              <div className="summary-label">Rolling Win</div>
              <div className="summary-value">{formatPct(audit?.rollingWinRate || 0, 1)}</div>
            </div>
            <div className="summary-card">
              <div className="summary-label">Rolling PF</div>
              <div className="summary-value">{fmt(audit?.rollingProfitFactor || 0, 2)}</div>
            </div>
          </div>

          <div className="section-subhead" style={{ marginTop: 10 }}>Model Weights</div>
          <div className="chain-table-wrap" style={{ maxHeight: 220 }}>
            <table className="chain-table">
              <thead>
                <tr>
                  <th>Feature</th>
                  <th>Weight</th>
                </tr>
              </thead>
              <tbody>
                {(snapshot.weights || []).slice(0, 12).map((weight) => (
                  <tr key={weight.name}>
                    <td>{weight.name}</td>
                    <td>{formatSigned(weight.weight, 3)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div className="split" style={{ marginTop: 12 }}>
        <div className="panel">
          <div className="section-subhead">Open Trades</div>
          <div className="chain-table-wrap" style={{ maxHeight: 260 }}>
            <table className="chain-table">
              <thead>
                <tr>
                  <th>Trade</th>
                  <th>Symbol</th>
                  <th>Qty</th>
                  <th>Entry</th>
                  <th>Mark</th>
                  <th>Unrealized</th>
                </tr>
              </thead>
              <tbody>
                {(snapshot.openTrades || []).map((trade) => (
                  <tr key={trade.tradeId}>
                    <td>{trade.tradeId}</td>
                    <td>{trade.symbol}</td>
                    <td>{fmt(trade.quantity, 2)}</td>
                    <td>{fmt(trade.entryPrice, 4)}</td>
                    <td>{fmt(trade.markPrice, 4)}</td>
                    <td>{formatUsd(trade.unrealizedPnl, 2)} ({formatSigned((trade.unrealizedPnlPct || 0) * 100, 1)}%)</td>
                  </tr>
                ))}
                {(snapshot.openTrades || []).length === 0 && (
                  <tr>
                    <td colSpan={6}>No open trades.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>

        <div className="panel">
          <div className="section-subhead">Recent Closed</div>
          <div className="chain-table-wrap" style={{ maxHeight: 260 }}>
            <table className="chain-table">
              <thead>
                <tr>
                  <th>Symbol</th>
                  <th>Entry</th>
                  <th>Exit</th>
                  <th>PnL</th>
                  <th>Rationale</th>
                </tr>
              </thead>
              <tbody>
                {(snapshot.recentClosedTrades || []).slice(0, 20).map((trade) => (
                  <tr key={trade.tradeId}>
                    <td>{trade.symbol}</td>
                    <td>{fmt(trade.entryPrice, 4)}</td>
                    <td>{fmt(trade.exitPrice, 4)}</td>
                    <td>{formatUsd(trade.realizedPnl || 0, 2)}</td>
                    <td>{trade.rationale}</td>
                  </tr>
                ))}
                {(snapshot.recentClosedTrades || []).length === 0 && (
                  <tr>
                    <td colSpan={5}>No closed trades yet.</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      </div>

      <div className="panel" style={{ marginTop: 12 }}>
        <div className="section-subhead">Real-Time Audit Trail</div>
        <div className="chain-table-wrap" style={{ maxHeight: 280 }}>
          <table className="chain-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Trade</th>
                <th>Symbol</th>
                <th>PnL</th>
                <th>Win</th>
                <th>Exit</th>
                <th>Rolling</th>
                <th>Comment</th>
              </tr>
            </thead>
            <tbody>
              {(snapshot.recentAudits || []).slice(0, 24).map((entry) => (
                <tr key={`${entry.tradeId}-${entry.timestamp}`}>
                  <td>{new Date(entry.timestamp).toLocaleTimeString()}</td>
                  <td>{entry.tradeId}</td>
                  <td>{entry.symbol}</td>
                  <td>{formatUsd(entry.realizedPnl || 0, 2)}</td>
                  <td>{entry.win ? "Yes" : "No"}</td>
                  <td>{entry.exitReason}</td>
                  <td>
                    WR {formatPct(entry.rollingWinRate || 0, 1)} | PF {fmt(entry.rollingProfitFactor || 0, 2)} | DD {formatPct(entry.rollingDrawdownPct || 0, 1)}
                  </td>
                  <td>{entry.learningComment}</td>
                </tr>
              ))}
              {(snapshot.recentAudits || []).length === 0 && (
                <tr>
                  <td colSpan={8}>No audit rows yet.</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="panel" style={{ marginTop: 12 }}>
        <div className="section-subhead">Decision Journal</div>
        <div className="decision-feed">
          {(snapshot.recentDecisions || []).slice(0, 24).map((decision) => (
            <div key={`${decision.timestamp}-${decision.action}`} className="decision-item">
              <div>
                <strong>{decision.action}</strong>
                <div className="subtle">{decision.reason}</div>
              </div>
              <div className="subtle">
                {new Date(decision.timestamp).toLocaleTimeString()} | {decision.bias} | {formatSigned(decision.score, 1)}
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
