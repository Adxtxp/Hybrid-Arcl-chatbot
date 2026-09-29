// ARCL Admin — Phase 5 telemetry + actionable review queue.
//
// Talks to the same API as app.js (see API_BASE below — keep in sync if the frontend moves).
// Every /api/admin/* call needs the X-Admin-Key header; the key lives in a plain JS variable for
// this tab's lifetime only (never localStorage) so it doesn't linger in the browser as a readable
// credential — refresh the page and you'll be asked for it again.
//
// conversation_log.query and .response_text are free-text (a user's own question, or an LLM/
// decision-tree answer) — untrusted content, unlike app.js's own bot messages. Every table cell
// below is built with textContent, never innerHTML, so a crafted question can't inject markup
// into this page.

const API_BASE = ""; // same-origin: this file is served from the API's own wwwroot (see README)

// Matches admin.css's --viz-series-* custom properties — kept as plain hex here (rather than
// read via getComputedStyle) since charts.js takes raw color strings and this avoids a layout
// dependency. Fixed order, never cycled — see charts.js's header comment and the dataviz skill's
// palette.md for why.
const VIZ = { series1: "#2a78d6", series2: "#eb6834", series3: "#1baf7a", series4: "#eda100" };

let adminKey = null;

const el = {
  gate: document.getElementById("admin-gate"),
  keyForm: document.getElementById("admin-key-form"),
  keyInput: document.getElementById("admin-key-input"),
  keyError: document.getElementById("admin-key-error"),
  app: document.getElementById("admin-app"),
  refreshBtn: document.getElementById("refresh-btn"),
  summaryCards: document.getElementById("summary-cards"),
  routeBreakdownChart: document.getElementById("route-breakdown-chart"),
  queryTrendChart: document.getElementById("query-trend-chart"),
  costTrendChart: document.getElementById("cost-trend-chart"),
  tokenUsageChart: document.getElementById("token-usage-chart"),
  leagueVolumeChart: document.getElementById("league-volume-chart"),
  topChunksChart: document.getElementById("top-chunks-chart"),
  topUnansweredChart: document.getElementById("top-unanswered-chart"),
  dailyActivityBody: document.querySelector("#daily-activity-table tbody"),
  leagueVolumeBody: document.querySelector("#league-volume-table tbody"),
  dropoffBody: document.querySelector("#dropoff-table tbody"),
  dropoffCount: document.getElementById("dropoff-count"),
  topChunksBody: document.querySelector("#top-chunks-table tbody"),
  topUnansweredBody: document.querySelector("#top-unanswered-table tbody"),
  reviewQueueBody: document.querySelector("#review-queue-table tbody"),
  reviewQueueCount: document.getElementById("review-queue-count"),
  insightsList: document.getElementById("insights-list"),
  lastUpdated: document.getElementById("last-updated"),
};

// Highlights the section nav link for whichever section is currently in view. Purely cosmetic —
// sections stay hidden (display:none via .hidden on #admin-app) until the key is accepted, so this
// only starts doing anything once the dashboard is actually visible.
const navLinks = Array.from(document.querySelectorAll(".admin-nav-link"));
function setupScrollSpy() {
  const sections = navLinks
    .map((link) => document.querySelector(link.getAttribute("href")))
    .filter(Boolean);
  const observer = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        const href = `#${entry.target.id}`;
        navLinks.forEach((link) => link.classList.toggle("active", link.getAttribute("href") === href));
      }
    },
    { rootMargin: "-45% 0px -50% 0px", threshold: 0 },
  );
  sections.forEach((s) => observer.observe(s));
}
setupScrollSpy();

async function adminFetch(path, options = {}) {
  const resp = await fetch(`${API_BASE}${path}`, {
    ...options,
    headers: { ...(options.headers ?? {}), "X-Admin-Key": adminKey },
  });
  return resp;
}

el.keyForm.addEventListener("submit", async (e) => {
  e.preventDefault();
  const candidate = el.keyInput.value.trim();
  if (!candidate) return;

  adminKey = candidate;
  el.keyError.classList.add("hidden");

  // Charts measure their own container's clientWidth to lay themselves out and to position
  // tooltips (see charts.js's makeTooltip) — that measurement returns 0 while #admin-app is still
  // display:none, silently falling back to a hardcoded width and throwing off every tooltip's
  // position once the app becomes visible at a different real width. Show the app *before*
  // loadAll() renders anything, and roll back if the key turns out to be invalid.
  el.gate.classList.add("hidden");
  el.app.classList.remove("hidden");

  const ok = await loadAll();
  if (!ok) {
    adminKey = null;
    el.app.classList.add("hidden");
    el.gate.classList.remove("hidden");
    el.keyError.classList.remove("hidden");
  }
});

