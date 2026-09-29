-- ARCL Hybrid Chatbot — decision trees: bad_weather, no_show.
--
-- Rewritten from real, verified rule text (see fetch_tree_sources.sql / fetch_womens_tail.sql
-- output) — the previous version of this script had two real bugs:
--   1. It looked up the WRONG section_number for bad_weather in all three leagues (written
--      before the rulebook text was actually verified against the live pages — mens pointed at
--      "22" which is really "Re-scheduling Options," womens at "5" which is really "Equipment,"
--      youth at "15" which is really "Registration Fees and Equipment Cost").
--   2. Even where the section number was right, it treated playoff matches the same as league
--      matches for a below-3/4-complete stoppage. That's wrong: a league match stopped early is
--      "not played" (0 points both teams), but a playoff match stopped early is instead decided
--      by whichever team had the higher AVERAGE POINTS during the regular league phase — a
--      completely different outcome that the old tree never asked about.
--
-- Run this AFTER Phase 1 ingestion has loaded rule_chunks for all three leagues.
--
-- This table has no clean way to delete "just the bad_weather nodes" — only root nodes carry a
-- topic tag, child/leaf nodes don't (see DecisionTreeService.cs, which only ever looks up a root
-- by (league, topic)). Per the original script's own comment, bad_weather was the ONLY topic ever
-- seeded here, so the safe move is to wipe the whole table and reseed it fresh rather than try to
-- patch around old rows with no way to identify which ones belong to which tree.
SET QUOTED_IDENTIFIER ON;
DELETE FROM dbo.decision_tree_nodes;

DECLARE @league VARCHAR(10), @chunkId UNIQUEIDENTIFIER, @teamChunkId UNIQUEIDENTIFIER, @umpireChunkId UNIQUEIDENTIFIER,
        @root UNIQUEIDENTIFIER, @n1 UNIQUEIDENTIFIER, @n2 UNIQUEIDENTIFIER, @optionsJson NVARCHAR(MAX);

-- ============================================================================
-- bad_weather
-- ============================================================================

-- ---------------------------------------------------------- Men's [27]
SET @league = 'mens';
SELECT @chunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='27' AND effective_end IS NULL;
IF @chunkId IS NULL PRINT 'WARNING: mens section 27 (Bad Weather) not found in rule_chunks - run Phase 1 ingestion first.';

SET @n2 = NEWID(); -- match-type node (league vs playoff) - only reached if below 3/4
SET @n1 = NEWID(); -- completion-threshold node
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Was this a regular league match, or a playoff match (quarterfinal, semifinal, or final)?',
    N'[
        {"label":"League match","answer":"The match is treated as \"not played.\" Both teams receive 0 points for this match (your season ranking is based on average points, so this match is simply excluded rather than counted as a loss). The match will not be rescheduled, unless the stoppage was ARCL''s own error (e.g. a scheduling or ground-booking conflict), in which case ARCL will reschedule it."},
        {"label":"Playoff match","answer":"The team with the higher average points from the regular league phase wins the match. It will not be rescheduled."}
     ]',
    @chunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Was more than three-quarters (3/4) of the match completed before it was stopped? (For a 16-over match, that means 16 overs of the 1st innings plus 8 overs of the 2nd innings.)',
    N'[
        {"label":"Yes, more than 3/4 complete","answer":"The match result stands - the team with the higher run rate at the point of stoppage wins. It will not be rescheduled, unless the stoppage was ARCL''s own error."},
        {"label":"No, less than 3/4 complete","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
        {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can confirm the completion percentage from the scorecard, or email {{SupportContact}} directly."}
     ]',
    @chunkId, 0
);

SET @optionsJson = N'[
    {"label":"Yes","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"No","answer":"This tool handles weather-related stoppages specifically. For other issues, please describe what happened, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'bad_weather', N'Was the match stopped early due to weather (rain, etc.)?', @optionsJson, @chunkId, 1);

-- ---------------------------------------------------------- Women's [7]
SET @league = 'womens';
SELECT @chunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='7' AND effective_end IS NULL;
IF @chunkId IS NULL PRINT 'WARNING: womens section 7 (Bad Weather) not found in rule_chunks - run Phase 1 ingestion first.';

SET @n2 = NEWID();
SET @n1 = NEWID();
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Was this a regular league match, or a playoff match (quarterfinal, semifinal, or final)?',
    N'[
        {"label":"League match","answer":"The match is treated as \"not played.\" Both teams receive 0 points for this match. You will need to log in to the website and mark the game as abandoned - the deadline for that is the same as the scorecard deadline. If you miss that deadline, the \"failure to upload scorecard\" penalty kicks in automatically: both teams get 0 points AND the match is counted as played, which will hurt your average. The match will not be rescheduled, unless the stoppage was ARCL''s own error."},
        {"label":"Playoff match","answer":"The team with the higher average points from the regular league phase wins the match. It will not be rescheduled."}
     ]',
    @chunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Was more than three-quarters (3/4) of the match completed before it was stopped? (For a 16-over match, that means 16 overs of the 1st innings plus 8 overs of the 2nd innings.)',
    N'[
        {"label":"Yes, more than 3/4 complete","answer":"The match result stands - the team with the higher run rate at the point of stoppage wins. It will not be rescheduled."},
        {"label":"No, less than 3/4 complete","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
        {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can confirm the completion percentage from the scorecard, or email {{SupportContact}} directly."}
     ]',
    @chunkId, 0
);

