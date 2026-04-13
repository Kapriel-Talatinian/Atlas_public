import { useEffect, useState, useCallback, useMemo } from "react";
import { getSnapshot } from "./api";

const REFRESH_MS = 1000;
const REFRESH_MS_HIDDEN = 15000;
const DAY_MS = 24 * 60 * 60 * 1000;

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
  if (diffMin < 1) return "now";
  if (diffMin < 60) return `${Math.floor(diffMin)}m`;
  if (diffMin < 1440) return `${Math.floor(diffMin / 60)}h`;
  const days = Math.floor(diffMin / 1440);
  if (days < 30) return `${days}d`;
  return d.toLocaleDateString(undefined, { day: "2-digit", month: "2-digit" });
}

function formatSpot(value) {
  if (value == null || isNaN(value)) return "—";
  if (value >= 1000) return `$${Math.round(value).toLocaleString()}`;
  return `$${value.toFixed(2)}`;
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
    webApp.setHeaderColor?.("bg_color");
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
    let timer;
    let stopped = false;

    const tick = async () => {
      if (stopped) return;
      if (!document.hidden) await fetchNow();
      const delay = document.hidden ? REFRESH_MS_HIDDEN : REFRESH_MS;
      timer = setTimeout(tick, delay);
    };

    const onVisibility = () => {
      if (!document.hidden) fetchNow();
    };

    tick();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      stopped = true;
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", onVisibility);
    };
  }, [fetchNow]);

  return { snapshot, loading, error, refresh: fetchNow };
}

function computeWeeklyPnl(closed) {
  if (!Array.isArray(closed)) return 0;
  const cutoff = Date.now() - 7 * DAY_MS;
  return closed
    .filter(p => p.exitTime && new Date(p.exitTime).getTime() >= cutoff)
    .reduce((s, p) => s + (p.realizedPnlUsd || 0), 0);
}

