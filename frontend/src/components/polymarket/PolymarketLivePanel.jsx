import {
  Button,
  Callout,
  Card,
  Classes,
  Divider,
  Intent,
  NonIdealState,
  ProgressBar,
  Spinner,
  Tag,
} from "@blueprintjs/core";
import { formatPct, formatSigned, formatUsd } from "../../quant";
import "./PolymarketLivePanel.css";

const ASSET_ORDER = ["BTC", "ETH", "SOL"];

function fmt(value, digits = 2) {
  if (!Number.isFinite(value)) return "-";
  return value.toLocaleString("en-US", {
    minimumFractionDigits: 0,
    maximumFractionDigits: digits,
  });
}

function minutesLabel(value) {
  if (!Number.isFinite(value)) return "-";
  if (value < 60) return `${fmt(value, 1)}m`;
  const hours = value / 60;
  if (hours < 24) return `${fmt(hours, 1)}h`;
  return `${fmt(hours / 24, 1)}d`;
}

function normalizeProgress(value, scale = 1) {
  if (!Number.isFinite(value)) return 0;
  const normalized = scale > 1 ? value / scale : value;
  return Math.max(0, Math.min(1, normalized));
}

function pnlIntent(value) {
  if (value > 0) return Intent.SUCCESS;
  if (value < 0) return Intent.DANGER;
  return Intent.NONE;
}

function statusIntent(value) {
  const normalized = String(value || "").toLowerCase();
  if (normalized.includes("ready") || normalized.includes("online") || normalized.includes("priced") || normalized.includes("armed") || normalized.includes("trading")) return Intent.SUCCESS;
  if (normalized.includes("guarded") || normalized.includes("watch") || normalized.includes("thin") || normalized.includes("paper")) return Intent.WARNING;
  if (normalized.includes("down") || normalized.includes("lock") || normalized.includes("blocked") || normalized.includes("disabled")) return Intent.DANGER;
  return Intent.PRIMARY;
}

function sideIntent(side) {
  const normalized = String(side || "").toLowerCase();
  if (normalized.includes("up") || normalized.includes("yes")) return Intent.SUCCESS;
  if (normalized.includes("down") || normalized.includes("no")) return Intent.WARNING;
  return Intent.NONE;
}

function qualityIntent(value) {
  if (value >= 75) return Intent.SUCCESS;
  if (value >= 55) return Intent.PRIMARY;
  if (value >= 40) return Intent.WARNING;
  return Intent.DANGER;
}

function categoryIntent(category) {
  return category === "directional" ? Intent.PRIMARY : Intent.NONE;
}

function intentColor(intent) {
  switch (intent) {
    case Intent.SUCCESS:
      return "#34d399";
    case Intent.WARNING:
      return "#fbbf24";
    case Intent.DANGER:
      return "#f87171";
    case Intent.PRIMARY:
      return "#60a5fa";
    default:
      return undefined;
  }
}

function sideLabel(side) {
  return String(side || "").replace(/^Buy\s+/i, "").trim().toUpperCase();
}

function grossProfitPerDollar(entryPrice) {
  if (!Number.isFinite(entryPrice) || entryPrice <= 0) return 0;
  return Math.max(0, 1 / entryPrice - 1);
}

function edgePct(signal) {
  return Math.max(signal.edgeYesPct || 0, signal.edgeNoPct || 0);
}

function signalCategoryLabel(signal) {
  return signal.signalCategory === "directional" ? "UP / DOWN" : "THRESHOLD";
}

function outcomePriceForSignal(signal) {
  if (Number.isFinite(signal.botEntryPrice) && signal.botEntryPrice > 0) {
    return signal.botEntryPrice;
  }
  const normalized = String(signal.recommendedSide || "").toLowerCase();
  return normalized.endsWith((signal.primaryOutcomeLabel || "yes").toLowerCase()) || normalized.endsWith("yes") || normalized.endsWith("up")
    ? signal.marketYesPrice || 0
    : signal.marketNoPrice || 0;
}

function readinessIntent(signal) {
  return signal.botEligible ? Intent.SUCCESS : Intent.WARNING;
}

