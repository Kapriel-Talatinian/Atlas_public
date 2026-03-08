import { formatPct, formatSigned, formatUsd } from "../../quant";

function normalizeRight(right) {
  if (right === "Put" || right === "put" || right === 1) return "Put";
  return "Call";
}

function fmt(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

export default function OptionDetailsPanel({ quote, onQuickOrder, onAddLeg, onUseSymbol }) {
  if (!quote) {
    return <div className="option-details-empty">Selectionne une option pour afficher tous les details.</div>;
  }

  const right = normalizeRight(quote.right);
  const spread = quote.ask > 0 && quote.bid > 0 ? quote.ask - quote.bid : 0;
  const mid = quote.mid || quote.mark || 0;
  const spreadPct = mid > 0 ? (spread / mid) * 100 : 0;

  return (
    <div className="option-details">
      <div className="option-details-head">
        <h4>{quote.symbol}</h4>
        <span className={`pill ${right === "Call" ? "pill-call" : "pill-put"}`}>{right}</span>
      </div>

      <div className="option-details-grid">
        <div>
          <span>Asset</span>
          <strong>{quote.asset}</strong>
        </div>
        <div>
          <span>Expiry</span>
          <strong>{String(quote.expiry).slice(0, 10)}</strong>
        </div>
        <div>
          <span>Strike</span>
          <strong>{fmt(quote.strike, 0)}</strong>
        </div>
        <div>
          <span>Underlying</span>
          <strong>{formatUsd(quote.underlyingPrice || 0, 2)}</strong>
        </div>
        <div>
          <span>Bid / Ask</span>
          <strong>{fmt(quote.bid, 4)} / {fmt(quote.ask, 4)}</strong>
        </div>
        <div>
          <span>Mid / Mark</span>
          <strong>{fmt(quote.mid, 4)} / {fmt(quote.mark, 4)}</strong>
        </div>
        <div>
          <span>Spread</span>
          <strong>{fmt(spread, 4)} ({fmt(spreadPct, 2)}%)</strong>
        </div>
        <div>
          <span>Mark IV</span>
          <strong>{formatPct(quote.markIv || 0, 2)}</strong>
        </div>
        <div>
          <span>Delta</span>
          <strong>{formatSigned(quote.delta || 0, 4)}</strong>
        </div>
        <div>
          <span>Gamma</span>
          <strong>{formatSigned(quote.gamma || 0, 6)}</strong>
        </div>
        <div>
          <span>Vega</span>
          <strong>{formatSigned(quote.vega || 0, 4)}</strong>
        </div>
        <div>
          <span>Theta</span>
          <strong>{formatSigned(quote.theta || 0, 4)}</strong>
        </div>
        <div>
          <span>Open Interest</span>
          <strong>{fmt(quote.openInterest || 0, 0)}</strong>
        </div>
        <div>
          <span>24h Volume</span>
          <strong>{fmt(quote.volume24h || 0, 0)}</strong>
        </div>
        <div>
          <span>24h Turnover</span>
          <strong>{formatUsd(quote.turnover24h || 0, 0)}</strong>
        </div>
        <div>
          <span>Timestamp</span>
          <strong>{new Date(quote.timestamp).toLocaleTimeString()}</strong>
        </div>
      </div>

      <div className="option-details-actions">
        <button className="btn btn-secondary" onClick={() => onQuickOrder(quote, "Buy")}>
          Buy ticket
        </button>
        <button className="btn btn-ghost" onClick={() => onQuickOrder(quote, "Sell")}>
          Sell ticket
        </button>
        <button className="btn btn-ghost" onClick={() => onAddLeg(quote, "Buy")}>+ Leg Buy</button>
        <button className="btn btn-ghost" onClick={() => onAddLeg(quote, "Sell")}>+ Leg Sell</button>
        <button className="btn btn-ghost" onClick={() => onUseSymbol(quote.symbol)}>Use symbol</button>
      </div>
    </div>
  );
}
