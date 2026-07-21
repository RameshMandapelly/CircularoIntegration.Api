-- =====================================================================
-- Circularo Integration Middleware – PostgreSQL setup (optional)
-- The app auto-creates tables via EnsureCreated() on first run,
-- so this script is only needed if you prefer manual control,
-- plus it has the demo-day queries at the bottom.
-- =====================================================================

-- 1. Create the database (run while connected to 'postgres' db)
-- CREATE DATABASE circularo_integration;

-- 2. Tables (mirror of the EF Core model)
CREATE TABLE IF NOT EXISTS phase_document_config (
    "Id"                  SERIAL PRIMARY KEY,
    "PhaseNo"             INT NOT NULL,
    "LeadStageTrigger"    TEXT NOT NULL,
    "NextLeadStage"       TEXT NOT NULL,
    "CircularoDocumentId" TEXT NOT NULL,
    "DocumentName"        TEXT NOT NULL,
    "IsActive"            BOOLEAN NOT NULL DEFAULT TRUE,
    "CreatedAtUtc"        TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS signing_transaction (
    "Id"              BIGSERIAL PRIMARY KEY,
    "LeadId"          TEXT NOT NULL,
    "PhaseNo"         INT NOT NULL,
    "CircularoRef"    TEXT NOT NULL,
    "CustomerEmail"   TEXT NOT NULL DEFAULT '',
    "Status"          INT NOT NULL DEFAULT 0,      -- 0 Sent, 1 Signed, 2 Attached, 9 Failed
    "SentAtUtc"       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    "SignedAtUtc"     TIMESTAMPTZ NULL,
    "AttachedAtUtc"   TIMESTAMPTZ NULL,
    "ErrorMessage"    TEXT NULL,
    "RetryCount"      INT NOT NULL DEFAULT 0,
    "InboundPayload"  TEXT NULL,
    "CallbackPayload" TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_signing_lead_phase ON signing_transaction ("LeadId", "PhaseNo");
CREATE INDEX IF NOT EXISTS ix_signing_ref        ON signing_transaction ("CircularoRef");

-- 3. Seed / update Phase 1 config with the REAL Circularo document id
-- UPDATE phase_document_config
--   SET "CircularoDocumentId" = '<PASTE_DOC_ID_FROM_CIRCULARO>'
--   WHERE "PhaseNo" = 1;

-- =====================================================================
-- Demo-day queries
-- =====================================================================
-- Watch transactions live:
-- SELECT "Id","LeadId","PhaseNo","Status","CustomerEmail","SentAtUtc","SignedAtUtc","ErrorMessage"
--   FROM signing_transaction ORDER BY "Id" DESC LIMIT 10;

-- Reset a lead for a re-run of the demo (delete its transaction):
-- DELETE FROM signing_transaction WHERE "LeadId" = '<lead-id>';
