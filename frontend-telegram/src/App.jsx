import { useEffect, useMemo, useState, useCallback } from "react";
import { getSnapshot } from "./api";

const REFRESH_MS = 8000;

function formatUsd(n, signed = false) {
  if (n == null || isNaN(n)) return "—";
  const abs = Math.abs(n).toLocaleString(undefined, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  if (!signed) return `$${abs}`;
  if (n > 0) return `+$${abs}`;
  if (n < 0) return `-$${abs}`;
  return `$${abs}`;
}

function formatPct(n) {
  if (n == null || isNaN(n)) return "—";
  return `${(n * 100).toFixed(1)}%`;
}

function formatTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  const now = new Date();
  const diffMin = (now - d) / 60000;
  if (diffMin < 1) return "just now";
  if (diffMin < 60) return `${Math.floor(diffMin)}m ago`;
  if (diffMin < 1440) return `${Math.floor(diffMin / 60)}h ago`;
  return d.toLocaleDateString(undefined, { day: "2-digit", month: "2-digit" });
}

function cls(value) {
  if (value > 0) return "pos";
  if (value < 0) return "neg";
  return "";
}

function useTelegram() {
  const [tg, setTg] = useState(null);
  useEffect(() => {
    const webApp = window.Telegram?.WebApp;
    if (!webApp) return;
    webApp.ready();
    webApp.expand();
    setTg(webApp);
  }, []);
  return tg;
}

