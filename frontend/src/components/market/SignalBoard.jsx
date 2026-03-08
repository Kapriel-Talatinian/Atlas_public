import { formatPct, formatSigned } from "../../quant";

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

function SignalTable({ title, rows, onSelect }) {
  return (
    <div>
      <div className="section-subhead">{title}</div>
      <div className="chain-table-wrap" style={{ maxHeight: 220 }}>
        <table className="chain-table">
          <thead>
            <tr>
              <th>Symbol</th>
              <th>Type</th>
              <th>Edge</th>
              <th>ITM</th>
              <th>Conf</th>
              <th>Signal</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <tr key={row.symbol} onClick={() => onSelect(row.symbol)}>
                <td>{row.symbol}</td>
                <td>{row.right}</td>
                <td>{formatSigned((row.edgePct || 0) * 100, 2)}%</td>
                <td>{formatPct(row.probItm || 0, 1)}</td>
                <td>{fmt(row.confidenceScore || 0, 1)}</td>
                <td>{row.signal}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export default function SignalBoard({ board, loading, onSelect }) {
  if (loading) {
    return <div className="status-chip">Computing quant signal board...</div>;
  }

  if (!board) {
    return <div className="status-chip">No signal board yet.</div>;
  }

  return (
    <div className="signal-board-wrap">
      <div className="inline-controls">
        <span className="status-chip">Spot: {fmt(board.spot || 0, 2)}</span>
        <span className="status-chip">Asset: {board.asset}</span>
      </div>
      <SignalTable title="Top Long Edges" rows={board.topLongEdges || []} onSelect={onSelect} />
      <SignalTable title="Top Short Edges" rows={board.topShortEdges || []} onSelect={onSelect} />
    </div>
  );
}
