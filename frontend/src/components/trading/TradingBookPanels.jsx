import { formatSigned, formatUsd } from "../../quant";

function pretty(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export function RiskSummary({ risk, limits }) {
  return (
    <div className="risk-grid">
      <div className="greek-item">
        Gross Notional: {formatUsd(risk?.grossNotional || 0, 0)}
        <div className="subtle">limit {formatUsd(limits?.maxGrossNotional || 0, 0)}</div>
      </div>
      <div className="greek-item">
        Net Delta: {formatSigned(risk?.netDelta || 0, 3)}
        <div className="subtle">limit +/-{pretty(limits?.maxNetDelta || 0, 0)}</div>
      </div>
      <div className="greek-item">
        Net Gamma: {formatSigned(risk?.netGamma || 0, 5)}
        <div className="subtle">limit +/-{pretty(limits?.maxNetGamma || 0, 2)}</div>
      </div>
      <div className="greek-item">
        Net Vega: {formatSigned(risk?.netVega || 0, 3)}
        <div className="subtle">limit +/-{pretty(limits?.maxNetVega || 0, 0)}</div>
      </div>
      <div className="greek-item">
        Net Theta: {formatSigned(risk?.netTheta || 0, 2)}
        <div className="subtle">limit +/-{pretty(limits?.maxNetThetaAbs || 0, 0)}</div>
      </div>
      <div className="greek-item">
        Init Margin: {formatUsd(risk?.initialMargin || 0, 0)}
      </div>
      <div className="greek-item">
        Maint Margin: {formatUsd(risk?.maintenanceMargin || 0, 0)}
      </div>
      <div className="greek-item">
        Equity: {formatUsd(risk?.equity || 0, 0)}
      </div>
      <div className="greek-item">
        Available: {formatUsd(risk?.availableMargin || 0, 0)}
      </div>
      <div className="greek-item">
        Margin Ratio: {pretty(risk?.marginRatio || 0, 2)}x
      </div>
      <div className="greek-item">
        Concentration: {pretty((risk?.largestPositionConcentrationPct || 0) * 100, 2)}%
        <div className="subtle">limit {pretty((limits?.maxConcentrationPct || 0) * 100, 1)}%</div>
      </div>
      <div className="greek-item">
        Daily PnL: {formatUsd(risk?.dailyPnl || 0, 2)}
      </div>
      <div className="greek-item">
        Kill-Switch: {risk?.killSwitchActive ? "ON" : "OFF"}
      </div>
      <div className="greek-item">
        Unrealized: {formatUsd(risk?.unrealizedPnl || 0, 2)}
      </div>
      <div className="greek-item">
        Realized: {formatUsd(risk?.realizedPnl || 0, 2)}
      </div>
    </div>
  );
}

export function PositionsTable({ positions }) {
  return (
    <div className="chain-table-wrap">
      <table className="chain-table">
        <thead>
          <tr>
            <th>Symbol</th>
            <th>Qty</th>
            <th>Avg Px</th>
            <th>Mark</th>
            <th>Notional</th>
            <th>U-PnL</th>
            <th>R-PnL</th>
            <th>Delta</th>
            <th>Vega</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((p) => (
            <tr key={p.symbol}>
              <td>{p.symbol}</td>
              <td>{pretty(p.netQuantity, 3)}</td>
              <td>{pretty(p.avgEntryPrice, 4)}</td>
              <td>{pretty(p.markPrice, 4)}</td>
              <td>{formatUsd(p.notional, 0)}</td>
              <td>{formatUsd(p.unrealizedPnl, 2)}</td>
              <td>{formatUsd(p.realizedPnl, 2)}</td>
              <td>{formatSigned(p.greeks?.delta || 0, 3)}</td>
              <td>{formatSigned(p.greeks?.vega || 0, 3)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function OrdersTable({ orders }) {
  return (
    <div className="chain-table-wrap">
      <table className="chain-table">
        <thead>
          <tr>
            <th>Time</th>
            <th>OrderId</th>
            <th>Symbol</th>
            <th>Side</th>
            <th>Qty</th>
            <th>Type</th>
            <th>Status</th>
            <th>Req Px</th>
            <th>Fill Px</th>
            <th>Filled</th>
            <th>Remain</th>
            <th>Slip</th>
            <th>QoE</th>
            <th>Fees</th>
            <th>Replay</th>
            <th>Reason</th>
          </tr>
        </thead>
        <tbody>
          {orders.map((o) => (
            <tr key={o.orderId}>
              <td>{new Date(o.timestamp).toLocaleTimeString()}</td>
              <td>{o.orderId}</td>
              <td>{o.symbol}</td>
              <td>{o.side}</td>
              <td>{pretty(o.quantity, 3)}</td>
              <td>{o.type}</td>
              <td>
                <span className={`pill ${o.status === "Filled" ? "pill-call" : o.status === "Rejected" ? "pill-put" : ""}`}>
                  {o.status}
                </span>
              </td>
              <td>{pretty(o.requestedPrice, 4)}</td>
              <td>{pretty(o.fillPrice, 4)}</td>
              <td>{pretty(o.filledQuantity, 3)}</td>
              <td>{pretty(o.remainingQuantity, 3)}</td>
              <td>{pretty((o.slippagePct || 0) * 100, 2)}%</td>
              <td>{pretty(o.executionQualityScore, 1)}</td>
              <td>{pretty(o.fees, 4)}</td>
              <td>{o.idempotentReplay ? "yes" : "-"}</td>
              <td>{o.rejectReason || "-"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
