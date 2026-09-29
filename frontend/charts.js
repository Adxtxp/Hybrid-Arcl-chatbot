// ARCL Admin — small reusable SVG chart primitives (no external chart library, consistent with
// the rest of this app being plain vanilla JS/HTML/CSS). Three forms, each per dataviz's method:
//   renderLineChart      — trend over time, one series per chart (never dual-axis: separate
//                           charts for measures of different scale, e.g. queries vs. cost).
//   renderStackedBarChart — part-to-whole per category (token usage, route breakdown, league
//                           volume) — categorical color, legend, per-segment tooltip.
//   renderBarChart        — ranked magnitude (top retrieved sections) — one sequential hue.
//
// Every chart: hover tooltip (crosshair on lines, per-mark on bars), a legend for >=2 series,
// value labels only where they fit, and text always in ink tokens (never the series color) per
// marks-and-anatomy.md. The existing data tables elsewhere on the page are each chart's "table
// view" fallback, so nothing here is only reachable by hovering.

const SVG_NS = "http://www.w3.org/2000/svg";

function svgEl(tag, attrs = {}) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

function niceMax(max) {
  if (max <= 0) return 1;
  const pow = Math.pow(10, Math.floor(Math.log10(max)));
  const steps = [1, 2, 2.5, 5, 10];
  for (const s of steps) if (max <= s * pow) return s * pow;
  return 10 * pow;
}

function makeTooltip(root) {
  // The tooltip's x/y are computed relative to `root`'s own top-left corner (matching the SVG's
  // 1:1 viewBox-to-pixel mapping), but `.viz-tooltip`'s CSS position:absolute anchors to the
  // nearest *positioned* ancestor — which without this would be .chart-card, not `root` (the
  // inner chart div passed in here). That mismatch put every tooltip off by .chart-card's padding
  // (and, for cards with a title above the chart div, by that title's full height) — found by
  // hovering a real chart and seeing the tooltip land in the wrong spot, not by reading the code.
  if (getComputedStyle(root).position === "static") root.style.position = "relative";
  const tip = document.createElement("div");
  tip.className = "viz-tooltip hidden";
  root.appendChild(tip);
  return {
    // Every call site calls setRows(rows) to populate content, then show(x, y) to reveal/position
    // it — show() used to also do `tip.innerHTML = ""` "just in case", which ran *after* setRows
    // had already populated the tooltip and silently wiped it back to empty every time. That's why
    // every tooltip on every chart rendered as a content-less box: found by checking what was
    // actually inside the tooltip element after a real hover, not from reading the code in isolation.
    show(x, y) {
      tip.classList.remove("hidden");
      tip.style.left = `${x}px`;
      tip.style.top = `${y}px`;
    },
    setRows(rows) {
      // rows: [{label, value, color}] — inserted via textContent, never innerHTML, since
      // series/category names can originate from API data (see admin.js's own note on this).
      tip.innerHTML = "";
      for (const row of rows) {
        const line = document.createElement("div");
        line.className = "viz-tooltip-row";
        if (row.color) {
          const key = document.createElement("span");
          key.className = "viz-tooltip-key";
          key.style.background = row.color;
          line.appendChild(key);
        }
        const val = document.createElement("span");
        val.className = "viz-tooltip-value";
        val.textContent = row.value;
        const lbl = document.createElement("span");
        lbl.className = "viz-tooltip-label";
        lbl.textContent = row.label;
        line.appendChild(val);
        line.appendChild(lbl);
        tip.appendChild(line);
      }
    },
    move(x, y) {
      tip.style.left = `${x}px`;
      tip.style.top = `${y}px`;
    },
    hide() {
      tip.classList.add("hidden");
    },
  };
}

function makeLegend(root, series) {
  if (series.length < 2) return; // single series: the title already says what's plotted
  const legend = document.createElement("div");
  legend.className = "viz-legend";
  for (const s of series) {
    const item = document.createElement("span");
    item.className = "viz-legend-item";
    const swatch = document.createElement("span");
    swatch.className = "viz-legend-swatch";
    swatch.style.background = s.color;
    const label = document.createElement("span");
    label.textContent = s.label;
    item.appendChild(swatch);
    item.appendChild(label);
    legend.appendChild(item);
  }
  root.appendChild(legend);
}

// ---------------------------------------------------------------------- line chart