function useSnapshot() {
  const [snapshot, setSnapshot] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const fetchNow = useCallback(async () => {
    try {
      const data = await getSnapshot();
      setSnapshot(data);
      setError(null);
    } catch (e) {
      setError(e.message || "Failed to load");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    fetchNow();
    const id = setInterval(fetchNow, REFRESH_MS);
    return () => clearInterval(id);
  }, [fetchNow]);

  return { snapshot, loading, error, refresh: fetchNow };
}

function Header({ snapshot }) {
  const paused = snapshot?.journal?.[0]?.headline === "Bot paused";
  const active = snapshot?.runtime?.executionArmed && !paused;
  return (
    <div className="tg-header">
      <div className="tg-header-title">Atlas</div>
      <div className={`tg-header-status ${active ? "active" : "paused"}`}>
        {active ? "Active" : paused ? "Paused" : "Idle"}
      </div>
    </div>
  );
}

function Tabs({ active, onChange }) {
  const tabs = ["Overview", "Open", "Closed", "Assets"];
  return (
    <div className="tg-tabs">
      {tabs.map(t => (
        <button
          key={t}
          className={`tg-tab ${active === t ? "active" : ""}`}
          onClick={() => onChange(t)}
        >
          {t}
        </button>
      ))}
    </div>
  );
}

function OverviewTab({ snapshot, tg }) {
  const p = snapshot?.portfolio;
  if (!p) return <div className="tg-loading">Loading portfolio…</div>;

  const handlePause = useCallback(() => {
    tg?.HapticFeedback?.impactOccurred?.("medium");
    tg?.showConfirm?.("Pause the bot? New entries will be blocked.", ok => {
      if (ok) tg?.sendData?.(JSON.stringify({ action: "pause" }));
    });
  }, [tg]);

  const handleResume = useCallback(() => {
    tg?.HapticFeedback?.impactOccurred?.("medium");
    tg?.sendData?.(JSON.stringify({ action: "resume" }));
  }, [tg]);

  return (
    <>
      <div className="tg-hero">
        <div className="tg-hero-label">Net PnL</div>
        <div className={`tg-hero-value ${cls(p.netPnlUsd)}`}>{formatUsd(p.netPnlUsd, true)}</div>
        <div className="tg-hero-sub">Equity {formatUsd(p.equityUsd)} · Cash {formatUsd(p.cashBalanceUsd)}</div>
      </div>

      <div className="tg-grid">
        <div className="tg-card">
          <div className="tg-card-label">Daily</div>
          <div className={`tg-card-value ${cls(p.dailyPnlUsd)}`}>{formatUsd(p.dailyPnlUsd, true)}</div>
        </div>
        <div className="tg-card">
          <div className="tg-card-label">Monthly</div>
          <div className={`tg-card-value ${cls(p.monthlyPnlUsd)}`}>{formatUsd(p.monthlyPnlUsd, true)}</div>
        </div>
        <div className="tg-card">
          <div className="tg-card-label">Win rate</div>
          <div className="tg-card-value">{formatPct(p.winRate)}</div>
          <div className="tg-card-sub">{p.closedPositionsCount} closed</div>
        </div>
        <div className="tg-card">
          <div className="tg-card-label">Drawdown</div>
          <div className={`tg-card-value ${p.drawdownUsd > 0 ? "neg" : ""}`}>{formatUsd(p.drawdownUsd)}</div>
          <div className="tg-card-sub">{formatPct(p.drawdownPct)}</div>
        </div>
        <div className="tg-card">
          <div className="tg-card-label">Open</div>
          <div className="tg-card-value">{p.openPositionsCount}</div>
          <div className="tg-card-sub">Exposure {formatUsd(p.grossExposureUsd)}</div>
        </div>
        <div className="tg-card">
          <div className="tg-card-label">Avg winner</div>
          <div className="tg-card-value pos">{formatUsd(p.avgWinnerUsd, true)}</div>
          <div className="tg-card-sub">Loser {formatUsd(p.avgLoserUsd, true)}</div>
        </div>
      </div>

      <div className="tg-section-title">Runtime</div>
      <div className="tg-list">
        <div className="tg-row">
          <div className="tg-row-main">
            <div className="tg-row-title">Mode</div>
            <div className="tg-row-sub">{snapshot.runtime.runtimeMode}</div>
          </div>
          <div className="tg-row-value">{snapshot.runtime.tradingEnabled ? "On" : "Off"}</div>
        </div>
        <div className="tg-row">
          <div className="tg-row-main">
            <div className="tg-row-title">Risk per trade</div>
            <div className="tg-row-sub">Daily loss limit {formatUsd(snapshot.runtime.dailyLossLimitUsd)}</div>
          </div>
          <div className="tg-row-value">{formatUsd(snapshot.runtime.maxTradeUsd)}</div>
        </div>
        <div className="tg-row">
          <div className="tg-row-main">
            <div className="tg-row-title">Scanner</div>
            <div className="tg-row-sub">{snapshot.stats.actionableSignals} bot-ready / {snapshot.stats.scannerSignals} signals</div>
          </div>
          <div className="tg-row-value">{snapshot.stats.tradeableMarkets}</div>
        </div>
      </div>
    </>
  );
}

function OpenTab({ snapshot }) {
  const positions = snapshot?.openPositions || [];
  if (positions.length === 0) {
    return <div className="tg-empty">Flat right now. Waiting for edge.</div>;
  }

  return (
    <div className="tg-list">
      {positions.map(p => {
        const outcome = (p.side || "").replace(/^Buy\s+/i, "").toUpperCase();
        return (
          <div className="tg-row" key={p.positionId}>
            <div className="tg-row-main">
              <div className="tg-row-title">
                <span className={`tg-badge ${outcome.toLowerCase()}`}>{outcome}</span>{" "}
                {p.displayLabel || p.question}
              </div>
              <div className="tg-row-sub">
                Stake {formatUsd(p.stakeUsd)} · entry {formatPct(p.entryPrice)} → {formatPct(p.currentPrice)}
              </div>
            </div>
            <div className={`tg-row-value ${cls(p.unrealizedPnlUsd)}`}>
              {formatUsd(p.unrealizedPnlUsd, true)}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function ClosedTab({ snapshot }) {
  const closed = snapshot?.recentClosedPositions || [];
  if (closed.length === 0) {
    return <div className="tg-empty">No closed trades yet.</div>;
  }

  return (
    <div className="tg-list">
      {closed.slice(0, 30).map(p => {
        const outcome = (p.side || "").replace(/^Buy\s+/i, "").toUpperCase();
        return (
          <div className="tg-row" key={p.positionId}>
            <div className="tg-row-main">
              <div className="tg-row-title">
                <span className={`tg-badge ${outcome.toLowerCase()}`}>{outcome}</span>{" "}
                {p.displayLabel || p.question}
              </div>
              <div className="tg-row-sub">
                {p.exitReason} · {formatTime(p.exitTime)}
              </div>
            </div>
            <div className={`tg-row-value ${cls(p.realizedPnlUsd)}`}>
              {formatUsd(p.realizedPnlUsd, true)}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function AssetsTab({ snapshot }) {
  const assets = ["BTC", "ETH", "SOL"];
  const open = snapshot?.openPositions || [];
  const closed = snapshot?.recentClosedPositions || [];

  const byAsset = assets.map(a => {
    const openForA = open.filter(p => p.asset === a);
    const closedForA = closed.filter(p => p.asset === a);
    const realized = closedForA.reduce((s, p) => s + (p.realizedPnlUsd || 0), 0);
    const wins = closedForA.filter(p => p.realizedPnlUsd > 0).length;
    const unrealized = openForA.reduce((s, p) => s + (p.unrealizedPnlUsd || 0), 0);
    const exposure = openForA.reduce((s, p) => s + (p.stakeUsd || 0), 0);
    const winRate = closedForA.length > 0 ? wins / closedForA.length : 0;
    const spot = snapshot.assets?.find(x => x.asset === a)?.spot;
    return { asset: a, openCount: openForA.length, closedCount: closedForA.length, realized, unrealized, exposure, winRate, spot };
  });

  return (
    <div className="tg-list">
      {byAsset.map(a => (
        <div className="tg-row" key={a.asset}>
          <div className="tg-row-main">
            <div className="tg-row-title">
              {a.asset} {a.spot ? `· $${Math.round(a.spot).toLocaleString()}` : ""}
            </div>
            <div className="tg-row-sub">
              {a.openCount} open · {a.closedCount} closed · WR {formatPct(a.winRate)}
            </div>
          </div>
          <div className={`tg-row-value ${cls(a.realized)}`}>
            {formatUsd(a.realized, true)}
          </div>
        </div>
      ))}
    </div>
  );
}

export default function App() {
  const tg = useTelegram();
  const [tab, setTab] = useState("Overview");
  const { snapshot, loading, error } = useSnapshot();

  useEffect(() => {
    if (!tg) return;
    tg.setHeaderColor?.("secondary_bg_color");
    tg.setBackgroundColor?.(getComputedStyle(document.body).getPropertyValue("--tg-bg") || "#0f1115");
  }, [tg]);

  if (loading && !snapshot) {
    return <div className="tg-loading">Loading Atlas…</div>;
  }

  return (
    <>
      <Header snapshot={snapshot} />
      {error && <div className="tg-error">Connection issue: {error}</div>}
      <Tabs active={tab} onChange={setTab} />
      {tab === "Overview" && <OverviewTab snapshot={snapshot} tg={tg} />}
      {tab === "Open" && <OpenTab snapshot={snapshot} />}
      {tab === "Closed" && <ClosedTab snapshot={snapshot} />}
      {tab === "Assets" && <AssetsTab snapshot={snapshot} />}
    </>
  );
}
