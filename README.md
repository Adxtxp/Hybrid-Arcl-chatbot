# ARCL Hybrid Chatbot — Phases 1-4 (+ Phase 5 wiring)

Everything below targets **SQL Server 2025**, using its native `VECTOR` type and `VECTOR_DISTANCE`
(both GA) instead of a separate vector database — the organization already runs and manages SQL
Server through SSMS, so this keeps the whole system in that one instance. See `sql/001_schema.sql`
for the reasoning on exact vs. approximate vector search.

**None of this has been compiled or run.** There's no .NET SDK in the sandbox this was written in,
so treat it as a strong first draft to review and build, not finished software — especially the
newly-GA SQL Server vector APIs and the Gemini request/response shapes, both confirmed against
current docs but worth a compile-and-smoke-test pass before anything touches real users.

## Layout

```
sql/       001_schema.sql                  — core tables (Phase 1)
           003_seed_decision_trees.sql     — bad_weather + no_show decision trees, all 3 leagues
           004_dashboards.sql              — admin/telemetry queries (Phase 5), now also served by
                                              GET /api/admin/summary and /review-queue
           005_admin_review_tracking.sql   — reviewed_at/admin_note columns for the review queue
           006_team_ranking.sql            — pending_team_ranking flag for the "which team?" flow
           007_token_tracking.sql          — prompt_tokens/completion_tokens on conversation_log
ingest/    C# console app — scrapes a rulebook page into rule_chunks (Phase 1)
api/       ArclChatbot.Api — ASP.NET Core backend (Phase 2, + Phase 5 telemetry + admin API +
           live team ranking lookup, TeamRankingService.cs)
frontend/  static HTML/CSS/JS — chat UI (Phase 3) + admin.html, a read-only telemetry dashboard
           and actionable review queue for whoever manages the bot (Phase 5), + widget.js, an
           embeddable popup version of the chat for arcl.org's own pages (widget-demo.html is a
           throwaway test host page for it — see "Embeddable popup widget" below). Source of truth
           for all frontend files — copy into api/wwwroot/ after editing (see "Serving everything
           from one origin" below); api/wwwroot/ itself is a build artifact, not source.
eval/      C# console app — runs test-questions.json against the API and scores it (Phase 4)
```

## Running order

```bash
# 1. Schema
sqlcmd -S <server> -d arcl -i sql/001_schema.sql

# 2. Ingest all three rulebooks (see ingest/README notes below — check the "anchors not found" output!)
export ARCL_DB_CONNECTION="Server=<server>;Database=arcl;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;"
export GEMINI_API_KEY="..."
cd ingest
dotnet run -- --league mens   --url https://arcl.org/Docs/Mens_League_Rules.htm   --anchors SectionAnchors/mens.json   --effective-start 2024-01-01
dotnet run -- --league womens --url https://arcl.org/Docs/Womens_League_Rules.htm --anchors SectionAnchors/womens.json --effective-start 2017-02-08
dotnet run -- --league youth  --url https://arcl.org/Docs/Kids_League_Rules.htm    --anchors SectionAnchors/youth.json  --effective-start 2024-02-24

# 2b. Also ingest the general ARCL FAQ page (login/registration/scorecard-upload/CricClubs
#     how-tos) — same page, same anchors file, once per league so it's retrievable regardless of
#     which league a session has selected (this content isn't league-specific, but rule_chunks.
#     league is NOT NULL and RetrievalService always filters on it)
dotnet run -- --league mens   --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
dotnet run -- --league womens --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
dotnet run -- --league youth  --url https://arcl.org/Pages/Content/FAQ.aspx --anchors SectionAnchors/faq.json --effective-start 2026-09-22
cd ..

# 3. Seed the decision trees (needs rule_chunks populated first, for SourceChunkID lookups)
sqlcmd -S <server> -d arcl -i sql/003_seed_decision_trees.sql

# 3b. Add the admin review-queue tracking columns (one-time, safe to skip if already applied)
sqlcmd -S <server> -d arcl -i sql/005_admin_review_tracking.sql

# 3c. Add the team-ranking "which team?" flag (one-time, safe to skip if already applied)
sqlcmd -S <server> -d arcl -i sql/006_team_ranking.sql

# 3d. Add prompt/completion token columns (one-time, safe to skip if already applied)
sqlcmd -S <server> -d arcl -i sql/007_token_tracking.sql

# 4. Copy the frontend into the API's wwwroot (see "Serving everything from one origin" below —
#    api/wwwroot/ is what's actually served; frontend/ is the source you edit)
cp frontend/*.html frontend/*.js frontend/*.css api/wwwroot/

# 5. Run the API
cd api
export ARCL_ADMIN_API_KEY="..."   # any random string — required, see "Admin page" below
dotnet run
# defaults to http://localhost:5000 and now serves everything from that one origin/port:
#   http://localhost:5000/index.html        — the chat UI
#   http://localhost:5000/admin.html        — the admin dashboard (see "Admin page" below)
#   http://localhost:5000/widget-demo.html  — the embeddable-widget test host page
# set Arcl:ConnectionString / Arcl:GeminiApiKey in appsettings.json, or ARCL_DB_CONNECTION /
# GEMINI_API_KEY env vars (either works)
cd ..

# 6. Evaluate
cd eval
dotnet run -- --api-base http://localhost:5000
# writes eval/results/eval-run-<timestamp>.csv
```