function readinessLabel(signal) {
  return signal.botEligible ? "BOT READY" : "WATCHLIST";
}

function metricTone(value) {
  return intentColor(pnlIntent(value));
}

function EmptyState({ title, description, icon = "search-template" }) {
  return (
    <div className="polymarket-empty">
      <NonIdealState icon={icon} title={title} description={description} />
    </div>
  );
}

function StatCard({ label, value, helper, intent = Intent.NONE }) {
  return (
    <Card className="polymarket-stat-card">
      <div className="polymarket-stat-label">{label}</div>
      <div className="polymarket-stat-value" style={{ color: intentColor(intent) }}>{value}</div>
      {helper ? <div className="polymarket-stat-helper">{helper}</div> : null}
    </Card>
  );
}

function TopSignalHero({ signal }) {
  if (!signal) {
    return <EmptyState title="No live edge right now" description="The bot is scanning, but nothing currently clears the spread, liquidity and conviction gates." icon="predictive-analysis" />;
  }

  const entryPrice = outcomePriceForSignal(signal);
  const grossProfit = grossProfitPerDollar(entryPrice);

  return (
    <Card className="polymarket-hero-focus">
      <div className="polymarket-hero-focus-top">
        <div>
          <div className="polymarket-kicker">Best live setup</div>
          <h2 className="polymarket-focus-title">{signal.displayLabel}</h2>
          <div className="polymarket-focus-subtitle">{signal.question}</div>
        </div>
        <div className="polymarket-chip-rail">
          <Tag large round intent={sideIntent(signal.recommendedSide)}>{sideLabel(signal.recommendedSide)}</Tag>
          <Tag large round intent={categoryIntent(signal.signalCategory)}>{signalCategoryLabel(signal)}</Tag>
          <Tag large round intent={readinessIntent(signal)}>{readinessLabel(signal)}</Tag>
          <Tag large minimal intent={Intent.PRIMARY}>{minutesLabel(signal.minutesToExpiry)}</Tag>
        </div>
      </div>

      <div className="polymarket-focus-metrics">
        <div className="polymarket-focus-metric">
          <div className="polymarket-focus-label">Entry</div>
          <div className="polymarket-focus-value">{formatPct(entryPrice, 2)}</div>
        </div>
        <div className="polymarket-focus-metric">
          <div className="polymarket-focus-label">Fair</div>
          <div className="polymarket-focus-value">{formatPct(sideLabel(signal.recommendedSide) === sideLabel(`Buy ${signal.primaryOutcomeLabel}`) ? signal.fairYesProbability || 0 : signal.fairNoProbability || 0, 2)}</div>
        </div>
        <div className="polymarket-focus-metric">
          <div className="polymarket-focus-label">Edge</div>
          <div className="polymarket-focus-value" style={{ color: intentColor(qualityIntent(signal.convictionScore || 0)) }}>{formatPct(edgePct(signal), 2)}</div>
        </div>
        <div className="polymarket-focus-metric">
          <div className="polymarket-focus-label">TP brut / 1$</div>
          <div className="polymarket-focus-value">{formatUsd(grossProfit, 2)}</div>
        </div>
        <div className="polymarket-focus-metric">
          <div className="polymarket-focus-label">Perte max / 1$</div>
          <div className="polymarket-focus-value" style={{ color: "#fbbf24" }}>{formatUsd(1, 2)}</div>
        </div>
      </div>

      <div className="polymarket-focus-bars">
        <div>
          <div className="polymarket-progress-row"><span>Quality</span><span>{fmt(signal.qualityScore || 0, 1)}</span></div>
          <ProgressBar animate={false} intent={qualityIntent(signal.qualityScore || 0)} value={normalizeProgress(signal.qualityScore || 0, 100)} />
        </div>
        <div>
          <div className="polymarket-progress-row"><span>Conviction</span><span>{fmt(signal.convictionScore || 0, 1)}</span></div>
          <ProgressBar animate={false} intent={qualityIntent(signal.convictionScore || 0)} value={normalizeProgress(signal.convictionScore || 0, 100)} />
        </div>
      </div>

      <div className="polymarket-rationale-grid">
        <Callout className="polymarket-rationale-card" intent={Intent.NONE} title="Macro">
          {signal.macroReasoning}
        </Callout>
        <Callout className="polymarket-rationale-card" intent={Intent.NONE} title="Micro">
          {signal.microReasoning}
        </Callout>
        <Callout className="polymarket-rationale-card" intent={Intent.NONE} title="Math">
          {signal.mathReasoning}
        </Callout>
      </div>
      {!signal.botEligible ? (
        <Callout className="polymarket-rationale-card polymarket-bot-gate-callout" intent={Intent.WARNING} title="Bot gate">
          {signal.botEligibilityReason}
        </Callout>
      ) : null}
    </Card>
  );
}

