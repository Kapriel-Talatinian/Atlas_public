import { formatPct, formatSigned } from "../../quant";

function prettyNumber(value, digits = 4) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

function normalizeRight(right) {
  if (right === "Put" || right === "put" || right === 1) return "Put";
  return "Call";
}

export default function ChainTable({ rows, onBuy, onSell, onUseSymbol, onSelect, selectedSymbol }) {
  return (
    <div className="chain-table-wrap">
      <table className="chain-table">
        <thead>
          <tr>
            <th>Symbol</th>
            <th>Strike</th>
            <th>Type</th>
            <th>Bid</th>
            <th>Ask</th>
            <th>Mid</th>
            <th>IV</th>
            <th>Delta</th>
            <th>OI</th>
            <th>24h Vol</th>
            <th>Actions</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((quote) => {
            const right = normalizeRight(quote.right);
            return (
              <tr
                key={quote.symbol}
                className={selectedSymbol === quote.symbol ? "chain-row-selected" : ""}
                onClick={() => onSelect?.(quote)}
              >
                <td>
                  <button
                    className="link-symbol"
                    onClick={(event) => {
                      event.stopPropagation();
                      onSelect?.(quote);
                    }}
                  >
                    {quote.symbol}
                  </button>
                </td>
                <td>{prettyNumber(quote.strike, 2)}</td>
                <td>
                  <span className={`pill ${right === "Call" ? "pill-call" : "pill-put"}`}>{right}</span>
                </td>
                <td>{prettyNumber(quote.bid, 4)}</td>
                <td>{prettyNumber(quote.ask, 4)}</td>
                <td>{prettyNumber(quote.mid, 4)}</td>
                <td>{formatPct(quote.markIv || 0, 2)}</td>
                <td>{formatSigned(quote.delta || 0, 3)}</td>
                <td>{prettyNumber(quote.openInterest || 0, 0)}</td>
                <td>{prettyNumber(quote.volume24h || 0, 0)}</td>
                <td className="action-cell">
                  <button
                    className="btn btn-primary"
                    onClick={(event) => {
                      event.stopPropagation();
                      onBuy(quote);
                    }}
                  >
                    Buy
                  </button>
                  <button
                    className="btn btn-ghost"
                    onClick={(event) => {
                      event.stopPropagation();
                      onSell(quote);
                    }}
                  >
                    Sell
                  </button>
                  <button
                    className="btn btn-ghost"
                    onClick={(event) => {
                      event.stopPropagation();
                      onUseSymbol(quote.symbol);
                    }}
                  >
                    Use
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