## Admin page

`frontend/admin.html` (+ `admin.js`, `admin.css`, `charts.js`) is a read-only telemetry dashboard
and actionable review queue for whoever manages the bot day-to-day — no more SSMS required. It
wraps six API endpoints, together covering every query in `sql/004_dashboards.sql`:

- `GET /api/admin/summary` — the last-7-days totals (query #6, extended with prompt/completion
  token sums from `sql/007_token_tracking.sql`), a 14-day daily-activity trend (queries, tokens,
  cost, and 👍/👎 feedback per day — richer than query #4, which only has cost), and query volume
  by league (query #1).
- `GET /api/admin/review-queue` — every `human_handoff` route plus every thumbs-down, i.e. exactly
  query #2, but not windowed to 7 days (this is the ongoing ticket queue, not a weekly snapshot).
- `POST /api/admin/review-queue/{logId}/review` — marks a row reviewed with an optional note
  (`sql/005_admin_review_tracking.sql`'s `reviewed_at`/`admin_note` columns), so the queue is a
  real lightweight ticket list instead of a raw SELECT nobody acts on.
- `GET /api/admin/decision-tree-dropoff` — query #3: sessions that started a decision tree (rain,
  no-show) and have sat idle mid-tree for over an hour — a real visitor who didn't get an answer.
- `GET /api/admin/top-chunks` — query #5: the rule sections retrieval pulls most often in the last
  30 days, i.e. what people are actually asking about — useful for prioritizing the next decision
  tree or rulebook clarification.
- `GET /api/admin/top-unanswered` — the exact same question, asked more than once, where every
  occurrence ended in `human_handoff` (i.e. the bot never successfully answered it). Grouped/counted
  server-side in `AdminService.GetTopUnansweredAsync`; the clearest "what to fix next" signal the
  dashboard surfaces, distinct from the review queue (which is every individual handoff/thumbs-down,
  not just the repeated ones).

All six require an `X-Admin-Key` header, checked by a single piece of middleware in
`api/Program.cs` against `Arcl:AdminApiKey` (appsettings.json — ships blank on purpose, same as
`ConnectionString`/`GeminiApiKey`) or the `ARCL_ADMIN_API_KEY` env var. There's no real user
account system behind it — one shared key, checked before any `/api/admin/*` route executes — which
is enough to keep conversation data and the review queue from being open to anyone who finds the
URL, without building infrastructure this project doesn't otherwise need. The key itself lives in
a plain JS variable in `admin.js` for the tab's lifetime only (never `localStorage`), so it doesn't
linger in the browser as a readable credential.

Every table on the page (daily activity, league volume, top chunks, top unanswered) also has a
chart above it, built from scratch as inline SVG in `frontend/charts.js` (no charting library) —
a horizontal bar for route breakdown/rankings, line charts for the two 14-day trends, and stacked
bars for token usage and per-league volume. Built following the `dataviz` skill's method end to
end: chart-type chosen by the data's job (magnitude vs. trend vs. part-to-whole), a categorical
4-color palette validated with `scripts/validate_palette.js` against the card's white surface
(`admin.css`'s `--viz-series-*` vars), thin 2px marks with rounded data-ends, a hover
crosshair+tooltip on every chart, and a legend wherever a chart has more than one series. The
tables stay as each chart's "table view" fallback. **One bug found during the mandatory
"render it and look at it" step:** the line charts' y-axis gridline labels first computed
`Math.round(yMax * frac)`, which collapsed small fractional values (e.g. a day costing $0.02) to
literally `0` — every gridline read "$0" regardless of the real range. Fixed by formatting the
un-rounded fractional value directly instead of rounding first.

Above the charts, an **Insights** panel (`computeInsights()` in `admin.js`) turns the same
already-fetched JSON into five or so plain-language observations — handoff rate, the single most-
repeated unanswered question, how many sessions are currently stuck mid-decision-tree, the most-
retrieved rule section, and the busiest day in the trend window. This is pure arithmetic over data
already on the page, not a separate Gemini call and not a new source of truth — every number in it
traces back to a field in one of the six endpoints above.

The page itself got a visual pass: a sticky header (with a live "Updated HH:MM:SS" stamp) and a
sticky pill-style section nav below it that scroll-spies the active section (`admin.js`'s
`setupScrollSpy`, a plain `IntersectionObserver` — no framework), plus consistent card/table
shadows, hover states, and tabular-number card values, all scoped to `admin.css` so none of it
touches the chat UI's `styles.css`.

**Three real chart tooltip bugs found and fixed** (a screenshot showing a stray black dot on the
Token usage chart, reported directly), all in `frontend/charts.js`'s `makeTooltip`:
1. **The tooltip always rendered empty.** Every call site calls `setRows(rows)` to populate content,
   then `show(x, y)` to reveal it — but `show()` also unconditionally did `tip.innerHTML = ""` as a
   defensive leftover, which ran *after* `setRows` and silently wiped the content back out every
   single time, on every chart. This was the actual root cause of the black dot: a visible tooltip
   box with nothing in it. Fixed by removing that line from `show()` (and dropping its now-unused,
   already-always-`null` first parameter — see the diff for the full before/after).
2. **Wrong positioning context.** The tooltip is appended into the chart's own inner `<div>`, and its
   x/y are computed relative to that div's top-left corner — but CSS `position: absolute` was
   anchoring to `.chart-card` (the only *positioned* ancestor) instead, offsetting every tooltip by
   the card's padding, and for the three cards with a title above the chart (Query volume, Gemini
   cost, Token usage), by that title block's full height too. Fixed by making `makeTooltip` itself
   set `position: relative` on the div it's given, so the coordinate system it computes in matches
   the one CSS actually anchors to.
3. **Hovering a zero-data day still triggered the tooltip.** The token-usage chart's hit rects cover
   every date column, including ones with no logged tokens that day; hovering one now correctly
   hides the tooltip instead of showing an empty box (independent of bug #1, since an empty
   `rows` array is a legitimate case, not a bug in `setRows` itself).

Also found while investigating: the *first* time the admin key is submitted, every chart renders
while `#admin-app` still has `display: none` (only becoming visible after a successful load), so
`container.clientWidth` reads `0` and every chart's SVG falls back to a hardcoded 600-unit
viewBox — the bars still look right (the whole SVG scales uniformly when it becomes visible at its
real width), but it threw off tooltip math specifically, since tooltip position is plain CSS
pixels, not part of that scaling. Fixed by showing `#admin-app` *before* calling `loadAll()` in the
key-form submit handler (rolling back to the gate if the key turns out to be invalid).

**Also fixed:** the section-sign character (`§`) in `admin.js`'s chart labels and insights text now
uses the JS escape `§` instead of the literal UTF-8 byte, after the local dev server (Python's
`http.server`) turned out to serve `.js`/`.html` with no `charset` in its `Content-Type` header —
the HTML document's own `<meta charset="UTF-8">` should rescue that per spec, but the escape
sequence sidesteps the whole question by never depending on file/transport encoding for that one
character.

## Chat UI (`index.html`, `app.js`, `styles.css`)

Given a visual pass to match the admin page's polish: a green gradient header with a small circular
"🏏" brand mark (also on the league-picker card), pill-shaped input/buttons/decision-chips, message
bubbles with a subtle shadow and entrance animation, citations rendered as small tag-pills instead
of plain inline links, thumbs up/down as circular hover-highlight icon buttons instead of
plain-opacity text, a focus ring (matching the brand green) on every input, and a polished NRR
calculator modal (colored fieldset legends, tabular-number result). All CSS-only — no changes to
`app.js`'s logic, DOM structure, or the textContent-only handling of untrusted `conversation_log`
text.

**Suggested questions:** right after picking a league (or resuming a session), the chat shows five
clickable example questions — one per route the bot actually handles (a decision tree, an FAQ
how-to, a plain rule lookup, and the live team-ranking lookup) — for a visitor who isn't sure what
to ask. Clicking one sends it exactly like typing it would (`sendMessage()` in `app.js`, refactored
out of the form's submit handler so both paths share the same request/error handling), and the
suggestions are removed as soon as any message — a suggestion or the visitor's own — is sent.

**Embedded/widget mode:** when loaded with `?embed=1` in the URL, the page hides its own header
title bar (`document.body.classList.add("embedded")` in `app.js`, `.embedded .header-title` in
`styles.css`) since the widget shell (below) already shows one — the NRR/Change League/Support
buttons stay, since they're only reachable from here.

## Embeddable popup widget (`widget.js`, `widget-demo.html`)

A single self-contained script arcl.org's own page template can include to get a floating chat
launcher + popup window, with no changes to that page's markup beyond one `<script>` tag:

```html
<script src="https://<wherever-this-is-hosted>/widget.js" data-src="https://<chat-host>/index.html" defer></script>
```

`data-src` is optional — it defaults to `index.html` next to wherever `widget.js` itself was
loaded from, so hosting both together (the common case, and what same-origin `wwwroot` serving
below gives you automatically) needs zero configuration. Other options: `data-title`,
`data-position` (`bottom-right` default, or `bottom-left`), `data-color`/`data-color-dark`.

The actual chat UI runs inside an `<iframe>`, not injected into the host page's DOM — the same
approach every major embeddable chat widget (Intercom, Drift, Crisp, etc.) uses, and the reason
`index.html` needed zero changes to work as a widget: an iframe is its own document with its own
CSS scope, so nothing on arcl.org's real page (resets, utility classes, id collisions, other
scripts) can ever clash with the chat UI's styles or vice versa. Only the launcher button and the
panel's header/close button are injected directly into the host page, and every one of their class
names is prefixed `arcl-cw-` to make an accidental collision with the host page's own CSS
essentially impossible. No dependencies, no build step, vanilla JS only. `widget-demo.html` is a
throwaway page styled to look nothing like ARCL's real site, kept specifically to prove the widget
doesn't leak either direction.

**Two real bugs found while verifying this against a real host page**, both in `widget.js`:
1. The `?embed=1` iframe-mode flag (above) was implemented in `app.js` but the widget's own
   `iframe.src = cfg.src` assignment never actually appended it — so the popup always showed two
   stacked "ARCL Rules Assistant" title bars (the widget shell's and the chat page's own). Fixed by
   appending `?embed=1` (or `&embed=1` if `data-src` already has a query string) when the iframe is
   first loaded.
2. On a full-screen mobile panel (see "Mobile" below), the floating launcher button stayed visible
   and overlapped the chat input, since nothing repositioned or hid it once the panel took over the
   whole screen. Fixed with a `arcl-cw-launcher-hidden` class toggled whenever the panel opens,
   which only has any effect inside the `max-width: 480px` media query — desktop keeps the launcher
   visible (clicking it again, now showing "✕", is the close affordance there).

Production note: the API's CORS policy is currently `AllowAnyOrigin()` (`api/Program.cs`, already
flagged in-code as dev-only) — tighten it to the real arcl.org origin before this widget is
embedded anywhere that isn't a test page.

## Serving everything from one origin

`api/wwwroot/` is a copy of `frontend/*` (`index.html`, `admin.html`, `widget.js`,
`widget-demo.html`, and their JS/CSS), served by `app.UseDefaultFiles(); app.UseStaticFiles();`
added to `api/Program.cs`. `app.js`/`admin.js`'s `API_BASE` is now `""` (same-origin, relative
fetches) instead of a hardcoded `http://localhost:5000` — the comment above it always said "assumes
same-origin deployment," the code just hadn't actually followed that yet. Practically: one
`dotnet run` on one port serves the chat UI, the admin dashboard, the widget demo, and every
`/api/*` route together, which is what let this whole app be shared as a single link (e.g. via a
tunnel to that one port) instead of needing a separately-hosted frontend with a cross-origin API.
**Remember to re-copy `frontend/*` into `api/wwwroot/` after editing any frontend file** — the two
aren't symlinked, so an edit to `frontend/app.js` alone won't reach what's actually being served
until it's copied over.

