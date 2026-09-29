// ARCL Rules Assistant — Phase 3 frontend.
//
// Talks to the Phase 2 API (Arcl.Api). Assumes same-origin deployment (this file served from
// the API's wwwroot); change API_BASE below if the frontend is hosted separately.
//
// state.sessionId/state.league are mirrored to localStorage so a page refresh resumes the same
// session instead of dropping back to the league picker. This only restores which session/league
// is active — the visible message history above it is not persisted (Phase 2's session state
// lives server-side, e.g. the current decision-tree node, so the conversation itself still works;
// only the chat bubbles on screen reset).

const API_BASE = ""; // same-origin: this file is served from the API's own wwwroot (see README)

// Set by widget.js's iframe src (?embed=1) when this page is running inside the popup chat
// widget rather than as its own standalone page — the widget shell already shows a title/close
// button, so this page's own duplicate title bar (but not its NRR/Change League/Support actions,
// which are only reachable from here) is hidden in that case. See styles.css's .embedded rule.
if (new URLSearchParams(location.search).get("embed") === "1") {
  document.body.classList.add("embedded");
}

const STORAGE_KEYS = { sessionId: "arcl.sessionId", league: "arcl.league" };

const state = {
  sessionId: null,
  league: null, // "Mens" | "Womens" | "Youth"
};

function saveSession(sessionId, league) {
  try {
    localStorage.setItem(STORAGE_KEYS.sessionId, sessionId);
    localStorage.setItem(STORAGE_KEYS.league, league);
  } catch { /* localStorage unavailable (e.g. private browsing) — session just won't survive a refresh */ }
}

function clearSavedSession() {
  try {
    localStorage.removeItem(STORAGE_KEYS.sessionId);
    localStorage.removeItem(STORAGE_KEYS.league);
  } catch { /* nothing to clean up if it wasn't available in the first place */ }
}

function loadSavedSession() {
  try {
    const sessionId = localStorage.getItem(STORAGE_KEYS.sessionId);
    const league = localStorage.getItem(STORAGE_KEYS.league);
    return sessionId && league ? { sessionId, league } : null;
  } catch {
    return null;
  }
}

const el = {
  gatekeeper: document.getElementById("gatekeeper"),
  app: document.getElementById("app"),
  leagueBadge: document.getElementById("current-league-badge"),
  messages: document.getElementById("messages"),
  decisionChips: document.getElementById("decision-chips"),
  chatForm: document.getElementById("chat-form"),
  chatInput: document.getElementById("chat-input"),
  nrrModal: document.getElementById("nrr-modal"),
  nrrResult: document.getElementById("nrr-result"),
};

// ---------------------------------------------------------------------- gatekeeper

document.querySelectorAll(".league-btn").forEach((btn) => {
  btn.addEventListener("click", () => startSession(btn.dataset.league));
});

async function startSession(league) {
  const resp = await fetch(`${API_BASE}/api/session`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ league }),
  });
  if (!resp.ok) {
    alert("Couldn't start a session. Please try again.");
    return;
  }
  const data = await resp.json();
  state.sessionId = data.sessionId;
  state.league = league;
  saveSession(data.sessionId, league);

  el.leagueBadge.textContent = leagueLabel(league);
  el.gatekeeper.classList.add("hidden");
  el.app.classList.remove("hidden");
  el.messages.innerHTML = "";
  addBotMessage(`You're asking about the ${leagueLabel(league)} league. What's your question?`);
  renderSuggestedQuestions();
}

function resumeSession(sessionId, league) {
  state.sessionId = sessionId;
  state.league = league;

  el.leagueBadge.textContent = leagueLabel(league);
  el.gatekeeper.classList.add("hidden");
  el.app.classList.remove("hidden");
  el.messages.innerHTML = "";
  addBotMessage(`Welcome back — continuing your ${leagueLabel(league)} league conversation.`);
  renderSuggestedQuestions();
}

// ---------------------------------------------------------------------- suggested questions
// Shown once, right after the greeting, for a visitor who isn't sure what to ask — one example
// from each route the bot actually handles (a decision tree, an FAQ how-to, a plain rule lookup,
// and the live team-ranking lookup), so clicking one also doubles as a quick tour of what's
// possible. Removed as soon as the visitor sends any message of their own, same as decision chips.

const SUGGESTED_QUESTIONS = [
  "What happens if a match is stopped early due to rain?",
  "How do I upload my team's scorecard?",
  "What's my team's current ranking?",
  "What are the rules for a no-ball?",
  "How do I register a new team?",
];

