-- ARCL Hybrid Chatbot — Phase 5 admin review tracking.
--
-- Adds two columns so an admin can mark a review-queue entry (see 004_dashboards.sql query #2:
-- every human_handoff route plus every thumbs-down) as looked at, with an optional note — turning
-- that raw SELECT into a real lightweight ticket queue ("looked at, added a decision tree for it")
-- instead of a number nobody acts on.
--
-- Read via GET /api/admin/review-queue, written via POST /api/admin/review-queue/{logId}/review
-- (see api/Services/AdminService.cs). Both endpoints require the X-Admin-Key header — see
-- Arcl:AdminApiKey in appsettings.json / api/Program.cs.
ALTER TABLE dbo.conversation_log ADD
    reviewed_at DATETIME2(3) NULL,
    admin_note  NVARCHAR(1000) NULL;
