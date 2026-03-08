import { useMemo, useState } from "react";
import { formatUsd } from "../../quant";

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function OrderLadder({ quote, onPlace, placing }) {
  const [qty, setQty] = useState("1");
  const [stepPct, setStepPct] = useState("0.5");

  const quantity = Math.max(0, Number(qty) || 0);
  const step = Math.max(0.05, Number(stepPct) || 0.5) / 100;

  const levels = useMemo(() => {
    if (!quote) return [];
    const mid = quote.mid || quote.mark || quote.ask || quote.bid || 0;
    const offsets = [-2, -1, -0.5, 0, 0.5, 1, 2];
    return offsets.map((factor) => {
      const price = mid > 0 ? mid * (1 + factor * step) : 0;
      return { factor, price };
    });
  }, [quote, step]);

  if (!quote) {
    return <div className="status-chip">Select an option to enable the order ladder.</div>;
  }

  return (
    <div className="ladder-wrap">
      <div className="inline-controls" style={{ marginBottom: 8 }}>
        <input
          className="input"
          style={{ width: 110 }}
          type="number"
          min="0.01"
          step="0.01"
          value={qty}
          onChange={(event) => setQty(event.target.value)}
          placeholder="qty"
        />
        <input
          className="input"
          style={{ width: 110 }}
          type="number"
          min="0.05"
          step="0.05"
          value={stepPct}
          onChange={(event) => setStepPct(event.target.value)}
          placeholder="step %"
        />
        <span className="status-chip">Ref: {formatUsd(quote.mid || quote.mark || 0, 4)}</span>
      </div>

      <table className="ladder-table">
        <thead>
          <tr>
            <th>Offset</th>
            <th>Limit Price</th>
            <th>Action</th>
          </tr>
        </thead>
        <tbody>
          {levels.map((row) => (
            <tr key={row.factor}>
              <td>{row.factor > 0 ? `+${row.factor}` : row.factor}%</td>
              <td>{fmt(row.price, 4)}</td>
              <td className="action-cell">
                <button
                  className="btn btn-secondary"
                  disabled={placing || quantity <= 0 || !Number.isFinite(row.price) || row.price <= 0}
                  onClick={() =>
                    onPlace({
                      symbol: quote.symbol,
                      side: "Buy",
                      quantity,
                      type: "Limit",
                      limitPrice: row.price,
                    })
                  }
                >
                  Buy
                </button>
                <button
                  className="btn btn-ghost"
                  disabled={placing || quantity <= 0 || !Number.isFinite(row.price) || row.price <= 0}
                  onClick={() =>
                    onPlace({
                      symbol: quote.symbol,
                      side: "Sell",
                      quantity,
                      type: "Limit",
                      limitPrice: row.price,
                    })
                  }
                >
                  Sell
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <div className="inline-controls" style={{ marginTop: 8 }}>
        <button
          className="btn btn-primary"
          disabled={placing || quantity <= 0}
          onClick={() => onPlace({ symbol: quote.symbol, side: "Buy", quantity, type: "Market" })}
        >
          Market Buy
        </button>
        <button
          className="btn btn-ghost"
          disabled={placing || quantity <= 0}
          onClick={() => onPlace({ symbol: quote.symbol, side: "Sell", quantity, type: "Market" })}
        >
          Market Sell
        </button>
      </div>
    </div>
  );
}