function renderSuggestedQuestions() {
  const wrap = document.createElement("div");
  wrap.className = "bubble bot suggested-questions";
  wrap.id = "suggested-questions";

  const label = document.createElement("div");
  label.className = "suggested-questions-label";
  label.textContent = "Not sure what to ask? Try one of these:";
  wrap.appendChild(label);

  const list = document.createElement("div");
  list.className = "suggested-questions-list";
  for (const question of SUGGESTED_QUESTIONS) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "chip suggested-question-chip";
    btn.textContent = question;
    btn.addEventListener("click", () => {
      clearSuggestedQuestions();
      sendMessage(question);
    });
    list.appendChild(btn);
  }
  wrap.appendChild(list);

  el.messages.appendChild(wrap);
  el.messages.scrollTop = el.messages.scrollHeight;
}

function clearSuggestedQuestions() {
  document.getElementById("suggested-questions")?.remove();
}

function leagueLabel(league) {
  return { Mens: "Men's", Womens: "Women's", Youth: "Youth" }[league] ?? league;
}

// ---------------------------------------------------------------------- escape hatch

document.getElementById("change-league-btn").addEventListener("click", () => {
  clearSavedSession(); // so a refresh while the picker is up doesn't resurrect the abandoned session
  el.app.classList.add("hidden");
  el.gatekeeper.classList.remove("hidden");
});

document.getElementById("human-support-btn").addEventListener("click", async () => {
  if (!state.sessionId) return;
  addUserMessage("[Requested human support]");
  const resp = await fetch(`${API_BASE}/api/human-handoff`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ sessionId: state.sessionId, message: "User requested human support directly." }),
  });
  if (resp.ok) {
    const data = await resp.json();
    addBotMessage(data.message, { handoff: true, logId: data.logId });
  }
});

// ---------------------------------------------------------------------- chat

el.chatForm.addEventListener("submit", (e) => {
  e.preventDefault();
  const text = el.chatInput.value.trim();
  if (!text || !state.sessionId) return;
  el.chatInput.value = "";
  sendMessage(text);
});

// Shared by the chat form's own submit and by clicking a suggested question — same request, same
// bubble/error handling either way.
async function sendMessage(text) {
  if (!state.sessionId) return;

  addUserMessage(text);
  clearDecisionChips();
  clearSuggestedQuestions();

  const thinkingBubble = addBotMessage("…", { pending: true });

  try {
    const resp = await fetch(`${API_BASE}/api/chat`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: state.sessionId, message: text }),
    });
    thinkingBubble.remove();

    if (!resp.ok) {
      const err = await resp.json().catch(() => ({}));
      addBotMessage(err.error ?? "Something went wrong. Please try again.", { handoff: true });
      return;
    }

    const data = await resp.json();
    handleChatResponse(data);
  } catch (err) {
    thinkingBubble.remove();
    addBotMessage("Couldn't reach the server. Please try again.", { handoff: true });
  }
}

function handleChatResponse(data) {
  const isHandoff = data.routePath === "human_handoff";
  addBotMessage(data.message, { handoff: isHandoff, logId: data.logId, citations: data.citations });

  if (data.decisionOptions && data.decisionOptions.length > 0) {
    renderDecisionChips(data.decisionOptions);
  }
}

// ---------------------------------------------------------------------- decision tree chips

function renderDecisionChips(options) {
  el.decisionChips.innerHTML = "";
  el.decisionChips.classList.remove("hidden");

  options.forEach((opt) => {
    const chip = document.createElement("button");
    chip.className = "chip";
    chip.textContent = opt.label;
    chip.addEventListener("click", () => answerDecision(opt.label));
    el.decisionChips.appendChild(chip);
  });
}

function clearDecisionChips() {
  el.decisionChips.innerHTML = "";
  el.decisionChips.classList.add("hidden");
}

async function answerDecision(label) {
  addUserMessage(label);
  clearDecisionChips();

  const thinkingBubble = addBotMessage("…", { pending: true });

  const resp = await fetch(`${API_BASE}/api/chat/decision-answer`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ sessionId: state.sessionId, selectedOptionLabel: label }),
  });
  thinkingBubble.remove();

  if (!resp.ok) {
    const err = await resp.json().catch(() => ({}));
    addBotMessage(err.error ?? "Something went wrong with that step.", { handoff: true });
    return;
  }

  const data = await resp.json();
  handleChatResponse(data);
}

// ---------------------------------------------------------------------- message rendering + feedback

function addUserMessage(text) {
  const bubble = document.createElement("div");
  bubble.className = "bubble user";
  bubble.textContent = text;
  el.messages.appendChild(bubble);
  el.messages.scrollTop = el.messages.scrollHeight;
  return bubble;
}

// Matches one email address inside otherwise-plain message text (e.g. the support contact in
// handoff/low-confidence messages) so it can be rendered as a real mailto: link instead of dead
// plain text. Only handles a single occurrence — every message this app generates mentions the
// support address at most once.
const EMAIL_PATTERN = /[\w.+-]+@[\w-]+\.[\w.-]+/;

