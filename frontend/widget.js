// ARCL chat widget loader — the single script tag arcl.org's own pages include to get a floating
// chat launcher + popup window, without touching the host page's markup, CSS, or JS at all.
//
// Usage (paste into arcl.org's page template, once, anywhere):
//   <script src="https://<wherever-this-is-hosted>/widget.js" defer></script>
// Optional attributes: data-src (chat page URL, default index.html next to this file),
// data-title, data-label (button text), data-position="bottom-left", data-color, data-color-dark.
//
// Design choices, and why:
// - The actual chat UI (index.html/app.js/styles.css) runs inside an <iframe>, not injected
//   directly into the host page's DOM. That's deliberate: an iframe is its own document with its
//   own CSS scope, so nothing ARCL's site already has (resets, utility classes, id collisions,
//   third-party scripts) can ever clash with the chat UI's styles or vice versa — the same
//   approach every major embeddable chat widget (Intercom, Drift, Crisp, etc.) uses.
// - Only the launcher, greeting bubble and panel "chrome" are injected into the host page, and
//   every class name is prefixed `arcl-cw-` so a collision with the host page's CSS is very unlikely.
// - Discoverability: a round icon alone reads as decoration on a busy page like arcl.org's, so the
//   launcher is a labelled pill with a speech-bubble icon (the convention visitors already know),
//   it pulses briefly on load, and a one-time greeting says what the bot is for. The greeting
//   shows once per browser session and never again once the visitor dismisses it or opens chat.
// - No dependencies, no build step — a single vanilla-JS file that works on any page.

