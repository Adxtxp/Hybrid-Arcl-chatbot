// ARCL Rules Assistant — chat frontend.
//
// Talks to Arcl.Api. Assumes same-origin deployment (this file served from the API's wwwroot);
// change API_BASE below if the frontend is hosted separately.
//
// state.sessionId/state.league are mirrored to localStorage so a page refresh resumes the same
// session instead of dropping back to the league picker. This only restores which session/league
// is active — the visible message history above it is not persisted (session state and the
// conversation memory both live server-side, so follow-up questions still work after a refresh;
// only the chat bubbles on screen reset).
//
// Answers stream in over Server-Sent Events from POST /api/chat/stream (event types documented on
// ChatEvent in api/Services/ChatOrchestrator.cs). Every bot reply has a `kind` that decides how it
// looks — answer / low_confidence / out_of_scope / error / handoff / decision / team_ranking — so
// "the rulebook doesn't cover this", "that's not an ARCL question" and "something broke on our
// side" are visibly different things, not the same orange bubble with different words.

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
  league: null,          // "Mens" | "Womens" | "Youth"
  busy: false,           // a reply is in flight — blocks double-sends
  lastQuestion: "",      // prefills the contact form
  lastRelated: [],       // sections from the latest reply, offered before contacting an admin
  config: { supportContact: "", handoffConsentText: "" },
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
  announcer: document.getElementById("sr-announcer"),
  decisionChips: document.getElementById("decision-chips"),
  chatForm: document.getElementById("chat-form"),
  chatInput: document.getElementById("chat-input"),
  sendBtn: document.getElementById("send-btn"),
  nrrModal: document.getElementById("nrr-modal"),
  nrrResult: document.getElementById("nrr-result"),
  contactModal: document.getElementById("contact-modal"),
  contactForm: document.getElementById("contact-form"),
  contactName: document.getElementById("contact-name"),
  contactEmail: document.getElementById("contact-email"),
  contactMessage: document.getElementById("contact-message"),
  contactConsent: document.getElementById("contact-consent"),
  contactConsentText: document.getElementById("contact-consent-text"),
  contactError: document.getElementById("contact-error"),
  contactRelated: document.getElementById("contact-related"),
  contactRelatedLinks: document.getElementById("contact-related-links"),
  contactSubmit: document.getElementById("contact-submit-btn"),
};

// Non-secret settings (support address, consent wording). Best-effort: the form still works with
// the generic consent line in index.html if this fails.
fetch(`${API_BASE}/api/config/public`)
  .then((r) => (r.ok ? r.json() : null))
  .then((cfg) => {
    if (!cfg) return;
    state.config = cfg;
    el.contactConsentText.textContent = cfg.handoffConsentText;
  })
  .catch(() => {});

// ---------------------------------------------------------------------- screen reader announcements

function announce(text) {
  // Clear first so announcing the same text twice in a row still fires.
  el.announcer.textContent = "";
  setTimeout(() => { el.announcer.textContent = text; }, 50);
}

// ---------------------------------------------------------------------- gatekeeper

document.querySelectorAll(".league-btn").forEach((btn) => {
  btn.addEventListener("click", () => startSession(btn.dataset.league));
});

async function startSession(league) {
  const resp = await fetch(`${API_BASE}/api/session`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ league }),
  }).catch(() => null);
  if (!resp || !resp.ok) {
    el.gatekeeper.querySelector("p").textContent = "Couldn't start a session — please check your connection and try again.";
    return;
  }
  const data = await resp.json();
  state.sessionId = data.sessionId;
  state.league = league;
  saveSession(data.sessionId, league);

  showApp(league);
  addBotMessage(`You're asking about the ${leagueLabel(league)} league. What's your question?`);
  renderSuggestedQuestions();
}

function resumeSession(sessionId, league) {
  state.sessionId = sessionId;
  state.league = league;
  showApp(league);
  addBotMessage(`Welcome back — continuing your ${leagueLabel(league)} league conversation.`);
  renderSuggestedQuestions();
}

