const ENV_BASE = String(import.meta.env.VITE_API_BASE_URL || "").replace(/\/+$/, "");

async function request(path) {
  const base = ENV_BASE || "";
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15000);
  try {
    const response = await fetch(`${base}${path}`, {
      signal: controller.signal,
      headers: { Accept: "application/json" },
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

export function getSnapshot() {
  return request(`/api/polymarket/live?lookaheadMinutes=${24 * 60}&maxMarkets=24`);
}
