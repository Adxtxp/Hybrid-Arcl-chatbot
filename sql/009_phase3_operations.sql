-- ARCL Hybrid Chatbot — Phase 3: post-launch operations.
--
-- 1. alerts — every alert raised by the API (error spikes, retrieval drift, budget thresholds,
--    stale content, rulebook changes; see api/Services/AlertService.cs). One row per alert,
--    written whether or not the email went out, so the admin page can show them as a banner and
--    an alert is never lost to a misconfigured mail server. alert_key + created_at is what the
--    per-key cooldown checks, so one ongoing problem doesn't send an email every 15 minutes.
--
-- 2. source_pages — one row per crawled arcl.org page (per league; the FAQ page is crawled once
--    per league). The nightly re-crawl (api/Services/RecrawlService.cs) records what it found
--    each time; last_success_at going old is what "stale content" means.
--
-- One-time, re-runnable. Run with sqlcmd -I (QUOTED_IDENTIFIER ON).

IF OBJECT_ID('dbo.alerts') IS NULL
BEGIN
    CREATE TABLE dbo.alerts
    (
        alert_id         BIGINT IDENTITY(1,1) NOT NULL,
        alert_key        VARCHAR(200) NOT NULL,   -- e.g. 'errors', 'drift', 'budget-100-2026-11', 'stale-mens-https://...'
        severity         VARCHAR(10) NOT NULL CONSTRAINT CK_alerts_severity CHECK (severity IN ('info', 'warning', 'critical')),
        subject          NVARCHAR(300) NOT NULL,
        body             NVARCHAR(MAX) NOT NULL,
        emailed          BIT NOT NULL CONSTRAINT DF_alerts_emailed DEFAULT 0,
        email_error      NVARCHAR(1000) NULL,
        acknowledged_at  DATETIME2(3) NULL,
        created_at       DATETIME2(3) NOT NULL CONSTRAINT DF_alerts_created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_alerts PRIMARY KEY (alert_id)
    );
    CREATE INDEX IX_alerts_key_created ON dbo.alerts (alert_key, created_at DESC);
END
GO

IF OBJECT_ID('dbo.source_pages') IS NULL
BEGIN
    CREATE TABLE dbo.source_pages
    (
        source_id         INT IDENTITY(1,1) NOT NULL,
        league            VARCHAR(10) NOT NULL CONSTRAINT CK_source_pages_league CHECK (league IN ('mens', 'womens', 'youth')),
        url               NVARCHAR(500) NOT NULL,
        last_checked_at   DATETIME2(3) NULL,
        last_success_at   DATETIME2(3) NULL,     -- last time the page was fetched AND chunked cleanly
        last_changed_at   DATETIME2(3) NULL,     -- last time a re-crawl actually updated rule_chunks
        last_status       VARCHAR(20) NULL CONSTRAINT CK_source_pages_status
                              CHECK (last_status IN ('unchanged', 'updated', 'needs_review', 'fetch_failed')),
        last_message      NVARCHAR(2000) NULL,
        sections_matched  INT NULL,
        CONSTRAINT PK_source_pages PRIMARY KEY (source_id),
        CONSTRAINT UX_source_pages UNIQUE (league, url)
    );
END
GO

-- Month-to-date spend (BudgetGovernor.cs) and the monitoring windows all filter conversation_log
-- by created_at; IX_conversation_log_created (sql/001) already covers that.