function Header({ snapshot }) {
  const lastJournal = snapshot?.journal?.[0];
  const paused = lastJournal?.headline === "Bot paused";
  const armed = snapshot?.runtime?.executionArmed && !paused;
  const label = paused ? "Paused" : armed ? "Active" : "Idle";
  const state = paused ? "paused" : armed ? "active" : "idle";
  return (
    <div className="tg-header">
      <div className="tg-header-brand">
        <div className="tg-header-logo">A</div>
        <div className="tg-header-title">Atlas</div>
      </div>
      <div className={`tg-header-status ${state}`}>{label}</div>
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

function OverviewTab({ snapshot }) {
  const p = snapshot?.portfolio;
  const weeklyPnl = useMemo(
    () => computeWeeklyPnl(snapshot?.recentClosedPositions),
    [snapshot]
  );

  if (!p) return <div className="tg-loading">Loading portfolio…</div>;

  return (
    <>
      <div className="tg-hero">
        <div className="tg-hero-label">Net PnL</div>
        <div className={`tg-hero-value ${cls(p.netPnlUsd)}`}>{formatUsd(p.netPnlUsd, true)}</div>
        <div className="tg-hero-sub">
          <span>Equity <strong>{formatUsd(p.equityUsd)}</strong></span>
          <span>Cash <strong>{formatUsd(p.cashBalanceUsd)}</strong></span>
        </div>
      </div>

      <div className="tg-period-grid">
        <div className="tg-period">
          <div className="tg-period-label">Today</div>
          <div className={`tg-period-value ${cls(p.dailyPnlUsd)}`}>{formatUsd(p.dailyPnlUsd, true)}</div>
        </div>
        <div className="tg-period">
          <div className="tg-period-label">7 days</div>
          <div className={`tg-period-value ${cls(weeklyPnl)}`}>{formatUsd(weeklyPnl, true)}</div>
        </div>
        <div className="tg-period">
          <div className="tg-period-label">Month</div>
          <div className={`tg-period-value ${cls(p.monthlyPnlUsd)}`}>{formatUsd(p.monthlyPnlUsd, true)}</div>
        </div>
      </div>

      <div className="tg-exposure">
        <div className="tg-exposure-col">
          <div className="tg-exposure-label">Open trades</div>
          <div className="tg-exposure-value">{p.openPositionsCount}</div>
        </div>
        <div className="tg-exposure-col" style={{ alignItems: "flex-end" }}>
          <div className="tg-exposure-label">Gross exposure</div>
          <div className="tg-exposure-value">{formatUsd(p.grossExposureUsd)}</div>
        </div>
      </div>

      <div className="tg-section-title">Runtime</div>
      <div className="tg-runtime">
        <div className="tg-pill">
          <div className="tg-pill-label">Mode</div>
          <div className="tg-pill-value">{snapshot.runtime.runtimeMode}</div>
        </div>
        <div className="tg-pill">
          <div className="tg-pill-label">Trading</div>
          <div className="tg-pill-value">{snapshot.runtime.tradingEnabled ? "On" : "Off"}</div>
        </div>
        <div className="tg-pill">
          <div className="tg-pill-label">Max per trade</div>
          <div className="tg-pill-value">{formatUsd(snapshot.runtime.maxTradeUsd)}</div>
        </div>
        <div className="tg-pill">
          <div className="tg-pill-label">Daily loss limit</div>
          <div className="tg-pill-value">{formatUsd(snapshot.runtime.dailyLossLimitUsd)}</div>
        </div>
        <div className="tg-pill">
          <div className="tg-pill-label">Scanner</div>
          <div className="tg-pill-value">{snapshot.stats.actionableSignals} ready · {snapshot.stats.scannerSignals} signals</div>
        </div>
        <div className="tg-pill">
          <div className="tg-pill-label">Markets</div>
          <div className="tg-pill-value">{snapshot.stats.tradeableMarkets} tradeable</div>
        </div>
      </div>
    </>
  );
}

function OpenTab({ snapshot }) {
  const positions = snapshot?.openPositions || [];
  if (positions.length === 0) {
    return (
      <div className="tg-empty">
        <div className="tg-empty-icon">—</div>
        Flat right now.
        <br />Waiting for edge.
      </div>
    );
  }

  return (
    <div className="tg-list">
      {positions.map(p => {
        const outcome = (p.side || "").replace(/^Buy\s+/i, "").toUpperCase();
        return (
          <div className="tg-row" key={p.positionId}>
            <div className="tg-row-main">
              <div className="tg-row-title">
                <span className={`tg-badge ${outcome.toLowerCase()}`}>{outcome}</span>
                <span className="tg-row-title-text">{p.displayLabel || p.question}</span>
              </div>
              <div className="tg-row-sub">
                Stake {formatUsd(p.stakeUsd)} · {formatPct(p.entryPrice)} → {formatPct(p.currentPrice)}
              </div>
            </div>
            <div className="tg-row-right">
              <div className={`tg-row-value ${cls(p.unrealizedPnlUsd)}`}>
                {formatUsd(p.unrealizedPnlUsd, true)}
              </div>
              <div className="tg-row-meta">{p.asset}</div>
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
    return (
      <div className="tg-empty">
        <div className="tg-empty-icon">—</div>
        No closed trades yet.
      </div>
    );
  }

  return (
    <div className="tg-list">
      {closed.slice(0, 40).map(p => {
        const outcome = (p.side || "").replace(/^Buy\s+/i, "").toUpperCase();
        return (
          <div className="tg-row" key={p.positionId}>
            <div className="tg-row-main">
              <div className="tg-row-title">
                <span className={`tg-badge ${outcome.toLowerCase()}`}>{outcome}</span>
                <span className="tg-row-title-text">{p.displayLabel || p.question}</span>
              </div>
              <div className="tg-row-sub">
                {p.exitReason} · {formatTime(p.exitTime)}
              </div>
            </div>
            <div className="tg-row-right">
              <div className={`tg-row-value ${cls(p.realizedPnlUsd)}`}>
                {formatUsd(p.realizedPnlUsd, true)}
              </div>
              <div className="tg-row-meta">{p.asset}</div>
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

  const rows = assets.map(a => {
    const openA = open.filter(p => p.asset === a);
    const closedA = closed.filter(p => p.asset === a);
    const realized = closedA.reduce((s, p) => s + (p.realizedPnlUsd || 0), 0);
    const unrealized = openA.reduce((s, p) => s + (p.unrealizedPnlUsd || 0), 0);
    const exposure = openA.reduce((s, p) => s + (p.stakeUsd || 0), 0);
    const spot = snapshot?.assets?.find(x => x.asset === a)?.spot;
    return {
      asset: a,
      openCount: openA.length,
      closedCount: closedA.length,
      realized,
      unrealized,
      exposure,
      spot,
    };
  });

  return (
    <div className="tg-list">
      {rows.map(r => (
        <div className="tg-asset-row" key={r.asset}>
          <div className="tg-asset-head">
            <div className="tg-asset-name">
              {r.asset}
              <span className="tg-asset-spot">{formatSpot(r.spot)}</span>
            </div>
            <div className={`tg-row-value ${cls(r.realized + r.unrealized)}`}>
              {formatUsd(r.realized + r.unrealized, true)}
            </div>
          </div>
          <div className="tg-asset-stats">
            <div className="tg-asset-stat">
              <div className="tg-asset-stat-label">Open</div>
              <div className="tg-asset-stat-value">{r.openCount}</div>
            </div>
            <div className="tg-asset-stat">
              <div className="tg-asset-stat-label">Exposure</div>
              <div className="tg-asset-stat-value">{formatUsd(r.exposure)}</div>
            </div>
            <div className="tg-asset-stat">
              <div className="tg-asset-stat-label">Closed</div>
              <div className="tg-asset-stat-value">{r.closedCount}</div>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

export default function App() {
  useTelegram();
  const [tab, setTab] = useState("Overview");
  const { snapshot, loading, error } = useSnapshot();

  if (loading && !snapshot) {
    return <div className="tg-loading">Loading Atlas…</div>;
  }

  return (
    <>
      <Header snapshot={snapshot} />
      {error && <div className="tg-error">Connection issue: {error}</div>}
      <Tabs active={tab} onChange={setTab} />
      {tab === "Overview" && <OverviewTab snapshot={snapshot} />}
      {tab === "Open" && <OpenTab snapshot={snapshot} />}
      {tab === "Closed" && <ClosedTab snapshot={snapshot} />}
      {tab === "Assets" && <AssetsTab snapshot={snapshot} />}
    </>
  );
}
