-- ARCL Hybrid Chatbot — reliable admin emails for contact-admin requests.
--
-- The admin email for a handoff request used to be fire-and-forget: if the API restarted (a
-- deploy, an App Service recycle) in the few seconds the SMTP send takes, the email was silently
-- lost. These columns let api/Services/HandoffNotifier.cs track delivery per request and retry
-- anything unsent, so a restart only delays an email, never drops it.
--
--   admin_emailed_at  set once the email went out; NULL = still owed
--   email_claimed_at  set while a sender is working on it, so two senders don't both send it
--   email_attempts / email_error  give up (and raise an alert) after repeated failures
--
-- Existing rows are marked as already emailed so applying this doesn't re-send old requests.
-- One-time, safe to re-run. Run with sqlcmd -I (QUOTED_IDENTIFIER ON).

IF COL_LENGTH('dbo.handoff_requests', 'admin_emailed_at') IS NULL
BEGIN
    ALTER TABLE dbo.handoff_requests ADD
        admin_emailed_at  DATETIME2(3) NULL,
        email_claimed_at  DATETIME2(3) NULL,
        email_attempts    INT NOT NULL CONSTRAINT DF_hr_email_attempts DEFAULT 0,
        email_error       NVARCHAR(1000) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_handoff_requests_unsent')
BEGIN
    UPDATE dbo.handoff_requests SET admin_emailed_at = created_at WHERE admin_emailed_at IS NULL;
    CREATE INDEX IX_handoff_requests_unsent ON dbo.handoff_requests (created_at)
        WHERE admin_emailed_at IS NULL;
END
GO