/**
 * @param {HTMLElement} container
 * @param {{label: string, color: string, points: {x: string, y: number}[]}[]} series
 * @param {{valueFormat?: (n:number)=>string}} opts
 */
function renderLineChart(container, series, opts = {}) {
  container.innerHTML = "";
  const valueFormat = opts.valueFormat ?? ((n) => n.toLocaleString());
  const width = container.clientWidth || 600;
  const height = 220;
  const margin = { top: 16, right: 16, bottom: 28, left: 48 };
  const innerW = width - margin.left - margin.right;
  const innerH = height - margin.top - margin.bottom;

  const points = series[0]?.points ?? [];
  if (points.length === 0) {
    container.innerHTML = '<p class="muted">No data yet.</p>';
    return;
  }

  const allValues = series.flatMap((s) => s.points.map((p) => p.y));
  const yMax = niceMax(Math.max(...allValues, 0));
  const xFor = (i) => margin.left + (points.length === 1 ? innerW / 2 : (i / (points.length - 1)) * innerW);
  const yFor = (v) => margin.top + innerH - (v / yMax) * innerH;

  const svg = svgEl("svg", { viewBox: `0 0 ${width} ${height}`, width: "100%", height, class: "viz-svg" });

  // gridlines (0 and max) — recessive hairlines. Uses the caller's own valueFormat (not a
  // Math.round + compact-number default) since a rounded-to-integer axis is fine for query
  // counts but silently collapses every tick to "$0" for small dollar-cost values — caught by
  // actually looking at the rendered cost chart, not by reasoning about the code.
  for (const frac of [0, 0.5, 1]) {
    const y = margin.top + innerH * (1 - frac);
    svg.appendChild(svgEl("line", { x1: margin.left, x2: width - margin.right, y1: y, y2: y, class: "viz-gridline" }));
    const label = svgEl("text", { x: margin.left - 8, y: y + 4, class: "viz-axis-label", "text-anchor": "end" });
    label.textContent = valueFormat(yMax * frac);
    svg.appendChild(label);
  }

  // x-axis: first, middle, last date labels only (avoid crowding)
  const tickIdxs = points.length <= 5 ? points.map((_, i) => i) : [0, Math.floor((points.length - 1) / 2), points.length - 1];
  for (const i of tickIdxs) {
    const label = svgEl("text", { x: xFor(i), y: height - 6, class: "viz-axis-label", "text-anchor": "middle" });
    label.textContent = points[i].x;
    svg.appendChild(label);
  }

  const crosshair = svgEl("line", { class: "viz-crosshair", y1: margin.top, y2: margin.top + innerH, opacity: 0 });

  for (const s of series) {
    const d = s.points.map((p, i) => `${i === 0 ? "M" : "L"} ${xFor(i)} ${yFor(p.y)}`).join(" ");
    svg.appendChild(svgEl("path", { d, class: "viz-line", stroke: s.color }));
    // end marker + end label (direct label at the line's end, per spec)
    const last = s.points[s.points.length - 1];
    const lastX = xFor(s.points.length - 1);
    const lastY = yFor(last.y);
    svg.appendChild(svgEl("circle", { cx: lastX, cy: lastY, r: 5, class: "viz-marker", fill: s.color }));
    const endLabel = svgEl("text", { x: lastX, y: lastY - 10, class: "viz-end-label", "text-anchor": "end" });
    endLabel.textContent = valueFormat(last.y);
    svg.appendChild(endLabel);
  }

  svg.appendChild(crosshair);

  // full-height hit strips, one per point — crosshair snaps to nearest, per interaction.md
  const tooltip = makeTooltip(container);
  const hitWidth = innerW / Math.max(points.length - 1, 1);
  points.forEach((p, i) => {
    const hit = svgEl("rect", {
      x: xFor(i) - hitWidth / 2, y: margin.top, width: hitWidth || innerW, height: innerH,
      fill: "transparent", tabindex: "0", class: "viz-hit",
    });
    const showAt = () => {
      crosshair.setAttribute("x1", xFor(i));
      crosshair.setAttribute("x2", xFor(i));
      crosshair.setAttribute("opacity", 1);
      tooltip.setRows(series.map((s) => ({ label: s.label, value: valueFormat(s.points[i].y), color: s.color })));
      const rect = container.getBoundingClientRect();
      tooltip.show(xFor(i), yFor(series[0].points[i].y));
    };
    hit.addEventListener("pointermove", showAt);
    hit.addEventListener("focus", showAt);
    hit.addEventListener("pointerleave", () => { crosshair.setAttribute("opacity", 0); tooltip.hide(); });
    hit.addEventListener("blur", () => { crosshair.setAttribute("opacity", 0); tooltip.hide(); });
    svg.appendChild(hit);
  });

  container.appendChild(svg);
  makeLegend(container, series);
}