## Mobile responsiveness

All three pages (chat, admin, widget) got a real mobile pass, verified with Playwright device
emulation at 375×667 (iPhone SE, the tightest common width) rather than just by eyeballing a
resized desktop browser:
- **Chat UI:** header actions become a horizontally-scrollable row instead of wrapping/cramming,
  message bubbles widen to 88% (75% is too narrow to be readable on a small phone), the chat input
  is pinned at 16px font (below that, iOS Safari auto-zooms in on focus — a common and easy-to-miss
  mobile bug), feedback buttons grow to a 32px touch target, the league-picker/NRR cards cap at
  `max-height: 88vh` with scroll (the NRR card's two fieldsets can exceed a short phone's viewport
  otherwise), and the chat input's bottom padding respects `env(safe-area-inset-bottom)` for the
  iPhone home-indicator area (`viewport-fit=cover` added to the meta viewport tag for this to work).
- **Admin dashboard:** every `<table class="admin-table">` is wrapped in a `.table-scroll` div
  (`overflow-x: auto`) so a wide table (the review queue has 8 columns) scrolls within its own box
  instead of blowing out the page width; the sticky header drops its "Updated HH:MM:SS" timestamp
  below 480px so the title + Refresh button always fit on one line within the header's fixed height.
  **Real bug found here:** a long bot response (off-screen to the right once a table is horizontally
  scrolled) was stretching its entire row's height — since all cells in an HTML table row share the
  tallest cell's height — which showed up on a narrow screen as a large blank gap under the visible
  short cells, with no visual indication anything was there. Fixed by clamping the query/response
  cells to 3 lines (`.cell-clamp`, `-webkit-line-clamp`) with the full text still reachable via a
  `title` attribute on hover/tap-hold, which bounds every row to a sane height on any screen size.