el.refreshBtn.addEventListener("click", () => loadAll());

async function loadAll() {
  const [summaryResp, queueResp, dropoffResp, topChunksResp, topUnansweredResp] = await Promise.all([
    adminFetch("/api/admin/summary"),
    adminFetch("/api/admin/review-queue"),
    adminFetch("/api/admin/decision-tree-dropoff"),
    adminFetch("/api/admin/top-chunks"),
    adminFetch("/api/admin/top-unanswered"),
  ]);

  const responses = [summaryResp, queueResp, dropoffResp, topChunksResp, topUnansweredResp];
  if (responses.some((r) => r.status === 401)) return false;
  if (responses.some((r) => !r.ok)) {
    alert("Couldn't load admin data. Check the API is running and try again.");
    return true; // key itself was accepted (not a 401) — don't re-prompt for it
  }

  const summary = await summaryResp.json();
  const dropoff = await dropoffResp.json();
  const topChunks = await topChunksResp.json();
  const topUnanswered = await topUnansweredResp.json();

  renderSummary(summary);
  renderReviewQueue(await queueResp.json());
  renderDropoff(dropoff);
  renderTopChunks(topChunks);
  renderTopUnanswered(topUnanswered);
  renderInsights({ summary, dropoff, topChunks, topUnanswered });
  el.lastUpdated.textContent = `Updated ${new Date().toLocaleTimeString()}`;
  return true;
}

// ---------------------------------------------------------------------- summary

function renderSummary(data) {
  el.summaryCards.innerHTML = "";
  const totalTokens = (data.totalPromptTokens ?? 0) + (data.totalCompletionTokens ?? 0);
  const cards = [
    ["Total queries", data.totalQueries],
    ["FAQ", data.faqQueries],
    ["Decision tree", data.decisionTreeQueries],
    ["Human handoff", data.humanHandoffQueries],
    ["👍 thumbs up", data.thumbsUpCount],
    ["👎 thumbs down", data.thumbsDownCount],
    ["Total cost (USD)", data.totalCostUsd != null ? `$${Number(data.totalCostUsd).toFixed(4)}` : "—"],
    ["Avg latency", data.avgLatencyMs != null ? `${Math.round(data.avgLatencyMs)} ms` : "—"],
    ["Total tokens", totalTokens > 0 ? totalTokens.toLocaleString() : "—"],
    ["Prompt tokens", data.totalPromptTokens != null ? data.totalPromptTokens.toLocaleString() : "—"],
    ["Completion tokens", data.totalCompletionTokens != null ? data.totalCompletionTokens.toLocaleString() : "—"],
  ];
  for (const [label, value] of cards) {
    const card = document.createElement("div");
    card.className = "card";
    const valueEl = document.createElement("div");
    valueEl.className = "card-value";
    valueEl.textContent = String(value);
    const labelEl = document.createElement("div");
    labelEl.className = "card-label";
    labelEl.textContent = label;
    card.appendChild(valueEl);
    card.appendChild(labelEl);
    el.summaryCards.appendChild(card);
  }

  el.dailyActivityBody.innerHTML = "";
  for (const row of data.dailyActivity) {
    const tr = document.createElement("tr");
    appendCell(tr, row.date);
    appendCell(tr, row.queryCount);
    appendCell(tr, row.promptTokens != null ? row.promptTokens.toLocaleString() : "—");
    appendCell(tr, row.completionTokens != null ? row.completionTokens.toLocaleString() : "—");
    appendCell(tr, row.totalCostUsd != null ? `$${Number(row.totalCostUsd).toFixed(4)}` : "—");
    appendCell(tr, row.avgCostPerQueryUsd != null ? `$${Number(row.avgCostPerQueryUsd).toFixed(5)}` : "—");
    appendCell(tr, row.thumbsUpCount);
    appendCell(tr, row.thumbsDownCount);
    el.dailyActivityBody.appendChild(tr);
  }

  el.leagueVolumeBody.innerHTML = "";
  for (const row of data.queryVolumeByLeague) {
    const tr = document.createElement("tr");
    appendCell(tr, row.date);
    appendCell(tr, row.league ?? "(no league selected)");
    appendCell(tr, row.queryCount);
    el.leagueVolumeBody.appendChild(tr);
  }

  renderCharts(data);
}

