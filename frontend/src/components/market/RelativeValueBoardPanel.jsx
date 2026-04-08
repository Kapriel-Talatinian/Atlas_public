import {
  CartesianGrid,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import { formatPct, formatSigned, formatUsd } from "../../quant";

function fmt(value, digits = 1) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    maximumFractionDigits: digits,
    minimumFractionDigits: 0,
  });
}

function toneClass(action) {
  if (action === "Buy vol" || action === "Exploit cheap vol" || action === "Express cheap vol with defined risk") return "tone-good";
  if (action === "Sell vol" || action === "Harvest rich vol with capped wings" || action === "Monetize rich vol with defined risk") return "tone-bad";
  return "";
}

function buildSurfaceSlice(surfaceNodes) {
  if (!Array.isArray(surfaceNodes) || surfaceNodes.length === 0) {
    return { expiryLabel: "", rows: [] };
  }

  const groups = new Map();
  surfaceNodes.forEach((node) => {
    const key = String(node.expiry);
    const current = groups.get(key) || [];
    current.push(node);
    groups.set(key, current);
  });

  const selectedGroup = Array.from(groups.values())
    .sort((left, right) => {
      const leftDte = left[0]?.daysToExpiry ?? 9999;
      const rightDte = right[0]?.daysToExpiry ?? 9999;
      if (leftDte !== rightDte) return leftDte - rightDte;
      return right.length - left.length;
    })[0];

  if (!selectedGroup?.length) return { expiryLabel: "", rows: [] };

  const ordered = [...selectedGroup].sort((a, b) => a.strike - b.strike);
  const expiryLabel = new Date(selectedGroup[0].expiry).toLocaleDateString("en-GB", {
    day: "2-digit",
    month: "short",
    year: "numeric",
  });

  return {
    expiryLabel,
    rows: ordered.map((node) => ({
      strike: node.strike,
      marketIv: (node.marketIv || 0) * 100,
      fairIv: (node.fairIv || 0) * 100,
      residual: node.residualVolPoints || 0,
      moneynessPct: ((node.moneyness || 0) - 1) * 100,
      confidence: node.confidenceScore || 0,
      liquidity: node.liquidityScore || 0,
    })),
  };
}

function SurfaceTooltip({ active, payload, label }) {
  if (!active || !payload?.length) return null;
  const row = payload[0]?.payload;
  if (!row) return null;

  return (
    <div className="tooltip-card">
      <div className="section-subhead" style={{ marginBottom: 4 }}>
        Strike {fmt(label, 0)}
      </div>
      <div>Market IV: {fmt(row.marketIv, 2)}%</div>
      <div>Fair IV: {fmt(row.fairIv, 2)}%</div>
      <div>Residual: {formatSigned(row.residual, 2)} pts</div>
      <div>Moneyness: {formatSigned(row.moneynessPct, 2)}%</div>
      <div>Conf: {fmt(row.confidence, 1)}</div>
      <div>Liq: {fmt(row.liquidity, 1)}</div>
    </div>
  );
}