- **Widget:** already had a `max-width: 480px` breakpoint making the popup full-screen instead of a
  floating box; see the two bugs fixed above (double header, overlapping launcher) that specifically
  only showed up once that breakpoint was actually exercised end-to-end.

## What's real vs. what's a starter/placeholder

**Real, pulled from the actual arcl.org rulebooks:** the rulebook section structure (all three
leagues, verified complete — see "Content coverage" below), the bad_weather decision tree's rule
text (3/4 completion threshold, run-rate tiebreaker), and the eval questions' expected sections.

**Content coverage:** as of 2026-09-22, every anchor-defined section in all three `*_League_Rules.
htm` pages ingests cleanly (`ingest/SectionAnchors/*.json`, 61/61 mens, 22/22 womens, 19/19 youth —
re-verified by re-running ingestion against the live pages and confirming zero "anchors not found").
The general ARCL FAQ page (`https://arcl.org/Pages/Content/FAQ.aspx` — login, team registration,
scorecard upload, CricClubs app setup, etc.) is also ingested, 41 Q&A sections × 3 leagues
(`SectionAnchors/faq.json`), since it isn't league-specific rule text but the schema requires a
league on every row. `GeminiClient.cs`'s FAQ prompt now also asks for numbered, one-step-per-line
answers when a question is a how-to (log in, register, upload a scorecard, etc.) and the source
describes a sequence of actions — plain rule questions still get concise prose, not a forced list.