// ---------------------------------------------------------------------- charts
// Data already fetched for the cards/tables above — these just re-project it. See charts.js.

function renderCharts(data) {
  // Route breakdown: one horizontal stacked bar, part-to-whole of the last-7-days total.
  ArclCharts.renderStackedBarChart(
    el.routeBreakdownChart,
    ["Total"],
    [
      { label: "FAQ", color: VIZ.series1, values: [data.faqQueries] },
      { label: "Decision tree", color: VIZ.series2, values: [data.decisionTreeQueries] },
      { label: "Human handoff", color: VIZ.series3, values: [data.humanHandoffQueries] },
    ],
    { horizontal: true },
  );

  // dailyActivity comes back most-recent-first (see AdminService.cs); charts read left-to-right.
  const activityAsc = [...data.dailyActivity].reverse();
  const shortDate = (d) => d.slice(5); // "2026-09-22" -> "09-22", plenty for a 14-point axis

  ArclCharts.renderLineChart(el.queryTrendChart, [
    { label: "Queries", color: VIZ.series1, points: activityAsc.map((d) => ({ x: shortDate(d.date), y: d.queryCount })) },
  ]);

  ArclCharts.renderLineChart(
    el.costTrendChart,
    [{ label: "Cost", color: VIZ.series1, points: activityAsc.map((d) => ({ x: shortDate(d.date), y: Number(d.totalCostUsd ?? 0) })) }],
    { valueFormat: (n) => `$${n.toFixed(4)}` },
  );

  ArclCharts.renderStackedBarChart(
    el.tokenUsageChart,
    activityAsc.map((d) => shortDate(d.date)),
    [
      { label: "Prompt tokens", color: VIZ.series1, values: activityAsc.map((d) => d.promptTokens ?? 0) },
      { label: "Completion tokens", color: VIZ.series2, values: activityAsc.map((d) => d.completionTokens ?? 0) },
    ],
  );

  // queryVolumeByLeague is a flat (date, league, count) list — pivot to one series per league.
  const leagueLabels = { mens: "Men's", womens: "Women's", youth: "Youth" };
  const leagueColors = { mens: VIZ.series1, womens: VIZ.series2, youth: VIZ.series3 };
  const dates = [...new Set(data.queryVolumeByLeague.map((r) => r.date))].sort();
  const leagueSeries = Object.keys(leagueLabels)
    .map((lg) => ({
      label: leagueLabels[lg],
      color: leagueColors[lg],
      values: dates.map((d) => data.queryVolumeByLeague.find((r) => r.date === d && r.league === lg)?.queryCount ?? 0),
    }))
    .filter((s) => s.values.some((v) => v > 0)); // don't legend a league with zero queries all week

  ArclCharts.renderStackedBarChart(el.leagueVolumeChart, dates.map(shortDate), leagueSeries);
}

// ---------------------------------------------------------------------- decision-tree drop-off

function renderDropoff(items) {
  el.dropoffCount.textContent = `${items.length} stuck`;
  el.dropoffBody.innerHTML = "";
  for (const item of items) {
    const tr = document.createElement("tr");
    appendCell(tr, new Date(item.updatedAt).toLocaleString());
    appendCell(tr, item.league ?? "—");
    appendCell(tr, item.stuckAtQuestion);
    appendCell(tr, item.sessionId.slice(0, 8) + "…");
    el.dropoffBody.appendChild(tr);
  }
}

// ---------------------------------------------------------------------- top retrieved chunks

function renderTopChunks(items) {
  el.topChunksBody.innerHTML = "";
  for (const item of items) {
    const tr = document.createElement("tr");
    appendCell(tr, item.league);
    appendCell(tr, item.sectionNumber);
    appendCell(tr, item.sectionTitle);
    appendCell(tr, item.timesRetrieved);
    el.topChunksBody.appendChild(tr);
  }

  // Ranked magnitude -> one sequential hue, top 10 only (the table above has the full 20).
  ArclCharts.renderBarChart(
    el.topChunksChart,
    items.slice(0, 10).map((item) => ({ label: `${item.league} \u00A7${item.sectionNumber}`, value: item.timesRetrieved })),
    { color: VIZ.series1 },
  );
}

