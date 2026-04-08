# Atlas Zero-Downtime Deploy Playbook

## Objective
Deploy API/UI without interrupting order/risk visibility.

## Pre-Deploy Gate
1. `dotnet build Atlas.sln`
2. `dotnet test Atlas.sln`
3. `cd frontend && npm run build`
4. Verify smoke endpoints locally:
   - `/health`
   - `/api/system/health`
   - `/api/system/slo`

## Deployment Strategy
- Use rolling or blue/green deployment.
- Keep at least one healthy API instance during rollout.
- Keep a single active bot worker during rollout.
- Preserve persistent storage for:
  - `TRADING_DB_PATH`
- Preserve central bot runtime storage:
  - `BOT_RUNTIME_DB_CONNECTION_STRING`
- Keep `ATLAS_RUNTIME_ROLE=api` on HTTP replicas and `ATLAS_RUNTIME_ROLE=bot-worker` on the dedicated worker.

## Health Gate
After deploy, verify:
1. `/health` => `ok=true`
2. `/api/system/health` => not degraded
3. `/api/system/slo` => no active 5m breach
4. `/api/trading/orders/reconcile` => no critical mismatch
5. `/api/experimental/runtime` => expected role / backend wiring
6. `/api/experimental/runtime/health` => leader healthy, lease valid

## Rollback Criteria
Rollback immediately if any of:
- sustained 5xx > 1% for 5 minutes,
- SLO 5m breach persists after mitigation,
- OMS reconciliation reports critical issues,
- market data unavailable for core assets.

## Rollback Steps
1. Route traffic back to previous healthy release.
2. Keep kill-switch active during rollback if risk is degraded.
3. Re-run health gate and reconciliation checks.