function AssetLane({ asset, reference, thresholdSignal, directionalSignal }) {
  return (
    <Card className="polymarket-asset-lane">
      <div className="polymarket-asset-header">
        <div>
          <div className="polymarket-kicker">{asset}</div>
          <h3 className="polymarket-asset-title">{formatUsd(reference?.spot || 0, asset === "SOL" ? 3 : 2)}</h3>
        </div>
        <div className="polymarket-chip-stack">
          <Tag minimal intent={qualityIntent(reference?.regimeConfidence || 0)}>{reference?.regime || "-"}</Tag>
          <Tag minimal intent={pnlIntent(reference?.liveBiasScore || 0)}>{reference?.liveBiasLabel || "-"} {formatSigned(reference?.liveBiasScore || 0, 1)}</Tag>
        </div>
      </div>
      <div className="polymarket-lane-vol">ATM IV {formatPct(reference?.atmIv || 0, 1)}</div>

      <Divider className="polymarket-divider" />

      <MiniSignalCard title="Threshold" signal={thresholdSignal} />
      <MiniSignalCard title="Micro Move" signal={directionalSignal} />
    </Card>
  );
}

function MiniSignalCard({ title, signal }) {
  if (!signal) {
    return (
      <div className="polymarket-mini-signal polymarket-mini-signal-empty">
        <div className="polymarket-mini-title">{title}</div>
        <div className="polymarket-mini-copy">No clean setup in this lane right now.</div>
      </div>
    );
  }

  const entryPrice = outcomePriceForSignal(signal);
  return (
    <div className="polymarket-mini-signal">
      <div className="polymarket-mini-top">
        <div>
          <div className="polymarket-mini-title">{title}</div>
          <div className="polymarket-mini-label">{signal.displayLabel}</div>
        </div>
        <Tag round intent={sideIntent(signal.recommendedSide)}>{sideLabel(signal.recommendedSide)}</Tag>
      </div>
      <div className="polymarket-mini-status-row">
        <Tag minimal intent={readinessIntent(signal)}>{readinessLabel(signal)}</Tag>
      </div>
      <div className="polymarket-mini-grid">
        <div>
          <span>Entry</span>
          <strong>{formatPct(entryPrice, 2)}</strong>
        </div>
        <div>
          <span>Edge</span>
          <strong>{formatPct(edgePct(signal), 2)}</strong>
        </div>
        <div>
          <span>TP / 1$</span>
          <strong>{formatUsd(grossProfitPerDollar(entryPrice), 2)}</strong>
        </div>
        <div>
          <span>Timer</span>
          <strong>{minutesLabel(signal.minutesToExpiry)}</strong>
        </div>
      </div>
    </div>
  );
}