// ---------------------------------------------------------------------- most commonly unanswered

function renderTopUnanswered(items) {
  el.topUnansweredBody.innerHTML = "";
  for (const item of items) {
    const tr = document.createElement("tr");
    appendCell(tr, item.query);
    appendCell(tr, item.timesAsked);
    appendCell(tr, new Date(item.lastAsked).toLocaleString());
    el.topUnansweredBody.appendChild(tr);
  }

  const chartCard = document.getElementById("top-unanswered-chart-card");
  if (items.length === 0) {
    chartCard.style.display = "none";
    return;
  }
  chartCard.style.display = "";
  ArclCharts.renderBarChart(
    el.topUnansweredChart,
    items.map((item) => ({ label: item.query, value: item.timesAsked })),
    { color: VIZ.series2 }, // orange — distinct from the "what people ask about" chart's blue, since this one is specifically about failures
  );
}

// ---------------------------------------------------------------------- insights
// Plain computed observations from the data already on this page — no separate model call, no
// invented numbers. Each one states a real figure; "notable" ones get a status color (icon +
// label, never color alone, per the dataviz skill) rather than a plain gray rule for everything.

function renderInsights({ summary, dropoff, topChunks, topUnanswered }) {
  const insights = computeInsights({ summary, dropoff, topChunks, topUnanswered });
  el.insightsList.innerHTML = "";
  if (insights.length === 0) {
    const li = document.createElement("li");
    li.className = "insight";
    li.textContent = "Not enough data yet for insights.";
    el.insightsList.appendChild(li);
    return;
  }
  for (const insight of insights) {
    const li = document.createElement("li");
    li.className = `insight ${insight.tone}`;
    const icon = document.createElement("span");
    icon.className = "insight-icon";
    icon.textContent = insight.tone === "warning" ? "⚠️" : insight.tone === "good" ? "✅" : "•";
    const text = document.createElement("span");
    text.className = "insight-text";
    // insight.parts is [{text, strong?}] — built with textContent throughout, same discipline as
    // every other free-text field on this page (a question in topUnanswered is untrusted input).
    for (const part of insight.parts) {
      if (part.strong) {
        const strong = document.createElement("strong");
        strong.textContent = part.text;
        text.appendChild(strong);
      } else {
        text.appendChild(document.createTextNode(part.text));
      }
    }
    li.appendChild(icon);
    li.appendChild(text);
    el.insightsList.appendChild(li);
  }
}

function computeInsights({ summary, dropoff, topChunks, topUnanswered }) {
  const insights = [];
  const total = summary.totalQueries || 0;

  if (total > 0) {
    const handoffPct = Math.round((summary.humanHandoffQueries / total) * 100);
    insights.push({
      tone: handoffPct >= 25 ? "warning" : "info",
      parts: [
        { text: `${handoffPct}%`, strong: true },
        { text: ` of the last 7 days' ${total.toLocaleString()} queries ended in a human handoff (${summary.humanHandoffQueries} of them)` +
                 (handoffPct >= 25 ? " — worth a look at the review queue below." : ".") },
      ],
    });
  }

  if (topUnanswered.length > 0) {
    const top = topUnanswered[0];
    insights.push({
      tone: "warning",
      parts: [
        { text: "Most repeated unanswered question: " },
        { text: `"${top.query}"`, strong: true },
        { text: ` — asked ${top.timesAsked} times, most recently ${new Date(top.lastAsked).toLocaleDateString()}.` },
      ],
    });
  } else {
    insights.push({
      tone: "good",
      parts: [{ text: "No question has failed more than once in a row — nothing repeating in the review queue." }],
    });
  }

  if (dropoff.length > 0) {
    insights.push({
      tone: dropoff.length >= 10 ? "warning" : "info",
      parts: [
        { text: `${dropoff.length}`, strong: true },
        { text: ` session${dropoff.length === 1 ? " is" : "s are"} currently stuck mid-decision-tree (idle over an hour) — see "Stuck decision trees" below.` },
      ],
    });
  }

  if (topChunks.length > 0) {
    const top = topChunks[0];
    insights.push({
      tone: "info",
      parts: [
        { text: "Most-asked-about rule: " },
        { text: `${top.league} \u00A7${top.sectionNumber} (${top.sectionTitle})`, strong: true },
        { text: `, retrieved ${top.timesRetrieved} times in the last 30 days.` },
      ],
    });
  }

  const activity = summary.dailyActivity ?? [];
  const withQueries = activity.filter((d) => d.queryCount > 0);
  if (withQueries.length >= 2) {
    const busiest = withQueries.reduce((a, b) => (b.queryCount > a.queryCount ? b : a));
    insights.push({
      tone: "info",
      parts: [
        { text: "Busiest day in the trend window: " },
        { text: `${busiest.date} (${busiest.queryCount} queries)`, strong: true },
        { text: "." },
      ],
    });
  }

  if (summary.thumbsDownCount > 0 && summary.thumbsUpCount === 0) {
    insights.push({
      tone: "warning",
      parts: [{ text: `All explicit feedback this week was negative (${summary.thumbsDownCount} 👎, 0 👍) — check the review queue for what's going wrong.` }],
    });
  }

  return insights;
}

