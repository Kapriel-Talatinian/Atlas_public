#!/usr/bin/env python3
"""
Atlas pricing demo.

Purpose:
- pick a liquid-ish option from the Atlas chain,
- compare market mid vs BS/Heston/SABR fair values,
- call the generic pricing comparison endpoint,
- print a small smile slice and calibration summary.

Usage:
    python3 examples/pricing_demo.py
    python3 examples/pricing_demo.py --asset ETH --right put
    python3 examples/pricing_demo.py --base-url http://127.0.0.1:5000 --asset BTC --expiry 2026-03-11
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from typing import Any


def fetch_json(base_url: str, path: str, params: dict[str, Any] | None = None) -> Any:
    query = f"?{urllib.parse.urlencode(params or {}, doseq=True)}" if params else ""
    url = f"{base_url.rstrip('/')}{path}{query}"
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    with urllib.request.urlopen(request, timeout=20) as response:
        return json.loads(response.read().decode("utf-8"))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run a pricing comparison against a local Atlas API.")
    parser.add_argument("--base-url", default="http://127.0.0.1:5000", help="Atlas API base URL")
    parser.add_argument("--asset", default="BTC", help="Underlying asset, e.g. BTC, ETH, SOL")
    parser.add_argument("--expiry", default="", help="Optional expiry in YYYY-MM-DD")
    parser.add_argument("--right", default="call", choices=["call", "put"], help="Option right to inspect")
    parser.add_argument("--rate", type=float, default=0.048, help="Risk-free rate used for /api/pricing/compare")
    parser.add_argument("--surface-points", type=int, default=9, help="How many smile points to print")
    return parser.parse_args()


def parse_timestamp(raw: str) -> datetime:
    normalized = raw.replace("Z", "+00:00")
    return datetime.fromisoformat(normalized)


def fmt_number(value: float, decimals: int = 4) -> str:
    if value is None or not math.isfinite(value):
        return "-"
    return f"{value:,.{decimals}f}"


def pct(value: float, decimals: int = 2) -> str:
    if value is None or not math.isfinite(value):
        return "-"
    return f"{value * 100:.{decimals}f}%"


def print_section(title: str) -> None:
    print()
    print(title)
    print("-" * len(title))


def choose_expiry(base_url: str, asset: str, explicit_expiry: str) -> str:
    if explicit_expiry:
        return explicit_expiry
    expiries = fetch_json(base_url, "/api/options/expiries", {"asset": asset})
    if not expiries:
        raise RuntimeError(f"No expiries returned for asset={asset}.")
    return expiries[0]


def choose_contract(chain: list[dict[str, Any]], right: str) -> dict[str, Any]:
    filtered = [quote for quote in chain if str(quote.get("right", "")).lower() == right.lower()]
    if not filtered:
        raise RuntimeError(f"No {right} quotes found in selected chain.")

    def score(quote: dict[str, Any]) -> tuple[float, float, float]:
        spot = float(quote.get("underlyingPrice") or 0.0)
        strike = float(quote.get("strike") or 0.0)
        turnover = float(quote.get("turnover24h") or 0.0)
        volume = float(quote.get("volume24h") or 0.0)
        distance = abs(strike - spot) if spot > 0 else abs(strike)
        return (distance, -turnover, -volume)

    return sorted(filtered, key=score)[0]


def smile_slice(surface: list[dict[str, Any]], expiry: str, right: str, spot: float, target_count: int) -> list[dict[str, Any]]:
    scoped = [
        point for point in surface
        if str(point.get("expiry", "")).startswith(expiry)
        and str(point.get("right", "")).lower() == right.lower()
        and float(point.get("markIv") or 0.0) > 0.0
    ]
    scoped.sort(key=lambda point: abs(float(point.get("strike") or 0.0) - spot))
    scoped = scoped[: max(target_count * 2, target_count)]
    scoped.sort(key=lambda point: float(point.get("strike") or 0.0))
    if len(scoped) <= target_count:
        return scoped

    center = min(range(len(scoped)), key=lambda i: abs(float(scoped[i].get("strike") or 0.0) - spot))
    half = target_count // 2
    start = max(0, center - half)
    end = min(len(scoped), start + target_count)
    start = max(0, end - target_count)
    return scoped[start:end]


def print_market_snapshot(snapshot: dict[str, Any]) -> None:
    print_section("Market vs model snapshot")
    rows = [
        ("Symbol", snapshot["symbol"]),
        ("Spot", fmt_number(float(snapshot["spot"]), 2)),
        ("Strike", fmt_number(float(snapshot["strike"]), 2)),
        ("Expiry", str(snapshot["expiry"])[:10]),
        ("Right", snapshot["right"]),
        ("Mid", fmt_number(float(snapshot["mid"]), 4)),
        ("Mark", fmt_number(float(snapshot["mark"]), 4)),
        ("Mark IV", pct(float(snapshot["markIv"]))),
        ("Fair BS", fmt_number(float(snapshot["fairBs"]), 4)),
        ("Fair Heston", fmt_number(float(snapshot["fairHeston"]), 4)),
        ("Fair SABR", fmt_number(float(snapshot["fairSabr"]), 4)),
        ("Fair Composite", fmt_number(float(snapshot["fairComposite"]), 4)),
        ("Edge vs mid", pct(float(snapshot["edgeVsMidPct"]))),
        ("Model dispersion", pct(float(snapshot["modelDispersionPct"]))),
        ("Confidence", fmt_number(float(snapshot["confidenceScore"]), 2)),
        ("Signal", snapshot["signal"]),
    ]
    width = max(len(label) for label, _ in rows)
    for label, value in rows:
        print(f"{label:<{width}} : {value}")


def print_compare_table(results: list[dict[str, Any]]) -> None:
    print_section("Generic pricing endpoint comparison")
    headers = ("Model", "Price", "IV", "Delta", "Gamma", "Vega", "Theta", "Latency ms")
    print(f"{headers[0]:<16} {headers[1]:>12} {headers[2]:>10} {headers[3]:>10} {headers[4]:>12} {headers[5]:>10} {headers[6]:>10} {headers[7]:>12}")
    for result in results:
        greeks = result["greeks"]
        latency_ms = float(result.get("computeTime", "0:0:0").split(":")[-1]) if isinstance(result.get("computeTime"), str) else 0.0
        print(
            f"{result['model']:<16} "
            f"{fmt_number(float(result['price']), 4):>12} "
            f"{pct(float(result['impliedVol'])):>10} "
            f"{fmt_number(float(greeks['delta']), 4):>10} "
            f"{fmt_number(float(greeks['gamma']), 6):>12} "
            f"{fmt_number(float(greeks['vega']), 4):>10} "
            f"{fmt_number(float(greeks['theta']), 4):>10} "
            f"{fmt_number(latency_ms * 1000.0, 3):>12}"
        )


def print_calibration(calibration: dict[str, Any]) -> None:
    print_section("Calibration summary")
    rows = [
        ("Asset", calibration["asset"]),
        ("Expiry scope", str(calibration.get("expiry") or "all")[:10]),
        ("Spot", fmt_number(float(calibration["spot"]), 2)),
        ("ATM IV 30D", pct(float(calibration["atmIv30D"]))),
        ("Skew 25D", pct(float(calibration["skew25D"]))),
        ("Term slope 30D-90D", pct(float(calibration["termSlope30To90"]))),
        ("Heston kappa", fmt_number(float(calibration["hestonKappa"]), 4)),
        ("Heston theta", fmt_number(float(calibration["hestonTheta"]), 4)),
        ("Heston xi", fmt_number(float(calibration["hestonXi"]), 4)),
        ("Heston rho", fmt_number(float(calibration["hestonRho"]), 4)),
        ("SABR alpha", fmt_number(float(calibration["sabrAlpha"]), 4)),
        ("SABR beta", fmt_number(float(calibration["sabrBeta"]), 4)),
        ("SABR rho", fmt_number(float(calibration["sabrRho"]), 4)),
        ("SABR nu", fmt_number(float(calibration["sabrNu"]), 4)),
        ("Confidence", fmt_number(float(calibration["confidenceScore"]), 2)),
    ]
    width = max(len(label) for label, _ in rows)
    for label, value in rows:
        print(f"{label:<{width}} : {value}")

    print()
    print("Fit metrics")
    print(f"{'Model':<14} {'MAE %':>10} {'RMSE %':>10} {'Samples':>10}")
    for metric in calibration.get("fitMetrics", []):
        print(
            f"{metric['model']:<14} "
            f"{fmt_number(float(metric['meanAbsErrorPct']), 3):>10} "
            f"{fmt_number(float(metric['rootMeanSquareErrorPct']), 3):>10} "
            f"{int(metric['sampleCount']):>10}"
        )


def print_smile(points: list[dict[str, Any]], spot: float) -> None:
    print_section("Smile slice")
    print(f"{'Strike':>12} {'Moneyness':>12} {'IV':>10} {'ATM dist %':>12}")
    for point in points:
        strike = float(point["strike"])
        moneyness = float(point["moneyness"])
        mark_iv = float(point["markIv"])
        distance = strike / spot - 1.0 if spot > 0 else 0.0
        print(
            f"{fmt_number(strike, 2):>12} "
            f"{fmt_number(moneyness, 4):>12} "
            f"{pct(mark_iv):>10} "
            f"{pct(distance):>12}"
        )


def main() -> int:
    args = parse_args()
    asset = args.asset.upper()
    try:
        expiry = choose_expiry(args.base_url, asset, args.expiry)
        chain = fetch_json(args.base_url, "/api/options/chain", {"asset": asset, "expiry": expiry, "type": "all", "limit": 320})
        if not chain:
            raise RuntimeError(f"Empty chain for asset={asset}, expiry={expiry}.")

        quote = choose_contract(chain, args.right)
        snapshot = fetch_json(args.base_url, "/api/options/models", {"symbol": quote["symbol"]})
        calibration = fetch_json(args.base_url, "/api/options/calibration", {"asset": asset, "expiry": expiry})
        surface = fetch_json(args.base_url, "/api/options/surface", {"asset": asset, "limit": 600})

        expiry_dt = parse_timestamp(quote["expiry"])
        now = datetime.now(timezone.utc)
        tte_days = max((expiry_dt - now).total_seconds() / 86400.0, 1.0 / 24.0)
        compare = fetch_json(
            args.base_url,
            "/api/pricing/compare",
            {
                "spot": quote["underlyingPrice"],
                "strike": quote["strike"],
                "vol": quote["markIv"],
                "tte": tte_days,
                "rate": args.rate,
                "type": args.right,
                "mcPaths": 12000,
                "binSteps": 160,
            },
        )

        print(f"Atlas pricing demo on {args.base_url}")
        print(f"Asset={asset} Expiry={expiry} Right={args.right}")
        print_market_snapshot(snapshot)
        print_compare_table(compare)
        print_calibration(calibration)
        print_smile(smile_slice(surface, expiry, args.right, float(snapshot["spot"]), args.surface_points), float(snapshot["spot"]))

        print()
        print("Notes")
        print("-----")
        print("- /api/options/models reflects the desk snapshot against market mid/mark.")
        print("- /api/pricing/compare reprices the same contract through BS, Heston, Monte Carlo, Binomial, and SABR.")
        print("- /api/options/calibration exposes the heuristic surface-fit state used by Atlas.")
        return 0
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        print(f"HTTP error {exc.code}: {body}", file=sys.stderr)
        return 1
    except urllib.error.URLError as exc:
        print(f"Network error: {exc}. Is Atlas API running on {args.base_url}?", file=sys.stderr)
        return 1
    except Exception as exc:  # pragma: no cover - demo script
        print(f"Error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
