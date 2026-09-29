-- ARCL Hybrid Chatbot — Phase 1 schema
-- Target: SQL Server 2025 (17.x) — uses the native VECTOR data type and VECTOR_DISTANCE,
-- both GA in SQL Server 2025. No separate vector database or Azure AI Search needed: this
-- keeps everything (rulebook chunks + embeddings, decision trees, session state, telemetry)
-- in the SQL Server instance the organization already runs and manages through SSMS.
--
-- On vector search strategy: this corpus is small (a few hundred rule chunks across three
-- leagues), well under the ~50,000-row threshold where Microsoft recommends switching from
-- exact to approximate search. So retrieval uses VECTOR_DISTANCE for exact nearest-neighbor
-- search (GA) rather than CREATE VECTOR INDEX / VECTOR_SEARCH (still preview as of SQL Server
-- 2025) — simpler, no preview features to enable, and fast enough at this scale.

-- Filtered/unique indexes below (UX_rule_chunks_current, UX_decision_tree_nodes_root_topic)
-- need ANSI_NULLS/QUOTED_IDENTIFIER ON at CREATE INDEX time. Setting them once at the top of
-- the script isn't enough on this setup — they don't reliably carry across GO batch
-- boundaries here — so each SET is repeated in the same batch as its CREATE INDEX, right
-- below, instead of relying on it persisting from here.

-- ---------------------------------------------------------------------------
-- 1. rule_chunks — one row per rulebook section, chunked by header, not tokens.
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.rule_chunks
(
    chunk_id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_rule_chunks_id DEFAULT NEWID(),
    league           VARCHAR(10)      NOT NULL CONSTRAINT CK_rule_chunks_league CHECK (league IN ('mens', 'womens', 'youth')),
    section_number   VARCHAR(20)      NOT NULL,   -- e.g. '22', 'Appendix A' — stable identifier used for upserts
    section_title    NVARCHAR(300)    NOT NULL,   -- e.g. 'Bad Weather Rules'
    content          NVARCHAR(MAX)    NOT NULL,   -- full section text, uncut
    source_url       NVARCHAR(500)    NOT NULL,
    effective_start  DATE             NOT NULL,   -- when this version of the rule took effect
    effective_end    DATE             NULL,       -- NULL = still in force
    embedding        VECTOR(768)      NULL,       -- 768 = Gemini text-embedding-004 dimension; adjust if you pick another model
    created_at       DATETIME2(3)     NOT NULL CONSTRAINT DF_rule_chunks_created DEFAULT SYSUTCDATETIME(),
    updated_at       DATETIME2(3)     NOT NULL CONSTRAINT DF_rule_chunks_updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_rule_chunks PRIMARY KEY (chunk_id)
);
GO

-- Only one "currently effective" row per (league, section) at a time.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
CREATE UNIQUE INDEX UX_rule_chunks_current
    ON dbo.rule_chunks (league, section_number)
    WHERE effective_end IS NULL;
GO

-- Full-text index (the keyword half of hybrid retrieval) is NOT created here. It requires
-- the "Full-Text and Semantic Extractions for Search" component, which is a separate SQL
-- Server Setup feature — on this instance it isn't installed yet ("Full-Text Search is not
-- installed, or a full-text component cannot be loaded"). Until an admin installs it, the
-- keyword half of retrieval falls back to a plain LIKE search (see RetrievalService.cs) so
-- the app runs without it. Once it's installed, run sql/002_fulltext_optional.sql and switch
-- RetrievalService.cs back to the CONTAINSTABLE query (both are kept ready to go).

-- ---------------------------------------------------------------------------
-- 2. rule_chunk_cross_references — links a section to others it depends on,
--    so retrieval can pull context a single chunk doesn't carry on its own.
--    Example: Men's "Hit Wicket" depends on "Dead Ball".
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.rule_chunk_cross_references
(
    chunk_id             UNIQUEIDENTIFIER NOT NULL,
    referenced_chunk_id  UNIQUEIDENTIFIER NOT NULL,
    note                 NVARCHAR(400) NULL,   -- optional: why they're linked
    CONSTRAINT PK_rule_chunk_cross_references PRIMARY KEY (chunk_id, referenced_chunk_id),
    CONSTRAINT FK_rccr_chunk FOREIGN KEY (chunk_id) REFERENCES dbo.rule_chunks (chunk_id) ON DELETE CASCADE,
    CONSTRAINT FK_rccr_referenced FOREIGN KEY (referenced_chunk_id) REFERENCES dbo.rule_chunks (chunk_id),
    CONSTRAINT CK_rccr_not_self CHECK (chunk_id <> referenced_chunk_id)
);
GO

-- ---------------------------------------------------------------------------
-- 3. decision_tree_nodes — deterministic rule navigation (rain, DLS-style
--    completion thresholds, forfeiture, etc.), each tied back to its source.
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.decision_tree_nodes
(
    node_id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_dtn_id DEFAULT NEWID(),
    league           VARCHAR(10)      NOT NULL CONSTRAINT CK_dtn_league CHECK (league IN ('mens', 'womens', 'youth')),
    topic            VARCHAR(50)      NULL,       -- e.g. 'bad_weather', 'no_show' — set ONLY on root nodes;
                                                   -- this is what the Phase 2 intent classifier matches against
    question_text    NVARCHAR(500)    NOT NULL,
    -- options: JSON array, e.g. [{"label":"Yes","next_node_id":"..."},{"label":"No","answer":"..."}]
    options          NVARCHAR(MAX)    NOT NULL CONSTRAINT CK_dtn_options_json CHECK (ISJSON(options) = 1),
    source_chunk_id  UNIQUEIDENTIFIER NULL,      -- flag for review when this chunk changes
    is_root          BIT              NOT NULL CONSTRAINT DF_dtn_is_root DEFAULT 0,
    created_at       DATETIME2(3)     NOT NULL CONSTRAINT DF_dtn_created DEFAULT SYSUTCDATETIME(),
    updated_at       DATETIME2(3)     NOT NULL CONSTRAINT DF_dtn_updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_decision_tree_nodes PRIMARY KEY (node_id),
    CONSTRAINT FK_dtn_source_chunk FOREIGN KEY (source_chunk_id) REFERENCES dbo.rule_chunks (chunk_id)
);
GO

