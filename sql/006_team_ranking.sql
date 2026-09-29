-- ARCL Hybrid Chatbot — team ranking lookup.
--
-- Adds a "we're waiting for a team name" flag to session_state. Team names are free text with no
-- fixed pattern (arcl.org's own team directory includes names like "1", "8", "A Few Good Men",
-- "ATB Seattle Shockers" — not reliably extractable from an arbitrary sentence via regex), so
-- rather than trying to parse a team name out of the first message, the bot always asks a
-- clarifying question and treats the visitor's next message, verbatim, as the team name. This
-- flag is what makes that next message skip normal intent classification.
--
-- See api/Services/TeamRankingService.cs (fetches/parses arcl.org/Pages/UI/TeamHistory.aspx,
-- which is public and requires no login) and Program.cs's /api/chat handler.
ALTER TABLE dbo.session_state ADD
    pending_team_ranking BIT NOT NULL CONSTRAINT DF_session_pending_ranking DEFAULT 0;
