import { formatPct, formatSigned } from "../../quant";

function normalizeRight(right) {
  if (right === "Put" || right === "put" || right === 1) return "Put";
  return "Call";
}

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function OptionPositionGrid({
  quotes,
  selectedSymbol,
  onSelect,
  onQuickOrder,
  onAddLeg,
}) {
  return (
    <div className="position-grid">
      {quotes.map((quote) => {
        const right = normalizeRight(quote.right);
        const selected = quote.symbol === selectedSymbol;
        return (
          <div
            key={quote.symbol}
            className={`option-card ${selected ? "active" : ""}`.trim()}
            onClick={() => onSelect(quote)}
            role="button"
            tabIndex={0}
            onKeyDown={(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                onSelect(quote);
              }
            }}
          >
            <div className="option-card-head">
              <span className="option-symbol">{quote.symbol}</span>
              <span className={`pill ${right === "Call" ? "pill-call" : "pill-put"}`}>{right}</span>
            </div>

            <div className="option-card-metrics">
              <div>Strike: {fmt(quote.strike, 0)}</div>
              <div>Bid: {fmt(quote.bid, 4)}</div>
              <div>Ask: {fmt(quote.ask, 4)}</div>
              <div>IV: {formatPct(quote.markIv || 0, 2)}</div>
              <div>Delta: {formatSigned(quote.delta || 0, 3)}</div>
              <div>OI: {fmt(quote.openInterest || 0, 0)}</div>
            </div>

            <div className="option-card-actions">
              <button
                className="btn btn-secondary"
                onClick={(event) => {
                  event.stopPropagation();
                  onQuickOrder(quote, "Buy");
                }}
              >
                Buy
              </button>
              <button
                className="btn btn-ghost"
                onClick={(event) => {
                  event.stopPropagation();
                  onQuickOrder(quote, "Sell");
                }}
              >
                Sell
              </button>
              <button
                className="btn btn-ghost"
                onClick={(event) => {
                  event.stopPropagation();
                  onAddLeg(quote, "Buy");
                }}
              >
                +Leg
              </button>
            </div>
          </div>
        );
      })}
    </div>
  );
}
