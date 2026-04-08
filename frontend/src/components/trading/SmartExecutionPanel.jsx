import { useState } from "react";

export default function SmartExecutionPanel({ defaultSymbol, onRunAlgo, running }) {
  const [side, setSide] = useState("Buy");
  const [style, setStyle] = useState("Twap");
  const [totalQty, setTotalQty] = useState("5");
  const [slices, setSlices] = useState("5");
  const [intervalSec, setIntervalSec] = useState("2");
  const [maxParticipationPct, setMaxParticipationPct] = useState("0.15");
  const [limitPrice, setLimitPrice] = useState("");

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
          <label>Algo Style</label>
          <select className="select" value={style} onChange={(event) => setStyle(event.target.value)}>
            <option value="Twap">TWAP</option>
            <option value="Vwap">VWAP</option>
            <option value="Pov">POV</option>
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
        <div>
          <label>Max Participation</label>
          <input
            className="input"
            type="number"
            min="0.01"
            max="1"
            step="0.01"
            value={maxParticipationPct}
            onChange={(event) => setMaxParticipationPct(event.target.value)}
          />
        </div>
        <div>
          <label>Limit (optional)</label>
          <input
            className="input"
            type="number"
            min="0"
            step="0.0001"
            value={limitPrice}
            onChange={(event) => setLimitPrice(event.target.value)}
          />
        </div>
      </div>

      <div className="inline-controls" style={{ marginTop: 8 }}>
        <span className="status-chip">Algo Symbol: {defaultSymbol || "-"}</span>
        <button
          className="btn btn-secondary"
          disabled={running}
          onClick={() =>
            onRunAlgo({
              side,
              style,
              totalQty: Number(totalQty),
              slices: Number(slices),
              intervalSec: Number(intervalSec),
              maxParticipationPct: Number(maxParticipationPct),
              limitPrice: Number(limitPrice),
            })
          }
        >
          {running ? "Running Algo..." : "Run Algo"}
        </button>
      </div>
    </div>
  );
}
