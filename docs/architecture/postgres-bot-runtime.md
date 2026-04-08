# Atlas Postgres Bot Runtime Schema

## Objective

This schema replaces the local `shared-portfolio.json` authority with a Postgres-backed runtime model suitable for Railway, VPS, or any multi-replica cloud deployment.

It is designed around one live shared portfolio bot:

- `bot_key = 'MULTI'`
- assets: `BTC`, `ETH`, `SOL`
- one active leader at a time
- full recovery after restart

SQL reference file:

- [`docs/sql/postgres-bot-runtime.sql`](/Users/kapriel.talatinian/Desktop/atlas/docs/sql/postgres-bot-runtime.sql)

## Design Principles

- append-only where history matters
- transactional update for mutable runtime state
- explicit leader election
- fencing token to block stale writes
- JSONB for evolving analytics payloads
- narrow relational columns for operational queries

## Core Tables

### `atlas_bot_leader_lock`

Purpose:

- controls exclusive trading authority

Important columns:

- `bot_key`
- `owner_instance_id`
- `owner_hostname`
- `fencing_token`
- `lease_until`
- `last_heartbeat_at`
- `updated_at`

Rules:

- one row per bot key
- valid leader iff `lease_until > now()`
- each successful takeover increments `fencing_token`

### `atlas_bot_runtime_state`

Purpose:

- single-row mutable snapshot of portfolio runtime

Important columns:

- `bot_key`
- `started_at`
- `last_evaluation_at`
- `last_signal_json`
- `neural_signals_json`
- `config_json`
- `peak_equity`
- `max_drawdown`
- `state_version`
- `last_cycle_status`
- `last_cycle_duration_ms`
- `last_persisted_at`

This table is the fast read model used by the snapshot endpoint.

### `atlas_bot_weights`

Purpose:

- stores live adaptive feature weights

Key:

- `(bot_key, feature_name)`

Columns:

- `weight`
- `updated_at`

### `atlas_bot_open_trades`

Purpose:

- authoritative set of active structures

Important columns:

- `trade_id`
- `bot_key`
- `asset`
- `primary_symbol`
- `strategy_template`
- `bias`
- `entry_net_premium`
- `current_liquidation_value`
- `unrealized_pnl`
- `max_profit`
- `max_loss`
- `expected_value`
- `entry_score`
- `confidence`
- `thesis`
- `math_summary`
- `rationale`
- `drivers_json`
- `features_json`
- `legs_json`
- `entry_time`
- `updated_at`

### `atlas_bot_closed_trades`

Purpose:

- immutable archive of completed structures

Same payload class as `open_trades`, plus:

- `exit_time`
- `exit_reason`
- `realized_pnl`
- `closed_at`

### `atlas_bot_decisions`

Purpose:

- append-only journal of bot actions

Columns:

- `decision_id`
- `bot_key`
- `timestamp`
- `bias`
- `score`
- `confidence`
- `action`
- `reason`
- `signal_json`

### `atlas_bot_audits`

Purpose:

- append-only trade audit and learning history

Columns:

- `audit_id`
- `bot_key`
- `trade_id`
- `timestamp`
- `asset`
- `strategy_template`
- `realized_pnl`
- `realized_pnl_pct`
- `win`
- `exit_reason`
- `rolling_win_rate`
- `rolling_profit_factor`
- `rolling_drawdown_pct`
- `learning_comment`
- `max_loss`
- `reward_risk_ratio`
- `math_summary`
- `payload_json`

### `atlas_bot_spot_history`

Purpose:

- compact spot history for feature reconstruction and restart recovery

Key:

- `(bot_key, asset, observed_at)`

### `atlas_bot_cycle_events`

Purpose:

- operational event stream for cycle-level debugging

Columns:

- `cycle_id`
- `bot_key`
- `instance_id`
- `fencing_token`
- `started_at`
- `completed_at`
- `status`
- `duration_ms`
- `opened_trades`
- `closed_trades`
- `payload_json`

## Transaction Model

A single cycle should save in one transaction:

1. validate current fencing token
2. update `atlas_bot_runtime_state`
3. upsert `atlas_bot_weights`
4. upsert `atlas_bot_open_trades`
5. insert `atlas_bot_closed_trades`
6. insert `atlas_bot_decisions`
7. insert `atlas_bot_audits`
8. insert `atlas_bot_spot_history`
9. insert `atlas_bot_cycle_events`

If fencing token mismatches, rollback the full cycle.

## Read Model Strategy

Fast UI endpoints should read:

- `atlas_bot_runtime_state`
- recent `atlas_bot_open_trades`
- recent `atlas_bot_closed_trades`
- recent `atlas_bot_decisions`
- recent `atlas_bot_audits`
- `atlas_bot_weights`

No expensive rebuild from append-only history is required for snapshot reads.

## Leader Election Strategy

Recommended strategy:

- table-backed lease row
- `UPDATE ... WHERE lease_until < now() OR owner_instance_id = $self`
- increment `fencing_token` only on takeover
- heartbeat renews `lease_until`

This is simpler to reason about operationally than advisory locks alone because the lock state is observable via SQL and API.

## Indexes That Matter

Must-have indexes:

- `open_trades(bot_key, asset, entry_time desc)`
- `closed_trades(bot_key, asset, exit_time desc)`
- `decisions(bot_key, timestamp desc)`
- `audits(bot_key, timestamp desc)`
- `spot_history(bot_key, asset, observed_at desc)`
- `cycle_events(bot_key, started_at desc)`

## Retention Policy

Recommended initial retention:

- `open_trades`: all active rows
- `closed_trades`: full history
- `decisions`: keep full history, archive later if needed
- `audits`: full history
- `spot_history`: keep rolling 30 days in hot store
- `cycle_events`: keep 14-30 days hot

## Environment Variables

Introduce:

- `BOT_RUNTIME_DB_CONNECTION_STRING`
- `ATLAS_INSTANCE_ID`
- `ATLAS_RUNTIME_ROLE`
- `BOT_LEASE_SECONDS`
- `BOT_HEARTBEAT_SECONDS`
- `BOT_CYCLE_TIMEOUT_SECONDS`

## Migration Path From Current State

1. create schema in Postgres
2. add repository implementation
3. write one-shot importer from `shared-portfolio.json`
4. verify snapshot parity between JSON and Postgres
5. switch worker to Postgres authority
6. disable local JSON persistence
7. keep SQLite OMS persistence temporarily

## What This Does Not Yet Solve

This schema solves bot runtime state.

It does not yet migrate:

- OMS event persistence
- risk event persistence
- broader desk-wide audit trail

That should happen in sprint 2 or 3 once the bot runtime is stable.