(function () {
  "use strict";

  var thisScript = document.currentScript;
  if (!thisScript) return; // extremely old browser or dynamically-injected without currentScript support — nothing safe to do

  var cfg = {
    // Defaults to "index.html" next to wherever widget.js itself was loaded from, so the common
    // case (both files hosted together) needs no configuration at all.
    src: thisScript.dataset.src || new URL("index.html", thisScript.src).href,
    title: thisScript.dataset.title || "ARCL Rules Assistant",
    label: thisScript.dataset.label || "Ask ARCL Chatbot",
    position: thisScript.dataset.position === "bottom-left" ? "bottom-left" : "bottom-right",
    color: thisScript.dataset.color || "#1f6f3d",
    colorDark: thisScript.dataset.colorDark || "#164f2b",
  };
  var side = cfg.position === "bottom-left" ? "left" : "right";
  var GREETING_DELAY_MS = 3000;

  var STYLE = "\
    .arcl-cw-launcher, .arcl-cw-greeting, .arcl-cw-panel {\
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;\
      box-sizing: border-box; z-index: 2147483000;\
    }\
    .arcl-cw-launcher {\
      position: fixed; bottom: 20px; " + side + ": 20px; height: 52px; padding: 0 20px 0 16px;\
      display: flex; align-items: center; gap: 9px; border: none; border-radius: 26px; cursor: pointer;\
      background: linear-gradient(160deg, " + cfg.color + " 0%, " + cfg.colorDark + " 100%); color: #fff;\
      font-size: 15px; font-weight: 600; letter-spacing: 0.01em; line-height: 1; white-space: nowrap;\
      box-shadow: 0 6px 18px rgba(0,0,0,0.25); transition: transform 0.15s ease, box-shadow 0.15s ease, padding 0.15s ease;\
    }\
    .arcl-cw-launcher:hover { transform: translateY(-2px); box-shadow: 0 10px 24px rgba(0,0,0,0.3); }\
    .arcl-cw-launcher:active { transform: translateY(0); }\
    .arcl-cw-launcher:focus-visible, .arcl-cw-greeting button:focus-visible, .arcl-cw-close:focus-visible {\
      outline: 3px solid #f2b631; outline-offset: 3px;\
    }\
    .arcl-cw-launcher svg { width: 22px; height: 22px; flex: none; }\
    .arcl-cw-launcher::after {\
      content: ''; position: absolute; inset: 0; border-radius: inherit; pointer-events: none;\
      box-shadow: 0 0 0 0 " + cfg.color + "; animation: arcl-cw-pulse 1.8s ease-out 1s 3;\
    }\
    @keyframes arcl-cw-pulse {\
      0% { box-shadow: 0 0 0 0 rgba(31,111,61,0.55); }\
      100% { box-shadow: 0 0 0 16px rgba(31,111,61,0); }\
    }\
    .arcl-cw-launcher.arcl-cw-is-open { padding: 0; width: 52px; justify-content: center; }\
    .arcl-cw-launcher.arcl-cw-is-open .arcl-cw-text { display: none; }\
    .arcl-cw-greeting {\
      position: fixed; bottom: 86px; " + side + ": 20px; width: 270px; padding: 14px 34px 14px 16px;\
      background: #fff; color: #1d2b22; border-radius: 14px; border: 1px solid rgba(0,0,0,0.06);\
      box-shadow: 0 10px 30px rgba(0,0,0,0.2); font-size: 14px; line-height: 1.45; cursor: pointer;\
      opacity: 0; transform: translateY(8px); pointer-events: none; transition: opacity 0.2s ease, transform 0.2s ease;\
    }\
    .arcl-cw-greeting.arcl-cw-show { opacity: 1; transform: none; pointer-events: auto; }\
    .arcl-cw-greeting::after {\
      content: ''; position: absolute; bottom: -7px; " + side + ": 30px; width: 14px; height: 14px;\
      background: #fff; transform: rotate(45deg); border-right: 1px solid rgba(0,0,0,0.06);\
      border-bottom: 1px solid rgba(0,0,0,0.06);\
    }\
    .arcl-cw-greeting strong { display: block; font-size: 15px; margin-bottom: 3px; color: " + cfg.colorDark + "; }\
    .arcl-cw-greeting-close {\
      position: absolute; top: 8px; right: 8px; width: 24px; height: 24px; padding: 0; border: none;\
      border-radius: 50%; background: transparent; color: #6b7a70; font-size: 14px; line-height: 1; cursor: pointer;\
    }\
    .arcl-cw-greeting-close:hover { background: #eef2ef; color: #1d2b22; }\
    .arcl-cw-panel {\
      position: fixed; bottom: 86px; " + side + ": 20px;\
      width: 380px; height: min(640px, calc(100vh - 110px)); max-height: 640px;\
      background: #fff; border-radius: 16px; overflow: hidden;\
      box-shadow: 0 12px 40px rgba(0,0,0,0.28);\
      display: flex; flex-direction: column;\
      opacity: 0; transform: translateY(16px) scale(0.98); pointer-events: none;\
      transition: opacity 0.18s ease, transform 0.18s ease;\
    }\
    .arcl-cw-panel.arcl-cw-open { opacity: 1; transform: none; pointer-events: auto; }\
    .arcl-cw-header {\
      flex: none; display: flex; align-items: center; justify-content: space-between; gap: 10px;\
      padding: 12px 14px; color: #fff;\
      background: linear-gradient(165deg, " + cfg.color + " 0%, " + cfg.colorDark + " 100%);\
    }\
    .arcl-cw-title { font-weight: 700; font-size: 15px; }\
    .arcl-cw-subtitle { font-size: 12px; opacity: 0.85; margin-top: 2px; }\
    .arcl-cw-close {\
      flex: none; background: rgba(255,255,255,0.15); border: none; color: #fff; width: 28px; height: 28px;\
      border-radius: 50%; cursor: pointer; font-size: 15px; line-height: 1; padding: 0;\
      display: flex; align-items: center; justify-content: center; transition: background 0.15s;\
    }\
    .arcl-cw-close:hover { background: rgba(255,255,255,0.3); }\
    .arcl-cw-iframe { flex: 1; border: none; width: 100%; }\
    @media (max-width: 480px) {\
      .arcl-cw-panel {\
        top: 0; left: 0; right: 0; bottom: 0; width: 100%; height: 100%; max-height: none;\
        border-radius: 0; transform: none;\
      }\
      .arcl-cw-launcher { bottom: 16px; " + side + ": 16px; font-size: 14px; }\
      .arcl-cw-greeting { bottom: 80px; " + side + ": 16px; width: calc(100vw - 32px); max-width: 300px; }\
      .arcl-cw-launcher.arcl-cw-is-open { display: none; }\
    }\
    @media (prefers-reduced-motion: reduce) {\
      .arcl-cw-launcher::after { animation: none; }\
      .arcl-cw-launcher, .arcl-cw-greeting, .arcl-cw-panel { transition: none; }\
    }\
  ";

  // Speech-bubble icon — the shape visitors already associate with "chat here".
  var CHAT_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
    'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' +
    '<path d="M21 12a8 8 0 0 1-11.6 7.1L4 20.5l1.4-4.9A8 8 0 1 1 21 12z"/>' +
    '<path d="M8.5 11h.01M12 11h.01M15.5 11h.01" stroke-width="2.6"/></svg>';
  var CLOSE_ICON = '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" ' +
    'stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18"/></svg>';

  // Storage can throw (privacy modes, blocked third-party storage) — the greeting just shows/hides
  // as if nothing was stored.
  function store(kind, key, value) {
    try {
      var s = kind === "local" ? window.localStorage : window.sessionStorage;
      if (value === undefined) return s.getItem(key);
      s.setItem(key, value);
    } catch (e) { return null; }
  }

  var styleEl = document.createElement("style");
  styleEl.textContent = STYLE;
  document.head.appendChild(styleEl);

  // ---------------------------------------------------------------- launcher
  var launcher = document.createElement("button");
  launcher.type = "button";
  launcher.className = "arcl-cw-launcher";
  launcher.setAttribute("aria-expanded", "false");
  launcher.setAttribute("aria-haspopup", "dialog");

  // ---------------------------------------------------------------- greeting bubble
  var greeting = document.createElement("div");
  greeting.className = "arcl-cw-greeting";
  greeting.setAttribute("role", "status");
  var greetingTitle = document.createElement("strong");
  greetingTitle.textContent = "Hi there 👋";
  var greetingText = document.createElement("span");
  greetingText.textContent = "Got a question about ARCL rules, registration or scorecards? Ask me — I answer from the official rulebook.";
  var greetingClose = document.createElement("button");
  greetingClose.type = "button";
  greetingClose.className = "arcl-cw-greeting-close";
  greetingClose.setAttribute("aria-label", "Dismiss");
  greetingClose.textContent = "✕";
  greeting.appendChild(greetingTitle);
  greeting.appendChild(greetingText);
  greeting.appendChild(greetingClose);

  // ---------------------------------------------------------------- panel
  var panel = document.createElement("div");
  panel.className = "arcl-cw-panel";
  panel.setAttribute("role", "dialog");
  panel.setAttribute("aria-label", cfg.title);

  var header = document.createElement("div");
  header.className = "arcl-cw-header";
  var heading = document.createElement("div");
  var titleEl = document.createElement("div");
  titleEl.className = "arcl-cw-title";
  titleEl.textContent = cfg.title;
  var subtitleEl = document.createElement("div");
  subtitleEl.className = "arcl-cw-subtitle";
  subtitleEl.textContent = "Instant answers from the official ARCL rules";
  heading.appendChild(titleEl);
  heading.appendChild(subtitleEl);
  var closeBtn = document.createElement("button");
  closeBtn.type = "button";
  closeBtn.className = "arcl-cw-close";
  closeBtn.setAttribute("aria-label", "Close chat");
  closeBtn.textContent = "✕";
  header.appendChild(heading);
  header.appendChild(closeBtn);

  var iframe = document.createElement("iframe");
  iframe.className = "arcl-cw-iframe";
  iframe.title = cfg.title;
  // Loaded lazily (only once the panel is first opened) so a visitor who never opens the widget
  // never pays for the chat UI's own load/session-init cost at all.
  var iframeLoaded = false;

  panel.appendChild(header);
  panel.appendChild(iframe);

  // ---------------------------------------------------------------- behaviour
  function renderLauncher(isOpen) {
    launcher.innerHTML = isOpen ? CLOSE_ICON : CHAT_ICON + '<span class="arcl-cw-text"></span>';
    if (!isOpen) launcher.querySelector(".arcl-cw-text").textContent = cfg.label;
    launcher.classList.toggle("arcl-cw-is-open", isOpen);
    launcher.setAttribute("aria-label", isOpen ? "Close " + cfg.title : "Open " + cfg.title);
    launcher.setAttribute("aria-expanded", String(isOpen));
  }

  function hideGreeting(forever) {
    greeting.classList.remove("arcl-cw-show");
    if (forever) store("local", "arcl-cw-greeting-dismissed", "1");
  }

  var open = false;
  function setOpen(next) {
    open = next;
    if (open) hideGreeting(true); // they've found it — no need to point it out again
    if (open && !iframeLoaded) {
      // ?embed=1 tells the chat page (see app.js) to hide its own header/title bar — without it,
      // the popup shows two stacked "ARCL Rules Assistant" bars, this shell's and the page's own.
      iframe.src = cfg.src + (cfg.src.indexOf("?") === -1 ? "?" : "&") + "embed=1";
      iframeLoaded = true;
    }
    panel.classList.toggle("arcl-cw-open", open);
    renderLauncher(open);
    if (!open) launcher.focus();
  }

  launcher.addEventListener("click", function () { setOpen(!open); });
  closeBtn.addEventListener("click", function () { setOpen(false); });
  greeting.addEventListener("click", function () { setOpen(true); });
  greetingClose.addEventListener("click", function (e) { e.stopPropagation(); hideGreeting(true); });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && open) setOpen(false);
  });

  renderLauncher(false);

  // Injected once the document body exists — widget.js is expected to load with `defer`, by
  // which point document.body is always available.
  function mount() {
    document.body.appendChild(panel);
    document.body.appendChild(greeting);
    document.body.appendChild(launcher);

    if (!store("local", "arcl-cw-greeting-dismissed") && !store("session", "arcl-cw-greeting-shown")) {
      setTimeout(function () {
        if (open) return;
        greeting.classList.add("arcl-cw-show");
        store("session", "arcl-cw-greeting-shown", "1");
      }, GREETING_DELAY_MS);
    }
  }
  if (document.body) mount();
  else document.addEventListener("DOMContentLoaded", mount);
})();