function SignalCard({ title, rows, onSelectSymbol }) {
  return (
    <div>
      <div className="section-subhead" style={{ marginBottom: 6 }}>{title}</div>
      {!rows?.length ? (
        <div className="status-chip">No actionable signal on this side.</div>
      ) : (
        <div className="reco-list" style={{ maxHeight: 420 }}>
          {rows.map((row) => (
            <div className={`reco-card ${toneClass(row.action)}`.trim()} key={row.symbol}>
              <div className="reco-top">
                <h4>{row.symbol}</h4>
                <button className="btn btn-secondary" onClick={() => onSelectSymbol?.(row.symbol)}>
                  Focus
                </button>
              </div>
              <div className="reco-metrics">
                <div>Action: {row.action}</div>
                <div>Strike: {fmt(row.strike, 0)}</div>
                <div>DTE: {fmt(row.daysToExpiry, 0)}</div>
                <div>Type: {row.right}</div>
                <div>Market IV: {formatPct(row.markIv || 0, 2)}</div>
                <div>Fair IV: {formatPct(row.fairIv || 0, 2)}</div>
                <div>Residual: {formatSigned(row.residualVolPoints || 0, 2)} pts</div>
                <div>Z-score: {formatSigned(row.residualZScore || 0, 2)}</div>
                <div>Edge: {formatSigned(row.edgeVsFairPct || 0, 2)}%</div>
                <div>Tradeability: {fmt(row.tradeabilityScore, 1)}</div>
                <div>Conf: {fmt(row.confidenceScore, 1)}</div>
                <div>Liq: {fmt(row.liquidityScore, 1)}</div>
                <div>Mid: {formatUsd(row.mid || 0, 2)}</div>
                <div>Structure: {row.structureHint}</div>
              </div>
              <div className="subtle" style={{ marginTop: 6 }}>{row.thesis}</div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function TradeIdeaCard({ idea, onLoad, onSelectSymbol }) {
  return (
    <div className={`reco-card ${toneClass(idea.action)}`.trim()} key={`${idea.signalSymbol}-${idea.name}`}>
      <div className="reco-top">
        <h4>{idea.name}</h4>
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn btn-secondary" onClick={() => onSelectSymbol?.(idea.signalSymbol)}>
            Focus
          </button>
          <button className="btn btn-primary" onClick={() => onLoad?.(idea.analysis)}>
            Load
          </button>
        </div>
      </div>
      <div className="reco-metrics">
        <div>Signal: {idea.signalSymbol}</div>
        <div>Action: {idea.action}</div>
        <div>Score: {fmt(idea.score, 1)}</div>
        <div>Residual: {formatSigned(idea.residualVolPoints || 0, 2)} pts</div>
        <div>Tradeability: {fmt(idea.tradeabilityScore, 1)}</div>
        <div>Conf: {fmt(idea.confidenceScore, 1)}</div>
        <div>Risk: {idea.riskLabel}</div>
        <div>Premium: {formatUsd(idea.analysis?.netPremium || 0, 2)}</div>
        <div>MaxP: {formatUsd(idea.analysis?.maxProfit || 0, 2)}</div>
        <div>MaxL: {formatUsd(idea.analysis?.maxLoss || 0, 2)}</div>
        <div>PoP: {fmt((idea.analysis?.probabilityOfProfitApprox || 0) * 100, 1)}%</div>
        <div>EV: {formatUsd(idea.analysis?.expectedValue || 0, 2)}</div>
        <div>Delta: {formatSigned(idea.analysis?.aggregateGreeks?.delta || 0, 3)}</div>
        <div>Vega: {formatSigned(idea.analysis?.aggregateGreeks?.vega || 0, 3)}</div>
        <div>Theta: {formatSigned(idea.analysis?.aggregateGreeks?.theta || 0, 3)}</div>
      </div>
      <div className="subtle" style={{ marginTop: 6 }}>{idea.thesis}</div>
    </div>
  );
}

export default function RelativeValueBoardPanel({
  board,
  loading,
  onSelectSymbol,
  onLoad,
}) {
  if (loading) return <div className="status-chip">Scanning fair surface dislocations...</div>;
  if (!board) return <div className="status-chip">Relative value engine unavailable.</div>;
  if (!board.topCheapVol?.length && !board.topRichVol?.length) {
    return <div className="status-chip">No relative-value dislocation detected for this scope.</div>;
  }

  const surfaceSlice = buildSurfaceSlice(board.surfaceNodes);

  return (
    <div className="reco-wrap">
      <div className="summary-grid">
        <div className="summary-card">
          <div className="summary-label">Spot</div>
          <div className="summary-value">{formatUsd(board.spot || 0, 2)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Regime</div>
          <div className="summary-value" style={{ fontFamily: "Space Grotesk, sans-serif" }}>{board.regime || "-"}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Surface Quality</div>
          <div className="summary-value">{fmt(board.surfaceQualityScore, 1)}</div>
        </div>
        <div className="summary-card">
          <div className="summary-label">Avg |Residual|</div>
          <div className="summary-value">{formatSigned(board.avgAbsResidualVolPoints || 0, 2)} pts</div>
        </div>
        <div className="summary-card tone-bad">
          <div className="summary-label">Richest Vol</div>
          <div className="summary-value">{formatSigned(board.maxRichVolPoints || 0, 2)} pts</div>
        </div>
        <div className="summary-card tone-good">
          <div className="summary-label">Cheapest Vol</div>
          <div className="summary-value">{formatSigned(board.maxCheapVolPoints || 0, 2)} pts</div>
        </div>
      </div>

      <div className="status-chip">
        Surface nodes: {board.surfaceNodes?.length || 0} | Cheap signals: {board.topCheapVol?.length || 0} | Rich signals: {board.topRichVol?.length || 0} | Trade ideas: {board.tradeIdeas?.length || 0}
      </div>

      {surfaceSlice.rows.length > 1 && (
        <div className="summary-card" style={{ height: 320 }}>
          <div className="section-subhead" style={{ marginBottom: 8 }}>
            Fair Vs Market IV Slice {surfaceSlice.expiryLabel ? `• ${surfaceSlice.expiryLabel}` : ""}
          </div>
          <ResponsiveContainer width="100%" height="100%">
            <LineChart data={surfaceSlice.rows} margin={{ top: 8, right: 14, bottom: 10, left: 0 }}>
              <CartesianGrid stroke="rgba(148,163,184,0.12)" strokeDasharray="3 3" />
              <XAxis
                dataKey="strike"
                tick={{ fill: "rgba(226,232,240,0.72)", fontSize: 11 }}
                tickFormatter={(value) => fmt(value, 0)}
              />
              <YAxis
                tick={{ fill: "rgba(226,232,240,0.72)", fontSize: 11 }}
                tickFormatter={(value) => `${fmt(value, 0)}%`}
                width={44}
              />
              <Tooltip content={<SurfaceTooltip />} />
              <ReferenceLine y={0} stroke="rgba(148,163,184,0.25)" />
              <Line type="monotone" dataKey="marketIv" name="Market IV" stroke="#f97316" strokeWidth={2.2} dot={false} />
              <Line type="monotone" dataKey="fairIv" name="Fair IV" stroke="#38bdf8" strokeWidth={2.2} dot={false} />
            </LineChart>
          </ResponsiveContainer>
        </div>
      )}

      {!!board.tradeIdeas?.length && (
        <div>
          <div className="section-subhead" style={{ marginBottom: 6 }}>Trade Constructions</div>
          <div className="reco-list">
            {board.tradeIdeas.map((idea) => (
              <TradeIdeaCard key={`${idea.signalSymbol}-${idea.name}`} idea={idea} onLoad={onLoad} onSelectSymbol={onSelectSymbol} />
            ))}
          </div>
        </div>
      )}

      <div className="split">
        <SignalCard title="Cheapest Vs Fair" rows={board.topCheapVol} onSelectSymbol={onSelectSymbol} />
        <SignalCard title="Richest Vs Fair" rows={board.topRichVol} onSelectSymbol={onSelectSymbol} />
      </div>
    </div>
  );
}
