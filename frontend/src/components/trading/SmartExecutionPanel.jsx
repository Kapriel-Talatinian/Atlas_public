import { useState } from "react";

export default function SmartExecutionPanel({ defaultSymbol, onRunTwap, running }) {
  const [side, setSide] = useState("Buy");
  const [totalQty, setTotalQty] = useState("5");
  const [slices, setSlices] = useState("5");
  const [intervalSec, setIntervalSec] = useState("2");

  return (
    <div className="smart-exec">
      <div className="ticket-grid">
        <div>
          <label>Side</label>
          <select className="select" value={side} onChange={(event) => setSide(event.target.value)}>
            <option value="Buy">Buy</option>
            <option value="Sell">Sell</option>
          </select>
        </div>
        <div>
          <label>Total Qty</label>
          <input
            className="input"
            type="number"
            min="0.01"
            step="0.01"
            value={totalQty}
            onChange={(event) => setTotalQty(event.target.value)}
          />
        </div>
        <div>
          <label>Slices</label>
          <input
            className="input"
            type="number"
            min="1"
            step="1"
            value={slices}
            onChange={(event) => setSlices(event.target.value)}
          />
        </div>
        <div>
          <label>Interval (s)</label>
          <input
            className="input"
            type="number"
            min="1"
            step="1"
            value={intervalSec}
            onChange={(event) => setIntervalSec(event.target.value)}
          />
        </div>
      </div>

      <div className="inline-controls" style={{ marginTop: 8 }}>
        <span className="status-chip">TWAP Symbol: {defaultSymbol || "-"}</span>
        <button
          className="btn btn-secondary"
          disabled={running}
          onClick={() =>
            onRunTwap({
              side,
              totalQty: Number(totalQty),
              slices: Number(slices),
              intervalSec: Number(intervalSec),
            })
          }
        >
          {running ? "Running TWAP..." : "Run TWAP"}
        </button>
      </div>
    </div>
  );
}