// ---------------------------------------------------------------------- stacked bar chart

/**
 * @param {HTMLElement} container
 * @param {string[]} categories
 * @param {{label: string, color: string, values: number[]}[]} series
 * @param {{horizontal?: boolean, valueFormat?: (n:number)=>string}} opts
 */
function renderStackedBarChart(container, categories, series, opts = {}) {
  container.innerHTML = "";
  const valueFormat = opts.valueFormat ?? ((n) => n.toLocaleString());
  const width = container.clientWidth || 600;
  const barThick = 24;
  const gap = 2;
  const barSlot = opts.horizontal ? 36 : Math.max((width - 80) / categories.length, 28);

  if (categories.length === 0) {
    container.innerHTML = '<p class="muted">No data yet.</p>';
    return;
  }

  const totals = categories.map((_, i) => series.reduce((sum, s) => sum + s.values[i], 0));
  const maxTotal = niceMax(Math.max(...totals, 0));

  const tooltip = makeTooltip(container);

  if (opts.horizontal) {
    // single horizontal stacked bar (e.g. route breakdown out of one total)
    const height = 60;
    const margin = { left: 8, right: 8, top: 8, bottom: 8 };
    const innerW = width - margin.left - margin.right;
    const svg = svgEl("svg", { viewBox: `0 0 ${width} ${height}`, width: "100%", height, class: "viz-svg" });
    let x = margin.left;
    const total = totals[0] || 1;
    series.forEach((s, si) => {
      const v = s.values[0];
      const segW = Math.max((v / total) * innerW - gap, 0);
      const rect = svgEl("rect", {
        x, y: margin.top, width: segW, height: barThick, fill: s.color, rx: 4,
        class: "viz-bar-segment",
      });
      rect.addEventListener("pointermove", (e) => {
        tooltip.setRows([{ label: s.label, value: `${valueFormat(v)} (${Math.round((v / total) * 100)}%)`, color: s.color }]);
        tooltip.show(e.offsetX, e.offsetY);
      });
      rect.addEventListener("pointerleave", () => tooltip.hide());
      svg.appendChild(rect);
      // direct label inside segment only if it fits comfortably
      if (segW > 46) {
        const label = svgEl("text", {
          x: x + segW / 2, y: margin.top + barThick / 2 + 4, class: "viz-inline-label", "text-anchor": "middle",
        });
        label.textContent = `${Math.round((v / total) * 100)}%`;
        svg.appendChild(label);
      }
      x += segW + gap;
    });
    container.appendChild(svg);
    makeLegend(container, series);
    return;
  }

  // vertical stacked bars, one per category (e.g. daily token usage)
  const height = 220;
  const margin = { top: 16, right: 16, bottom: 28, left: 48 };
  const innerW = width - margin.left - margin.right;
  const innerH = height - margin.top - margin.bottom;
  const slotW = innerW / categories.length;
  const barW = Math.min(barThick, slotW - 8);

  const svg = svgEl("svg", { viewBox: `0 0 ${width} ${height}`, width: "100%", height, class: "viz-svg" });

  for (const frac of [0, 0.5, 1]) {
    const y = margin.top + innerH * (1 - frac);
    svg.appendChild(svgEl("line", { x1: margin.left, x2: width - margin.right, y1: y, y2: y, class: "viz-gridline" }));
    const label = svgEl("text", { x: margin.left - 8, y: y + 4, class: "viz-axis-label", "text-anchor": "end" });
    label.textContent = valueFormat(maxTotal * frac); // same fix as renderLineChart — see its comment
    svg.appendChild(label);
  }

  categories.forEach((cat, ci) => {
    const cx = margin.left + slotW * ci + slotW / 2;
    let yCursor = margin.top + innerH;
    const barGroup = svgEl("g", { class: "viz-bar-group" });

    series.forEach((s) => {
      const v = s.values[ci];
      if (v <= 0) return;
      const segH = Math.max((v / maxTotal) * innerH - gap, 0);
      yCursor -= segH + gap;
      const rect = svgEl("rect", {
        x: cx - barW / 2, y: yCursor, width: barW, height: segH, fill: s.color, rx: 3,
        class: "viz-bar-segment",
      });
      barGroup.appendChild(rect);
    });

    // one shared hit rect per category column, full height — simpler + bigger hit target
    const hit = svgEl("rect", {
      x: cx - slotW / 2, y: margin.top, width: slotW, height: innerH, fill: "transparent", class: "viz-hit", tabindex: "0",
    });
    const showAt = (e) => {
      const rows = series
        .filter((s) => s.values[ci] > 0)
        .map((s) => ({ label: s.label, value: valueFormat(s.values[ci]), color: s.color }));
      // A category with no data in any series (e.g. a day before the bot logged token counts)
      // has no bars drawn, but its hit rect still covers the column — show nothing rather than an
      // empty tooltip box (previously rendered as a stray dark dot with no content).
      if (rows.length === 0) { tooltip.hide(); return; }
      tooltip.setRows(rows);
      tooltip.show(cx, margin.top + innerH * 0.3);
    };
    hit.addEventListener("pointermove", showAt);
    hit.addEventListener("focus", showAt);
    hit.addEventListener("pointerleave", () => tooltip.hide());
    hit.addEventListener("blur", () => tooltip.hide());

    svg.appendChild(barGroup);
    svg.appendChild(hit);

    const label = svgEl("text", { x: cx, y: height - 6, class: "viz-axis-label", "text-anchor": "middle" });
    label.textContent = cat;
    svg.appendChild(label);
  });

  container.appendChild(svg);
  makeLegend(container, series);
}

