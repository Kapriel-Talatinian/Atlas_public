CREATE TABLE IF NOT EXISTS atlas_bot_leader_lock (
    bot_key              text PRIMARY KEY,
    owner_instance_id    text,
    owner_hostname       text,
    fencing_token        bigint NOT NULL DEFAULT 0,
    lease_until          timestamptz,
    last_heartbeat_at    timestamptz,
    updated_at           timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS atlas_bot_runtime_state (
    bot_key                  text PRIMARY KEY,
    started_at               timestamptz NOT NULL,
    last_evaluation_at       timestamptz,
    last_signal_json         jsonb NOT NULL DEFAULT '{}'::jsonb,
    neural_signals_json      jsonb NOT NULL DEFAULT '[]'::jsonb,
    config_json              jsonb NOT NULL DEFAULT '{}'::jsonb,
    peak_equity              double precision NOT NULL DEFAULT 0,
    max_drawdown             double precision NOT NULL DEFAULT 0,
    state_version            bigint NOT NULL DEFAULT 0,
    last_cycle_status        text NOT NULL DEFAULT 'cold',
    last_cycle_duration_ms   integer NOT NULL DEFAULT 0,
    last_persisted_at        timestamptz NOT NULL DEFAULT now(),
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_at               timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS atlas_bot_weights (
    bot_key         text NOT NULL,
    feature_name    text NOT NULL,
    weight          double precision NOT NULL,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (bot_key, feature_name)
);

CREATE TABLE IF NOT EXISTS atlas_bot_open_trades (
    trade_id                    text PRIMARY KEY,
    bot_key                     text NOT NULL,
    asset                       text NOT NULL,
    fingerprint                 text NOT NULL,
    primary_symbol              text NOT NULL,
    strategy_template           text NOT NULL,
    bias                        text NOT NULL,
    entry_net_premium           double precision NOT NULL,
    current_liquidation_value   double precision NOT NULL,
    unrealized_pnl              double precision NOT NULL,
    unrealized_pnl_pct          double precision NOT NULL,
    realized_pnl                double precision NOT NULL DEFAULT 0,
    max_profit                  double precision NOT NULL,
    max_loss                    double precision NOT NULL,
    reward_risk_ratio           double precision NOT NULL,
    probability_of_profit_approx double precision NOT NULL,
    expected_value              double precision NOT NULL,
    entry_score                 double precision NOT NULL,
    confidence                  double precision NOT NULL,
    risk_budget_pct             double precision NOT NULL,
    portfolio_weight_pct        double precision NOT NULL,
    thesis                      text NOT NULL,
    math_summary                text NOT NULL,
    rationale                   text NOT NULL,
    drivers_json                jsonb NOT NULL DEFAULT '[]'::jsonb,
    features_json               jsonb NOT NULL DEFAULT '{}'::jsonb,
    legs_json                   jsonb NOT NULL DEFAULT '[]'::jsonb,
    entry_time                  timestamptz NOT NULL,
    updated_at                  timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_atlas_bot_open_trades_bot_fingerprint
    ON atlas_bot_open_trades(bot_key, fingerprint);
CREATE INDEX IF NOT EXISTS ix_atlas_bot_open_trades_bot_asset_entry
    ON atlas_bot_open_trades(bot_key, asset, entry_time DESC);

CREATE TABLE IF NOT EXISTS atlas_bot_closed_trades (
    trade_id                    text PRIMARY KEY,
    bot_key                     text NOT NULL,
    asset                       text NOT NULL,
    fingerprint                 text NOT NULL,
    primary_symbol              text NOT NULL,
    strategy_template           text NOT NULL,
    bias                        text NOT NULL,
    entry_net_premium           double precision NOT NULL,
    current_liquidation_value   double precision NOT NULL,
    unrealized_pnl              double precision NOT NULL,
    unrealized_pnl_pct          double precision NOT NULL,
    realized_pnl                double precision NOT NULL,
    max_profit                  double precision NOT NULL,
    max_loss                    double precision NOT NULL,
    reward_risk_ratio           double precision NOT NULL,
    probability_of_profit_approx double precision NOT NULL,
    expected_value              double precision NOT NULL,
    entry_score                 double precision NOT NULL,
    confidence                  double precision NOT NULL,
    risk_budget_pct             double precision NOT NULL,
    portfolio_weight_pct        double precision NOT NULL,
    thesis                      text NOT NULL,
    math_summary                text NOT NULL,
    rationale                   text NOT NULL,
    drivers_json                jsonb NOT NULL DEFAULT '[]'::jsonb,
    features_json               jsonb NOT NULL DEFAULT '{}'::jsonb,
    legs_json                   jsonb NOT NULL DEFAULT '[]'::jsonb,
    entry_time                  timestamptz NOT NULL,
    exit_time                   timestamptz,
    exit_reason                 text NOT NULL,
    closed_at                   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_atlas_bot_closed_trades_bot_asset_exit
    ON atlas_bot_closed_trades(bot_key, asset, exit_time DESC NULLS LAST);

CREATE TABLE IF NOT EXISTS atlas_bot_decisions (
    decision_id      bigserial PRIMARY KEY,
    bot_key          text NOT NULL,
    timestamp        timestamptz NOT NULL,
    bias             text NOT NULL,
    score            double precision NOT NULL,
    confidence       double precision NOT NULL,
    action           text NOT NULL,
    reason           text NOT NULL,
    signal_json      jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_atlas_bot_decisions_bot_ts
    ON atlas_bot_decisions(bot_key, timestamp DESC);

CREATE TABLE IF NOT EXISTS atlas_bot_audits (
    audit_id                 bigserial PRIMARY KEY,
    bot_key                  text NOT NULL,
    trade_id                 text NOT NULL,
    timestamp                timestamptz NOT NULL,
    asset                    text NOT NULL,
    strategy_template        text NOT NULL,
    realized_pnl             double precision NOT NULL,
    realized_pnl_pct         double precision NOT NULL,
    win                      boolean NOT NULL,
    exit_reason              text NOT NULL,
    rolling_win_rate         double precision NOT NULL,
    rolling_profit_factor    double precision NOT NULL,
    rolling_drawdown_pct     double precision NOT NULL,
    learning_comment         text NOT NULL,
    max_loss                 double precision NOT NULL,
    reward_risk_ratio        double precision NOT NULL,
    math_summary             text NOT NULL,
    payload_json             jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_atlas_bot_audits_bot_ts
    ON atlas_bot_audits(bot_key, timestamp DESC);
CREATE INDEX IF NOT EXISTS ix_atlas_bot_audits_trade
    ON atlas_bot_audits(trade_id);

CREATE TABLE IF NOT EXISTS atlas_bot_spot_history (
    bot_key        text NOT NULL,
    asset          text NOT NULL,
    observed_at    timestamptz NOT NULL,
    spot           double precision NOT NULL,
    PRIMARY KEY (bot_key, asset, observed_at)
);

CREATE INDEX IF NOT EXISTS ix_atlas_bot_spot_history_bot_asset_time
    ON atlas_bot_spot_history(bot_key, asset, observed_at DESC);

CREATE TABLE IF NOT EXISTS atlas_bot_cycle_events (
    cycle_id           bigserial PRIMARY KEY,
    bot_key            text NOT NULL,
    instance_id        text NOT NULL,
    fencing_token      bigint NOT NULL,
    started_at         timestamptz NOT NULL,
    completed_at       timestamptz,
    status             text NOT NULL,
    duration_ms        integer NOT NULL DEFAULT 0,
    opened_trades      integer NOT NULL DEFAULT 0,
    closed_trades      integer NOT NULL DEFAULT 0,
    payload_json       jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX IF NOT EXISTS ix_atlas_bot_cycle_events_bot_start
    ON atlas_bot_cycle_events(bot_key, started_at DESC);

INSERT INTO atlas_bot_leader_lock(bot_key, fencing_token)
VALUES ('MULTI', 0)
ON CONFLICT (bot_key) DO NOTHING;

INSERT INTO atlas_bot_runtime_state(bot_key, started_at, config_json)
VALUES ('MULTI', now(), '{}'::jsonb)
ON CONFLICT (bot_key) DO NOTHING;