function appendCell(tr, text) {
  const td = document.createElement("td");
  td.textContent = text;
  tr.appendChild(td);
  return td;
}

// ---------------------------------------------------------------------- review queue

function renderReviewQueue(items) {
  const openCount = items.filter((i) => !i.reviewedAt).length;
  el.reviewQueueCount.textContent = `${openCount} open / ${items.length} total`;

  el.reviewQueueBody.innerHTML = "";
  for (const item of items) {
    const tr = document.createElement("tr");
    if (item.reviewedAt) tr.classList.add("reviewed");

    appendCell(tr, new Date(item.createdAt).toLocaleString());
    appendCell(tr, item.league ?? "—");
    appendCell(tr, item.routePath);

    const feedbackText = item.userFeedback === 1 ? "👍" : item.userFeedback === 0 ? "👎" : "—";
    appendCell(tr, feedbackText);

    const queryCell = appendCell(tr, item.query);
    queryCell.classList.add("cell-clamp");
    queryCell.title = item.query; // full text on hover — clamped cells only show the first few lines

    const responseCell = appendCell(tr, item.responseText ?? "—");
    responseCell.style.maxWidth = "260px";
    responseCell.classList.add("cell-clamp");
    if (item.responseText) responseCell.title = item.responseText;

    const statusCell = document.createElement("td");
    const pill = document.createElement("span");
    pill.className = "status-pill " + (item.reviewedAt ? "reviewed" : "open");
    pill.textContent = item.reviewedAt ? "Reviewed" : "Open";
    statusCell.appendChild(pill);
    if (item.reviewedAt) {
      const when = document.createElement("div");
      when.className = "cell-note";
      when.textContent = new Date(item.reviewedAt).toLocaleString();
      statusCell.appendChild(when);
    }
    if (item.adminNote) {
      const note = document.createElement("div");
      note.className = "cell-note";
      note.textContent = `"${item.adminNote}"`;
      statusCell.appendChild(note);
    }
    tr.appendChild(statusCell);

    const actionCell = document.createElement("td");
    if (!item.reviewedAt) {
      const form = document.createElement("form");
      form.className = "review-form";
      const noteInput = document.createElement("input");
      noteInput.type = "text";
      noteInput.placeholder = "Note (optional)";
      const submitBtn = document.createElement("button");
      submitBtn.type = "submit";
      submitBtn.textContent = "Mark reviewed";
      form.appendChild(noteInput);
      form.appendChild(submitBtn);
      form.addEventListener("submit", async (e) => {
        e.preventDefault();
        submitBtn.disabled = true;
        await markReviewed(item.logId, noteInput.value.trim());
        await loadAll();
      });
      actionCell.appendChild(form);
    }
    tr.appendChild(actionCell);

    el.reviewQueueBody.appendChild(tr);
  }
}

async function markReviewed(logId, note) {
  const resp = await adminFetch(`/api/admin/review-queue/${logId}/review`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ note: note || null }),
  });
  if (!resp.ok) alert("Couldn't mark this reviewed. Please try again.");
}