function showApp(league) {
  el.leagueBadge.textContent = leagueLabel(league);
  el.gatekeeper.classList.add("hidden");
  el.app.classList.remove("hidden");
  el.messages.innerHTML = "";
  el.chatInput.focus();
}

// ---------------------------------------------------------------------- suggested questions
// Shown once, right after the greeting. Fetched per league from GET /api/suggestions — a seasonal
// question, what this league's visitors have been asking about lately, and a hand-picked set (see
// api/Services/SuggestionService.cs). FALLBACK_SUGGESTIONS only covers the API being unreachable.
// Removed as soon as the visitor sends any message of their own, same as decision chips.

const FALLBACK_SUGGESTIONS = [
  "What happens if a match is stopped early due to rain?",
  "How do I upload my team's scorecard?",
  "What are the rules for a no-ball?",
  "What's my team's current ranking?",
];

async function renderSuggestedQuestions() {
  let questions = FALLBACK_SUGGESTIONS;
  try {
    const resp = await fetch(`${API_BASE}/api/suggestions?sessionId=${encodeURIComponent(state.sessionId)}`);
    if (resp.ok) {
      const data = await resp.json();
      if (data.questions && data.questions.length > 0) questions = data.questions;
    }
  } catch { /* keep the fallback list */ }

  const wrap = document.createElement("div");
  wrap.className = "bubble bot suggested-questions";
  wrap.id = "suggested-questions";

  const label = document.createElement("div");
  label.className = "suggested-questions-label";
  label.textContent = "Not sure what to ask? Try one of these:";
  wrap.appendChild(label);

  wrap.appendChild(buildChipList(questions, "suggested-questions-list"));
  el.messages.appendChild(wrap);
  scrollToBottom();
}

function clearSuggestedQuestions() {
  document.getElementById("suggested-questions")?.remove();
}

function buildChipList(questions, className) {
  const list = document.createElement("div");
  list.className = className;
  for (const question of questions) {
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
  return list;
}

function leagueLabel(league) {
  return { Mens: "Men's", Womens: "Women's", Youth: "Youth" }[league] ?? league;
}

// ---------------------------------------------------------------------- escape hatches

document.getElementById("change-league-btn").addEventListener("click", () => {
  clearSavedSession(); // so a refresh while the picker is up doesn't resurrect the abandoned session
  el.app.classList.add("hidden");
  el.gatekeeper.classList.remove("hidden");
});

document.getElementById("human-support-btn").addEventListener("click", () => openContactForm());

// ---------------------------------------------------------------------- contact-admin form

function openContactForm() {
  if (!state.sessionId) return;
  el.contactError.classList.add("hidden");
  el.contactMessage.value = state.lastQuestion;

  // Related-sections step: whatever the last reply turned up, offered before sending this off.
  el.contactRelatedLinks.innerHTML = "";
  if (state.lastRelated.length > 0) {
    el.contactRelatedLinks.appendChild(buildRelatedLinks(state.lastRelated));
    el.contactRelated.classList.remove("hidden");
  } else {
    el.contactRelated.classList.add("hidden");
  }

  el.contactModal.classList.remove("hidden");
  (el.contactEmail.value ? el.contactMessage : el.contactEmail).focus();
}

function closeContactForm() {
  el.contactModal.classList.add("hidden");
  document.getElementById("human-support-btn").focus();
}

document.getElementById("contact-cancel-btn").addEventListener("click", closeContactForm);
el.contactModal.addEventListener("keydown", (e) => { if (e.key === "Escape") closeContactForm(); });

el.contactForm.addEventListener("submit", async (e) => {
  e.preventDefault();
  const showError = (msg) => {
    el.contactError.textContent = msg;
    el.contactError.classList.remove("hidden");
  };

  const email = el.contactEmail.value.trim();
  const message = el.contactMessage.value.trim();
  if (!email || !el.contactEmail.checkValidity()) return showError("Please enter a valid email address so an admin can reply.");
  if (!message) return showError("Please tell us what you need help with.");
  if (!el.contactConsent.checked) return showError("Please tick the consent box so we can use your details to reply.");

  el.contactSubmit.disabled = true;
  try {
    const resp = await fetch(`${API_BASE}/api/human-handoff`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        sessionId: state.sessionId,
        message,
        name: el.contactName.value.trim() || null,
        email,
        consentGiven: true,
      }),
    });
    const data = await resp.json().catch(() => ({}));
    if (!resp.ok) return showError(data.error ?? "Couldn't send that — please try again.");

    closeContactForm();
    el.contactConsent.checked = false;
    addUserMessage("[Contacted a league admin]");
    addBotMessage(data.message, { kind: "handoff_sent", logId: data.logId });
  } catch {
    showError("Couldn't reach the server — please check your connection and try again.");
  } finally {
    el.contactSubmit.disabled = false;
  }
});

