import { formatUsd } from "../../quant";

export default function OrderTicket({
  order,
  onChange,
  onSubmit,
  placing,
  selectedAsset,
  previewQuote,
}) {
  const showLimit = order.type === "Limit";

  return (
    <div className="order-ticket">
      <div className="ticket-row">
        <label>Symbol</label>
        <input
          className="input"
          value={order.symbol}
          onChange={(e) => onChange({ symbol: e.target.value })}
          placeholder={`${selectedAsset}-DDMMMYY-STRIKE-C/P-USDT`}
        />
      </div>
      <div className="ticket-grid">
        <div>
          <label>Side</label>
          <select className="select" value={order.side} onChange={(e) => onChange({ side: e.target.value })}>
            <option value="Buy">Buy</option>
            <option value="Sell">Sell</option>
          </select>
        </div>
        <div>
          <label>Type</label>
          <select className="select" value={order.type} onChange={(e) => onChange({ type: e.target.value })}>
            <option value="Market">Market</option>
            <option value="Limit">Limit</option>
          </select>
        </div>
        <div>
          <label>Qty</label>
          <input
            className="input"
            type="number"
            min="0.01"
            step="0.01"
            value={order.quantity}
            onChange={(e) => onChange({ quantity: e.target.value })}
          />
        </div>
      </div>

      {showLimit && (
        <div className="ticket-row">
          <label>Limit Price</label>
          <input
            className="input"
            type="number"
            min="0"
            step="0.0001"
            value={order.limitPrice}
            onChange={(e) => onChange({ limitPrice: e.target.value })}
          />
        </div>
      )}

      <div className="ticket-info">
        <div>Bid/Ask: {previewQuote ? `${previewQuote.bid} / ${previewQuote.ask}` : "-"}</div>
        <div>Mark: {previewQuote ? formatUsd(previewQuote.mark || previewQuote.mid || 0, 4) : "-"}</div>
      </div>

      <button className="btn btn-secondary" disabled={placing} onClick={onSubmit}>
        {placing ? "Placing..." : "Place Paper Order"}
      </button>
    </div>
  );
}

