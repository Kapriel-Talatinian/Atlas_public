const IS_DEV = Boolean(import.meta.env.DEV);
const LOCAL_FALLBACK_BASES = [
  "http://127.0.0.1:5000",
  "http://127.0.0.1:5050",
  "http://127.0.0.1:5186",
  "http://localhost:5000",
  "http://localhost:5050",
  "http://localhost:5186",
];
const FALLBACK_BASES = IS_DEV ? LOCAL_FALLBACK_BASES : [];
const REQUEST_TIMEOUT_MS = 12000;
const RETRYABLE_HTTP_STATUS = new Set([404, 500, 502, 503, 504]);
const ENV_BASE = normalizeBase(import.meta.env.VITE_API_BASE_URL || "");
let preferredBase = ENV_BASE || "";

function normalizeBase(value) {
  return String(value || "").trim().replace(/\/+$/, "");
}

function candidateBases() {
  const list = [];
  if (preferredBase) list.push(preferredBase);
  if (ENV_BASE) list.push(ENV_BASE);
  if (IS_DEV) list.push(...FALLBACK_BASES);
  if (IS_DEV || !ENV_BASE) list.push("");
  return [...new Set(list)];
}

function streamCandidateBases() {
  const list = [];
  if (preferredBase) list.push(preferredBase);
  if (ENV_BASE) list.push(ENV_BASE);
  if (IS_DEV) list.push(...FALLBACK_BASES);
  if (!preferredBase && !ENV_BASE && (IS_DEV || !ENV_BASE)) list.push("");
  return [...new Set(list)];
}

function withTimeout(ms) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), ms);
  return { signal: controller.signal, clear: () => clearTimeout(timeout) };
}

function buildHeaders(options) {
  const headers = { ...(options.headers || {}) };
  const hasBody = options.body !== undefined && options.body !== null;
  const hasContentType = Object.keys(headers).some((key) => key.toLowerCase() === "content-type");
  if (hasBody && !hasContentType) headers["Content-Type"] = "application/json";
  return headers;
}

function isNetworkLike(err) {
  const text = String(err?.message || "");
  return (
    err?.name === "AbortError" ||
    err instanceof TypeError ||
    /Failed to fetch|NetworkError|Load failed|timeout/i.test(text)
  );
}

