-- 001_core.sql — Test Fabric V0.1 核心表结构
-- 关键不变量（PRD §12 / §18）：
--   1. 同一独占资源最多一个 Active Lease —— 由部分唯一索引在数据库层兜底，不依赖应用层先查后写；
--   2. 幂等键全局唯一 —— 重复提交返回原 Lease，不产生重复占用；
--   3. 审计事件 append-only。

CREATE TABLE IF NOT EXISTS resources (
    resource_id          TEXT PRIMARY KEY,
    resource_type        TEXT NOT NULL,
    manufacturer         TEXT NOT NULL DEFAULT '',
    model                TEXT NOT NULL DEFAULT '',
    serial_number        TEXT NOT NULL DEFAULT '',
    asset_tag            TEXT NOT NULL DEFAULT '',
    address_type         TEXT NOT NULL,
    address              TEXT NOT NULL,
    adapter_id           TEXT NOT NULL,
    adapter_version      TEXT NOT NULL DEFAULT '0.0.0',
    location             TEXT NOT NULL DEFAULT '',
    description          TEXT NOT NULL DEFAULT '',
    administrative_state TEXT NOT NULL DEFAULT 'Enabled',
    operational_state    TEXT NOT NULL DEFAULT 'Unknown',
    health_state         TEXT NOT NULL DEFAULT 'Unknown',
    connectivity_state   TEXT NOT NULL DEFAULT 'Unknown',
    calibration_due_date TIMESTAMPTZ NULL,
    last_released_at    TIMESTAMPTZ NULL,
    deleted_at           TIMESTAMPTZ NULL,
    created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
    CONSTRAINT ck_resources_admin   CHECK (administrative_state IN ('Enabled', 'Disabled', 'Maintenance')),
    CONSTRAINT ck_resources_oper     CHECK (operational_state IN ('Unknown', 'Available', 'Busy', 'Recovering', 'Error')),
    CONSTRAINT ck_resources_health   CHECK (health_state IN ('Unknown', 'Healthy', 'Degraded')),
    CONSTRAINT ck_resources_conn     CHECK (connectivity_state IN ('Unknown', 'Online', 'Offline'))
);

CREATE TABLE IF NOT EXISTS capabilities (
    capability_id TEXT PRIMARY KEY,
    version       INT  NOT NULL DEFAULT 1,
    schema_json   JSONB NOT NULL DEFAULT '{}'::jsonb
);

CREATE TABLE IF NOT EXISTS resource_capabilities (
    resource_id     TEXT NOT NULL REFERENCES resources(resource_id) ON DELETE CASCADE,
    capability_id   TEXT NOT NULL REFERENCES capabilities(capability_id) ON DELETE CASCADE,
    properties_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (resource_id, capability_id)
);

CREATE TABLE IF NOT EXISTS leases (
    lease_id          TEXT PRIMARY KEY,
    resource_id       TEXT NOT NULL REFERENCES resources(resource_id),
    capability_id     TEXT NOT NULL,
    client_id         TEXT NOT NULL,
    idempotency_key   TEXT NULL,
    request_hash      TEXT NULL,
    work_order        TEXT NOT NULL DEFAULT '',
    serial_number     TEXT NOT NULL DEFAULT '',
    operation         TEXT NOT NULL DEFAULT '',
    status            TEXT NOT NULL,
    ttl_seconds       INT  NOT NULL,
    acquired_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at        TIMESTAMPTZ NOT NULL,
    last_heartbeat_at TIMESTAMPTZ NULL,
    released_at       TIMESTAMPTZ NULL,
    end_reason        TEXT NULL,
    CONSTRAINT ck_leases_status CHECK (status IN ('Active', 'Expired', 'Closed')),
    CONSTRAINT ck_leases_ttl CHECK (ttl_seconds > 0)
);

-- ★ 核心不变量：同一独占资源最多一个 Active Lease（NFR-05 / E2E-02）
CREATE UNIQUE INDEX IF NOT EXISTS ux_leases_one_active_per_resource
    ON leases(resource_id) WHERE status = 'Active';

-- 幂等键唯一（AC-04-03：相同 Idempotency-Key 重试返回同一 Lease）
CREATE UNIQUE INDEX IF NOT EXISTS ux_leases_idempotency
    ON leases(idempotency_key) WHERE idempotency_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_leases_resource       ON leases(resource_id);
CREATE INDEX IF NOT EXISTS ix_leases_client        ON leases(client_id);
CREATE INDEX IF NOT EXISTS ix_leases_status        ON leases(status);
CREATE INDEX IF NOT EXISTS ix_leases_expires       ON leases(expires_at) WHERE status = 'Active';

CREATE TABLE IF NOT EXISTS events (
    seq         BIGSERIAL PRIMARY KEY,
    event_id    TEXT NOT NULL UNIQUE,
    event_type  TEXT NOT NULL,
    resource_id TEXT NULL,
    lease_id    TEXT NULL,
    client_id   TEXT NULL,
    actor       TEXT NULL,
    payload     JSONB NOT NULL DEFAULT '{}'::jsonb,
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_events_occurred ON events(occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_events_resource ON events(resource_id);
CREATE INDEX IF NOT EXISTS ix_events_lease    ON events(lease_id);
CREATE INDEX IF NOT EXISTS ix_events_client  ON events(client_id);
