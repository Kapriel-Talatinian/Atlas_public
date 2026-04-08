import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Area,
  AreaChart,
  CartesianGrid,
  Legend,
  Line,
  LineChart,
  ReferenceLine,
  ResponsiveContainer,
  Scatter,
  ScatterChart,
  Tooltip,
  XAxis,
  YAxis,
  ZAxis,
} from "recharts";
import {
  analyzeCustomStrategy,
  createMarketStream,
  getMarketStreamUrls,
  getAssetsOverview,
  getExpiries,
  getHealth,
  getOptionChain,
  getOptionModelSnapshot,
  getModelCalibration,
  getOptionSignals,
  getArbitrageScan,
  getGreeksExposureGrid,
  getRelativeValueBoard,
  getStrategyOptimizer,
  getStrategyRecommendations,
  getPresetStrategies,
  getExperimentalBotSnapshot,
  getKillSwitchState,
  retryOpenOrders,
  setKillSwitchState,
  getTradingBook,
  getVolRegime,
  getLiveBias,
  getVolSurface,
  previewPaperOrder,
  placePaperOrder,
  executeAlgoOrder,
  runAutoHedge,
  runTradingStress,
  resetPaperBook,
} from "./api";
import {
  buildSmileData,
  buildSurfaceSlice,
  formatRatio,
  formatSigned,
  formatTimeAgo,
  formatUsd,
} from "./quant";
import ChainTable from "./components/market/ChainTable";
import OptionDetailsPanel from "./components/market/OptionDetailsPanel";
import OptionPositionGrid from "./components/market/OptionPositionGrid";
import QuantModelPanel from "./components/market/QuantModelPanel";
import SignalBoard from "./components/market/SignalBoard";
import StrategyRecommendationPanel from "./components/market/StrategyRecommendationPanel";
import StrategyOptimizerPanel from "./components/market/StrategyOptimizerPanel";
import VolRegimePanel from "./components/market/VolRegimePanel";
import LiveBiasPanel from "./components/market/LiveBiasPanel";
import ArbitrageScannerPanel from "./components/market/ArbitrageScannerPanel";
import GreeksExposureHeatmapPanel from "./components/market/GreeksExposureHeatmapPanel";
import RelativeValueBoardPanel from "./components/market/RelativeValueBoardPanel";
import ModelCalibrationPanel from "./components/market/ModelCalibrationPanel";
import MarketStatsStrip from "./components/market/MarketStatsStrip";
import OverviewMetrics from "./components/market/OverviewMetrics";
import ExperimentalBotPanel from "./components/experimental/ExperimentalBotPanel";
import BookSummaryCards from "./components/trading/BookSummaryCards";
import OrderLadder from "./components/trading/OrderLadder";
import OrderTicket from "./components/trading/OrderTicket";
import RiskStressPanel from "./components/trading/RiskStressPanel";
import RiskUtilizationBars from "./components/trading/RiskUtilizationBars";
import SmartExecutionPanel from "./components/trading/SmartExecutionPanel";
import { OrdersTable, PositionsTable, RiskSummary } from "./components/trading/TradingBookPanels";
import SectionCard from "./components/ui/SectionCard";
import TabBar from "./components/ui/TabBar";
import "./App.css";

const ASSETS = ["BTC", "ETH", "SOL", "WTI"];
const OPTION_TYPE_FILTERS = ["all", "call", "put"];

function readInitialUiState() {
  if (typeof window === "undefined") {
    return { tab: "market", asset: "BTC", preset: "" };
  }

  const params = new URLSearchParams(window.location.search);
  const rawTab = String(params.get("tab") || "").trim().toLowerCase();
  const rawAsset = String(params.get("asset") || "").trim().toUpperCase();
  const rawPreset = String(params.get("preset") || "").trim().toLowerCase();

  const allowedTabs = new Set(["market", "execution", "strategy", "alpha", "experimental"]);
  const tab = allowedTabs.has(rawTab) ? rawTab : "market";
  const asset = ASSETS.includes(rawAsset) ? rawAsset : "BTC";
  const preset = rawPreset === "first" ? "first" : "";

  return { tab, asset, preset };
}

function syncUiStateToUrl(activeTab, asset, presetDirective = "") {
  if (typeof window === "undefined") return;

  const url = new URL(window.location.href);
  url.searchParams.set("tab", activeTab);
  url.searchParams.set("asset", asset);
  if (presetDirective === "first") url.searchParams.set("preset", "first");
  else url.searchParams.delete("preset");
  window.history.replaceState({}, "", url);
}

function normalizeDirection(direction) {
  if (direction === "Sell" || direction === "sell" || direction === 1) return "Sell";
  return "Buy";
}

function normalizeRight(right) {
  if (right === "Put" || right === "put" || right === 1) return "Put";
  return "Call";
}

function filterByOptionType(rows, optionType) {
  if (!Array.isArray(rows) || optionType === "all") return rows || [];
  const expected = optionType === "put" ? "Put" : "Call";
  return rows.filter((row) => normalizeRight(row.right ?? row.Right) === expected);
}

