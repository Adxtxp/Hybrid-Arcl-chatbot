-- ARCL Hybrid Chatbot — Phase 5 telemetry & admin review queries.
-- These all read conversation_log (+ conversation_log_retrieved_chunks), the same table Phase 4's
-- evaluation pipeline writes to via the API — one table, two purposes, as designed in Phase 1.
--
-- Point these at whatever renders dashboards for you (Grafana, Power BI, a scheduled email, or
-- just SSMS) — they're plain SELECTs, nothing dashboard-tool-specific.

-- ---------------------------------------------------------------------------
-- 1. Query volume by league, last 7 days.
-- ---------------------------------------------------------------------------
SELECT
    s.selected_league AS league,
    CAST(cl.created_at AS DATE) AS [date],
    COUNT(*) AS query_count
FROM dbo.conversation_log cl
JOIN dbo.session_state s ON s.session_id = cl.session_id
WHERE cl.created_at >= DATEADD(DAY, -7, SYSUTCDATETIME())
GROUP BY s.selected_league, CAST(cl.created_at AS DATE)
ORDER BY [date] DESC, league;

-- ---------------------------------------------------------------------------
-- 2. Top low-confidence / handed-off queries this week — the admin review queue.
--    Anything here is a candidate for a new decision tree, a rulebook clarification,
--    or a chunking/cross-reference fix.
-- ---------------------------------------------------------------------------
SELECT
    cl.log_id, cl.created_at, s.selected_league AS league, cl.query, cl.response_text,
    cl.route_path, cl.user_feedback
FROM dbo.conversation_log cl
JOIN dbo.session_state s ON s.session_id = cl.session_id
WHERE cl.created_at >= DATEADD(DAY, -7, SYSUTCDATETIME())
  AND (cl.route_path = 'human_handoff' OR cl.user_feedback = 0)
ORDER BY cl.created_at DESC;

-- ---------------------------------------------------------------------------
-- 3. Decision-tree drop-off — sessions that entered a tree (current_decision_tree_node
--    was set at some point) but have no terminal answer in the log, i.e. abandoned mid-tree.
--    Approximated here via sessions whose most recent decision_tree log entry isn't followed
--    by a session state reset to NULL; simplest reliable signal is "session still has a
--    current_decision_tree_node set and hasn't been touched in the last hour".
-- ---------------------------------------------------------------------------
SELECT
    s.session_id, s.selected_league AS league, dtn.question_text AS stuck_at_question, s.updated_at
FROM dbo.session_state s
JOIN dbo.decision_tree_nodes dtn ON dtn.node_id = s.current_decision_tree_node
WHERE s.current_decision_tree_node IS NOT NULL
  AND s.updated_at < DATEADD(HOUR, -1, SYSUTCDATETIME())
ORDER BY s.updated_at DESC;

-- ---------------------------------------------------------------------------
-- 4. Daily cost trend (Gemini spend, from conversation_log.token_cost_usd).
-- ---------------------------------------------------------------------------
SELECT
    CAST(created_at AS DATE) AS [date],
    COUNT(*) AS query_count,
    SUM(token_cost_usd) AS total_cost_usd,
    AVG(token_cost_usd) AS avg_cost_per_query_usd
FROM dbo.conversation_log
WHERE token_cost_usd IS NOT NULL
GROUP BY CAST(created_at AS DATE)
ORDER BY [date] DESC;

-- ---------------------------------------------------------------------------
-- 5. Most-retrieved rule chunks — tells you which sections people actually ask about,
--    which is useful both for prioritizing which topics get a decision tree next and for
--    spotting a rule that keeps needing clarification (candidate for a rulebook rewrite).
-- ---------------------------------------------------------------------------
SELECT TOP (20)
    rc.league, rc.section_number, rc.section_title, COUNT(*) AS times_retrieved
FROM dbo.conversation_log_retrieved_chunks clrc
JOIN dbo.rule_chunks rc ON rc.chunk_id = clrc.chunk_id
JOIN dbo.conversation_log cl ON cl.log_id = clrc.log_id
WHERE cl.created_at >= DATEADD(DAY, -30, SYSUTCDATETIME())
GROUP BY rc.league, rc.section_number, rc.section_title
ORDER BY times_retrieved DESC;

-- ---------------------------------------------------------------------------
-- 6. Simple weekly summary — the numbers for an automated weekly email to whoever manages
--    the bot (Phase 5's "weekly summary" requirement). Wire this into a SQL Server Agent job
--    or an external scheduler that emails the result.
-- ---------------------------------------------------------------------------
SELECT
    COUNT(*) AS total_queries,
    SUM(CASE WHEN route_path = 'faq' THEN 1 ELSE 0 END) AS faq_queries,
    SUM(CASE WHEN route_path = 'decision_tree' THEN 1 ELSE 0 END) AS decision_tree_queries,
    SUM(CASE WHEN route_path = 'human_handoff' THEN 1 ELSE 0 END) AS human_handoff_queries,
    SUM(CASE WHEN user_feedback = 0 THEN 1 ELSE 0 END) AS thumbs_down_count,
    SUM(CASE WHEN user_feedback = 1 THEN 1 ELSE 0 END) AS thumbs_up_count,
    SUM(token_cost_usd) AS total_cost_usd,
    AVG(latency_ms) AS avg_latency_ms
FROM dbo.conversation_log
WHERE created_at >= DATEADD(DAY, -7, SYSUTCDATETIME());
