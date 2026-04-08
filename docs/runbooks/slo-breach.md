# Atlas SLO Breach Runbook

## Scope
Handle availability/latency SLO breaches exposed by `/api/system/slo`.

## SLO Targets
- Availability: `>= 99.5%`
- P95 latency: `<= 450ms`

## Detection
1. Check `/api/system/slo` and identify breached windows (`5m`, `1h`).
2. Correlate with:
   - `/api/system/alerts`
   - `/api/system/market-data`
   - `/api/system/ops`

## Immediate Mitigation
1. If availability breach includes 5xx surge:
   - Enable kill-switch (`POST /api/trading/killswitch`) to prevent new risk.
   - Run recovery playbook (`POST /api/system/recovery/execute`).
2. If latency breach dominates:
   - Reduce heavy polling jobs on UI.
   - Reduce expensive scans frequency (surface/arbitrage/optimizer).
   - Scale API replicas and verify DB I/O latency.

## Validation
1. Confirm no active critical alerts for 10 minutes.
2. Confirm `/api/system/slo` reports no breach on 5m window.
3. Re-enable normal execution if risk manager approves.

## Postmortem
- Root cause, impact window, mitigation timeline.
- Add preventive checks in CI/CD and monitoring thresholds.
