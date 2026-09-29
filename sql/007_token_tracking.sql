-- ARCL Hybrid Chatbot — token usage tracking.
--
-- conversation_log.token_cost_usd already existed (a derived dollar figure), but nothing stored
-- the raw token counts it was computed from. Added so the admin dashboard can show actual token
-- volume, not just an estimated cost that depends on pricing staying accurate (see the pricing
-- caveat already in GeminiClient.cs). NULL for routes that never call the chat completion model
-- (decision_tree, human_handoff, team_ranking) — same convention token_cost_usd already uses.
ALTER TABLE dbo.conversation_log ADD
    prompt_tokens     INT NULL,
    completion_tokens INT NULL;