function App() {
  const [initialUiState] = useState(() => readInitialUiState());
  const [activeTab, setActiveTab] = useState(initialUiState.tab);
  const [asset, setAsset] = useState(initialUiState.asset);
  const [initialPresetDirective, setInitialPresetDirective] = useState(initialUiState.preset);
  const [overview, setOverview] = useState([]);
  const [expiries, setExpiries] = useState([]);
  const [selectedExpiry, setSelectedExpiry] = useState("");
  const [optionType, setOptionType] = useState("all");
  const [search, setSearch] = useState("");
  const [chain, setChain] = useState([]);
  const [surface, setSurface] = useState([]);
  const [presets, setPresets] = useState([]);
  const [strategySize, setStrategySize] = useState(1);
  const [strategyName, setStrategyName] = useState("Custom Strategy");
  const [customLegs, setCustomLegs] = useState([]);
  const [analysis, setAnalysis] = useState(null);
  const [shockRangePct, setShockRangePct] = useState(0.35);
  const [loadingChain, setLoadingChain] = useState(false);
  const [loadingPresets, setLoadingPresets] = useState(false);
  const [loadingSignals, setLoadingSignals] = useState(false);
  const [loadingCalibration, setLoadingCalibration] = useState(false);
  const [loadingRegime, setLoadingRegime] = useState(false);
  const [loadingLiveBias, setLoadingLiveBias] = useState(false);
  const [loadingRecommendations, setLoadingRecommendations] = useState(false);
  const [loadingOptimizer, setLoadingOptimizer] = useState(false);
  const [loadingRelativeValue, setLoadingRelativeValue] = useState(false);
  const [loadingExposure, setLoadingExposure] = useState(false);
  const [loadingArbitrage, setLoadingArbitrage] = useState(false);
  const [loadingBot, setLoadingBot] = useState(false);
  const [loadingBook, setLoadingBook] = useState(false);
  const [placingOrder, setPlacingOrder] = useState(false);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [loadingStress, setLoadingStress] = useState(false);
  const [retryingOpenOrders, setRetryingOpenOrders] = useState(false);
  const [togglingKillSwitch, setTogglingKillSwitch] = useState(false);
  const [runningTwap, setRunningTwap] = useState(false);
  const [runningAutoHedge, setRunningAutoHedge] = useState(false);
  const [error, setError] = useState("");
  const [preview, setPreview] = useState(null);
  const [stress, setStress] = useState(null);
  const [modelSnapshot, setModelSnapshot] = useState(null);
  const [calibration, setCalibration] = useState(null);
  const [signalBoard, setSignalBoard] = useState(null);
  const [loadingModelSnapshot, setLoadingModelSnapshot] = useState(false);
  const [regime, setRegime] = useState(null);
  const [liveBias, setLiveBias] = useState(null);
  const [recommendationBoard, setRecommendationBoard] = useState(null);
  const [optimizerBoard, setOptimizerBoard] = useState(null);
  const [relativeValueBoard, setRelativeValueBoard] = useState(null);
  const [exposureGrid, setExposureGrid] = useState(null);
  const [exposureMetric, setExposureMetric] = useState("gamma");
  const [arbitrageScan, setArbitrageScan] = useState(null);
  const [botSnapshot, setBotSnapshot] = useState(null);
  const [riskProfile, setRiskProfile] = useState("balanced");
  const [optimizerTargets, setOptimizerTargets] = useState({
    targetDelta: 0,
    targetVega: 0,
    targetTheta: 0,
  });
  const [lastUpdate, setLastUpdate] = useState(null);
  const [apiHealth, setApiHealth] = useState("unknown");
  const [streamStatus, setStreamStatus] = useState("idle");
  const [streamEnabled, setStreamEnabled] = useState(true);
  const [killSwitch, setKillSwitch] = useState(null);
  const [selectedOptionSymbol, setSelectedOptionSymbol] = useState("");
  const [pendingFocusCell, setPendingFocusCell] = useState(null);
  const [orderForm, setOrderForm] = useState({
    symbol: "",
    side: "Buy",
    type: "Market",
    quantity: "1",
    limitPrice: "",
  });
  const [book, setBook] = useState({
    positions: [],
    recentOrders: [],
    risk: null,
    limits: null,
  });

  const currentOverview = useMemo(
    () => overview.find((o) => o.asset === asset) || null,
    [overview, asset]
  );

  const previewQuote = useMemo(
    () => chain.find((q) => q.symbol === orderForm.symbol) || null,
    [chain, orderForm.symbol]
  );

  const smileData = useMemo(() => buildSmileData(chain), [chain]);
  const surfaceData = useMemo(() => buildSurfaceSlice(surface, 6), [surface]);

  const filteredChain = useMemo(() => {
    const q = search.trim().toUpperCase();
    if (!q) return chain;
    return chain.filter((row) => row.symbol.includes(q));
  }, [chain, search]);

  const focusUniverse = useMemo(
    () => (filteredChain.length > 0 ? filteredChain : chain),
    [filteredChain, chain]
  );

  const selectedOption = useMemo(
    () => focusUniverse.find((quote) => quote.symbol === selectedOptionSymbol) || focusUniverse[0] || null,
    [focusUniverse, selectedOptionSymbol]
  );

  const positionGridRows = useMemo(() => {
    const spot = currentOverview?.underlyingPrice || 0;
    return [...focusUniverse]
      .filter((quote) => quote.bid > 0 || quote.ask > 0 || quote.mark > 0 || quote.mid > 0)
      .map((quote) => {
        const spread =
          quote.ask > 0 && quote.bid > 0 ? (quote.ask - quote.bid) / Math.max((quote.ask + quote.bid) / 2, 1e-9) : 2;
        const moneynessDist = spot > 0 ? Math.abs(quote.strike / spot - 1) : 0;
        const liquidity = Math.log1p((quote.openInterest || 0) + (quote.volume24h || 0));
        const score = moneynessDist * 2.2 + spread * 0.8 - liquidity * 0.14;
        return { ...quote, score };
      })
      .sort((a, b) => a.score - b.score)
      .slice(0, 24);
  }, [focusUniverse, currentOverview]);

  const termData = useMemo(() => {
    if (!currentOverview?.termStructure) return [];
    return currentOverview.termStructure.map((point) => ({
      dte: point.daysToExpiry,
      iv: point.atmIv * 100,
      expiry: point.expiry.slice(0, 10),
    }));
  }, [currentOverview]);

  const refreshOverview = useCallback(async () => {
    const data = await getAssetsOverview(ASSETS);
    setOverview(data);
  }, []);

  const refreshTradingBook = useCallback(async () => {
    setLoadingBook(true);
    try {
      const [state, killSwitchState] = await Promise.all([
        getTradingBook(200),
        getKillSwitchState().catch(() => null),
      ]);
      setBook(state);
      if (killSwitchState) setKillSwitch(killSwitchState);
    } finally {
      setLoadingBook(false);
    }
  }, []);

  const retryOpen = useCallback(async () => {
    setRetryingOpenOrders(true);
    try {
      setError("");
      await retryOpenOrders(30);
      await refreshTradingBook();
    } catch (err) {
      setError(err.message);
    } finally {
      setRetryingOpenOrders(false);
    }
  }, [refreshTradingBook]);

  const toggleKillSwitch = useCallback(async () => {
    setTogglingKillSwitch(true);
    try {
      setError("");
      const nextActive = !(killSwitch?.isActive);
      const state = await setKillSwitchState({
        isActive: nextActive,
        reason: nextActive ? "manual-trader-toggle" : "manual-trader-release",
        updatedBy: "ui",
      });
      setKillSwitch(state);
      await refreshTradingBook();
    } catch (err) {
      setError(err.message);
    } finally {
      setTogglingKillSwitch(false);
    }
  }, [killSwitch?.isActive, refreshTradingBook]);

  useEffect(() => {
    if (focusUniverse.length === 0) {
      if (selectedOptionSymbol) setSelectedOptionSymbol("");
      return;
    }
    const stillExists = focusUniverse.some((quote) => quote.symbol === selectedOptionSymbol);
    if (!stillExists) setSelectedOptionSymbol(focusUniverse[0].symbol);
  }, [focusUniverse, selectedOptionSymbol]);

  useEffect(() => {
    syncUiStateToUrl(activeTab, asset, initialPresetDirective);
  }, [activeTab, asset, initialPresetDirective]);

  useEffect(() => {
    if (!selectedOption?.symbol) {
      setModelSnapshot(null);
      return undefined;
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        setLoadingModelSnapshot(true);
        const snapshot = await getOptionModelSnapshot(selectedOption.symbol);
        if (!cancelled) setModelSnapshot(snapshot);
      } catch {
        if (!cancelled) setModelSnapshot(null);
      } finally {
        if (!cancelled) setLoadingModelSnapshot(false);
      }
    }, 260);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [selectedOption?.symbol]);

  useEffect(() => {
    let mounted = true;
    (async () => {
      try {
        const data = await getExpiries(asset);
        if (!mounted) return;
        setExpiries(data);
        setSelectedExpiry((prev) => (prev && data.includes(prev) ? prev : data[0] || ""));
      } catch (err) {
        if (mounted) setError(err.message);
      }
    })();
    return () => {
      mounted = false;
    };
  }, [asset]);

  const refreshAssetData = useCallback(async () => {
    if (!selectedExpiry) return;
    setError("");
    setLoadingChain(true);
    setLoadingPresets(true);
    setLoadingSignals(true);

    try {
      const [chainData, surfaceDataResp, presetsResp] = await Promise.all([
        getOptionChain({ asset, expiry: selectedExpiry, type: optionType, limit: 320 }),
        getVolSurface({ asset, limit: 900 }),
        getPresetStrategies({ asset, expiry: selectedExpiry, size: strategySize }),
      ]);
      setChain(chainData);
      setSurface(surfaceDataResp);
      setPresets(presetsResp);
      getOptionSignals({ asset, expiry: selectedExpiry, type: optionType, limit: 120 })
        .then((signalsResp) => setSignalBoard(signalsResp))
        .catch(() => null)
        .finally(() => setLoadingSignals(false));
      setLastUpdate(new Date().toISOString());
    } catch (err) {
      setError(err.message);
      setLoadingSignals(false);
    } finally {
      setLoadingChain(false);
      setLoadingPresets(false);
    }
  }, [asset, selectedExpiry, optionType, strategySize]);

  const refreshAlphaData = useCallback(async () => {
    if (!selectedExpiry) return;
    setLoadingCalibration(true);
    setLoadingRegime(true);
    setLoadingRecommendations(true);
    setLoadingOptimizer(true);
    setLoadingRelativeValue(true);
    setLoadingExposure(true);
    setLoadingArbitrage(true);
    try {
      const [calibrationResp, regimeResp, recoResp, optimizerResp, rvResp, exposureResp, arbResp] = await Promise.all([
        getModelCalibration({ asset, expiry: selectedExpiry }),
        getVolRegime(asset),
        getStrategyRecommendations({
          asset,
          expiry: selectedExpiry,
          size: strategySize,
          riskProfile,
        }),
        getStrategyOptimizer({
          asset,
          expiry: selectedExpiry,
          size: strategySize,
          riskProfile,
          targetDelta: optimizerTargets.targetDelta,
          targetVega: optimizerTargets.targetVega,
          targetTheta: optimizerTargets.targetTheta,
        }),
        getRelativeValueBoard({
          asset,
          expiry: selectedExpiry,
          limit: 18,
        }),
        getGreeksExposureGrid({
          asset,
          maxExpiries: 6,
          maxStrikes: 26,
        }),
        getArbitrageScan({
          asset,
          expiry: selectedExpiry,
          limit: 120,
        }),
      ]);
      setCalibration(calibrationResp);
      setRegime(regimeResp);
      setRecommendationBoard(recoResp);
      setOptimizerBoard(optimizerResp);
      setRelativeValueBoard(rvResp);
      setExposureGrid(exposureResp);
      setArbitrageScan(arbResp);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoadingCalibration(false);
      setLoadingRegime(false);
      setLoadingRecommendations(false);
      setLoadingOptimizer(false);
      setLoadingRelativeValue(false);
      setLoadingExposure(false);
      setLoadingArbitrage(false);
    }
  }, [
    asset,
    selectedExpiry,
    strategySize,
    riskProfile,
    optimizerTargets.targetDelta,
    optimizerTargets.targetVega,
    optimizerTargets.targetTheta,
  ]);

  const refreshExperimentalBot = useCallback(async () => {
    setLoadingBot(true);
    try {
      const snapshot = await getExperimentalBotSnapshot();
      setBotSnapshot(snapshot);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoadingBot(false);
    }
  }, []);

  useEffect(() => {
    let cancelled = false;
    const refresh = async () => {
      try {
        setLoadingLiveBias(true);
        const snapshot = await getLiveBias({ asset, horizonDays: 30 });
        if (!cancelled) setLiveBias(snapshot);
      } catch (err) {
        if (!cancelled) setError(err.message);
      } finally {
        if (!cancelled) setLoadingLiveBias(false);
      }
    };

    refresh().catch(() => null);
    const id = setInterval(() => refresh().catch(() => null), 15000);
    return () => {
      cancelled = true;
      clearInterval(id);
    };
  }, [asset]);

  useEffect(() => {
    refreshOverview().catch((err) => setError(err.message));
    refreshTradingBook().catch((err) => setError(err.message));
    const id = setInterval(() => {
      refreshOverview().catch(() => null);
      refreshTradingBook().catch(() => null);
    }, 15000);
    return () => clearInterval(id);
  }, [refreshOverview, refreshTradingBook]);

  useEffect(() => {
    if (!selectedExpiry) return undefined;
    refreshAssetData().catch(() => null);
    const id = setInterval(() => {
      refreshAssetData().catch(() => null);
    }, 12000);
    return () => clearInterval(id);
  }, [refreshAssetData, selectedExpiry]);

  useEffect(() => {
    if (!selectedExpiry) return undefined;
    refreshAlphaData().catch(() => null);
    const id = setInterval(() => {
      refreshAlphaData().catch(() => null);
    }, 20000);
    return () => clearInterval(id);
  }, [refreshAlphaData, selectedExpiry]);

  useEffect(() => {
    refreshExperimentalBot().catch(() => null);
  }, [refreshExperimentalBot]);

  useEffect(() => {
    if (activeTab !== "experimental") return undefined;
    const id = setInterval(() => {
      refreshExperimentalBot().catch(() => null);
    }, 9000);
    return () => clearInterval(id);
  }, [activeTab, refreshExperimentalBot]);

  useEffect(() => {
    let mounted = true;
    const ping = async () => {
      try {
        await getHealth();
        if (mounted) setApiHealth("up");
      } catch {
        if (mounted) setApiHealth("down");
      }
    };
    ping();
    const id = setInterval(ping, 10000);
    return () => {
      mounted = false;
      clearInterval(id);
    };
  }, []);

  useEffect(() => {
    if (!streamEnabled || !selectedExpiry) return undefined;
    const streamUrls = getMarketStreamUrls({
      asset,
      expiry: selectedExpiry,
      chainLimit: asset === "WTI" ? 60 : 120,
    });
    let disposed = false;
    let source = null;
    let reconnectTimer = null;
    let urlIndex = 0;
    let retriesOnUrl = 0;
    let reconnectDelayMs = 900;

    const onMarket = (event) => {
      try {
        const payload = JSON.parse(event.data);
        const incomingOverviewRaw = payload?.overview ?? payload?.Overview;
        const incomingChainRaw = payload?.chain ?? payload?.Chain;
        const incomingOverview = incomingOverviewRaw
          ? {
              ...incomingOverviewRaw,
              asset: incomingOverviewRaw.asset ?? incomingOverviewRaw.Asset,
            }
          : null;
        const incomingChain = filterByOptionType(incomingChainRaw, optionType);
        if (incomingOverview) {
          setOverview((prev) => {
            const next = prev.filter((item) => item.asset !== incomingOverview.asset);
            next.push(incomingOverview);
            return next.sort((a, b) => a.asset.localeCompare(b.asset));
          });
        }
        if (Array.isArray(incomingChain)) {
          setChain(incomingChain);
        }
        setLastUpdate(new Date().toISOString());
        setStreamStatus("live");
      } catch {
        setStreamStatus("degraded");
      }
    };

    const onStatus = () => {
      setStreamStatus("degraded");
    };

    const connect = () => {
      if (disposed || streamUrls.length === 0) return;
      const activeUrl = streamUrls[urlIndex % streamUrls.length];
      if (urlIndex > 0 && retriesOnUrl === 0) setStreamStatus("fallback");
      else setStreamStatus("connecting");
      source = createMarketStream(activeUrl);
      source.onopen = () => {
        retriesOnUrl = 0;
        reconnectDelayMs = 900;
        setStreamStatus("live");
      };

      source.addEventListener("market", onMarket);
      source.addEventListener("status", onStatus);
      source.onmessage = onMarket;
      source.onerror = () => {
        source?.close();
        if (disposed) return;
        retriesOnUrl += 1;
        if (retriesOnUrl >= 3 && streamUrls.length > 1) {
          urlIndex = (urlIndex + 1) % streamUrls.length;
          retriesOnUrl = 0;
          setStreamStatus("fallback");
        } else {
          setStreamStatus("reconnecting");
        }
        const delay = reconnectDelayMs;
        reconnectDelayMs = Math.min(Math.round(reconnectDelayMs * 1.6), 6000);
        reconnectTimer = setTimeout(connect, delay);
      };
    };

    connect();

    return () => {
      disposed = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      source?.close();
      setStreamStatus("idle");
    };
  }, [asset, selectedExpiry, optionType, streamEnabled]);

  const addLeg = useCallback((quote, direction) => {
    const normalizedDirection = normalizeDirection(direction);
    setCustomLegs((prev) => {
      const idx = prev.findIndex(
        (leg) => leg.symbol === quote.symbol && leg.direction === normalizedDirection
      );
      if (idx >= 0) {
        const updated = [...prev];
        updated[idx] = { ...updated[idx], quantity: updated[idx].quantity + 1 };
        return updated;
      }
      return [...prev, { symbol: quote.symbol, direction: normalizedDirection, quantity: 1 }];
    });
  }, []);

  const selectOption = useCallback((quote) => {
    if (quote?.symbol) setSelectedOptionSymbol(quote.symbol);
  }, []);

  const selectSymbolFromSignals = useCallback((symbol) => {
    if (!symbol) return;
    setSelectedOptionSymbol(symbol);
  }, []);

  const focusExposureCell = useCallback((cell) => {
    if (!cell) return;
    const targetExpiry = String(cell.expiry || "").slice(0, 10);
    const targetStrike = Number(cell.strike);
    setPendingFocusCell({
      expiry: targetExpiry,
      strike: Number.isFinite(targetStrike) ? targetStrike : 0,
    });
    if (targetExpiry) setSelectedExpiry(targetExpiry);
    setActiveTab("market");
  }, []);

  useEffect(() => {
    if (!pendingFocusCell || chain.length === 0) return;
    const targetExpiry = pendingFocusCell.expiry;
    const targetStrike = pendingFocusCell.strike;
    const scope = chain.filter((quote) => String(quote.expiry).slice(0, 10) === targetExpiry);
    const universe = scope.length > 0 ? scope : chain;
    const nearest = [...universe].sort(
      (a, b) => Math.abs(a.strike - targetStrike) - Math.abs(b.strike - targetStrike)
    )[0];
    if (nearest?.symbol) setSelectedOptionSymbol(nearest.symbol);
    setPendingFocusCell(null);
  }, [chain, pendingFocusCell]);

  const quickOrderFromQuote = useCallback((quote, side) => {
    if (!quote?.symbol) return;
    const suggestedLimit =
      side === "Sell"
        ? quote.bid || quote.mid || quote.mark || ""
        : quote.ask || quote.mid || quote.mark || "";
    setSelectedOptionSymbol(quote.symbol);
    setOrderForm((prev) => ({
      ...prev,
      symbol: quote.symbol,
      side: side || prev.side,
      limitPrice: suggestedLimit ? String(suggestedLimit) : prev.limitPrice,
    }));
    setActiveTab("execution");
  }, []);

  const useForOrder = useCallback((symbol) => {
    const quote = chain.find((row) => row.symbol === symbol);
    const suggestedLimit = quote ? quote.mid || quote.mark || quote.ask || quote.bid || "" : "";
    setSelectedOptionSymbol(symbol);
    setOrderForm((prev) => ({
      ...prev,
      symbol,
      limitPrice: suggestedLimit ? String(suggestedLimit) : prev.limitPrice,
    }));
    setActiveTab("execution");
  }, [chain]);

  const removeLeg = useCallback((index) => {
    setCustomLegs((prev) => prev.filter((_, i) => i !== index));
  }, []);

  const updateLeg = useCallback((index, patch) => {
    setCustomLegs((prev) => prev.map((leg, i) => (i === index ? { ...leg, ...patch } : leg)));
  }, []);

  const loadPreset = useCallback((preset) => {
    setStrategyName(preset.name);
    setInitialPresetDirective("");
    setCustomLegs(
      preset.legs.map((leg) => ({
        symbol: leg.symbol,
        direction: normalizeDirection(leg.direction),
        quantity: leg.quantity,
      }))
    );
    setAnalysis(preset);
  }, []);

  const loadRecommendedStrategy = useCallback((strategyAnalysis) => {
    if (!strategyAnalysis) return;
    loadPreset(strategyAnalysis);
    setActiveTab("strategy");
  }, [loadPreset]);

  useEffect(() => {
    if (initialPresetDirective !== "first") return;
    if (presets.length === 0) return;
    if (customLegs.length > 0 || analysis) {
      setInitialPresetDirective("");
      return;
    }

    loadPreset(presets[0]);
  }, [analysis, customLegs.length, initialPresetDirective, loadPreset, presets]);

  const analyzeCustom = useCallback(async () => {
    try {
      setError("");
      const validLegs = customLegs
        .map((leg) => ({
          symbol: leg.symbol.trim(),
          direction: normalizeDirection(leg.direction),
          quantity: Number(leg.quantity),
        }))
        .filter((leg) => leg.symbol && Number.isFinite(leg.quantity) && leg.quantity > 0);

      if (validLegs.length === 0) {
        setError("Add at least one valid leg before analysis.");
        return;
      }

      const result = await analyzeCustomStrategy({
        asset,
        name: strategyName || "Custom Strategy",
        legs: validLegs,
        shockRangePct,
        gridPoints: 121,
      });
      setAnalysis(result);
      setActiveTab("strategy");
    } catch (err) {
      setError(err.message);
    }
  }, [asset, customLegs, shockRangePct, strategyName]);

  const submitOrderPayload = useCallback(async (payload) => {
    setPlacingOrder(true);
    try {
      setError("");
      await placePaperOrder(payload);
      await refreshTradingBook();
      return true;
    } catch (err) {
      setError(err.message);
      await refreshTradingBook().catch(() => null);
      return false;
    } finally {
      setPlacingOrder(false);
    }
  }, [refreshTradingBook]);

  const submitOrder = useCallback(async () => {
    if (!orderForm.symbol.trim()) {
      setError("Order symbol is required.");
      return;
    }
    const quantity = Number(orderForm.quantity);
    if (!Number.isFinite(quantity) || quantity <= 0) {
      setError("Order quantity must be > 0.");
      return;
    }

    const payload = {
      symbol: orderForm.symbol.trim(),
      side: orderForm.side,
      quantity,
      type: orderForm.type,
    };
    if (orderForm.type === "Limit") {
      const limitPrice = Number(orderForm.limitPrice);
      if (!Number.isFinite(limitPrice) || limitPrice <= 0) {
        setError("Limit price must be > 0 for limit orders.");
        return;
      }
      payload.limitPrice = limitPrice;
    }

    await submitOrderPayload(payload);
  }, [orderForm, submitOrderPayload]);

  const placeLadderOrder = useCallback(async (payload) => {
    setOrderForm((prev) => ({
      ...prev,
      symbol: payload.symbol,
      side: payload.side,
      type: payload.type,
      quantity: String(payload.quantity),
      limitPrice: payload.limitPrice ? String(payload.limitPrice) : prev.limitPrice,
    }));
    await submitOrderPayload(payload);
  }, [submitOrderPayload]);

  const runAlgoExecution = useCallback(async ({
    side,
    style,
    totalQty,
    slices,
    intervalSec,
    maxParticipationPct,
    limitPrice,
  }) => {
    const symbol = orderForm.symbol.trim() || selectedOption?.symbol || "";
    if (!symbol) {
      setError("Set a symbol before algo execution.");
      return;
    }

    const safeTotalQty = Number(totalQty);
    const safeSlices = Math.max(1, Math.floor(Number(slices) || 1));
    const safeIntervalSec = Math.max(1, Math.floor(Number(intervalSec) || 1));
    const safeParticipation = Math.min(1, Math.max(0.01, Number(maxParticipationPct) || 0.15));
    const normalizedStyle = ["Twap", "Vwap", "Pov"].includes(style) ? style : "Twap";
    const maybeLimit = Number.isFinite(Number(limitPrice)) && Number(limitPrice) > 0 ? Number(limitPrice) : null;
    if (!Number.isFinite(safeTotalQty) || safeTotalQty <= 0) {
      setError("Algo total quantity must be > 0.");
      return;
    }

    setRunningTwap(true);
    setError("");
    try {
      await executeAlgoOrder({
        symbol,
        side,
        quantity: safeTotalQty,
        style: normalizedStyle,
        slices: safeSlices,
        intervalSeconds: safeIntervalSec,
        maxParticipationPct: safeParticipation,
        limitPrice: maybeLimit,
        allowPartialFill: true,
        maxRetriesPerSlice: 1,
        clientOrderId: `ALGO-${Date.now()}`,
      });
      await refreshTradingBook();
    } finally {
      setRunningTwap(false);
    }
  }, [orderForm.symbol, selectedOption, refreshTradingBook]);

  const runAutoHedgeExecution = useCallback(async () => {
    setRunningAutoHedge(true);
    setError("");
    try {
      await runAutoHedge({
        asset,
        targetDelta: 0,
        targetVega: 0,
        targetGamma: 0,
        maxLegs: 3,
        maxNotionalPerLeg: 150000,
        execute: true,
        useAlgoExecution: true,
        algoStyle: "Twap",
        algoSlices: 3,
        algoIntervalSeconds: 1,
        requestedBy: "ui",
      });
      await refreshTradingBook();
    } catch (err) {
      setError(err.message);
    } finally {
      setRunningAutoHedge(false);
    }
  }, [asset, refreshTradingBook]);

  const runStress = useCallback(async () => {
    setLoadingStress(true);
    try {
      setError("");
      const result = await runTradingStress({
        scenarios: [
          { name: "Spot -10%", underlyingShockPct: -0.1, ivShockPct: 0, daysForward: 0 },
          { name: "Spot +10%", underlyingShockPct: 0.1, ivShockPct: 0, daysForward: 0 },
          { name: "Vol +20%", underlyingShockPct: 0, ivShockPct: 0.2, daysForward: 0 },
          { name: "Crash -15 / Vol +25", underlyingShockPct: -0.15, ivShockPct: 0.25, daysForward: 1 },
        ],
      });
      setStress(result);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoadingStress(false);
    }
  }, []);

  useEffect(() => {
    if (activeTab !== "execution") return undefined;
    const symbol = orderForm.symbol.trim();
    const quantity = Number(orderForm.quantity);
    if (!symbol || !Number.isFinite(quantity) || quantity <= 0) {
      setPreview(null);
      return undefined;
    }

    const payload = {
      symbol,
      side: orderForm.side,
      quantity,
      type: orderForm.type,
    };
    if (orderForm.type === "Limit") {
      const limitPrice = Number(orderForm.limitPrice);
      if (!Number.isFinite(limitPrice) || limitPrice <= 0) {
        setPreview(null);
        return undefined;
      }
      payload.limitPrice = limitPrice;
    }

    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        setLoadingPreview(true);
        const data = await previewPaperOrder(payload);
        if (!cancelled) setPreview(data);
      } catch (err) {
        if (!cancelled) {
          setPreview({
            accepted: false,
            rejectReason: err.message,
            projectedRisk: null,
            estimatedFees: 0,
            estimatedInitialMargin: 0,
            estimatedMaintenanceMargin: 0,
          });
        }
      } finally {
        if (!cancelled) setLoadingPreview(false);
      }
    }, 320);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [activeTab, orderForm]);

  const resetBook = useCallback(async () => {
    try {
      setError("");
      await resetPaperBook();
      await refreshTradingBook();
      setStress(null);
    } catch (err) {
      setError(err.message);
    }
  }, [refreshTradingBook]);

  return (
    <div className="app-shell">
      <section className="hero">
        <div className="hero-top">
          <div>
            <h1 className="hero-title">Atlas Institutional Options Desk</h1>
            <p className="hero-subtitle">
              Live options chain (BTC/ETH/SOL/WTI), institutional paper execution, pre-trade risk guard,
              portfolio greeks and strategy analytics in one terminal-grade interface.
            </p>
            <div className="asset-switch">
              {ASSETS.map((symbol) => (
                <button
                  key={symbol}
                  className={`asset-button ${asset === symbol ? "active" : ""}`}
                  onClick={() => setAsset(symbol)}
                >
                  {symbol}
                </button>
              ))}
            </div>
          </div>
          <div className="status-col">
            <div className="status-chip">Last update: {formatTimeAgo(lastUpdate)}</div>
            <div className={`status-chip ${apiHealth === "down" ? "status-bad" : "status-good"}`}>
              API: {apiHealth}
            </div>
            <div className={`status-chip ${streamStatus === "live" ? "status-good" : "status-warn"}`}>
              Stream: {streamStatus}
            </div>
            <label className="stream-toggle">
              <input
                type="checkbox"
                checked={streamEnabled}
                onChange={(e) => setStreamEnabled(e.target.checked)}
              />
              live stream
            </label>
          </div>
        </div>
        <OverviewMetrics overview={currentOverview} />
        <TabBar activeTab={activeTab} onChange={setActiveTab} />
      </section>

      {activeTab === "market" && (
        <section className="desk-grid">
          <div>
            <SectionCard
              title="Live Options Chain"
              right={
                <div className="inline-controls">
                  <select className="select" value={selectedExpiry} onChange={(e) => setSelectedExpiry(e.target.value)}>
                    {expiries.map((exp) => (
                      <option key={exp} value={exp}>
                        {exp}
                      </option>
                    ))}
                  </select>
                  <select className="select" value={optionType} onChange={(e) => setOptionType(e.target.value)}>
                    {OPTION_TYPE_FILTERS.map((type) => (
                      <option key={type} value={type}>
                        {type.toUpperCase()}
                      </option>
                    ))}
                  </select>
                  <input
                    className="input"
                    placeholder="Search symbol"
                    value={search}
                    onChange={(e) => setSearch(e.target.value)}
                  />
                  <button className="btn btn-secondary" onClick={() => refreshAssetData()}>
                    Refresh
                  </button>
                </div>
              }
            >
              <MarketStatsStrip
                overview={currentOverview}
                selectedExpiry={selectedExpiry}
                optionType={optionType}
                totalQuotes={chain.length}
                filteredQuotes={filteredChain.length}
                lastUpdate={lastUpdate}
              />
              <div className="positioning-layout">
                <div>
                  <div className="section-subhead">Position Grid</div>
                  <OptionPositionGrid
                    quotes={positionGridRows}
                    selectedSymbol={selectedOption?.symbol || ""}
                    onSelect={selectOption}
                    onQuickOrder={quickOrderFromQuote}
                    onAddLeg={addLeg}
                  />
                </div>
                <div>
                  <div className="section-subhead">Option Details</div>
                  <OptionDetailsPanel
                    quote={selectedOption}
                    onQuickOrder={quickOrderFromQuote}
                    onAddLeg={addLeg}
                    onUseSymbol={useForOrder}
                  />
                </div>
              </div>
              <ChainTable
                rows={filteredChain.slice(0, 200)}
                onBuy={(quote) => addLeg(quote, "Buy")}
                onSell={(quote) => addLeg(quote, "Sell")}
                onUseSymbol={useForOrder}
                onSelect={selectOption}
                selectedSymbol={selectedOption?.symbol || ""}
              />
              {loadingChain && <div className="status-chip">Loading chain...</div>}
            </SectionCard>

            <div className="split" style={{ marginTop: 12 }}>
              <SectionCard title="Smile + Term Structure">
                <div style={{ width: "100%", height: 280 }}>
                  <ResponsiveContainer>
                    <LineChart data={smileData}>
                      <CartesianGrid strokeDasharray="3 3" stroke="rgba(15,23,35,0.12)" />
                      <XAxis dataKey="strike" />
                      <YAxis tickFormatter={(v) => `${v.toFixed(2)}`} />
                      <Tooltip formatter={(value) => `${(value * 100).toFixed(2)}%`} />
                      <Legend />
                      <Line type="monotone" dataKey="callIv" stroke="#0da37f" strokeWidth={2} dot={false} name="Call IV" />
                      <Line type="monotone" dataKey="putIv" stroke="#dc2626" strokeWidth={2} dot={false} name="Put IV" />
                      <Line type="monotone" dataKey="avgIv" stroke="#0686cc" strokeWidth={2} dot={false} name="ATM Blend" />
                    </LineChart>
                  </ResponsiveContainer>
                </div>
                <div style={{ width: "100%", height: 220, marginTop: 8 }}>
                  <ResponsiveContainer>
                    <LineChart data={termData}>
                      <CartesianGrid strokeDasharray="3 3" stroke="rgba(15,23,35,0.12)" />
                      <XAxis dataKey="dte" />
                      <YAxis tickFormatter={(v) => `${v.toFixed(1)}%`} />
                      <Tooltip formatter={(value) => `${value.toFixed(2)}%`} labelFormatter={(dte) => `${dte}D`} />
                      <Line type="monotone" dataKey="iv" stroke="#f59e0b" strokeWidth={2.2} dot />
                    </LineChart>
                  </ResponsiveContainer>
                </div>
              </SectionCard>

              <SectionCard title="Vol Surface Slice">
                <div style={{ width: "100%", height: 520 }}>
                  <ResponsiveContainer>
                    <ScatterChart margin={{ top: 8, right: 8, left: 0, bottom: 8 }}>
                      <CartesianGrid strokeDasharray="3 3" stroke="rgba(15,23,35,0.12)" />
                      <XAxis dataKey="dte" name="DTE" />
                      <YAxis dataKey="moneyness" name="Moneyness" />
                      <ZAxis dataKey="iv" range={[50, 320]} />
                      <Tooltip
                        formatter={(value, key) => (key === "moneyness" ? value.toFixed(3) : `${value.toFixed(2)}%`)}
                      />
                      <Scatter data={surfaceData} fill="#0686cc" />
                    </ScatterChart>
                  </ResponsiveContainer>
                </div>
              </SectionCard>
            </div>
          </div>
          <div>
            <SectionCard title="Macro Bias Engine">
              <LiveBiasPanel snapshot={liveBias} loading={loadingLiveBias} />
            </SectionCard>

            <SectionCard title="Quant Signal Board" className="mt12">
              <SignalBoard
                board={signalBoard}
                loading={loadingSignals}
                onSelect={selectSymbolFromSignals}
              />
            </SectionCard>

            <SectionCard title="Option Model Snapshot" className="mt12">
              <QuantModelPanel snapshot={modelSnapshot} loading={loadingModelSnapshot} />
            </SectionCard>

            <SectionCard title="Preset Strategies">
              <div className="inline-controls" style={{ marginBottom: 8 }}>
                <input
                  className="input"
                  style={{ width: 92 }}
                  type="number"
                  min="0.1"
                  step="0.1"
                  value={strategySize}
                  onChange={(e) => setStrategySize(Number(e.target.value) || 1)}
                />
                <span className="status-chip">size</span>
              </div>
              <div className="preset-list">
                {presets.map((preset) => (
                  <div className="preset" key={preset.name}>
                    <div className="preset-top">
                      <h4 className="preset-title">{preset.name}</h4>
                      <button className="btn btn-secondary" onClick={() => loadPreset(preset)}>
                        Load
                      </button>
                    </div>
                    <div className="preset-grid">
                      <div>Premium: {formatUsd(preset.netPremium, 2)}</div>
                      <div>MaxP: {formatUsd(preset.maxProfit, 2)}</div>
                      <div>MaxL: {formatUsd(preset.maxLoss, 2)}</div>
                      <div>R/R: {formatRatio(preset.rewardRiskRatio || 0, 2)}</div>
                      <div>PoP: {formatRatio((preset.probabilityOfProfitApprox || 0) * 100, 1)}%</div>
                      <div>EV: {formatUsd(preset.expectedValue || 0, 2)}</div>
                    </div>
                  </div>
                ))}
              </div>
              {loadingPresets && <div className="status-chip">Loading presets...</div>}
            </SectionCard>
          </div>
        </section>
      )}

      {activeTab === "execution" && (
        <section className="desk-grid">
          <div>
            <SectionCard title="Desk Snapshot">
              <BookSummaryCards book={book} />
              <RiskUtilizationBars risk={book.risk} limits={book.limits} />
            </SectionCard>

            <SectionCard title="Execution Ticket" className="mt12">
              <OrderTicket
                order={orderForm}
                onChange={(patch) => setOrderForm((prev) => ({ ...prev, ...patch }))}
                onSubmit={submitOrder}
                placing={placingOrder}
                selectedAsset={asset}
                previewQuote={previewQuote}
              />
              <div className="inline-controls" style={{ marginTop: 10 }}>
                <button className="btn btn-secondary" onClick={() => refreshTradingBook()}>
                  Refresh Book
                </button>
                <button className="btn btn-ghost" onClick={retryOpen} disabled={retryingOpenOrders || runningTwap}>
                  {retryingOpenOrders ? "Retrying..." : "Retry Open Orders"}
                </button>
                <button className="btn btn-secondary" onClick={runAutoHedgeExecution} disabled={runningAutoHedge || runningTwap}>
                  {runningAutoHedge ? "Auto-Hedging..." : "Run Auto-Hedge"}
                </button>
                <button className={`btn ${killSwitch?.isActive ? "btn-ghost" : "btn-secondary"}`} onClick={toggleKillSwitch} disabled={togglingKillSwitch}>
                  {togglingKillSwitch ? "Updating..." : killSwitch?.isActive ? "Disable Kill-Switch" : "Enable Kill-Switch"}
                </button>
                <button className="btn btn-ghost" onClick={resetBook}>
                  Reset Book
                </button>
              </div>
              <div className={`status-chip ${killSwitch?.isActive ? "status-bad" : "status-good"}`} style={{ marginTop: 8 }}>
                Kill-Switch: {killSwitch?.isActive ? `ON (${killSwitch?.reason || "manual"})` : "OFF"}
              </div>
            </SectionCard>

            <SectionCard title="Order Ladder + Smart Execution" className="mt12">
              <OrderLadder
                quote={selectedOption || previewQuote}
                onPlace={placeLadderOrder}
                placing={placingOrder || runningTwap}
              />
              <SmartExecutionPanel
                defaultSymbol={orderForm.symbol || selectedOption?.symbol || ""}
                onRunAlgo={runAlgoExecution}
                running={runningTwap}
              />
            </SectionCard>

            <SectionCard title="Pre-Trade Risk Lab" className="mt12">
              <RiskStressPanel
                preview={preview}
                stress={stress}
                loadingPreview={loadingPreview}
                loadingStress={loadingStress}
                onRunStress={runStress}
              />
            </SectionCard>

            <SectionCard title="Portfolio Risk" className="mt12">
              <RiskSummary risk={book.risk} limits={book.limits} />
              {book?.risk?.flags?.length > 0 && (
                <div className="error">Risk flags: {book.risk.flags.join(", ")}</div>
              )}
            </SectionCard>
          </div>

          <div>
            <SectionCard title="Positions">
              <PositionsTable positions={book.positions || []} />
            </SectionCard>
            <SectionCard title="Recent Orders" className="mt12">
              <OrdersTable orders={book.recentOrders || []} />
              {loadingBook && <div className="status-chip">Refreshing trading book...</div>}
            </SectionCard>
          </div>
        </section>
      )}

      {activeTab === "strategy" && (
        <section className="desk-grid">
          <div>
            <SectionCard
              title="Custom Strategy Builder"
              right={
                <button className="btn btn-primary" onClick={analyzeCustom}>
                  Analyze
                </button>
              }
            >
              <div className="inline-controls" style={{ marginBottom: 8 }}>
                <input
                  className="input"
                  style={{ flex: 1 }}
                  value={strategyName}
                  onChange={(e) => setStrategyName(e.target.value)}
                  placeholder="Strategy name"
                />
                <input
                  className="input"
                  type="number"
                  min="0.05"
                  max="0.9"
                  step="0.05"
                  value={shockRangePct}
                  onChange={(e) => setShockRangePct(Number(e.target.value) || 0.35)}
                  title="Shock range pct"
                />
              </div>

              <div className="strategy-legs">
                {customLegs.map((leg, idx) => (
                  <div className="strategy-leg" key={`${leg.symbol}-${idx}`}>
                    <input
                      className="input"
                      list="chain-symbols"
                      value={leg.symbol}
                      onChange={(e) => updateLeg(idx, { symbol: e.target.value })}
                    />
                    <select
                      className="select"
                      value={leg.direction}
                      onChange={(e) => updateLeg(idx, { direction: normalizeDirection(e.target.value) })}
                    >
                      <option value="Buy">Buy</option>
                      <option value="Sell">Sell</option>
                    </select>
                    <input
                      className="input"
                      type="number"
                      min="0.01"
                      step="0.01"
                      value={leg.quantity}
                      onChange={(e) => updateLeg(idx, { quantity: Number(e.target.value) || 0 })}
                    />
                    <button className="btn btn-ghost" onClick={() => removeLeg(idx)}>
                      x
                    </button>
                  </div>
                ))}
                <button
                  className="btn btn-ghost"
                  onClick={() =>
                    setCustomLegs((prev) => [...prev, { symbol: "", direction: "Buy", quantity: 1 }])
                  }
                >
                  + Add leg
                </button>
              </div>

              <datalist id="chain-symbols">
                {chain.map((q) => (
                  <option key={q.symbol} value={q.symbol} />
                ))}
              </datalist>
            </SectionCard>
          </div>

          <div>
            <SectionCard title="Strategy Payoff + Greeks">
              {analysis ? (
                <>
                  <div className="preset-grid">
                    <div>Premium: {formatUsd(analysis.netPremium, 2)}</div>
                    <div>MaxP: {formatUsd(analysis.maxProfit, 2)}</div>
                    <div>MaxL: {formatUsd(analysis.maxLoss, 2)}</div>
                    <div>R/R: {formatRatio(analysis.rewardRiskRatio || 0, 2)}</div>
                    <div>PoP: {formatRatio((analysis.probabilityOfProfitApprox || 0) * 100, 1)}%</div>
                    <div>EV: {formatUsd(analysis.expectedValue || 0, 2)}</div>
                    <div>Premium at Risk: {formatUsd(analysis.premiumAtRisk || 0, 2)}</div>
                  </div>

                  <div style={{ width: "100%", height: 260, marginTop: 8 }}>
                    <ResponsiveContainer>
                      <AreaChart data={analysis.payoffCurve}>
                        <CartesianGrid strokeDasharray="3 3" stroke="rgba(15,23,35,0.12)" />
                        <XAxis dataKey="spot" tickFormatter={(v) => v.toFixed(0)} />
                        <YAxis tickFormatter={(v) => v.toFixed(0)} />
                        <Tooltip formatter={(value) => formatUsd(value, 2)} />
                        <ReferenceLine y={0} stroke="#111827" strokeDasharray="4 4" />
                        <ReferenceLine x={analysis.underlyingPrice} stroke="#f59e0b" strokeDasharray="4 4" />
                        <Area type="monotone" dataKey="pnl" stroke="#0da37f" fill="rgba(13,163,127,0.25)" />
                      </AreaChart>
                    </ResponsiveContainer>
                  </div>

                  <div className="greek-grid">
                    <div className="greek-item">Delta: {formatSigned(analysis.aggregateGreeks.delta, 4)}</div>
                    <div className="greek-item">Gamma: {formatSigned(analysis.aggregateGreeks.gamma, 5)}</div>
                    <div className="greek-item">Vega: {formatSigned(analysis.aggregateGreeks.vega, 4)}</div>
                    <div className="greek-item">Theta: {formatSigned(analysis.aggregateGreeks.theta, 4)}</div>
                  </div>
                  {analysis.breakevens?.length > 0 && (
                    <div className="status-chip" style={{ marginTop: 8 }}>
                      Breakevens: {analysis.breakevens.map((b) => formatRatio(b, 2)).join(" / ")}
                    </div>
                  )}
                </>
              ) : (
                <div className="status-chip">Run analysis to display payoff and portfolio greeks.</div>
              )}
            </SectionCard>
          </div>
        </section>
      )}

      {activeTab === "alpha" && (
        <section className="desk-grid">
          <div>
            <SectionCard title="Model Calibration">
              <ModelCalibrationPanel calibration={calibration} loading={loadingCalibration} />
            </SectionCard>

            <SectionCard title="Relative Value Engine" className="mt12">
              <RelativeValueBoardPanel
                board={relativeValueBoard}
                loading={loadingRelativeValue}
                onSelectSymbol={selectSymbolFromSignals}
                onLoad={loadRecommendedStrategy}
              />
            </SectionCard>

            <SectionCard title="Vol Regime Engine" className="mt12">
              <VolRegimePanel regime={regime} loading={loadingRegime} />
            </SectionCard>

            <SectionCard title="Signal Context" className="mt12">
              <SignalBoard board={signalBoard} loading={loadingSignals} onSelect={selectSymbolFromSignals} />
            </SectionCard>

            <SectionCard title="No-Arbitrage Scanner" className="mt12">
              <ArbitrageScannerPanel
                scan={arbitrageScan}
                loading={loadingArbitrage}
                onSelectSymbol={selectSymbolFromSignals}
              />
            </SectionCard>

            <SectionCard title="Greeks Exposure Heatmap" className="mt12">
              <GreeksExposureHeatmapPanel
                grid={exposureGrid}
                loading={loadingExposure}
                metric={exposureMetric}
                onMetricChange={setExposureMetric}
                onFocusCell={focusExposureCell}
              />
            </SectionCard>
          </div>

          <div>
            <SectionCard title="Strategy Recommender">
              <StrategyRecommendationPanel
                board={recommendationBoard}
                loading={loadingRecommendations}
                riskProfile={riskProfile}
                onRiskProfileChange={setRiskProfile}
                onLoad={loadRecommendedStrategy}
              />
            </SectionCard>

            <SectionCard title="Greeks Target Optimizer" className="mt12">
              <StrategyOptimizerPanel
                board={optimizerBoard}
                loading={loadingOptimizer}
                targets={optimizerTargets}
                onTargetsChange={(patch) =>
                  setOptimizerTargets((prev) => ({ ...prev, ...patch }))
                }
                onLoad={loadRecommendedStrategy}
              />
            </SectionCard>
          </div>
        </section>
      )}

      {activeTab === "experimental" && (
        <section className="desk-grid">
          <div style={{ gridColumn: "1 / -1" }}>
            <SectionCard title="Autopilot Portfolio">
              <ExperimentalBotPanel
                snapshot={botSnapshot}
                loading={loadingBot}
                onRefresh={() => refreshExperimentalBot()}
              />
            </SectionCard>
          </div>
        </section>
      )}

      {error && <div className="error">{error}</div>}
    </div>
  );
}

export default App;
