CREATE TABLE IF NOT EXISTS alerts (
    id              bigserial PRIMARY KEY,
    uid             uuid        NOT NULL UNIQUE,
    created_at      timestamptz NOT NULL,
    symbol          text        NOT NULL,
    direction       text        NOT NULL,
    tier            text        NOT NULL,
    price           numeric     NOT NULL,
    breakout_level  numeric     NOT NULL,
    invalidation    numeric     NOT NULL,
    target          numeric     NULL,
    must_haves      jsonb       NOT NULL,
    confirmations   jsonb       NOT NULL,
    vetoes          jsonb       NOT NULL,
    metrics         jsonb       NOT NULL,
    config_hash     text        NOT NULL,
    delivery        text        NOT NULL DEFAULT 'sent'
);
CREATE INDEX IF NOT EXISTS ix_alerts_created_at ON alerts (created_at);
CREATE INDEX IF NOT EXISTS ix_alerts_symbol_created ON alerts (symbol, created_at);

CREATE TABLE IF NOT EXISTS alert_outcomes (
    alert_id     bigint      PRIMARY KEY REFERENCES alerts (id) ON DELETE CASCADE,
    price_5m     numeric     NULL,
    price_15m    numeric     NULL,
    price_60m    numeric     NULL,
    mfe_pct      numeric     NOT NULL,
    mae_pct      numeric     NOT NULL,
    first_hit    text        NOT NULL CHECK (first_hit IN ('target', 'invalidation', 'none')),
    resolved_at  timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS near_misses (
    id          bigserial   PRIMARY KEY,
    created_at  timestamptz NOT NULL,
    symbol      text        NOT NULL,
    direction   text        NOT NULL,
    passed      jsonb       NOT NULL,
    failed      jsonb       NOT NULL,
    metrics     jsonb       NOT NULL,
    config_hash text        NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_near_misses_created_at ON near_misses (created_at);

CREATE TABLE IF NOT EXISTS liquidations (
    id        bigserial   PRIMARY KEY,
    time      timestamptz NOT NULL,
    symbol    text        NOT NULL,
    side      text        NOT NULL,
    price     numeric     NOT NULL,
    qty       numeric     NOT NULL,
    notional  numeric     NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_liquidations_time ON liquidations (time);