function OpportunityCard({ signal }) {
  const entryPrice = outcomePriceForSignal(signal);
  const grossProfit = grossProfitPerDollar(entryPrice);

  return (
    <Card className="polymarket-opportunity-card">
      <div className="polymarket-opportunity-top">
        <div>
          <div className="polymarket-chip-rail polymarket-chip-rail-tight">
            <Tag minimal intent={categoryIntent(signal.signalCategory)}>{signalCategoryLabel(signal)}</Tag>
            <Tag minimal intent={Intent.PRIMARY}>{signal.asset}</Tag>
            <Tag minimal>{minutesLabel(signal.minutesToExpiry)}</Tag>
          </div>
          <h3 className="polymarket-opportunity-title">{signal.displayLabel}</h3>
          <div className="polymarket-opportunity-subtitle">{signal.question}</div>
        </div>
        <div className="polymarket-chip-stack">
          <Tag large round intent={sideIntent(signal.recommendedSide)}>{sideLabel(signal.recommendedSide)}</Tag>
          <Tag minimal intent={readinessIntent(signal)}>{readinessLabel(signal)}</Tag>
          <Tag minimal intent={qualityIntent(signal.convictionScore || 0)}>Conv {fmt(signal.convictionScore || 0, 1)}</Tag>
        </div>
      </div>

      <div className="polymarket-opportunity-grid">
        <div className="polymarket-opportunity-cell">
          <span>Entry</span>
          <strong>{formatPct(entryPrice, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>TP brut / 1$</span>
          <strong>{formatUsd(grossProfit, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Perte max / 1$</span>
          <strong>{formatUsd(1, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Fair</span>
          <strong>{formatPct(sideLabel(signal.recommendedSide) === sideLabel(`Buy ${signal.primaryOutcomeLabel}`) ? signal.fairYesProbability || 0 : signal.fairNoProbability || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Market</span>
          <strong>{formatPct(sideLabel(signal.recommendedSide) === sideLabel(`Buy ${signal.primaryOutcomeLabel}`) ? signal.marketYesPrice || 0 : signal.marketNoPrice || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Edge</span>
          <strong>{formatPct(edgePct(signal), 2)}</strong>
        </div>
      </div>

      <div className="polymarket-opportunity-body">
        <div className="polymarket-opportunity-summary">{signal.summary}</div>
        <div className="polymarket-opportunity-text"><strong>Math:</strong> {signal.mathReasoning}</div>
        <div className="polymarket-opportunity-text"><strong>Bot:</strong> {signal.botEligibilityReason}</div>
      </div>
    </Card>
  );
}

function PositionCard({ position }) {
  return (
    <Card className="polymarket-position-card">
      <div className="polymarket-position-top">
        <div>
          <div className="polymarket-chip-rail polymarket-chip-rail-tight">
            <Tag minimal intent={categoryIntent(position.signalCategory)}>{position.signalCategory === "directional" ? "UP / DOWN" : "THRESHOLD"}</Tag>
            <Tag minimal>{position.asset}</Tag>
          </div>
          <h3 className="polymarket-position-title">{position.displayLabel || position.question}</h3>
          <div className="polymarket-position-subtitle">{position.question}</div>
        </div>
        <div className="polymarket-chip-stack">
          <Tag large round intent={sideIntent(position.side)}>{sideLabel(position.side)}</Tag>
          <Tag large round intent={pnlIntent(position.unrealizedPnlUsd || 0)}>{formatUsd(position.unrealizedPnlUsd || 0, 2)}</Tag>
        </div>
      </div>

      <div className="polymarket-opportunity-grid">
        <div className="polymarket-opportunity-cell">
          <span>Stake</span>
          <strong>{formatUsd(position.stakeUsd || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Entry</span>
          <strong>{formatPct(position.entryPrice || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Mark</span>
          <strong>{formatPct(position.currentPrice || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>TP brut</span>
          <strong>{formatUsd(position.maxProfitUsd || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Perte max</span>
          <strong>{formatUsd(position.maxLossUsd || 0, 2)}</strong>
        </div>
        <div className="polymarket-opportunity-cell">
          <span>Timer</span>
          <strong>{minutesLabel((new Date(position.expiry).getTime() - Date.now()) / 60000)}</strong>
        </div>
      </div>

      <div className="polymarket-position-text"><strong>Why:</strong> {position.thesis}</div>
    </Card>
  );
}

function ClosedPositionsTable({ positions }) {
  if (!positions.length) {
    return <EmptyState title="No closed positions yet" description="Closed trades and realized PnL will appear here as soon as the bot starts exiting tickets." icon="history" />;
  }

  return (
    <div className="polymarket-table-wrap">
      <table className={`${Classes.HTML_TABLE} ${Classes.HTML_TABLE_STRIPED} ${Classes.INTERACTIVE}`}>
        <thead>
          <tr>
            <th>Exit</th>
            <th>Setup</th>
            <th>Side</th>
            <th>Stake</th>
            <th>Exit Px</th>
            <th>PnL</th>
            <th>Reason</th>
          </tr>
        </thead>
        <tbody>
          {positions.map((position) => (
            <tr key={position.positionId}>
              <td>{new Date(position.exitTime || position.entryTime).toLocaleString()}</td>
              <td>{position.displayLabel || position.question}</td>
              <td>{sideLabel(position.side)}</td>
              <td>{formatUsd(position.stakeUsd || 0, 2)}</td>
              <td>{formatPct(position.exitPrice || 0, 2)}</td>
              <td style={{ color: metricTone(position.realizedPnlUsd || 0) }}>{formatUsd(position.realizedPnlUsd || 0, 2)}</td>
              <td>{position.exitReason}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function JournalList({ journal, notes }) {
  if (!journal.length && !notes.length) {
    return <EmptyState title="No journal yet" description="Entries, exits and runtime notes will appear here once the worker is cycling." icon="manual" />;
  }

  return (
    <div className="polymarket-journal-list">
      {journal.map((entry, index) => (
        <Card key={`${entry.timestamp}-${entry.headline}-${index}`} className="polymarket-journal-card">
          <div className="polymarket-journal-top">
            <div>
              <h4 className="polymarket-journal-title">{entry.headline}</h4>
              <div className="polymarket-journal-detail">{entry.detail}</div>
            </div>
            <div className="polymarket-chip-stack">
              <Tag minimal intent={Intent.PRIMARY}>{entry.type}</Tag>
              <Tag minimal>{new Date(entry.timestamp).toLocaleString()}</Tag>
            </div>
          </div>
        </Card>
      ))}
      {notes.map((note, index) => (
        <Callout key={`${index}-${note}`} className="polymarket-note-card" intent={Intent.NONE}>{note}</Callout>
      ))}
    </div>
  );
}

export default function PolymarketLivePanel({ snapshot, loading, onRefresh }) {
  const runtime = snapshot?.runtime || {};
  const opportunities = snapshot?.opportunities || [];
  const stats = snapshot?.stats || {};
  const assets = snapshot?.assets || [];
  const notes = snapshot?.notes || [];
  const portfolio = snapshot?.portfolio || {};
  const openPositions = snapshot?.openPositions || [];
  const recentClosed = snapshot?.recentClosedPositions || [];
  const journal = snapshot?.journal || [];

  const sortedSignals = [...opportunities].sort((a, b) => (b.convictionScore || 0) - (a.convictionScore || 0));
  const scannerSignals = sortedSignals.filter((signal) => !String(signal.recommendedSide || "").toLowerCase().includes("pass"));
  const topSignal = scannerSignals.find((signal) => signal.botEligible) || scannerSignals[0] || sortedSignals[0] || null;
  const referenceByAsset = Object.fromEntries(assets.map((item) => [item.asset, item]));

  const assetLanes = ASSET_ORDER.map((asset) => ({
    asset,
    reference: referenceByAsset[asset] || null,
    thresholdSignal: sortedSignals.find((signal) => signal.asset === asset && signal.signalCategory !== "directional" && signal.botEligible) || sortedSignals.find((signal) => signal.asset === asset && signal.signalCategory !== "directional" && !String(signal.recommendedSide || "").toLowerCase().includes("pass")) || null,
    directionalSignal: sortedSignals.find((signal) => signal.asset === asset && signal.signalCategory === "directional" && signal.botEligible) || sortedSignals.find((signal) => signal.asset === asset && signal.signalCategory === "directional" && !String(signal.recommendedSide || "").toLowerCase().includes("pass")) || null,
  }));

  if (loading && !snapshot) {
    return (
      <div className={`polymarket-workspace ${Classes.DARK}`}>
        <div className="polymarket-empty polymarket-loading">
          <Spinner size={48} intent={Intent.PRIMARY} />
        </div>
      </div>
    );
  }

  if (!snapshot) {
    return (
      <div className={`polymarket-workspace ${Classes.DARK}`}>
        <EmptyState title="No Polymarket snapshot yet" description="Start the API and let the scan run once. This screen is dedicated entirely to the live Polymarket workflow." icon="offline" />
      </div>
    );
  }

  return (
    <div className={`polymarket-workspace ${Classes.DARK}`}>
      <section className="polymarket-hero">
        <div className="polymarket-hero-header">
          <div className="polymarket-hero-copy">
            <div className="polymarket-kicker">Atlas · Polymarket only</div>
            <h1 className="polymarket-title">24/24 crypto threshold and micro-move trading</h1>
            <p className="polymarket-subtitle">
              A single visual workspace for BTC, ETH and SOL Polymarket trades. No front-end tuning, no noisy controls. Just live edge,
              open tickets, risk, and short-horizon setups explained clearly.
            </p>
          </div>
          <div className="polymarket-hero-actions">
            <Button intent={Intent.PRIMARY} large loading={loading} onClick={onRefresh}>Refresh live board</Button>
            <div className="polymarket-chip-rail">
              <Tag large round intent={statusIntent(snapshot.status)}>{snapshot.status}</Tag>
              <Tag large round intent={runtime.tradingEnabled ? Intent.SUCCESS : Intent.WARNING}>bot {runtime.tradingEnabled ? "enabled" : "disabled"}</Tag>
              <Tag large round intent={runtime.telegramConfigured ? Intent.SUCCESS : Intent.WARNING}>telegram {runtime.telegramConfigured ? "ready" : "missing"}</Tag>
              <Tag large round intent={runtime.dailyLossLockActive ? Intent.DANGER : Intent.SUCCESS}>risk lock {runtime.dailyLossLockActive ? "on" : "off"}</Tag>
            </div>
          </div>
        </div>

        <div className="polymarket-stat-grid">
          <StatCard label="Equity" value={formatUsd(portfolio.equityUsd || 0, 2)} helper={`Cash ${formatUsd(portfolio.cashBalanceUsd || 0, 2)}`} />
          <StatCard label="Daily PnL" value={formatUsd(portfolio.dailyPnlUsd || 0, 2)} helper={`Monthly ${formatUsd(portfolio.monthlyPnlUsd || 0, 2)}`} intent={pnlIntent(portfolio.dailyPnlUsd || 0)} />
          <StatCard label="Open positions" value={String(portfolio.openPositionsCount || 0)} helper={`${formatUsd(portfolio.grossExposureUsd || 0, 2)} gross exposure`} />
          <StatCard label="Win rate" value={formatPct(portfolio.winRate || 0, 1)} helper={`${portfolio.closedPositionsCount || 0} closed`} />
          <StatCard label="Bot-ready" value={String(stats.actionableSignals || 0)} helper={`${stats.scannerSignals || scannerSignals.length || 0} scanner signals`} intent={(stats.actionableSignals || 0) > 0 ? Intent.SUCCESS : Intent.WARNING} />
          <StatCard label="Risk per trade" value={formatUsd(runtime.maxTradeUsd || portfolio.maxTradeRiskUsd || 0, 2)} helper={runtime.runtimeMode || "guarded"} intent={Intent.WARNING} />
        </div>

        <Callout className="polymarket-summary-callout" intent={statusIntent(snapshot.status)} title="Live thesis">
          {snapshot.summary || runtime.summary || "No live thesis yet."}
        </Callout>
      </section>

      <section className="polymarket-main-grid">
        <TopSignalHero signal={topSignal} />
        <Card className="polymarket-runtime-panel">
          <div className="polymarket-panel-head">
            <h3>Runtime guardrails</h3>
            <Tag minimal intent={statusIntent(runtime.runtimeMode)}>{runtime.runtimeMode || "guarded"}</Tag>
          </div>
          <div className="polymarket-runtime-kv">
            <div><span>Wallet</span><strong>{runtime.walletAddressHint || "not loaded"}</strong></div>
            <div><span>Signer</span><strong>{runtime.signerConfigured ? "configured" : "missing"}</strong></div>
            <div><span>Execution</span><strong>{runtime.executionArmed ? "armed" : "guarded"}</strong></div>
            <div><span>Daily loss limit</span><strong>{formatUsd(runtime.dailyLossLimitUsd || 0, 2)}</strong></div>
          </div>
          <Divider className="polymarket-divider" />
          <div className="polymarket-progress-stack">
            <div>
              <div className="polymarket-progress-row"><span>Drawdown</span><span>{formatUsd(portfolio.drawdownUsd || 0, 2)} · {formatPct(portfolio.drawdownPct || 0, 1)}</span></div>
              <ProgressBar animate={false} intent={(portfolio.drawdownPct || 0) > 0.08 ? Intent.DANGER : (portfolio.drawdownPct || 0) > 0.04 ? Intent.WARNING : Intent.SUCCESS} value={normalizeProgress(portfolio.drawdownPct || 0, 0.15)} />
            </div>
            <div>
              <div className="polymarket-progress-row"><span>Scanner → bot-ready</span><span>{stats.actionableSignals || 0} / {stats.scannerSignals || scannerSignals.length || 0}</span></div>
              <ProgressBar animate={false} intent={qualityIntent((stats.scannerSignals || scannerSignals.length || 0) > 0 ? ((stats.actionableSignals || 0) / (stats.scannerSignals || scannerSignals.length || 1)) * 100 : 0)} value={normalizeProgress((stats.scannerSignals || scannerSignals.length || 0) > 0 ? (stats.actionableSignals || 0) / (stats.scannerSignals || scannerSignals.length || 1) : 0)} />
            </div>
          </div>
        </Card>
      </section>

      <section className="polymarket-section">
        <div className="polymarket-section-head">
          <div>
            <div className="polymarket-kicker">Asset radar</div>
            <h2 className="polymarket-section-title">BTC / ETH / SOL at a glance</h2>
          </div>
        </div>
        <div className="polymarket-asset-grid">
          {assetLanes.map((lane) => (
            <AssetLane key={lane.asset} {...lane} />
          ))}
        </div>
      </section>

      <section className="polymarket-section">
        <div className="polymarket-section-head">
          <div>
            <div className="polymarket-kicker">Live scanner</div>
            <h2 className="polymarket-section-title">Scanner board</h2>
          </div>
        </div>
        {!sortedSignals.length ? (
          <EmptyState title="No current opportunity" description="The board is live, but nothing currently clears the required edge and quality thresholds." icon="heat-grid" />
        ) : (
          <div className="polymarket-opportunity-grid-wrap">
            {sortedSignals.map((signal) => (
              <OpportunityCard key={signal.marketId} signal={signal} />
            ))}
          </div>
        )}
      </section>

      <section className="polymarket-section polymarket-section-split">
        <Card className="polymarket-split-card">
          <div className="polymarket-panel-head">
            <h2>Open positions</h2>
            <Tag minimal intent={Intent.PRIMARY}>{openPositions.length} open</Tag>
          </div>
          {!openPositions.length ? (
            <EmptyState title="Flat right now" description="The bot is waiting for better edge. The next accepted trade will appear here with its rationale." icon="timeline-line-chart" />
          ) : (
            <div className="polymarket-position-list">
              {openPositions.map((position) => (
                <PositionCard key={position.positionId} position={position} />
              ))}
            </div>
          )}
        </Card>

        <Card className="polymarket-split-card">
          <div className="polymarket-panel-head">
            <h2>Recent closed</h2>
            <Tag minimal intent={Intent.NONE}>{recentClosed.length} items</Tag>
          </div>
          <ClosedPositionsTable positions={recentClosed} />
        </Card>
      </section>

      <section className="polymarket-section polymarket-section-split polymarket-section-bottom">
        <Card className="polymarket-split-card">
          <div className="polymarket-panel-head">
            <h2>Decision journal</h2>
            <Tag minimal intent={Intent.PRIMARY}>{journal.length} entries</Tag>
          </div>
          <JournalList journal={journal} notes={[]} />
        </Card>

        <Card className="polymarket-split-card">
          <div className="polymarket-panel-head">
            <h2>System notes</h2>
            <Tag minimal>{notes.length} notes</Tag>
          </div>
          <JournalList journal={[]} notes={notes} />
        </Card>
      </section>
    </div>
  );
}