SET @optionsJson = N'[
    {"label":"Yes","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"No","answer":"This tool handles weather-related stoppages specifically. For other issues, please describe what happened, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'bad_weather', N'Was the match stopped early due to weather (rain, etc.)?', @optionsJson, @chunkId, 1);

-- ---------------------------------------------------------- Youth [12]
SET @league = 'youth';
SELECT @chunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='12' AND effective_end IS NULL;
IF @chunkId IS NULL PRINT 'WARNING: youth section 12 (Bad Weather) not found in rule_chunks - run Phase 1 ingestion first.';

SET @n2 = NEWID();
SET @n1 = NEWID();
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Was this a regular league match, or a playoff match?',
    N'[
        {"label":"League match","answer":"The match is treated as \"not played.\" Both teams receive 0 points for this match. You will need to log in to the website and mark the game as abandoned - the deadline for that is the same as the scorecard deadline. If you miss that deadline, the \"failure to upload scorecard\" penalty kicks in automatically: both teams get 0 points AND the match is counted as played, which will hurt your average. The match will not be rescheduled, unless the stoppage was ARCL''s own error. Note: if this game was at the Microsoft ground, the Grass Lawn cancellation policy applies instead."},
        {"label":"Playoff match","answer":"The team with the higher average points from the regular league phase wins the match. It will not be rescheduled."}
     ]',
    @chunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Was more than three-quarters (3/4) of the match completed before it was stopped? (For a 16-over match, that means 16 overs of the 1st innings plus 8 overs of the 2nd innings.)',
    N'[
        {"label":"Yes, more than 3/4 complete","answer":"The match result stands - the team with the higher run rate at the point of stoppage wins. It will not be rescheduled."},
        {"label":"No, less than 3/4 complete","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
        {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can confirm the completion percentage from the scorecard, or email {{SupportContact}} directly."}
     ]',
    @chunkId, 0
);

SET @optionsJson = N'[
    {"label":"Yes","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"No","answer":"This tool handles weather-related stoppages specifically. For other issues, please describe what happened, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'bad_weather', N'Was the match stopped early due to weather (rain, etc.)?', @optionsJson, @chunkId, 1);

PRINT 'Seeded bad_weather decision trees for mens, womens, youth.';

-- ============================================================================
-- no_show
-- ============================================================================
-- Every league actually has TWO separate no-show rules with different grace periods and
-- different penalties: a team failing to show up for their own game, and an umpiring team
-- failing to send an umpire for their assignment. IntentClassifier.cs's "no_show" pattern
-- doesn't distinguish between the two, so the root node here asks which one applies first.

-- ---------------------------------------------------------- Men's (team [13], umpire [12])
SET @league = 'mens';
SELECT @teamChunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='13' AND effective_end IS NULL;
SELECT @umpireChunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='12' AND effective_end IS NULL;
IF @teamChunkId IS NULL PRINT 'WARNING: mens section 13 (Team No-Show) not found in rule_chunks.';
IF @umpireChunkId IS NULL PRINT 'WARNING: mens section 12 (Umpire No-Show) not found in rule_chunks.';

SET @n1 = NEWID(); -- team no-show node
SET @n2 = NEWID(); -- umpire no-show node
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Did the team fail to show up entirely, forfeit, or arrive more than 12 minutes after the scheduled start time?',
    N'[
        {"label":"Yes","answer":"This counts as an official team no-show. The other team gets a walkover. Someone needs to log in to the website and mark the game as a no-show by the scorecard deadline - if that deadline is missed, no penalty is applied (a missed deadline is treated as both teams having agreed not to penalize the defaulting team). If reported, the penalties are: $250 fine, 10 points deducted, 3 additional umpiring assignments, and demotion to the last division."},
        {"label":"No, they arrived within 12 minutes","answer":"This is not an official no-show. Instead, the late team loses 1 over for every 4 minutes they were late, counted from the scheduled start time."}
     ]',
    @teamChunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Did the umpiring team fail to send an umpire entirely, forfeit the umpiring assignment, or send an umpire more than 12 minutes late?',
    N'[
        {"label":"Yes","answer":"This counts as an official umpire no-show. One of the teams needs to log in to the website and mark it by the scorecard deadline - if that deadline is missed, no penalty is applied. If reported, the penalties are: $50 fine, 10 points deducted, and 3 additional umpiring assignments at ARCL''s discretion. A repeat offense while under probation escalates to a $200 fine, 20 points deducted, and 5 additional umpiring assignments."},
        {"label":"No","answer":"This is not an official umpire no-show. Please describe the specific issue, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
     ]',
    @umpireChunkId, 0
);