// ---------------------------------------------------------------------- horizontal ranked bar chart

/**
 * @param {HTMLElement} container
 * @param {{label: string, value: number}[]} items
 * @param {{color: string, valueFormat?: (n:number)=>string}} opts
 */
function renderBarChart(container, items, opts = {}) {
  container.innerHTML = "";
  const valueFormat = opts.valueFormat ?? ((n) => n.toLocaleString());
  const color = opts.color ?? "#2a78d6";
  const width = container.clientWidth || 600;
  const rowH = 26;
  const margin = { top: 4, right: 56, bottom: 4, left: 140 };
  const height = margin.top + margin.bottom + items.length * rowH;
  const innerW = width - margin.left - margin.right;

  if (items.length === 0) {
    container.innerHTML = '<p class="muted">No data yet.</p>';
    return;
  }

  const maxVal = niceMax(Math.max(...items.map((i) => i.value), 0));
  const svg = svgEl("svg", { viewBox: `0 0 ${width} ${height}`, width: "100%", height, class: "viz-svg" });
  const tooltip = makeTooltip(container);

  items.forEach((item, i) => {
    const y = margin.top + i * rowH;
    const barW = Math.max((item.value / maxVal) * innerW, 2);

    const label = svgEl("text", { x: margin.left - 8, y: y + rowH / 2 + 4, class: "viz-axis-label", "text-anchor": "end" });
    label.textContent = item.label.length > 22 ? item.label.slice(0, 21) + "…" : item.label;
    svg.appendChild(label);

    const rect = svgEl("rect", {
      x: margin.left, y: y + 3, width: barW, height: rowH - 8, fill: color, rx: 4, class: "viz-bar-segment",
    });
    rect.addEventListener("pointermove", (e) => {
      tooltip.setRows([{ label: item.label, value: valueFormat(item.value) }]);
      tooltip.show(e.offsetX, e.offsetY);
    });
    rect.addEventListener("pointerleave", () => tooltip.hide());
    svg.appendChild(rect);

    // value at the bar's tip (outside if the bar is short, so it never overflows the bar itself)
    const valueLabel = svgEl("text", {
      x: margin.left + barW + 6, y: y + rowH / 2 + 4, class: "viz-inline-label-outside",
    });
    valueLabel.textContent = valueFormat(item.value);
    svg.appendChild(valueLabel);
  });

  container.appendChild(svg);
}

window.ArclCharts = { renderLineChart, renderStackedBarChart, renderBarChart };
