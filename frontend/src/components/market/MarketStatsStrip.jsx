import { formatPct, formatTimeAgo, formatUsd } from "../../quant";

function StatItem({ label, value }) {
  return (
    <div className="mini-stat">
      <div className="mini-stat-label">{label}</div>
      <div className="mini-stat-value">{value}</div>
    </div>
  );
}

export default function MarketStatsStrip({
  overview,
  selectedExpiry,
  optionType,
  totalQuotes,
  filteredQuotes,
  lastUpdate,
}) {
  return (
    <div className="mini-stat-grid">
      <StatItem label="Spot" value={formatUsd(overview?.underlyingPrice || 0, 2)} />
      <StatItem label="ATM IV" value={formatPct(overview?.atmIv || 0, 2)} />
      <StatItem label="Expiry" value={selectedExpiry || "-"} />
      <StatItem label="Filter" value={optionType.toUpperCase()} />
      <StatItem label="Quotes" value={`${filteredQuotes}/${totalQuotes}`} />
      <StatItem label="Update" value={formatTimeAgo(lastUpdate)} />
    </div>
  );
}
