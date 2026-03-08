import { formatPct } from "../../quant";

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function ArbitrageScannerPanel({ scan, loading, onSelectSymbol }) {
  if (loading) return <div className="status-chip">Scanning no-arbitrage constraints...</div>;
  if (!scan) return <div className="status-chip">No scan available yet.</div>;

  return (
    <div className="arb-wrap">
      <div className="inline-controls">
        <span className="status-chip">Anomalies: {scan.count || 0}</span>
        <span className="status-chip">Asset: {scan.asset}</span>
      </div>
      <div className="chain-table-wrap" style={{ maxHeight: 340 }}>
        <table className="chain-table">
          <thead>
            <tr>
              <th>Type</th>
              <th>Expiry</th>
              <th>Right</th>
              <th>Metric</th>
              <th>Threshold</th>
              <th>Severity</th>
              <th>Liq</th>
              <th>Cost</th>
              <th>Tradability</th>
              <th>Symbols</th>
            </tr>
          </thead>
          <tbody>
            {(scan.anomalies || []).map((a, idx) => (
              <tr key={`${a.type}-${a.symbolA}-${idx}`}>
                <td>{a.type}</td>
                <td>{String(a.expiry).slice(0, 10)}</td>
                <td>{a.right}</td>
                <td>{formatPct(a.metric || 0, 2)}</td>
                <td>{formatPct(a.threshold || 0, 2)}</td>
                <td>{fmt(a.severityScore || 0, 1)}</td>
                <td>{fmt(a.liquidityScore || 0, 1)}</td>
                <td>{formatPct(a.estimatedCostPct || 0, 2)}</td>
                <td>{fmt(a.tradeabilityScore || 0, 1)}</td>
                <td>
                  <button className="link-symbol" onClick={() => onSelectSymbol?.(a.symbolA)}>{a.symbolA}</button>
                  {a.symbolB ? (
                    <>
                      {" / "}
                      <button className="link-symbol" onClick={() => onSelectSymbol?.(a.symbolB)}>{a.symbolB}</button>
                    </>
                  ) : null}
                  {a.symbolC ? (
                    <>
                      {" / "}
                      <button className="link-symbol" onClick={() => onSelectSymbol?.(a.symbolC)}>{a.symbolC}</button>
                    </>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
