-- ARCL Hybrid Chatbot — Phase 2 hardening.
--
-- 1. conversation_log.best_distance / confidence_tier / output_filtered
--    best_distance is the raw cosine distance of the closest chunk retrieval found for that turn
--    (NULL for routes that never retrieve). Logging it per query is what makes threshold tuning
--    (Arcl:*CosineDistanceThreshold in appsettings.json) evidence-based instead of guesswork, and
--    is the raw signal for drift monitoring later. confidence_tier is which band of the graduated
--    gate the turn landed in (high / medium / low / out_of_scope — see ChatOrchestrator.cs).
--    output_filtered = 1 when OutputFilter.cs had to strip something from the model's answer.
--
-- 2. handoff_requests — the contact-admin form (name/email/message + consent). One row per
--    submission, linked to the conversation_log row the submission also writes, so the admin
--    review queue (GET /api/admin/review-queue) can show who to reply to next to the question.
--    consent_text stores the exact wording the visitor agreed to, not just a flag — if the
--    wording changes later, each row still records what that person actually consented to.
--
-- One-time, safe to re-run (every change is guarded).

IF COL_LENGTH('dbo.conversation_log', 'best_distance') IS NULL
    ALTER TABLE dbo.conversation_log ADD best_distance FLOAT NULL;
GO
IF COL_LENGTH('dbo.conversation_log', 'confidence_tier') IS NULL
    ALTER TABLE dbo.conversation_log ADD confidence_tier VARCHAR(12) NULL
        CONSTRAINT CK_cl_confidence_tier CHECK (confidence_tier IN ('high', 'medium', 'low', 'out_of_scope'));
GO
IF COL_LENGTH('dbo.conversation_log', 'output_filtered') IS NULL
    ALTER TABLE dbo.conversation_log ADD output_filtered BIT NULL;
GO

IF OBJECT_ID('dbo.handoff_requests') IS NULL
BEGIN
    CREATE TABLE dbo.handoff_requests
    (
        request_id        BIGINT IDENTITY(1,1) NOT NULL,
        log_id            BIGINT NOT NULL,
        session_id        UNIQUEIDENTIFIER NOT NULL,
        contact_name      NVARCHAR(200) NULL,
        contact_email     NVARCHAR(320) NOT NULL,
        message           NVARCHAR(2000) NOT NULL,
        consent_text      NVARCHAR(1000) NOT NULL,
        consent_given_at  DATETIME2(3) NOT NULL,
        created_at        DATETIME2(3) NOT NULL CONSTRAINT DF_hr_created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_handoff_requests PRIMARY KEY (request_id),
        CONSTRAINT FK_hr_log FOREIGN KEY (log_id) REFERENCES dbo.conversation_log (log_id) ON DELETE CASCADE,
        CONSTRAINT FK_hr_session FOREIGN KEY (session_id) REFERENCES dbo.session_state (session_id)
    );
    CREATE UNIQUE INDEX UX_handoff_requests_log ON dbo.handoff_requests (log_id);
END
GO