CREATE INDEX IX_decision_tree_nodes_source_chunk ON dbo.decision_tree_nodes (source_chunk_id);
GO

-- One root node per (league, topic) — this is the lookup the classifier's matched topic resolves through.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
CREATE UNIQUE INDEX UX_decision_tree_nodes_root_topic
    ON dbo.decision_tree_nodes (league, topic)
    WHERE is_root = 1;
GO

-- ---------------------------------------------------------------------------
-- 4. session_state — one row per active chat session.
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.session_state
(
    session_id                    UNIQUEIDENTIFIER NOT NULL,
    selected_league                VARCHAR(10)      NULL CONSTRAINT CK_session_league CHECK (selected_league IN ('mens', 'womens', 'youth')),
    current_decision_tree_node     UNIQUEIDENTIFIER NULL,
    created_at                     DATETIME2(3)     NOT NULL CONSTRAINT DF_session_created DEFAULT SYSUTCDATETIME(),
    updated_at                     DATETIME2(3)     NOT NULL CONSTRAINT DF_session_updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_session_state PRIMARY KEY (session_id),
    CONSTRAINT FK_session_node FOREIGN KEY (current_decision_tree_node) REFERENCES dbo.decision_tree_nodes (node_id)
);
GO

-- ---------------------------------------------------------------------------
-- 5. conversation_log — every query + answer. Feeds Phase 4 (evaluation) and
--    Phase 5 (telemetry/feedback) so both reuse the same table.
-- ---------------------------------------------------------------------------
CREATE TABLE dbo.conversation_log
(
    log_id           BIGINT IDENTITY(1,1) NOT NULL,
    session_id        UNIQUEIDENTIFIER NOT NULL,
    query             NVARCHAR(MAX) NOT NULL,
    route_path        VARCHAR(20) NOT NULL CONSTRAINT CK_cl_route CHECK (route_path IN ('faq', 'decision_tree', 'human_handoff')),
    response_text     NVARCHAR(MAX) NULL,
    latency_ms        INT NULL,
    token_cost_usd    DECIMAL(10, 6) NULL,
    user_feedback     TINYINT NULL,   -- 0 = thumbs down, 1 = thumbs up, NULL = no feedback given (Phase 5 maps -1/+1 to this)
    created_at        DATETIME2(3) NOT NULL CONSTRAINT DF_cl_created DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_conversation_log PRIMARY KEY (log_id),
    CONSTRAINT FK_cl_session FOREIGN KEY (session_id) REFERENCES dbo.session_state (session_id)
);
GO

CREATE INDEX IX_conversation_log_session ON dbo.conversation_log (session_id);
CREATE INDEX IX_conversation_log_created ON dbo.conversation_log (created_at);
CREATE INDEX IX_conversation_log_low_confidence ON dbo.conversation_log (user_feedback) WHERE user_feedback = 0;
GO

-- Junction table for RetrievedChunkIDs (SQL Server has no native array type) — also gives
-- Phase 5's "top retrieved chunks" dashboard a simple GROUP BY instead of unpacking JSON.
CREATE TABLE dbo.conversation_log_retrieved_chunks
(
    log_id     BIGINT NOT NULL,
    chunk_id   UNIQUEIDENTIFIER NOT NULL,
    CONSTRAINT PK_cl_retrieved_chunks PRIMARY KEY (log_id, chunk_id),
    CONSTRAINT FK_clrc_log FOREIGN KEY (log_id) REFERENCES dbo.conversation_log (log_id) ON DELETE CASCADE,
    CONSTRAINT FK_clrc_chunk FOREIGN KEY (chunk_id) REFERENCES dbo.rule_chunks (chunk_id)
);
GO

-- ---------------------------------------------------------------------------
-- Example hybrid retrieval query (semantic + keyword, filtered by league and
-- effective date) — this is what Phase 2's retrieval engine will wrap in C#.
-- ---------------------------------------------------------------------------
-- DECLARE @qv VECTOR(768) = '[...]';           -- the query embedding
-- DECLARE @league VARCHAR(10) = 'mens';
-- DECLARE @asOf DATE = CAST(GETDATE() AS DATE);
--
-- SELECT TOP (5)
--     rc.chunk_id, rc.section_number, rc.section_title,
--     VECTOR_DISTANCE('cosine', @qv, rc.embedding) AS semantic_distance,
--     ft.[RANK] AS keyword_rank
-- FROM dbo.rule_chunks rc
-- LEFT JOIN CONTAINSTABLE(dbo.rule_chunks, (section_title, content), N'"CricClubs"') ft
--     ON ft.[KEY] = rc.chunk_id
-- WHERE rc.league = @league
--   AND rc.effective_start <= @asOf
--   AND (rc.effective_end IS NULL OR rc.effective_end > @asOf)
-- ORDER BY semantic_distance ASC;