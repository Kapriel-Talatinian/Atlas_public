import { formatPct, formatSigned } from "../../quant";

const METRICS = [
  { key: "gamma", label: "Gamma" },
  { key: "vega", label: "Vega" },
  { key: "theta", label: "Theta" },
  { key: "delta", label: "Delta" },
];

function keyForCell(expiry, strike) {
  return `${expiry}|${strike}`;
}

function getMetricValue(cell, metric) {
  if (!cell) return NaN;
  switch (metric) {
    case "delta":
      return cell.deltaExposure;
    case "vega":
      return cell.vegaExposure;
    case "theta":
      return cell.thetaExposure;
    default:
      return cell.gammaExposure;
  }
}

function formatMetric(metric, value) {
  if (!Number.isFinite(value)) return "-";
  switch (metric) {
    case "gamma":
      return formatSigned(value, 2);
    case "vega":
      return formatSigned(value, 1);
    case "theta":
      return formatSigned(value, 1);
    case "delta":
      return formatSigned(value, 1);
    default:
      return formatSigned(value, 2);
  }
}

function valueTone(metricValue, maxAbs) {
  if (!Number.isFinite(metricValue) || !Number.isFinite(maxAbs) || maxAbs <= 0) {
    return "rgba(15, 23, 35, 0.06)";
  }

  const intensity = Math.min(1, Math.abs(metricValue) / maxAbs);
  if (metricValue >= 0) {
    return `rgba(13, 163, 127, ${0.1 + intensity * 0.45})`;
  }
  return `rgba(220, 38, 38, ${0.1 + intensity * 0.45})`;
}

export default function GreeksExposureHeatmapPanel({
  grid,
  loading,
  metric,
  onMetricChange,
  onFocusCell,
}) {
  if (loading) return <div className="status-chip">Computing exposure grid...</div>;
  if (!grid?.cells?.length) return <div className="status-chip">No exposure grid available yet.</div>;

  const expiries = [...new Set(grid.cells.map((cell) => String(cell.expiry).slice(0, 10)))].sort();
  const strikes = [...new Set(grid.cells.map((cell) => Number(cell.strike)))].sort((a, b) => a - b);

  const byKey = new Map(
    grid.cells.map((cell) => [keyForCell(String(cell.expiry).slice(0, 10), Number(cell.strike)), cell])
  );

  const maxAbs = Math.max(
    1,
    ...grid.cells.map((cell) => Math.abs(getMetricValue(cell, metric))).filter((value) => Number.isFinite(value))
  );

  return (
    <div className="exposure-wrap">
      <div className="inline-controls">
        <span className="section-subhead" style={{ marginBottom: 0 }}>Exposure Metric</span>
        <select className="select" value={metric} onChange={(event) => onMetricChange?.(event.target.value)}>
          {METRICS.map((item) => (
            <option value={item.key} key={item.key}>{item.label}</option>
          ))}
        </select>
        <span className="status-chip">Spot: {Number(grid.spot || 0).toFixed(2)}</span>
        <span className="status-chip">Cells: {grid.cells.length}</span>
      </div>

      <div className="exposure-table-wrap">
        <table className="chain-table exposure-table">
          <thead>
            <tr>
              <th>Strike</th>
              {expiries.map((expiry) => (
                <th key={expiry}>{expiry.slice(5)}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {strikes.map((strike) => (
              <tr key={strike}>
                <td>{strike.toFixed(0)}</td>
                {expiries.map((expiry) => {
                  const cell = byKey.get(keyForCell(expiry, strike)) || null;
                  const metricValue = getMetricValue(cell, metric);
                  return (
                    <td key={`${expiry}-${strike}`}>
                      <button
                        className="exposure-cell"
                        style={{ background: valueTone(metricValue, maxAbs) }}
                        onClick={() => cell && onFocusCell?.(cell)}
                        disabled={!cell}
                        title={cell ? `OI ${Number(cell.openInterest || 0).toFixed(0)} / Dist ${formatPct(cell.distanceToSpotPct || 0, 2)}` : "No quotes"}
                      >
                        {cell ? formatMetric(metric, metricValue) : "-"}
                      </button>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="section-subhead" style={{ marginTop: 2 }}>Pin Risk Hotspots</div>
      <div className="exposure-hotspots">
        {(grid.topHotspots || []).map((spot) => (
          <div className="summary-card" key={`${spot.expiry}-${spot.strike}`}>
            <div className="summary-label">{String(spot.expiry).slice(0, 10)} | K {Number(spot.strike).toFixed(0)}</div>
            <div className="summary-value">Pin: {Number(spot.pinRiskScore || 0).toFixed(1)}</div>
            <div className="subtle">
              OI {Number(spot.openInterest || 0).toFixed(0)} | Γ {formatSigned(spot.gammaExposure || 0, 2)} | ν {formatSigned(spot.vegaExposure || 0, 1)}
            </div>
            <button className="btn btn-secondary" style={{ marginTop: 6 }} onClick={() => onFocusCell?.(spot)}>
              Focus
            </button>
          </div>
        ))}
      </div>
    </div>
  );
}