// ---------------------------------------------------------------------- chat

el.chatForm.addEventListener("submit", (e) => {
  e.preventDefault();
  const text = el.chatInput.value.trim();
  if (!text || !state.sessionId || state.busy) return;
  el.chatInput.value = "";
  sendMessage(text);
});

function setBusy(busy) {
  state.busy = busy;
  el.sendBtn.disabled = busy;
}

/**
 * Sends one question and streams the reply into a new bot bubble.
 * options.regenerate: ask for a fresh answer to the same question (↻) — the old bubble
 * (options.replaces) is swapped out for the new one in place.
 */
async function sendMessage(text, { regenerate = false, replaces = null } = {}) {
  if (!state.sessionId || state.busy) return;
  setBusy(true);
  state.lastQuestion = text;

  if (!regenerate) addUserMessage(text);
  clearDecisionChips();
  clearSuggestedQuestions();

  const typing = addTypingIndicator(replaces);
  replaces?.remove();
  announce("Assistant is typing…");

  let bubble = null;   // the answer bubble, created on the first streamed text
  let raw = "";        // answer text so far, inline markers and all
  let sources = {};    // sectionNumber -> citation, from the "sources" event
  let final = null;

  const ensureBubble = () => {
    if (bubble) return bubble;
    bubble = createBotBubble("answer");
    bubble.setAttribute("aria-busy", "true");
    bubble.appendChild(document.createElement("div")).className = "bubble-text";
    typing.replaceWith(bubble);
    return bubble;
  };

  try {
    const resp = await fetch(`${API_BASE}/api/chat/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: state.sessionId, message: text, regenerate }),
    });

    if (!resp.ok) {
      typing.remove();
      const err = await resp.json().catch(() => ({}));
      const msg = resp.status === 429
        ? "You're sending messages faster than I can keep up with — please wait a minute and try again."
        : err.error ?? "Something went wrong. Please try again.";
      addBotMessage(msg, { kind: "error", retry: resp.status === 400 ? null : () => sendMessage(text, { regenerate: true }) });
      return;
    }

    for await (const { event, data } of readSse(resp)) {
      if (event === "sources") {
        sources = Object.fromEntries(data.map((c) => [c.sectionNumber, c]));
      } else if (event === "delta") {
        raw += data.text;
        renderAnswerText(ensureBubble().querySelector(".bubble-text"), raw, sources, { streaming: true });
        scrollToBottom();
      } else if (event === "replace") {
        raw = data.text;
        renderAnswerText(ensureBubble().querySelector(".bubble-text"), raw, sources);
      } else if (event === "done") {
        final = data;
      }
    }
  } catch {
    // network drop mid-stream, or fetch itself failed
  } finally {
    setBusy(false);
  }

  if (!final) {
    typing.remove();
    bubble?.remove();
    addBotMessage("Couldn't reach the server — the answer didn't come through. Please try again.",
      { kind: "error", retry: () => sendMessage(text, { regenerate: true }) });
    return;
  }

  if (final.kind === "answer" && bubble) {
    // Streamed answer: finish the bubble that's already on screen.
    renderAnswerText(bubble.querySelector(".bubble-text"), final.message, sources);
    bubble.removeAttribute("aria-busy");
    decorateAnswer(bubble, final, text);
    announce(plainText(final.message));
  } else {
    // Non-answer kinds (and anything that never streamed) arrive whole in "done".
    typing.remove();
    bubble?.remove();
    handleChatResponse(final, text);
  }
}

// Minimal SSE parser over a fetch() body — EventSource can't POST. Yields { event, data }.
async function* readSse(resp) {
  const reader = resp.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let sep;
    while ((sep = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, sep);
      buffer = buffer.slice(sep + 2);
      let event = "message";
      const dataLines = [];
      for (const line of block.split("\n")) {
        if (line.startsWith("event:")) event = line.slice(6).trim();
        else if (line.startsWith("data:")) dataLines.push(line.slice(5).trimStart());
      }
      if (dataLines.length > 0) yield { event, data: JSON.parse(dataLines.join("\n")) };
    }
  }
}

function handleChatResponse(data, question) {
  const related = data.relatedLinks?.length ? data.relatedLinks : data.citations ?? [];
  if (related.length) state.lastRelated = related;

  const bubble = addBotMessage(data.message, {
    kind: data.kind ?? (data.routePath === "human_handoff" ? "handoff" : "answer"),
    logId: data.logId,
    citations: data.citations,
    relatedLinks: data.relatedLinks,
    retry: data.kind === "error" && question ? () => sendMessage(question, { regenerate: true }) : null,
  });
  if (data.kind === "answer") decorateAnswer(bubble, data, question);

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
  el.decisionChips.querySelector("button")?.focus();
}

function clearDecisionChips() {
  el.decisionChips.innerHTML = "";
  el.decisionChips.classList.add("hidden");
}

async function answerDecision(label) {
  addUserMessage(label);
  clearDecisionChips();

  const typing = addTypingIndicator();
  try {
    const resp = await fetch(`${API_BASE}/api/chat/decision-answer`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sessionId: state.sessionId, selectedOptionLabel: label }),
    });
    typing.remove();

    if (!resp.ok) {
      const err = await resp.json().catch(() => ({}));
      addBotMessage(err.error ?? "Something went wrong with that step.", { kind: "error", retry: () => answerDecision(label) });
      return;
    }
    handleChatResponse(await resp.json());
  } catch {
    typing.remove();
    addBotMessage("Couldn't reach the server. Please try again.", { kind: "error", retry: () => answerDecision(label) });
  }
}

// ---------------------------------------------------------------------- message rendering

function scrollToBottom() {
  el.messages.scrollTop = el.messages.scrollHeight;
}

function addUserMessage(text) {
  const bubble = document.createElement("div");
  bubble.className = "bubble user";
  bubble.textContent = text;
  el.messages.appendChild(bubble);
  scrollToBottom();
  return bubble;
}

function addTypingIndicator(before = null) {
  const bubble = document.createElement("div");
  bubble.className = "bubble bot typing";
  bubble.setAttribute("role", "status");
  bubble.setAttribute("aria-label", "Assistant is typing");
  for (let i = 0; i < 3; i++) bubble.appendChild(document.createElement("span")).className = "dot";
  if (before) el.messages.insertBefore(bubble, before);
  else el.messages.appendChild(bubble);
  scrollToBottom();
  return bubble;
}

// Short label shown above non-answer replies, so the state reads at a glance (not only by color).
const KIND_LABELS = {
  low_confidence: "No confident answer",
  out_of_scope: "Outside what I can help with",
  error: "Something went wrong",
  handoff: "Needs a league admin",
  handoff_sent: "Request sent",
};

function createBotBubble(kind) {
  const bubble = document.createElement("div");
  bubble.className = `bubble bot kind-${kind}`;
  if (KIND_LABELS[kind]) {
    const label = document.createElement("div");
    label.className = "kind-label";
    label.textContent = KIND_LABELS[kind];
    bubble.appendChild(label);
  }
  return bubble;
}

function addBotMessage(text, { kind = "info", logId = null, citations = [], relatedLinks = [], retry = null } = {}) {
  const bubble = createBotBubble(kind);
  const body = bubble.appendChild(document.createElement("div"));
  body.className = "bubble-text";
  renderAnswerText(body, text, Object.fromEntries((citations ?? []).map((c) => [c.sectionNumber, c])));

  if (citations && citations.length > 0 && kind !== "answer") bubble.appendChild(buildCitations(citations));

  if (relatedLinks && relatedLinks.length > 0) {
    const label = document.createElement("div");
    label.className = "related-label";
    label.textContent = "Related sections:";
    bubble.appendChild(label);
    bubble.appendChild(buildRelatedLinks(relatedLinks));
  }

  if (kind === "low_confidence" || kind === "handoff") {
    bubble.appendChild(buildActionButton("Contact a league admin", () => openContactForm()));
  }
  if (kind === "error" && retry) {
    bubble.appendChild(buildActionButton("↻ Try again", () => { bubble.remove(); retry(); }));
  }

  if (logId) bubble.appendChild(buildFeedbackRow(logId));

  el.messages.appendChild(bubble);
  scrollToBottom();
  if (kind !== "info") announce(plainText(text));
  return bubble;
}

// Answer-only extras: medium-confidence caveat, bottom citation list, related follow-ups,
// feedback, and the regenerate/rephrase actions.
function decorateAnswer(bubble, data, question) {
  state.lastRelated = data.citations ?? [];

  if (data.confidence === "medium") {
    const caveat = document.createElement("div");
    caveat.className = "caveat";
    caveat.textContent = "I'm not fully sure about this one — please check the cited sections, or confirm with a league admin.";
    bubble.insertBefore(caveat, bubble.firstChild);
  }

  if (data.citations && data.citations.length > 0) bubble.appendChild(buildCitations(data.citations));

  if (data.followUps && data.followUps.length > 0) {
    const label = document.createElement("div");
    label.className = "related-label";
    label.textContent = "You might also want to know:";
    bubble.appendChild(label);
    bubble.appendChild(buildChipList(data.followUps, "follow-up-list"));
  }

  const actions = document.createElement("div");
  actions.className = "answer-actions";
  if (data.logId) actions.appendChild(buildFeedbackRow(data.logId));
  if (question) {
    const regen = document.createElement("button");
    regen.type = "button";
    regen.className = "text-btn";
    regen.textContent = "↻ Regenerate";
    regen.title = "Get a fresh answer to the same question";
    regen.addEventListener("click", () => sendMessage(question, { regenerate: true, replaces: bubble }));

    const rephrase = document.createElement("button");
    rephrase.type = "button";
    rephrase.className = "text-btn";
    rephrase.textContent = "✎ Rephrase";
    rephrase.title = "Edit your question and ask again";
    rephrase.addEventListener("click", () => {
      el.chatInput.value = question;
      el.chatInput.focus();
      el.chatInput.select();
    });

    actions.append(regen, rephrase);
  }
  bubble.appendChild(actions);
}

// Matches one email address inside otherwise-plain message text (e.g. the support contact in
// handoff/low-confidence messages) so it can be rendered as a real mailto: link.
// Domain parts are matched label by label so a sentence's closing "." isn't swallowed into the link.
const EMAIL_PATTERN = /[\w.+-]+@[\w-]+(?:\.[\w-]+)+/g;

// Inline citation markers — "[§4]", "[§Appendix B]", "[§4, 39]". The server rewrites everything
// to that form once the answer is complete, but while it's still streaming the model's raw "[4]"
// (no §) shows up too, so both are matched here; a bare bracket only becomes a link if its
// content is one of this answer's sources, otherwise it's left as ordinary text.
const CITATION_MARKER = /\[(§)?\s*([^\[\]\n]{1,80})\]/g;

function escapeHtml(s) {
  return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

/**
 * Renders message text into `target`: HTML-escaped first, then the only markup ever introduced
 * is built from that escaped text — mailto: links for emails, and per-claim citation links for
 * [§n] markers (pointing at the section's source page, labeled with its section number).
 * While streaming, a half-arrived marker at the very end ("[§Appen") is hidden until it closes.
 */
function renderAnswerText(target, text, sources, { streaming = false } = {}) {
  const t = streaming ? text.replace(/\[[^\]\n]*$/, "") : text;
  let html = escapeHtml(t);
  html = html.replace(EMAIL_PATTERN, (email) => `<a href="mailto:${email}">${email}</a>`);
  html = html.replace(CITATION_MARKER, (whole, sectionSign, inner) => {
    const links = inner.split(/[,;]/).map((part) => {
      const num = part.trim().replace(/^(§|section|sec\.?)\s*/i, "").split(":")[0].trim();
      const src = sources?.[num];
      if (!src) return "";
      // escapeHtml'd values only; sourceUrl comes from our own rule_chunks table.
      return `<a class="cite" href="${escapeHtml(src.sourceUrl)}" target="_blank" rel="noopener" ` +
             `title="Section ${escapeHtml(src.sectionNumber)}: ${escapeHtml(src.sectionTitle)}">§${escapeHtml(src.sectionNumber)}</a>`;
    }).join("");
    return links || (sectionSign ? "" : whole);
  });
  target.innerHTML = html;
}

function plainText(text) {
  return text.replace(/\[§[^\]]*\]/g, "").replace(/\s+/g, " ").replace(/ ([.,;:!?])/g, "$1").trim();
}

function buildCitations(citations) {
  const cite = document.createElement("div");
  cite.className = "citations";
  cite.appendChild(document.createTextNode("Sources: "));
  for (const c of citations) {
    const a = document.createElement("a");
    a.href = c.sourceUrl;
    a.target = "_blank";
    a.rel = "noopener";
    a.textContent = `Section ${c.sectionNumber}: ${c.sectionTitle}`;
    cite.appendChild(a);
  }
  return cite;
}

function buildRelatedLinks(links) {
  const list = document.createElement("ul");
  list.className = "related-links";
  for (const c of links) {
    const li = list.appendChild(document.createElement("li"));
    const a = li.appendChild(document.createElement("a"));
    a.href = c.sourceUrl;
    a.target = "_blank";
    a.rel = "noopener";
    a.textContent = `Section ${c.sectionNumber}: ${c.sectionTitle}`;
  }
  return list;
}

function buildActionButton(label, onClick) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "bubble-action";
  btn.textContent = label;
  btn.addEventListener("click", onClick);
  return btn;
}

function buildFeedbackRow(logId) {
  const row = document.createElement("div");
  row.className = "feedback-row";

  const up = document.createElement("button");
  up.textContent = "👍";
  up.setAttribute("aria-label", "This answer was helpful");
  const down = document.createElement("button");
  down.textContent = "👎";
  down.setAttribute("aria-label", "This answer was not helpful");

  const send = async (thumbsUp, clickedBtn, otherBtn) => {
    clickedBtn.classList.add("selected");
    clickedBtn.setAttribute("aria-pressed", "true");
    otherBtn.disabled = true;
    clickedBtn.disabled = true;
    await fetch(`${API_BASE}/api/feedback`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ logId, thumbsUp }),
    }).catch(() => {}); // best-effort — feedback failing shouldn't interrupt the chat
    announce("Thanks for the feedback.");
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
  document.getElementById("nrr-runs-for").focus();
});
document.getElementById("nrr-close-btn").addEventListener("click", () => {
  el.nrrModal.classList.add("hidden");
});
el.nrrModal.addEventListener("keydown", (e) => { if (e.key === "Escape") el.nrrModal.classList.add("hidden"); });

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
// Deliberately last in the file: resumeSession() calls addBotMessage(), which reads module-level
// `const`s (EMAIL_PATTERN, CITATION_MARKER, KIND_LABELS) — a `const` isn't usable before its own
// declaration line runs (unlike a `function`, which is fully hoisted), so calling this any
// earlier throws "Cannot access ... before initialization" for any returning visitor whose
// browser already has a saved session. That exception, happening during top-level script
// evaluation, used to abort the rest of this file — silently skipping every addEventListener
// below it. Confirmed via a real bug report: league buttons, NRR, and chat all appeared
// completely unresponsive for a returning user, working fine for a fresh one.
const saved = loadSavedSession();
if (saved) resumeSession(saved.sessionId, saved.league);