**Live team ranking** (added 2026-09-22, see `api/Services/TeamRankingService.cs`): not part of
the ingested corpus at all — arcl.org has no single static standings page to scrape ahead of time,
only live per-division pages. Since the chat has no team identity/auth, the bot can't know which
team is asking, so `IntentClassifier.cs` detects a ranking/standing question generically (it can't
reliably pull a team name out of arbitrary phrasing — arcl.org's own team directory has names like
"1", "8", and "A Few Good Men") and always asks a clarifying "which team?" first. The session
remembers it's waiting (`session_state.pending_team_ranking`, `sql/006_team_ranking.sql`) and
treats the visitor's *next* message, verbatim, as the team name, live-fetching
`Pages/UI/TeamHistory.aspx?team_name=X` (public, no login) and reading its last table row (most
recent season = current, live, in-progress standing — confirmed directly against a real team mid-
season). **Known tradeoff:** if the visitor replies with anything other than the team name after
being asked — a new question, a typo, changing their mind — that reply is still treated as a team
name lookup attempt (fails gracefully with "couldn't find a team named ...", and the pending flag
clears either way, so the session self-heals on the turn after that). It doesn't get permanently
stuck, but that one turn can be a confusing dead end. Worth hardening later (e.g. re-running intent
classification on the "team name" reply first and bailing out of the pending state if it strongly
matches something else) if this comes up in practice.

