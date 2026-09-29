// ARCL chat widget loader — the single script tag arcl.org's own pages include to get a floating
// chat launcher + popup window, without touching the host page's markup, CSS, or JS at all.
//
// Usage (paste into arcl.org's page template, once, anywhere):
//   <script src="https://<wherever-this-is-hosted>/widget.js" data-src="https://<chat-host>/index.html" defer></script>
//
// Design choices, and why:
// - The actual chat UI (index.html/app.js/styles.css) runs inside an <iframe>, not injected
//   directly into the host page's DOM. That's deliberate: an iframe is its own document with its
//   own CSS scope, so nothing ARCL's site already has (resets, utility classes, id collisions,
//   third-party scripts) can ever clash with the chat UI's styles or vice versa — the same
//   approach every major embeddable chat widget (Intercom, Drift, Crisp, etc.) uses. It also means
//   the chat UI needs *zero* changes to work as a widget: its existing 100vh flex layout just
//   fills whatever box the iframe is given.
// - Only the launcher button + the panel "chrome" (header bar, close button) are injected into the
//   host page directly, and every one of their class names is prefixed `arcl-cw-` specifically to
//   make an accidental collision with the host page's own CSS astronomically unlikely.
// - No dependencies, no build step — this is a single vanilla-JS file so it can be dropped into
//   any page regardless of what else that page's stack looks like.