function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
}

function addBotMessage(text, { handoff = false, pending = false, logId = null, citations = [] } = {}) {
  const bubble = document.createElement("div");
  bubble.className = "bubble bot" + (handoff ? " handoff" : "");

  const emailMatch = text.match(EMAIL_PATTERN);
  if (emailMatch) {
    const email = emailMatch[0];
    const before = text.slice(0, emailMatch.index);
    const after = text.slice(emailMatch.index + email.length);
    // innerHTML, same as the citations block below — safe here because before/after are
    // HTML-escaped first and the only markup introduced is the mailto: link built from the
    // matched email text itself, not from unescaped user input.
    bubble.innerHTML = escapeHtml(before) + `<a href="mailto:${email}">${email}</a>` + escapeHtml(after);
  } else {
    bubble.textContent = text;
  }

  if (citations && citations.length > 0) {
    const cite = document.createElement("div");
    cite.className = "citations";
    cite.innerHTML =
      "Source: " +
      citations
        .map((c) => `<a href="${c.sourceUrl}" target="_blank" rel="noopener">Section ${c.sectionNumber}: ${c.sectionTitle}</a>`)
        .join(", ");
    bubble.appendChild(cite);
  }

  if (!pending && logId != null) {
    bubble.appendChild(buildFeedbackRow(logId));
  }

  el.messages.appendChild(bubble);
  el.messages.scrollTop = el.messages.scrollHeight;
  return bubble;
}

function buildFeedbackRow(logId) {
  const row = document.createElement("div");
  row.className = "feedback-row";

  const up = document.createElement("button");
  up.textContent = "👍";
  const down = document.createElement("button");
  down.textContent = "👎";

  const send = async (thumbsUp, clickedBtn, otherBtn) => {
    clickedBtn.classList.add("selected");
    otherBtn.disabled = true;
    clickedBtn.disabled = true;
    await fetch(`${API_BASE}/api/feedback`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ logId, thumbsUp }),
    }).catch(() => {}); // best-effort — feedback failing shouldn't interrupt the chat
  };

  up.addEventListener("click", () => send(true, up, down));
  down.addEventListener("click", () => send(false, down, up));

  row.appendChild(up);
  row.appendChild(down);
  return row;
}

// ---------------------------------------------------------------------- NRR calculator (Phase 3 micro-widget)
// Pure client-side arithmetic — no backend call. Standard formula:
//   NRR = (runs scored ÷ overs faced) − (runs conceded ÷ overs bowled)
// Overs must be entered in decimal form (e.g. 15 overs 3 balls = 15.5, NOT 15.3) — cricket overs
// are base-6, not base-10, so this is the single most common NRR calculation mistake.

document.getElementById("nrr-btn").addEventListener("click", () => {
  el.nrrModal.classList.remove("hidden");
});
document.getElementById("nrr-close-btn").addEventListener("click", () => {
  el.nrrModal.classList.add("hidden");
});

["nrr-runs-for", "nrr-overs-for", "nrr-runs-against", "nrr-overs-against"].forEach((id) => {
  document.getElementById(id).addEventListener("input", computeNrr);
});

function computeNrr() {
  const runsFor = parseFloat(document.getElementById("nrr-runs-for").value);
  const oversFor = parseFloat(document.getElementById("nrr-overs-for").value);
  const runsAgainst = parseFloat(document.getElementById("nrr-runs-against").value);
  const oversAgainst = parseFloat(document.getElementById("nrr-overs-against").value);

  if ([runsFor, oversFor, runsAgainst, oversAgainst].some((v) => isNaN(v) || v < 0) || oversFor === 0 || oversAgainst === 0) {
    el.nrrResult.textContent = "—";
    return;
  }

  const nrr = runsFor / oversFor - runsAgainst / oversAgainst;
  const sign = nrr > 0 ? "+" : "";
  el.nrrResult.textContent = `${sign}${nrr.toFixed(3)}`;
}

// ---------------------------------------------------------------------- resume on load
// Deliberately last in the file: resumeSession() calls addBotMessage(), which reads the
// module-level `const EMAIL_PATTERN` — a `const` isn't usable before its own declaration line
// runs (unlike a `function`, which is fully hoisted), so calling this any earlier throws
// "Cannot access 'EMAIL_PATTERN' before initialization" for any returning visitor whose browser
// already has a saved session. That exception, happening during top-level script evaluation,
// used to abort the rest of this file — silently skipping every addEventListener below it
// (Change League, NRR, the chat form). Confirmed via a real bug report: league buttons, NRR, and
// chat all appeared completely unresponsive for a returning user, working fine for a fresh one.
const saved = loadSavedSession();
if (saved) resumeSession(saved.sessionId, saved.league);