function parseJsonSafe(text) {
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function extractApiErrorMessage(status, statusText, text, payload) {
  if (payload && typeof payload === "object") {
    if (typeof payload.rejectReason === "string" && payload.rejectReason.trim()) {
      return payload.rejectReason.trim();
    }
    if (typeof payload.message === "string" && payload.message.trim()) {
      return payload.message.trim();
    }
    if (typeof payload.title === "string" && payload.title.trim()) {
      return payload.title.trim();
    }
  }
  if (typeof text === "string" && text.trim()) {
    return text.trim();
  }
  return `API ${status} ${statusText}`;
}

async function fetchJson(base, path, options = {}) {
  const { signal, clear } = withTimeout(REQUEST_TIMEOUT_MS);
  try {
    const response = await fetch(`${base}${path}`, {
      ...options,
      signal,
      headers: buildHeaders(options),
    });

    if (!response.ok) {
      const text = await response.text();
      const payload = parseJsonSafe(text);
      const reason = extractApiErrorMessage(response.status, response.statusText, text, payload);
      const error = new Error(reason);
      error.status = response.status;
      error.statusText = response.statusText;
      error.payload = payload;
      error.isHttpError = true;
      throw error;
    }

    if (response.status === 204) return null;
    return response.json();
  } catch (err) {
    if (err?.name === "AbortError") {
      throw new Error(`Request timeout after ${REQUEST_TIMEOUT_MS}ms`);
    }
    throw err;
  } finally {
    clear();
  }
}

async function request(path, options = {}) {
  const bases = candidateBases();
  let lastError = null;

  for (const base of bases) {
    try {
      const payload = await fetchJson(base, path, options);
      preferredBase = base;
      return payload;
    } catch (err) {
      lastError = err;
      const retryableHttp = RETRYABLE_HTTP_STATUS.has(err?.status);
      if (!isNetworkLike(err) && !retryableHttp) {
        throw err;
      }
    }
  }

  if (lastError && lastError?.isHttpError) {
    throw lastError;
  }

  const hint = IS_DEV
    ? `Check that API is running on ${LOCAL_FALLBACK_BASES.join(" / ")} or set VITE_API_BASE_URL.`
    : "Set VITE_API_BASE_URL to your deployed API URL (for example https://<atlas-api>.up.railway.app).";

  throw new Error(`${lastError?.message || "Request failed"}\n${hint}`);
}

export function getMarketStreamUrls({ asset, expiry, chainLimit = 80 }) {
  const params = new URLSearchParams({
    asset,
    chainLimit: String(chainLimit),
  });
  if (expiry) params.set("expiry", expiry);
  return streamCandidateBases().map((base) => `${base}/api/options/stream?${params.toString()}`);
}

export function createMarketStream(url) {
  return new EventSource(url);
}

export async function getHealth() {
  try {
    return await request("/api/system/health");
  } catch {
    return request("/health");
  }
}

export function getAssetsOverview(assets = ["BTC", "ETH", "SOL", "WTI"]) {
  const query = encodeURIComponent(assets.join(","));
  return request(`/api/options/assets?assets=${query}`);
}

export function getExpiries(asset) {
  return request(`/api/options/expiries?asset=${encodeURIComponent(asset)}`);
}

export function getOptionChain({ asset, expiry, type = "all", limit = 260 }) {
  const params = new URLSearchParams({
    asset,
    type,
    limit: String(limit),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/chain?${params.toString()}`);
}

export function getVolSurface({ asset, limit = 700 }) {
  const params = new URLSearchParams({
    asset,
    limit: String(limit),
  });
  return request(`/api/options/surface?${params.toString()}`);
}

export function getOptionModelSnapshot(symbol) {
  return request(`/api/options/models?symbol=${encodeURIComponent(symbol)}`);
}

export function getModelCalibration({ asset, expiry }) {
  const params = new URLSearchParams({ asset });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/calibration?${params.toString()}`);
}

export function getOptionSignals({ asset, expiry, type = "all", limit = 120 }) {
  const params = new URLSearchParams({
    asset,
    type,
    limit: String(limit),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/signals?${params.toString()}`);
}

export function getVolRegime(asset) {
  return request(`/api/options/regime?asset=${encodeURIComponent(asset)}`);
}

export function getMacroBias({
  asset,
  horizonDays = 30,
  growthMomentum = 0,
  inflationShock = 0,
  policyTightening = 0,
  usdStrength = 0,
  liquidityStress = 0,
  supplyShock = 0,
  riskAversion = 0,
}) {
  const params = new URLSearchParams({
    asset,
    horizonDays: String(horizonDays),
    growthMomentum: String(growthMomentum),
    inflationShock: String(inflationShock),
    policyTightening: String(policyTightening),
    usdStrength: String(usdStrength),
    liquidityStress: String(liquidityStress),
    supplyShock: String(supplyShock),
    riskAversion: String(riskAversion),
  });
  return request(`/api/options/macro-bias?${params.toString()}`);
}

export function getLiveBias({ asset, horizonDays = 30 }) {
  const params = new URLSearchParams({
    asset,
    horizonDays: String(horizonDays),
  });
  return request(`/api/options/live-bias?${params.toString()}`);
}

export function getStrategyRecommendations({ asset, expiry, size = 1, riskProfile = "balanced" }) {
  const params = new URLSearchParams({
    asset,
    size: String(size),
    riskProfile,
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/recommendations?${params.toString()}`);
}

export function getStrategyOptimizer({
  asset,
  expiry,
  size = 1,
  riskProfile = "balanced",
  targetDelta = 0,
  targetVega = 0,
  targetTheta = 0,
}) {
  const params = new URLSearchParams({
    asset,
    size: String(size),
    riskProfile,
    targetDelta: String(targetDelta),
    targetVega: String(targetVega),
    targetTheta: String(targetTheta),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/optimizer?${params.toString()}`);
}

export function getGreeksExposureGrid({
  asset,
  expiry,
  maxExpiries = 6,
  maxStrikes = 24,
}) {
  const params = new URLSearchParams({
    asset,
    maxExpiries: String(maxExpiries),
    maxStrikes: String(maxStrikes),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/exposure-grid?${params.toString()}`);
}

export function getRelativeValueBoard({
  asset,
  expiry,
  limit = 18,
}) {
  const params = new URLSearchParams({
    asset,
    limit: String(limit),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/rv-board?${params.toString()}`);
}

export function getArbitrageScan({ asset, expiry, limit = 120 }) {
  const params = new URLSearchParams({
    asset,
    limit: String(limit),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/arbitrage?${params.toString()}`);
}

export function getPresetStrategies({ asset, expiry, size = 1 }) {
  const params = new URLSearchParams({
    asset,
    size: String(size),
  });
  if (expiry) params.set("expiry", expiry);
  return request(`/api/options/strategies/presets?${params.toString()}`);
}

export function analyzeCustomStrategy(payload) {
  return request("/api/options/strategies/analyze", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function getTradingBook(orderLimit = 150) {
  return request(`/api/trading/book?orderLimit=${orderLimit}`);
}

export function getTradingNotifications(limit = 120) {
  return request(`/api/trading/notifications?limit=${limit}`);
}

export function getTradingRisk() {
  return request("/api/trading/risk");
}

export function getTradingLimits() {
  return request("/api/trading/limits");
}

export function retryOpenOrders(maxOrders = 25) {
  return request(`/api/trading/orders/retry?maxOrders=${maxOrders}`, {
    method: "POST",
  });
}

export function getKillSwitchState() {
  return request("/api/trading/killswitch");
}

export function setKillSwitchState(payload) {
  return request("/api/trading/killswitch", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function placePaperOrder(payload) {
  return request("/api/trading/orders", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function executeAlgoOrder(payload) {
  return request("/api/trading/algo/execute", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function runAutoHedge(payload) {
  return request("/api/trading/hedge/auto", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function previewPaperOrder(payload) {
  return request("/api/trading/preview", {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function runTradingStress(payload) {
  return request("/api/trading/stress", {
    method: "POST",
    body: JSON.stringify(payload || { scenarios: [] }),
  });
}

export function resetPaperBook() {
  return request("/api/trading/reset", {
    method: "POST",
  });
}

export function getSystemOps() {
  return request("/api/system/ops");
}

export function getSystemSlo() {
  return request("/api/system/slo");
}

export function getTradingHistory({ orderLimit = 250, positionLimit = 250, riskLimit = 250, auditLimit = 250 } = {}) {
  const params = new URLSearchParams({
    orderLimit: String(orderLimit),
    positionLimit: String(positionLimit),
    riskLimit: String(riskLimit),
    auditLimit: String(auditLimit),
  });
  return request(`/api/trading/history?${params.toString()}`);
}

export function getExperimentalBotSnapshot(asset = "MULTI") {
  return request(`/api/experimental/bot/snapshot?asset=${encodeURIComponent(asset)}`);
}

export function configureExperimentalBot(asset = "MULTI", payload = {}) {
  return request(`/api/experimental/bot/configure?asset=${encodeURIComponent(asset)}`, {
    method: "POST",
    body: JSON.stringify(payload),
  });
}

export function runExperimentalBotCycles(asset = "MULTI", cycles = 1) {
  return request(
    `/api/experimental/bot/run?asset=${encodeURIComponent(asset)}&cycles=${encodeURIComponent(String(cycles))}`,
    { method: "POST" }
  );
}

export function resetExperimentalBot(asset = "MULTI") {
  return request(`/api/experimental/bot/reset?asset=${encodeURIComponent(asset)}`, {
    method: "POST",
  });
}

export function getPolymarketLiveSnapshot({ lookaheadMinutes = 24 * 60, maxMarkets = 24 } = {}) {
  const params = new URLSearchParams({
    lookaheadMinutes: String(lookaheadMinutes),
    maxMarkets: String(maxMarkets),
  });
  return request(`/api/polymarket/live?${params.toString()}`);
}
