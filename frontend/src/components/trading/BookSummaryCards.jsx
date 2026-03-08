import { formatSigned, formatUsd } from "../../quant";

function Card({ label, value, tone = "" }) {
  return (
    <div className={`summary-card ${tone}`.trim()}>
      <div className="summary-label">{label}</div>
      <div className="summary-value">{value}</div>
    </div>
  );
}

export default function BookSummaryCards({ book }) {
  const positions = book?.positions || [];
  const recentOrders = book?.recentOrders || [];
  const risk = book?.risk;

  return (
    <div className="summary-grid">
      <Card label="Open Positions" value={positions.length} />
      <Card label="Recent Orders" value={recentOrders.length} />
      <Card label="Gross Notional" value={formatUsd(risk?.grossNotional || 0, 0)} />
      <Card label="Net Delta" value={formatSigned(risk?.netDelta || 0, 3)} />
      <Card
        label="Unrealized PnL"
        value={formatUsd(risk?.unrealizedPnl || 0, 2)}
        tone={(risk?.unrealizedPnl || 0) >= 0 ? "tone-good" : "tone-bad"}
      />
      <Card
        label="Realized PnL"
        value={formatUsd(risk?.realizedPnl || 0, 2)}
        tone={(risk?.realizedPnl || 0) >= 0 ? "tone-good" : "tone-bad"}
      />
    </div>
  );
}