**Starter / needs expansion, clearly marked in each file:**
- **Decision trees:** `bad_weather` and `no_show` are both built (`sql/003_seed_decision_trees.sql`),
  for all three leagues. `tie_breaking` is already wired into the classifier
  (`api/Services/IntentClassifier.cs`) but has no tree yet — until one exists, those questions
  correctly fall back to human handoff rather than failing silently.
- **Eval set:** 14 starter questions (`eval/test-questions.json`), not the 50+ real historical
  disputes the blueprint's Phase 4 calls for. This is a harness to run real questions through, not
  a finished benchmark.
- **Cross-references** (`rule_chunk_cross_references`): seeded with 5 rows so far, all in the Men's
  league, each verified against the actual chunk text rather than guessed: "Hit Wicket" (44) ↔
  "No-ball" (42) and "Hit Wicket" → "Dead-Ball" (43) ("...in case of a dead-ball or a no-ball, it
  is invalid"), and "No-ball" → "Run-outs" (47) / "Caught Out" (48) ("The only way to get out on a
  no-ball is a run-out. Hit-wicket or being caught on such a ball will not get the player out.").
  Still sparse — only covers the dismissal rules one question happened to surface; extending it to
  Women's/Youth and other rule areas is worth doing the same way (verify against the real text,
  don't guess).
- **Correctness scoring:** the eval pipeline computes retrieval precision/recall and route
  accuracy automatically, but leaves a blank `correctness` column in its CSV output for a human to
  fill in per the blueprint's rubric (correct / partially correct / wrong / refused-appropriately)
  — deliberately not LLM-graded, since a dispute-resolution tool grading itself isn't a real check.

## Phase 5 (telemetry) status

`api/Program.cs` wires up OpenTelemetry tracing + metrics (console exporter by default; set
`OpenTelemetry:OtlpEndpoint` to ship to Application Insights or a self-hosted collector), a
`/api/feedback` endpoint for the frontend's thumbs up/down, and rate limiting per client IP. The
dashboard queries are plain SQL (`sql/004_dashboards.sql`) — now also served directly by the admin
API + `frontend/admin.html` (see "Admin page" above), so "look at the numbers" and "work the review
queue" no longer require SSMS. What's NOT built: the automated weekly-summary email job and
threshold alerting (error rate / cost spikes) — both still need a scheduler + SMTP/notification
channel wired up (Windows Task Scheduler + a small console app reusing `eval/`'s pattern, per the
plan discussed, rather than assuming SQL Server Agent + Database Mail is configured on a dev/course
setup), which hasn't been built yet.

## Known simplifications worth knowing about

- **A mid-decision-tree session that goes idle never gets cleaned up or resumed.** There's no
  timeout, expiry job, or cleanup anywhere — `session_state.current_decision_tree_node` just stays
  set indefinitely (confirmed by reading `DecisionTreeService.cs`/`SessionService.cs`: nothing ever
  clears it except reaching a terminal answer or clicking "Change League"). It's inert, not
  harmful — a stale node doesn't block the visitor from asking anything else, since `/api/chat`'s
  normal routing never even looks at it (only the decision-tree chip buttons do, via the separate
  `/api/chat/decision-answer` endpoint). But two real gaps follow from that: (1) `app.js`'s
  `resumeSession()` (a page refresh, via the mirrored session/league in localStorage) never
  re-fetches or redisplays the tree's current question/chips, so a visitor who refreshes mid-tree
  has genuinely lost their place and has to start over by asking again; (2) if they *do* ask the
  same decision-tree-triggering question again, `DecisionTreeService.StartAsync` unconditionally
  overwrites the stale node and restarts that tree from the root — a reasonable self-heal, but
  silent (no "picking up where you left off"). The admin dashboard's "Stuck decision trees" report
  (session idle >1hr) is read-only telemetry surfacing this, not a fix. Worth building a real
  "resume the tree on reload" path if visitors hitting this becomes a real pattern in the data.
- **`Arcl:RetrievalTopN` is 10, not 5.** Raised from the original default after adding the FAQ
  page's 41 short, keyword-tight Q&A chunks per league (see "Content coverage" above): a handful of
  those (e.g. "How can I upload my team's scorecard?") sit very close in embedding space to any
  question containing the word "scorecard," and at `topN=5` they filled every slot and crowded out
  the actual rule section for questions like "by when is the scorecard due" — retrieval found only
  *how*-to-upload chunks, not the deadline rule, and the model correctly said it wasn't confident.
  At `topN=10` both kinds of chunk fit. If more heterogeneous content gets added later (mixing many
  short procedural snippets with longer rule sections), watch for this same dilution pattern and
  retune from there — it's a real tradeoff, not a one-time fix.
- **NRR calculator is pure client-side math** (`frontend/app.js`) — no backend call, so it works
  even if the API is down. Standard formula, overs entered in decimal (15 overs 3 balls = 15.5,
  not 15.3 — flagged in the UI since this is the single most common NRR mistake).
- **"Change League" starts a brand-new session** rather than reusing the old one (simpler, and
  sidesteps any stale decision-tree state) — the `/api/session/{id}/league` endpoint that would
  let a client reuse a session is still there if you want that behavior instead.
- **Session ID and league are mirrored to localStorage**, so a page refresh resumes the same
  server-side session instead of dropping back to the league picker. Only the session/league
  survive the refresh, not the on-screen message bubbles — the conversation itself keeps working
  (e.g. an in-progress decision tree still resumes at the right node) since that state lives
  server-side, but the visible chat history above it resets.
- **Rate limiting is per-IP, in-memory** — resets if the API restarts, and won't coordinate across
  multiple API instances if this ever gets load-balanced. Fine for a first deployment; swap to a
  distributed limiter (e.g. backed by SQL Server or Redis) if that becomes a real setup.

## Naming (resolved)

The league's full name is the **American Recreational Cricket League**, not American-Romanian —
confirmed directly against the live site's FAQ page (arcl.org/Pages/Content/FAQ.aspx), which
spells it out as "ARCL - American Recreational Cricket League."
