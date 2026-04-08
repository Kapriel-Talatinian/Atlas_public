# Atlas Incident Recovery Runbook

## Objective
Restore trading safety and service health with minimal risk while preserving auditability.

## Triggers
- Critical alerts in `/api/system/alerts`
- Degraded health in `/api/system/health`
- Market data stale or unavailable in `/api/system/market-data`
- Reconciliation issues in `/api/trading/orders/reconcile`

## Immediate Actions (T+0 to T+5 minutes)
1. Activate kill-switch if not already active.
2. Freeze new risk-taking orders.
3. Run recovery playbook:
   - `GET /api/system/recovery-playbook`
   - `POST /api/system/recovery/execute?dryRun=false`
4. Capture current state snapshot:
   - `/api/system/ops`
   - `/api/trading/history`

## OMS Integrity
1. Run `GET /api/trading/orders/reconcile?limit=500`.
2. If critical mismatches exist:
   - keep kill-switch active,
   - cancel orphan/open inconsistent orders,
   - re-run reconciliation.

## Market Data Degradation
1. Verify source status (`/api/system/market-data`).
2. Confirm fallback mode behavior.
3. If real venues recovered, monitor switch-back and stale lag.

## Margin and Liquidation
1. Inspect `/api/trading/risk` for maintenance breach.
2. If margin ratio under threshold, reduce gross notional and maintain kill-switch.
3. Confirm liquidation events are recorded in trading history.

## Service Recovery and Validation
1. Backend health endpoint must be green.
2. No new critical alerts for at least 10 minutes.
3. Reconcile OMS and verify no critical issues.
4. Disable kill-switch only after risk manager approval.

## Post-Incident (T+30+)
1. Export persisted audit trail from `/api/trading/history`.
2. Write incident summary (root cause, impact, timeline, corrective actions).
3. Add preventive actions to backlog and link to CI/CD checks.