SET @optionsJson = N'[
    {"label":"Team no-show","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"Umpire no-show","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
    {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can help sort out what happened, or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'no_show', N'Are you asking about a team not showing up for their game, or an umpire not showing up for their umpiring assignment?', @optionsJson, @teamChunkId, 1);

-- ---------------------------------------------------------- Women's (team + umpire both in [6])
SET @league = 'womens';
SELECT @teamChunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='6' AND effective_end IS NULL;
SET @umpireChunkId = @teamChunkId; -- same combined "Penalties" chunk covers both for womens
IF @teamChunkId IS NULL PRINT 'WARNING: womens section 6 (Penalties) not found in rule_chunks.';

SET @n1 = NEWID();
SET @n2 = NEWID();
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Did the team fail to show up entirely, forfeit, or arrive more than 15 minutes after the scheduled start time?',
    N'[
        {"label":"Yes","answer":"This counts as an official team no-show. The other team gets a walkover. Someone needs to log in to the website and mark the game as a no-show by the scorecard deadline - if that deadline is missed, no penalty is applied. If reported, the penalties are: $250 fine, 10 points deducted, and 3 additional umpiring assignments at ARCL''s discretion."},
        {"label":"No, they arrived within 15 minutes","answer":"This is not an official no-show. Instead, the late team loses 1 over for every 5 minutes they were late, counted from the scheduled start time."}
     ]',
    @teamChunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Did the umpiring team fail to send an umpire entirely, forfeit the umpiring assignment, or send an umpire more than 15 minutes late?',
    N'[
        {"label":"Yes","answer":"This counts as an official umpire no-show. One of the teams needs to log in to the website and mark it by the scorecard deadline - if that deadline is missed, no penalty is applied. If reported, the penalties are: $50 fine, 5 points deducted, and 3 additional umpiring assignments. A repeat offense while under probation escalates to a $300 fine and 5 additional umpiring assignments."},
        {"label":"No","answer":"This is not an official umpire no-show. Please describe the specific issue, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
     ]',
    @umpireChunkId, 0
);

SET @optionsJson = N'[
    {"label":"Team no-show","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"Umpire no-show","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
    {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can help sort out what happened, or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'no_show', N'Are you asking about a team not showing up for their game, or an umpire not showing up for their umpiring assignment?', @optionsJson, @teamChunkId, 1);

-- ---------------------------------------------------------- Youth (team + umpire both in [8])
SET @league = 'youth';
SELECT @teamChunkId = chunk_id FROM dbo.rule_chunks WHERE league=@league AND section_number='8' AND effective_end IS NULL;
SET @umpireChunkId = @teamChunkId;
IF @teamChunkId IS NULL PRINT 'WARNING: youth section 8 (Penalties) not found in rule_chunks.';

SET @n1 = NEWID();
SET @n2 = NEWID();
SET @root = NEWID();

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n1, @league, NULL,
    N'Did the team fail to show up entirely, forfeit, or arrive more than 12 minutes after the scheduled start time?',
    N'[
        {"label":"Yes","answer":"This counts as an official team no-show. The other team gets a walkover. Someone needs to log in to the website and mark the game as a no-show by the scorecard deadline - if that deadline is missed, no penalty is applied. If reported, the penalties are: $250 fine, 10 points deducted, 3 additional umpiring assignments, and demotion to the last division."},
        {"label":"No, they arrived within 12 minutes","answer":"This is not an official no-show. Instead, the late team loses 1 over (batting) for every 4 minutes they were late, counted from the scheduled start time."}
     ]',
    @teamChunkId, 0
);

INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root) VALUES (
    @n2, @league, NULL,
    N'Did the umpiring team fail to send an umpire entirely, forfeit the umpiring assignment, or send an umpire more than 12 minutes late?',
    N'[
        {"label":"Yes","answer":"This counts as an official umpire no-show. One of the teams needs to log in to the website and mark it by the scorecard deadline - if that deadline is missed, no penalty is applied. If reported, the penalties are: $50 fine, 10 points deducted, and 3 additional umpiring assignments at ARCL''s discretion. A repeat offense while under probation escalates to a $200 fine, 20 points deducted, and 5 additional umpiring assignments."},
        {"label":"No","answer":"This is not an official umpire no-show. Please describe the specific issue, use \"Contact Human Support,\" or email {{SupportContact}} directly."}
     ]',
    @umpireChunkId, 0
);

SET @optionsJson = N'[
    {"label":"Team no-show","next_node_id":"' + CAST(@n1 AS VARCHAR(36)) + N'"},
    {"label":"Umpire no-show","next_node_id":"' + CAST(@n2 AS VARCHAR(36)) + N'"},
    {"label":"Not sure","answer":"Please use \"Contact Human Support\" so a league admin can help sort out what happened, or email {{SupportContact}} directly."}
]';
INSERT INTO dbo.decision_tree_nodes (node_id, league, topic, question_text, options, source_chunk_id, is_root)
VALUES (@root, @league, 'no_show', N'Are you asking about a team not showing up for their game, or an umpire not showing up for their umpiring assignment?', @optionsJson, @teamChunkId, 1);

PRINT 'Seeded no_show decision trees for mens, womens, youth.';