(function () {
  "use strict";

  var thisScript = document.currentScript;
  if (!thisScript) return; // extremely old browser or dynamically-injected without currentScript support — nothing safe to do

  var cfg = {
    // Defaults to "index.html" next to wherever widget.js itself was loaded from, so the common
    // case (both files hosted together) needs no configuration at all. Override with data-src if
    // the chat UI is hosted somewhere else than widget.js.
    src: thisScript.dataset.src || new URL("index.html", thisScript.src).href,
    title: thisScript.dataset.title || "ARCL Rules Assistant",
    position: thisScript.dataset.position === "bottom-left" ? "bottom-left" : "bottom-right",
    color: thisScript.dataset.color || "#1f6f3d",
    colorDark: thisScript.dataset.colorDark || "#164f2b",
  };

  var STYLE = "\
    .arcl-cw-launcher {\
      position: fixed; bottom: 20px; " + (cfg.position === "bottom-left" ? "left" : "right") + ": 20px;\
      width: 60px; height: 60px; border-radius: 50%; border: none; cursor: pointer;\
      background: linear-gradient(160deg, " + cfg.color + " 0%, " + cfg.colorDark + " 100%);\
      box-shadow: 0 4px 14px rgba(0,0,0,0.28); font-size: 26px; line-height: 1;\
      display: flex; align-items: center; justify-content: center; padding: 0;\
      z-index: 2147483000; transition: transform 0.15s ease, box-shadow 0.15s ease;\
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;\
    }\
    .arcl-cw-launcher:hover { transform: scale(1.06); box-shadow: 0 6px 20px rgba(0,0,0,0.34); }\
    .arcl-cw-launcher:active { transform: scale(0.97); }\
    .arcl-cw-launcher .arcl-cw-badge {\
      position: absolute; top: -2px; " + (cfg.position === "bottom-left" ? "right" : "left") + ": -2px;\
      width: 14px; height: 14px; border-radius: 50%; background: #e0473e; border: 2px solid #fff;\
    }\
    .arcl-cw-panel {\
      position: fixed; bottom: 92px; " + (cfg.position === "bottom-left" ? "left" : "right") + ": 20px;\
      width: 380px; height: min(640px, calc(100vh - 120px)); max-height: 640px;\
      background: #fff; border-radius: 16px; overflow: hidden;\
      box-shadow: 0 12px 40px rgba(0,0,0,0.28); z-index: 2147483000;\
      display: flex; flex-direction: column;\
      opacity: 0; transform: translateY(16px) scale(0.98); pointer-events: none;\
      transition: opacity 0.18s ease, transform 0.18s ease;\
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;\
    }\
    .arcl-cw-panel.arcl-cw-open { opacity: 1; transform: none; pointer-events: auto; }\
    .arcl-cw-header {\
      flex: none; display: flex; align-items: center; justify-content: space-between;\
      padding: 12px 14px; color: #fff; font-weight: 700; font-size: 0.95rem;\
      background: linear-gradient(165deg, " + cfg.color + " 0%, " + cfg.colorDark + " 100%);\
    }\
    .arcl-cw-close {\
      background: rgba(255,255,255,0.15); border: none; color: #fff; width: 26px; height: 26px;\
      border-radius: 50%; cursor: pointer; font-size: 15px; line-height: 1; padding: 0;\
      display: flex; align-items: center; justify-content: center; transition: background 0.15s;\
    }\
    .arcl-cw-close:hover { background: rgba(255,255,255,0.3); }\
    .arcl-cw-iframe { flex: 1; border: none; width: 100%; }\
    @media (max-width: 480px) {\
      .arcl-cw-panel {\
        top: 0; left: 0; right: 0; bottom: 0; width: 100%; height: 100%; max-height: none;\
        border-radius: 0; transform: translateY(0) scale(1);\
      }\
      .arcl-cw-launcher { bottom: 16px; " + (cfg.position === "bottom-left" ? "left" : "right") + ": 16px; }\
      .arcl-cw-launcher-hidden { display: none; }\
    }\
  ";

  var styleEl = document.createElement("style");
  styleEl.textContent = STYLE;
  document.head.appendChild(styleEl);

  var launcher = document.createElement("button");
  launcher.type = "button";
  launcher.className = "arcl-cw-launcher";
  launcher.setAttribute("aria-label", "Open " + cfg.title);
  launcher.setAttribute("aria-expanded", "false");
  launcher.textContent = "🏏"; // 🏏 = cricket bat and ball emoji — escaped for the
  // same reason admin.js's section-sign fix uses §: this file's own encoding/serving isn't
  // guaranteed on every host page's server, so the character is built from a JS escape instead of
  // depending on it.

  var panel = document.createElement("div");
  panel.className = "arcl-cw-panel";

  var header = document.createElement("div");
  header.className = "arcl-cw-header";
  var titleEl = document.createElement("span");
  titleEl.textContent = cfg.title;
  var closeBtn = document.createElement("button");
  closeBtn.type = "button";
  closeBtn.className = "arcl-cw-close";
  closeBtn.setAttribute("aria-label", "Close chat");
  closeBtn.textContent = "✕";
  header.appendChild(titleEl);
  header.appendChild(closeBtn);

  var iframe = document.createElement("iframe");
  iframe.className = "arcl-cw-iframe";
  iframe.title = cfg.title;
  // Loaded lazily (only once the panel is first opened) so a visitor who never opens the widget
  // never pays for the chat UI's own load/session-init cost at all.
  var iframeLoaded = false;

  panel.appendChild(header);
  panel.appendChild(iframe);

  var open = false;
  function setOpen(next) {
    open = next;
    if (open && !iframeLoaded) {
      // ?embed=1 tells the chat page (see app.js) to hide its own header/title bar — without it,
      // the popup shows two stacked "ARCL Rules Assistant" bars, this shell's and the page's own.
      iframe.src = cfg.src + (cfg.src.indexOf("?") === -1 ? "?" : "&") + "embed=1";
      iframeLoaded = true;
    }
    panel.classList.toggle("arcl-cw-open", open);
    // On a full-screen mobile panel the launcher would otherwise float on top of the chat input
    // (nothing below 480px repositions it) — the panel's own header close button is the way to
    // close it there, so the launcher steps aside entirely while the panel is open on that size.
    launcher.classList.toggle("arcl-cw-launcher-hidden", open);
    launcher.setAttribute("aria-expanded", String(open));
    launcher.textContent = open ? "✕" : "🏏";
    launcher.setAttribute("aria-label", open ? "Close " + cfg.title : "Open " + cfg.title);
  }

  launcher.addEventListener("click", function () { setOpen(!open); });
  closeBtn.addEventListener("click", function () { setOpen(false); });

  // Injected once the document body exists — widget.js is expected to load with `defer` (see the
  // usage comment above), by which point document.body is always available.
  function mount() {
    document.body.appendChild(panel);
    document.body.appendChild(launcher);
  }
  if (document.body) mount();
  else document.addEventListener("DOMContentLoaded", mount);
})();
