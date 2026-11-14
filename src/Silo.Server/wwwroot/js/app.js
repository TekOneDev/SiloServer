/* Silo Server — web front end. A thin client over /api/v1; no build step, no framework. */
(function () {
"use strict";

// ---------------------------------------------------------------- utilities
const $ = (sel, el) => (el || document).querySelector(sel);
const $$ = (sel, el) => Array.from((el || document).querySelectorAll(sel));
const esc = s => s == null ? "" : String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const num = n => n == null ? "" : Number(n).toLocaleString();
const bytes = b => { if (b == null) return ""; const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; while (b >= 1024 && i < u.length - 1) { b /= 1024; i++; } return (i ? b.toFixed(b < 10 ? 1 : 0) : b) + " " + u[i]; };
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
const plural = (n, w, p) => num(n) + " " + (n === 1 ? w : (p || w + "s"));

// Errors (0.1.7). Every failure the server reports carries a code from the Silo error registry:
// {"error":{"code","message","severity","kb","traceId"}}. silo-errors.js (generated from the registry) holds the same
// messages for failures that happen in the page itself, such as the server not answering.
function siloError(code, args) {
  const d = (window.SiloErrorTable || {})[code] || { m: "Something went wrong.", t: "Something went wrong" };
  const plain = String(d.m).replace(/\{(\w+)\}/g, (_, k) => args && args[k] != null ? args[k] : "").replace(/\s{2,}/g, " ").trim();
  const e = new Error(plain + " (" + code + ")");
  e.code = code; e.plain = plain; e.title = d.t;
  return e;
}
// The error in a failed response. A reply without a code (a proxy's page, an older server) still gets one.
async function errorFrom(r) {
  let j = null;
  try { j = await r.json(); } catch (e) { }
  const x = j && j.error;
  let e;
  if (x && typeof x === "object" && x.code) {
    e = new Error(x.message + " (" + x.code + ")");
    e.code = x.code; e.plain = x.message; e.kb = x.kb; e.ref = x.traceId; e.title = ((window.SiloErrorTable || {})[x.code] || {}).t;
  }
  else e = siloError(r.status === 401 ? "SILO-SRV-AUTH-0001" : "SILO-SRV-API-0007", { status: "HTTP " + r.status });
  e.status = r.status;
  return e;
}

async function api(path, body, method) {
  const opt = { method: method || (body ? "POST" : "GET"), headers: { "X-Silo": "1" } };
  if (body) { opt.headers["Content-Type"] = "application/json"; opt.body = JSON.stringify(body); }
  let r;
  try { r = await fetch("/api/v1" + path, opt); }
  catch (e) { throw siloError("SILO-SRV-API-0008"); }
  if (!r.ok) throw await errorFrom(r);
  const ct = r.headers.get("content-type") || "";
  return ct.includes("json") ? r.json() : r.text();
}

function toast(msg, ms) {
  const t = document.createElement("div"); t.className = "toast"; t.textContent = msg; document.body.appendChild(t);
  setTimeout(() => t.remove(), ms || 2600);
}

const store = {
  get(k, d) { try { const v = localStorage.getItem("silo." + k); return v == null ? d : JSON.parse(v); } catch (e) { return d; } },
  set(k, v) { try { localStorage.setItem("silo." + k, JSON.stringify(v)); } catch (e) { } },
};

// ---------------------------------------------------------------- icons
const I = {
  search: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.6-3.6"/></svg>',
  x: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round"><path d="M6 6l12 12M18 6 6 18"/></svg>',
  list: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01"/></svg>',
  map: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"><path d="m9 4-6 2v14l6-2 6 2 6-2V4l-6 2-6-2z"/><path d="M9 4v14M15 6v14"/></svg>',
  sun: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/></svg>',
  moon: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"><path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/></svg>',
  gear: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>',
  help: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01"/></svg>',
  pin: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2"><path d="M12 22s7-6.3 7-12a7 7 0 1 0-14 0c0 5.7 7 12 7 12z"/><circle cx="12" cy="10" r="2.5"/></svg>',
  ext: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M14 4h6v6M20 4l-9 9M19 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1h5"/></svg>',
  file: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round"><path d="M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8z"/><path d="M14 3v5h5"/></svg>',
  copy: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h10"/></svg>',
  rect: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-dasharray="3 2"><rect x="4" y="5" width="16" height="14" rx="1"/></svg>',
  target: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="8"/><circle cx="12" cy="12" r="3"/></svg>',
  chevL: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="m15 18-6-6 6-6"/></svg>',
  chevR: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"><path d="m9 18 6-6-6-6"/></svg>',
};

const LOGO = (size) => `<a class="logo" href="/" data-nav>
  <svg width="${size}" height="${size}" viewBox="0 0 64 64" aria-hidden="true"><rect width="64" height="64" rx="14" fill="var(--ink)"/>
  <path d="M10 22c8-5 15 3 22-1s14-6 22-1v6c-8-5-15-3-22 1s-14-4-22 1z" fill="#c99a2e"/>
  <path d="M10 32c8-5 15 3 22-1s14-6 22-1v6c-8-5-15-3-22 1s-14-4-22 1z" fill="#b5622a"/>
  <path d="M10 42c8-5 15 3 22-1s14-6 22-1v6c-8-5-15-3-22 1s-14-4-22 1z" fill="#2f7a78"/></svg>
  <span class="word">Sil<b>o</b></span></a>`;

// ---------------------------------------------------------------- domain look-ups
// Commodities shown as element-style tiles. Colours are muted mineral tones.
const EL = {
  "Gold": ["Au", "#c99a2e"], "Silver": ["Ag", "#8a96a3"], "Copper": ["Cu", "#b5622a"], "Nickel": ["Ni", "#5f8a6a"], "Cobalt": ["Co", "#3c5fa8"],
  "Zinc": ["Zn", "#6b7f99"], "Lead": ["Pb", "#5a5f6e"], "Iron ore": ["Fe", "#9b3d2a"], "Manganese": ["Mn", "#7d4f8a"], "Uranium": ["U", "#5f8f2a"],
  "Lithium": ["Li", "#b0478a"], "Rare earths": ["REE", "#8a5fb0"], "Platinum group": ["PGE", "#7b8794"], "Tin": ["Sn", "#7a8c8f"], "Tungsten": ["W", "#566573"],
  "Molybdenum": ["Mo", "#4f6d7a"], "Vanadium": ["V", "#3f7f8f"], "Titanium": ["Ti", "#6d7f8f"], "Mineral sands": ["HMS", "#b08a4a"], "Bauxite": ["Al", "#b8683a"],
  "Chromium": ["Cr", "#4f8a5f"], "Tantalum": ["Ta", "#5a6a8a"], "Graphite": ["Gr", "#3d4248"], "Diamonds": ["Dia", "#4a90a8"], "Phosphate": ["P", "#8a7a3a"],
  "Potash": ["K", "#a85f5f"], "Salt": ["NaCl", "#8f99a3"], "Gypsum": ["Gyp", "#a8a08a"], "Kaolin": ["Kao", "#a89f8f"], "Silica sand": ["Si", "#a8925f"],
  "Limestone": ["Lst", "#8f8f7a"], "Coal": ["C", "#3a3a3a"], "Coal seam gas": ["CSG", "#4f6f8f"], "Petroleum": ["Oil", "#2f3f4f"], "Geothermal": ["Geo", "#b04a3a"],
  "Water": ["H₂O", "#3a7fb0"], "Garnet": ["Grt", "#8f2f3f"], "Base metals": ["BM", "#6f7a6a"], "Antimony": ["Sb", "#6a6f80"], "Bismuth": ["Bi", "#9a7aa0"],
  "Magnesite": ["Mg", "#8a9a7a"], "Vermiculite": ["Vrm", "#9a8a6a"], "Gemstones": ["Gem", "#3a8f8a"], "Industrial minerals": ["Ind", "#7a7a7a"],
  "Hydrogen": ["H₂", "#4a8fb0"], "Helium": ["He", "#7ab0b0"], "Carbon storage": ["CO₂", "#5a6a5a"],
};
const el = name => EL[name] || [name.slice(0, 2), "#7a828c"];
const FT = {
  "PDF": ["PDF", "#b23b2e"], "Word document": ["DOC", "#2b5797"], "Spreadsheet": ["XLS", "#217346"], "Presentation": ["PPT", "#c4502a"],
  "Image or scan": ["IMG", "#7a4fa0"], "Datamine": ["DM", "#2f7a78"], "Rocscience": ["RS3", "#c46a1a"], "Data table": ["CSV", "#3f7f5f"],
  "Text": ["TXT", "#5a636e"], "GIS data": ["GIS", "#3a7fb0"], "CAD drawing": ["CAD", "#8f5a2a"], "Web / data file": ["XML", "#6a6a8a"], "Archive": ["ZIP", "#8a7a3a"],
};
const ft = (name, ext) => FT[name] || [(ext || "?").replace(".", "").slice(0, 4).toUpperCase(), "#6a737d"];
const DS_COLOR = { WAMEX: "#2f7a78", GSQ: "#8f4a1d", SARIG: "#7d4f8a", GEMIS: "#9b3d2a", MRT: "#3c5fa8", DIGS: "#5f8a2a" };

const DIM_ORDER = ["f.dataset", "f.commodity", "f.rtype", "f.year", "f.state", "f.tenement", "f.company", "f.owner", "f.asx", "f.lith", "f.method", "f.ftype", "f.geo"];
let LABELS = {
  "f.dataset": "Source", "f.commodity": "Commodity", "f.rtype": "Report type", "f.year": "Year", "f.state": "State", "f.tenement": "Tenement",
  "f.company": "Company", "f.owner": "Company now (ASX)", "f.asx": "ASX code", "f.lith": "Rock type", "f.method": "Method", "f.ftype": "File type", "f.geo": "Location",
};

// ---------------------------------------------------------------- URL state
function readState() {
  const u = new URL(location.href), p = u.searchParams;
  const filters = {};
  for (const [k, v] of p) if (k.startsWith("f.") || k.startsWith("x.")) (filters[k] = filters[k] || []).push(v);
  return {
    path: u.pathname.replace(/\/+$/, "") || "/",
    q: p.get("q") || "",
    filters,
    yf: p.get("yf") ? +p.get("yf") : null,
    yt: p.get("yt") ? +p.get("yt") : null,
    sort: p.get("sort") || "relevance",
    page: Math.max(1, +(p.get("p") || 1)),
    bbox: p.get("bbox") ? p.get("bbox").split(",").map(Number) : null,
    sel: p.get("sel") ? +p.get("sel") : null,
  };
}
function buildUrl(path, st) {
  const p = new URLSearchParams();
  if (st.q) p.set("q", st.q);
  for (const k of Object.keys(st.filters || {})) for (const v of st.filters[k]) p.append(k, v);
  if (st.yf) p.set("yf", st.yf);
  if (st.yt) p.set("yt", st.yt);
  if (st.sort && st.sort !== "relevance") p.set("sort", st.sort);
  if (st.page > 1) p.set("p", st.page);
  if (st.bbox) p.set("bbox", st.bbox.map(x => x.toFixed(3)).join(","));
  if (st.sel) p.set("sel", st.sel);
  const qs = p.toString();
  return path + (qs ? "?" + qs : "");
}
function go(path, st, replace) {
  const url = buildUrl(path, st);
  if (replace) history.replaceState(null, "", url); else history.pushState(null, "", url);
  route();
}
function request(st, extra) {
  return Object.assign({
    q: st.q, filters: st.filters, yearFrom: st.yf, yearTo: st.yt, bbox: st.bbox, sort: st.sort,
    offset: (st.page - 1) * 10, limit: 10,
  }, extra || {});
}

// ---------------------------------------------------------------- theme
function themeToggleHtml() {
  const dark = document.documentElement.getAttribute("data-theme") === "dark";
  return `<button class="icon-btn" data-act="theme" title="${dark ? "Light" : "Dark"} theme">${dark ? I.sun : I.moon}</button>`;
}
function toggleTheme() {
  const dark = document.documentElement.getAttribute("data-theme") === "dark";
  const t = dark ? "light" : "dark";
  document.documentElement.setAttribute("data-theme", t);
  store.set("theme", t); try { localStorage.setItem("silo.theme", t); } catch (e) { }
  $$("[data-act=theme]").forEach(b => { b.innerHTML = t === "dark" ? I.sun : I.moon; });
  if (MapView.map) MapView.applyTheme();
}

// ---------------------------------------------------------------- search box with suggestions
function searchBoxHtml(q, placeholder) {
  return `<form class="searchbox" autocomplete="off" role="search">
    <div class="field">
      <span class="lens">${I.search}</span>
      <input name="q" type="search" value="${esc(q)}" placeholder="${esc(placeholder || "Search reports, tenements, commodities, rock types…")}" aria-label="Search" spellcheck="false">
      <button type="button" class="clear ${q ? "" : "hidden"}" title="Clear">${I.x}</button>
      <button class="go" type="submit" title="Search">${I.search}</button>
    </div>
    <div class="suggest hidden"></div>
  </form>`;
}
function wireSearchBox(form, onSubmit) {
  const input = $("input", form), box = $(".suggest", form), clear = $(".clear", form);
  let items = [], active = -1, seq = 0;
  const close = () => { box.classList.add("hidden"); form.classList.remove("open"); active = -1; };
  const show = () => {
    if (!items.length) { close(); return; }
    box.innerHTML = items.map((it, i) => `<div class="row ${i === active ? "active" : ""}" data-i="${i}"><span class="k">${esc(it.k)}</span><span class="v">${it.html}</span>${it.m ? `<span class="m">${esc(it.m)}</span>` : ""}</div>`).join("")
      + `<div class="hint">Tip: <code>commodity:gold</code> <code>type:annual</code> <code>year:2000..2010</code> <code>"exact phrase"</code> <code>-exclude</code></div>`;
    box.classList.remove("hidden"); form.classList.add("open");
  };
  const fetchSuggest = debounce(async () => {
    const v = input.value, mine = ++seq;
    const last = v.split(/\s+/).pop();
    if (v.trim().length < 2 || /:/.test(last)) { items = []; close(); return; }
    try {
      const r = await api("/suggest?q=" + encodeURIComponent(v));
      if (mine !== seq) return;
      items = r.slice(0, 10).map(s => s.kind === "facet"
        ? { k: s.label, html: hl(s.value, v), m: "filter", apply: () => applyFacetSuggestion(input, s) }
        : { k: s.ds + " " + (s.year || ""), html: hl(s.value, v), m: s.coll, apply: () => { input.value = "report:" + s.coll; onSubmit(input.value); } });
      active = -1; show();
    } catch (e) { items = []; close(); }
  }, 140);
  input.addEventListener("input", () => { clear.classList.toggle("hidden", !input.value); fetchSuggest(); });
  input.addEventListener("keydown", e => {
    if (box.classList.contains("hidden")) return;
    if (e.key === "ArrowDown") { active = Math.min(items.length - 1, active + 1); show(); e.preventDefault(); }
    else if (e.key === "ArrowUp") { active = Math.max(-1, active - 1); show(); e.preventDefault(); }
    else if (e.key === "Escape") { close(); }
    else if (e.key === "Enter" && active >= 0) { e.preventDefault(); const it = items[active]; close(); it.apply(); }
  });
  input.addEventListener("blur", () => setTimeout(close, 150));
  box.addEventListener("mousedown", e => { const r = e.target.closest(".row"); if (!r) return; e.preventDefault(); const it = items[+r.dataset.i]; close(); it.apply(); });
  clear.addEventListener("click", () => { input.value = ""; clear.classList.add("hidden"); input.focus(); });
  form.addEventListener("submit", e => { e.preventDefault(); close(); onSubmit(input.value.trim()); });
  function applyFacetSuggestion(inp, s) {
    // Replace the word being typed with an operator, e.g. "gold E28" → "gold tenement:"E 28/589"".
    const ops = { "f.tenement": "tenement", "f.commodity": "commodity", "f.company": "company", "f.lith": "rock", "f.method": "method", "f.rtype": "type", "f.asx": "asx" };
    const words = inp.value.trimEnd().split(/\s+/); words.pop();
    const val = /\s/.test(s.value) ? `"${s.value}"` : s.value;
    words.push(ops[s.dim] + ":" + val);
    inp.value = words.join(" ") + " ";
    onSubmit(inp.value.trim());
  }
}
function hl(text, typed) {
  const words = typed.toLowerCase().split(/\s+/).filter(w => w.length > 1 && !w.includes(":"));
  let h = esc(text);
  for (const w of words) h = h.replace(new RegExp("(" + w.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&") + ")", "ig"), "<b>$1</b>");
  return h;
}

// ---------------------------------------------------------------- shared top bar
function topbarHtml(st, active) {
  return `<header class="topbar">
    ${LOGO(32)}
    ${searchBoxHtml(st.q)}
    <div class="right">
      <button class="icon-btn" data-act="help" title="Search tips">${I.help}</button>
      ${themeToggleHtml()}
      <a class="icon-btn" href="/admin" data-nav title="Server status and settings">${I.gear}</a>
    </div>
  </header>
  <nav class="tabs">
    <a href="${buildUrl("/search", Object.assign({}, st, { bbox: active === "map" ? null : st.bbox, sel: null, page: 1 }))}" data-nav class="${active === "search" ? "on" : ""}">${I.list} Reports</a>
    <a href="${buildUrl("/map", Object.assign({}, st, { sel: null, page: 1 }))}" data-nav class="${active === "map" ? "on" : ""}">${I.map} Map</a>
    <span class="spacer"></span>
    <span class="meta" id="tabmeta"></span>
  </nav>`;
}
function wireTopbar(root, st, path) {
  wireSearchBox($(".topbar .searchbox", root), q => go(path, Object.assign({}, st, { q, page: 1, sel: null })));
}

// ---------------------------------------------------------------- home page
async function renderHome(st) {
  const app = $("#app");
  document.title = "Silo — mining data search";
  app.innerHTML = `<div class="home">
    ${contoursSvg()}
    <header>
      <a class="btn ghost" href="/map" data-nav>${I.map} Map</a>
      <button class="icon-btn" data-act="help" title="Search tips">${I.help}</button>
      ${themeToggleHtml()}
      <a class="icon-btn" href="/admin" data-nav title="Server status and settings">${I.gear}</a>
    </header>
    <main>
      <div class="brand">${LOGO(64)}<div class="tag">Exploration reports, drilling data and technical files, searchable to the page.</div></div>
      ${searchBoxHtml("", "Search reports, tenements, commodities, rock types…")}
      <div class="examples">
        ${[["gold RC drilling", "gold RC drilling"], ["komatiite nickel", "komatiite nickel"], ["EPM 17533", "EPM 17533"], ["commodity:copper type:annual", "<code>commodity:</code>copper <code>type:</code>annual"], ["banded iron formation year:2010..2020", "BIF <code>year:</code>2010..2020"], ["has:location coal", "<code>has:</code>location coal"]]
          .map(([q, h]) => `<a class="chip-link" href="${buildUrl("/search", { q })}" data-nav>${h}</a>`).join("")}
      </div>
      <div id="home-stats" class="stats"><span class="skeleton" style="display:inline-block;width:320px;height:16px"></span></div>
      <div id="home-body"></div>
    </main>
    <footer>Silo Server · public data under each source's licence (most are CC BY 4.0) · <a href="/api/v1/" target="_blank">API</a></footer>
  </div>`;
  wireSearchBox($(".searchbox", app), q => go("/search", { q, filters: {}, page: 1 }));
  setTimeout(() => $(".searchbox input", app).focus(), 0);
  let o;
  try { o = await api("/overview"); } catch (e) { $("#home-stats").textContent = e.message; return; }
  if (o.labels) LABELS = Object.assign(LABELS, o.labels);
  $("#home-stats").innerHTML = o.files
    ? `<b>${num(o.reports)}</b> reports · <b>${num(o.files)}</b> files indexed${o.geo ? ` · <b>${num((o.geo || []).filter(g => g.value !== "No location").reduce((a, g) => a + g.count, 0))}</b> on the map` : ""}
       ${o.pending ? `<div class="building-note"><span class="pulse"></span> Indexing: ${plural(o.pending, "file")} still to read</div>` : ""}`
    : `Nothing is indexed yet. ${o.pending ? `<div class="building-note"><span class="pulse"></span> Reading ${plural(o.pending, "file")}…</div>` : `Add a source on the <a href="/admin" data-nav>server page</a>.`}`;
  const com = (o.commodities || []).slice(0, 18), src = o.sources || [], types = o.types || [];
  $("#home-body").innerHTML = `
    ${com.length ? `<div class="section-title">Browse by commodity</div>
    <div class="tiles">${com.map(c => { const [s, col] = el(c.value); return `<a class="tile" style="--el:${col}" href="${buildUrl("/search", { filters: { "f.commodity": [c.value] } })}" data-nav><span class="n">${num(c.count)}</span><span class="sym">${esc(s)}</span><span class="name">${esc(c.value)}</span></a>`; }).join("")}</div>` : ""}
    ${src.length ? `<div class="section-title">Sources</div>
    <div class="sources">${src.map(s => `<a class="source-card" href="${buildUrl("/search", { filters: { "f.dataset": [s.value] } })}" data-nav><span class="badge-lg" style="background:${DS_COLOR[s.value] || "var(--teal)"}">${esc(s.value.slice(0, 5))}</span><span><div class="t">${esc(s.value)}</div><div class="s">${plural(s.count, "file")}</div></span></a>`).join("")}</div>` : ""}
    ${types.length ? `<div class="section-title">Report types</div>
    <div class="examples">${types.map(t => `<a class="chip-link" href="${buildUrl("/search", { filters: { "f.rtype": [t.value] } })}" data-nav>${esc(t.value)} <span class="muted">${num(t.count)}</span></a>`).join("")}</div>` : ""}`;
}

function contoursSvg() {
  // Soft topographic contours behind the home page, generated once.
  let paths = "";
  const cx = [0.18, 0.82, 0.62], cy = [0.78, 0.22, 0.92];
  for (let k = 0; k < 3; k++) for (let i = 1; i <= 9; i++) {
    const r = i * 46 + k * 12, pts = [];
    for (let a = 0; a <= 64; a++) {
      const t = a / 64 * Math.PI * 2;
      const wob = 1 + 0.12 * Math.sin(3 * t + k * 2 + i * .3) + 0.06 * Math.sin(5 * t + i);
      pts.push(((cx[k] * 1440) + Math.cos(t) * r * wob * 1.25).toFixed(1) + "," + ((cy[k] * 900) + Math.sin(t) * r * wob * .8).toFixed(1));
    }
    paths += `<path d="M${pts.join("L")}Z" />`;
  }
  return `<svg class="contours" viewBox="0 0 1440 900" preserveAspectRatio="xMidYMid slice" aria-hidden="true"><g fill="none" stroke="var(--line-2)" stroke-width="1">${paths}</g></svg>`;
}

// ---------------------------------------------------------------- results page
const Search = { last: null, st: null, facetOpen: store.get("facetOpen", {}), more: {} };

async function renderSearch(st) {
  const app = $("#app");
  Search.st = st;
  document.title = (st.q ? st.q + " — " : "") + "Silo";
  if (!$(".results-wrap", app) || Search.view !== "search") {
    app.innerHTML = topbarHtml(st, "search") + `<div class="results-wrap"><aside class="facets" id="facets"></aside><section id="results"></section><aside class="preview hidden" id="preview"></aside></div>`;
    Search.view = "search";
  } else {
    // Keep the layout; refresh the top bar's query and tabs.
    const tmp = document.createElement("div"); tmp.innerHTML = topbarHtml(st, "search");
    $(".topbar", app).replaceWith(tmp.children[0]); $(".tabs", app).replaceWith(tmp.children[0]);
  }
  wireTopbar(app, st, "/search");
  $("#results").innerHTML = resultsSkeleton();
  let r;
  try { r = await api("/search", request(st)); }
  catch (e) { $("#results").innerHTML = `<div class="empty"><h3>Search failed</h3>${esc(e.message)}</div>`; return; }
  if (Search.st !== st) return;
  Search.last = r;
  $("#tabmeta").innerHTML = `<select id="sort" title="Sort">${[["relevance", "Most relevant"], ["newest", "Newest first"], ["oldest", "Oldest first"]].map(([v, l]) => `<option value="${v}" ${st.sort === v ? "selected" : ""}>${l}</option>`).join("")}</select>`;
  $("#sort").onchange = e => go("/search", Object.assign({}, st, { sort: e.target.value, page: 1 }));
  renderFacets(r, st);
  renderResults(r, st);
  if (st.sel) Preview.open(st.sel, st.q, $("#preview"), false); else Preview.close(true);
}

function resultsSkeleton() {
  return Array.from({ length: 5 }, () => `<div class="result"><div class="skeleton" style="width:30%;height:12px;margin-bottom:8px"></div><div class="skeleton" style="width:75%;height:20px;margin-bottom:10px"></div><div class="skeleton" style="width:95%;height:12px;margin-bottom:6px"></div><div class="skeleton" style="width:85%;height:12px"></div></div>`).join("");
}

function chipsHtml(r, st) {
  const chips = [];
  for (const c of r.chips || []) chips.push({ html: `${esc(c.label)}${c.value ? ": <b>" + esc(c.value) + "</b>" : ""}`, rm: { token: c.token } });
  for (const [dim, vals] of Object.entries(st.filters)) for (const v of vals) chips.push({ html: `${esc(LABELS[dim] || dim.replace(/^x\./, ""))}: <b>${esc(v)}</b>`, rm: { dim, v } });
  if (st.yf || st.yt) chips.push({ html: `Year: <b>${st.yf && st.yt ? (st.yf === st.yt ? st.yf : st.yf + "–" + st.yt) : st.yf ? st.yf + " on" : "to " + st.yt}</b>`, rm: { year: 1 } });
  if (st.bbox) chips.push({ html: `Map area: <b>${areaLabel(st.bbox)}</b>`, rm: { bbox: 1 } });
  if (!chips.length) return "";
  return `<div class="chips">${chips.map((c, i) => `<span class="chip">${c.html}<button data-rm="${i}" title="Remove">${I.x.replace('stroke-width="2.2"', 'stroke-width="2.6" width="12" height="12"')}</button></span>`).join("")}
    ${chips.length > 1 ? `<span class="chip clear-all" data-rm="all">Clear all</span>` : ""}</div>`;
  function areaLabel(b) { return `${(-b[1]).toFixed(1)}–${(-b[3]).toFixed(1)}°S, ${b[0].toFixed(1)}–${b[2].toFixed(1)}°E`; }
}
function wireChips(container, r, st, path) {
  const list = [];
  for (const c of r.chips || []) list.push({ token: c.token });
  for (const [dim, vals] of Object.entries(st.filters)) for (const v of vals) list.push({ dim, v });
  if (st.yf || st.yt) list.push({ year: 1 });
  if (st.bbox) list.push({ bbox: 1 });
  $$("[data-rm]", container).forEach(b => b.addEventListener("click", () => {
    const n = Object.assign({}, st, { filters: JSON.parse(JSON.stringify(st.filters)), page: 1, sel: null });
    if (b.dataset.rm === "all") { n.filters = {}; n.yf = n.yt = null; n.bbox = null; n.q = stripOperators(st.q, (r.chips || []).map(c => c.token)); return go(path, n); }
    const it = list[+b.dataset.rm];
    if (it.token) n.q = stripOperators(st.q, [it.token]);
    if (it.dim) { n.filters[it.dim] = n.filters[it.dim].filter(x => x !== it.v); if (!n.filters[it.dim].length) delete n.filters[it.dim]; }
    if (it.year) { n.yf = n.yt = null; }
    if (it.bbox) n.bbox = null;
    go(path, n);
  }));
}
function stripOperators(q, tokens) {
  let s = " " + q + " ";
  for (const t of tokens) s = s.replace(" " + t + " ", " ").replace(t, "");
  return s.replace(/\s+/g, " ").trim();
}

function renderResults(r, st) {
  const el_ = $("#results");
  const groups = r.groups || [];
  let h = chipsHtml(r, st);
  if (r.didYouMean) h += `<p class="dym">Did you mean <a href="${buildUrl("/search", Object.assign({}, st, { q: r.didYouMean, page: 1, sel: null }))}" data-nav>${esc(r.didYouMean)}</a>?</p>`;
  if (r.building) h += `<div class="relaxed"><span class="pulse" style="display:inline-block;color:var(--warn);margin-right:6px"></span>The index is still being built, so some files are not searchable yet.</div>`;
  if (r.relaxed) h += `<div class="relaxed">No report has every word, so these match most of them.</div>`;
  if (groups.length) {
    h += `<div class="summary-line"><b>${plural(r.reports || r.total, "report")}</b>${r.total !== r.reports ? ` · ${plural(r.total, "file")}` : ""} · ${(r.tookMs / 1000).toFixed(2)} s</div>`;
    h += groups.map((g, i) => resultHtml(g, i, st)).join("");
    h += pagerHtml(r, st);
  } else {
    h += `<div class="empty"><h3>No reports match${st.q ? ` <q>${esc(st.q)}</q>` : ""}</h3>
      <ul><li>Check the spelling, or try fewer or more general words.</li>
      <li>Remove a filter: they all have to match.</li>
      <li>Tenements can be typed as <code>E28/589</code>, <code>E 28/589</code> or <code>EPM 17533</code>.</li></ul></div>`;
  }
  el_.innerHTML = h;
  wireChips(el_, r, st, "/search");
  $$(".result", el_).forEach(div => div.addEventListener("click", e => {
    if (e.target.closest("a[href]") && !e.target.closest("[data-open]")) return;
    e.preventDefault();
    const id = +(e.target.closest("[data-open]")?.dataset.open || div.dataset.id);
    const page = +(e.target.closest("[data-page]")?.dataset.page || div.dataset.page || 0);
    selectResult(id, page);
  }));
  $$(".pager button[data-p]", el_).forEach(b => b.addEventListener("click", () => { go("/search", Object.assign({}, st, { page: +b.dataset.p, sel: null })); scrollTo(0, 0); }));
}

function resultHtml(g, i, st) {
  const c = g.top.card || {}, top = g.top;
  const facts = [];
  if (c.year) facts.push(`<span class="pill">${c.year}</span>`);
  if (c.rtype && c.rtype !== "Other report") facts.push(`<span class="pill">${esc(c.rtype)}</span>`);
  for (const m of (c.commodity || []).slice(0, 4)) { const [s, col] = el(m); facts.push(`<span class="pill el" style="--el:${col}"><i>${esc(s)}</i>${esc(m)}</span>`); }
  if ((c.commodity || []).length > 4) facts.push(`<span class="pill">+${c.commodity.length - 4}</span>`);
  for (const t of (c.ten || []).slice(0, 3)) facts.push(`<span class="pill ten">${esc(t)}</span>`);
  if ((c.ten || []).length > 3) facts.push(`<span class="pill ten">+${c.ten.length - 3}</span>`);
  if (c.lat != null) facts.push(`<span class="pill loc" title="${esc(locBasis(c))}">${I.pin}${c.geo === "map sheet" ? "Map sheet" : "Located"}</span>`);
  if (c.ocr) facts.push(`<span class="pill" title="Text read by OCR from scanned pages">OCR</span>`);
  const company = (c.company || [])[0];
  const files = g.files || 1;
  const sub = (g.more || []).slice(0, 2).map(m => `<div class="sh"><span class="fn" data-open="${m.id}" data-page="${m.page || ""}">${esc(m.card?.name || "")}</span>${m.page ? ` · p.${m.page}` : ""}${m.snippet ? ` — ${m.snippet}` : ""}</div>`).join("");
  return `<article class="result ${Preview.id === top.id ? "sel" : ""}" data-id="${top.id}" data-page="${top.page || ""}" data-i="${i}">
    <div class="crumb"><span class="ds" style="color:${DS_COLOR[c.ds] || "var(--teal)"}">${esc(c.ds || c.src || "")}</span>${c.coll ? `<span>${esc(c.coll)}</span>` : ""}${company ? `<span>·</span><span class="co">${esc(company)}</span>` : ""}<span>·</span><span class="path">${esc(c.name)}</span></div>
    <h3>${esc(c.title || c.name)}</h3>
    <div class="facts">${facts.join("")}</div>
    ${top.snippet ? `<div class="snip">${top.page ? `<span class="pg">p.${top.page}</span>` : ""}${top.snippet}</div>` : c.summary ? `<div class="snip">${esc(c.summary)}</div>` : ""}
    ${sub ? `<div class="sub-hits">${sub}</div>` : ""}
    <div class="files-line">${files > 1 ? `<span>${plural(files, "matching file")} in this report</span>` : ""}${top.copies ? `<span>${plural(top.copies, "identical copy", "identical copies")} folded</span>` : ""}${c.pages ? `<span>${plural(c.pages, "page")}</span>` : ""}<span>${esc(ft(c.ftype, c.ext)[0])} · ${bytes(c.size)}</span></div>
  </article>`;
}
function locBasis(c) {
  return { text: "Location read from coordinates in the report", "map sheet": "Approximate: from the 1:250 000 map sheet named in the report", table: "From coordinates in the data", exif: "From the photo's GPS" }[c.geo] || "Report footprint from the state's tenement data";
}
function pagerHtml(r, st) {
  const total = r.reports || r.total, pages = Math.ceil(total / 10);
  if (pages <= 1) return "";
  const cur = st.page, out = [];
  const add = p => out.push(`<button data-p="${p}" class="${p === cur ? "on" : ""}">${p}</button>`);
  out.push(`<button data-p="${cur - 1}" ${cur <= 1 ? "disabled" : ""} title="Previous">${I.chevL.replace("<svg", '<svg width="16" height="16"')}</button>`);
  const from = Math.max(1, cur - 4), to = Math.min(pages, from + 8);
  for (let p = from; p <= to; p++) add(p);
  out.push(`<button data-p="${cur + 1}" ${cur >= pages ? "disabled" : ""} title="Next">${I.chevR.replace("<svg", '<svg width="16" height="16"')}</button>`);
  return `<div class="pager">${out.join("")}</div>`;
}

function selectResult(id, page) {
  const st = Object.assign({}, Search.st, { sel: id });
  history.replaceState(null, "", buildUrl("/search", st));
  Search.st.sel = id;
  $$(".result").forEach(r => r.classList.toggle("sel", +r.dataset.id === id));
  Preview.open(id, Search.st.q, $("#preview"), page || true);
}

// ---------------------------------------------------------------- facets
function renderFacets(r, st) {
  const f = r.facets || {};
  const dims = DIM_ORDER.concat(Object.keys(f).filter(k => !k.endsWith(":n") && !DIM_ORDER.includes(k)));
  let h = "";
  for (const dim of dims) {
    let vals = f[dim];
    const selected = st.filters[dim] || [];
    if (!vals || (!vals.length && !selected.length)) continue;
    if (dim === "f.year") { h += yearFacetHtml(vals, st); continue; }
    if (vals.length === 1 && !selected.length && dim !== "f.dataset" && dim !== "f.geo") continue; // one value tells nothing
    for (const s of selected) if (!vals.some(v => v.value === s)) vals = [{ value: s, count: 0 }].concat(vals);
    const distinct = (f[dim + ":n"] || [{}])[0].count || vals.length;
    const open = Search.facetOpen[dim] !== false && !(Search.facetOpen[dim] === undefined && ["f.owner", "f.asx", "f.ftype", "f.method"].includes(dim) && !selected.length);
    const showAll = Search.more[dim];
    const limit = showAll ? 500 : 6;
    const list = vals.slice(0, Math.max(limit, selected.length));
    h += `<div class="facet ${open ? "" : "collapsed"}" data-dim="${esc(dim)}">
      <h4>${esc(LABELS[dim] || dim.replace(/^x\./, ""))} <span class="n">${distinct > 6 ? num(distinct) : ""}</span></h4>
      ${showAll && vals.length > 12 ? `<input class="find" placeholder="Filter ${esc((LABELS[dim] || "").toLowerCase())}…">` : ""}
      <div class="vals">${list.map(v => facetValHtml(dim, v, selected.includes(v.value))).join("")}</div>
      ${distinct > list.length || showAll ? `<button class="more" data-more="${esc(dim)}">${showAll ? "Show fewer" : `Show all ${num(distinct)}`}</button>` : ""}
    </div>`;
  }
  const box = $("#facets");
  box.innerHTML = h;
  $$(".facet h4", box).forEach(h4 => h4.addEventListener("click", () => {
    const fa = h4.parentElement; fa.classList.toggle("collapsed");
    Search.facetOpen[fa.dataset.dim] = !fa.classList.contains("collapsed"); store.set("facetOpen", Search.facetOpen);
  }));
  $$(".facet .val", box).forEach(v => v.addEventListener("click", () => {
    const dim = v.closest(".facet").dataset.dim, val = v.dataset.v;
    const n = Object.assign({}, st, { filters: JSON.parse(JSON.stringify(st.filters)), page: 1, sel: null });
    const cur = n.filters[dim] || [];
    n.filters[dim] = cur.includes(val) ? cur.filter(x => x !== val) : cur.concat([val]);
    if (!n.filters[dim].length) delete n.filters[dim];
    go(Search.view === "map" ? "/map" : "/search", n);
  }));
  $$(".facet .more", box).forEach(b => b.addEventListener("click", async () => {
    const dim = b.dataset.more;
    if (Search.more[dim]) { Search.more[dim] = false; renderFacets(r, st); return; }
    Search.more[dim] = true;
    try {
      const full = await api("/search", request(st, { limit: 1, facetAll: dim }));
      r.facets[dim] = full.facets[dim]; renderFacets(r, st);
    } catch (e) { toast(e.message); }
  }));
  $$(".facet .find", box).forEach(inp => inp.addEventListener("input", () => {
    const t = inp.value.toLowerCase();
    $$(".val", inp.parentElement).forEach(v => v.classList.toggle("hidden", !v.dataset.v.toLowerCase().includes(t)));
  }));
  wireYearFacet(box, st);
}
function facetValHtml(dim, v, on) {
  let dot = "";
  if (dim === "f.commodity") dot = `<span class="dot" style="background:${el(v.value)[1]}"></span>`;
  if (dim === "f.dataset") dot = `<span class="dot" style="background:${DS_COLOR[v.value] || "var(--teal)"}"></span>`;
  return `<div class="val ${on ? "on" : ""}" data-v="${esc(v.value)}"><span class="box"></span>${dot}<span class="lbl" title="${esc(v.value)}">${esc(v.value)}</span><span class="c">${num(v.count)}</span></div>`;
}
function yearFacetHtml(vals, st) {
  const ys = vals.map(v => ({ y: +v.value, n: v.count })).filter(v => v.y > 1800).sort((a, b) => a.y - b.y);
  if (!ys.length) return "";
  const min = ys[0].y, max = ys[ys.length - 1].y, peak = Math.max(...ys.map(v => v.n));
  const by = Object.fromEntries(ys.map(v => [v.y, v.n]));
  // One bar per year, or per five years when the span is long.
  const step = max - min > 60 ? 5 : 1;
  const bars = [];
  for (let y = min - (min % step); y <= max; y += step) {
    let n = 0; for (let k = 0; k < step; k++) n += by[y + k] || 0;
    const on = (st.yf || st.yt) && y >= (st.yf || 0) && y <= (st.yt || 9999);
    bars.push(`<div class="b ${on ? "on" : ""}" data-y="${y}" data-step="${step}" style="height:${n ? Math.max(6, n / (peak * step) * 100) : 0}%" title="${step > 1 ? y + "–" + (y + step - 1) : y}: ${num(n)}"></div>`);
  }
  return `<div class="facet" data-dim="f.year"><h4>Year <span class="n">${min}–${max}</span></h4>
    <div class="years">${bars.join("")}</div><div class="years-axis"><span>${min}</span><span>${max}</span></div>
    <div class="year-range"><input type="number" id="yf" placeholder="${min}" value="${st.yf || ""}"> to <input type="number" id="yt" placeholder="${max}" value="${st.yt || ""}"><button class="btn small" id="ygo">Go</button></div></div>`;
}
function wireYearFacet(box, st) {
  const path = Search.view === "map" ? "/map" : "/search";
  $$(".years .b", box).forEach(b => b.addEventListener("click", () => {
    const y = +b.dataset.y, step = +b.dataset.step;
    go(path, Object.assign({}, st, { yf: y, yt: y + step - 1, page: 1, sel: null }));
  }));
  const goBtn = $("#ygo", box);
  if (goBtn) {
    const apply = () => go(path, Object.assign({}, st, { yf: +$("#yf", box).value || null, yt: +$("#yt", box).value || null, page: 1, sel: null }));
    goBtn.addEventListener("click", apply);
    $$("#yf,#yt", box).forEach(i => i.addEventListener("keydown", e => { if (e.key === "Enter") apply(); }));
  }
}

// ---------------------------------------------------------------- preview pane
const Preview = {
  id: null, data: null, page: 1, q: "", tab: "overview", mini: null,
  async open(id, q, host, page) {
    this.host = host; this.q = q || "";
    host.classList.remove("hidden");
    host.parentElement.classList.add("preview-open");
    if (this.id !== id || !this.data) {
      this.id = id; this.data = null;
      host.innerHTML = `<div class="ph"><div class="skeleton" style="width:40%;height:12px;margin-bottom:10px"></div><div class="skeleton" style="width:85%;height:22px"></div></div>`;
      try { this.data = await api("/items/" + id); }
      catch (e) { host.innerHTML = `<div class="ph"><button class="icon-btn close" data-act="close-preview">${I.x}</button><h2>Not available</h2><p class="muted">${esc(e.message)}</p></div>`; return; }
      if (this.id !== id) return;
      this.tab = (page && page !== true) || this.q ? "text" : "overview";
    }
    if (typeof page === "number" && page > 0) { this.page = page; this.tab = "text"; }
    else if (page === true || page === false) this.page = this.firstPage();
    this.render();
  },
  firstPage() {
    const g = (Search.last?.groups || []).flatMap(g => [g.top].concat(g.more || [])).find(h => h.id === this.id);
    return g?.page || 1;
  },
  close(silent) {
    this.id = null; this.data = null;
    if (this.mini) { this.mini.remove(); this.mini = null; }
    const host = this.host || $("#preview");
    if (host) { host.classList.add("hidden"); host.innerHTML = ""; host.parentElement?.classList.remove("preview-open"); }
    $$(".result.sel, .map-list .item.sel").forEach(r => r.classList.remove("sel"));
    if (!silent) {
      const st = readState(); st.sel = null; history.replaceState(null, "", buildUrl(st.path, st));
      if (Search.st) Search.st.sel = null;
      if (MapView.map) MapView.clearSelection();
    }
  },
  render() {
    const d = this.data, c = d.card || {}, host = this.host;
    const isPdf = (c.ext || "").toLowerCase() === ".pdf";
    const files = d.files || [];
    host.innerHTML = `
      <div class="ph">
        <button class="icon-btn close" data-act="close-preview" title="Close (Esc)">${I.x}</button>
        <div class="crumb"><span class="ds" style="color:${DS_COLOR[c.ds] || "var(--teal)"}">${esc(c.ds || "")}</span>${c.coll ? `<span>${esc(c.coll)}</span>` : ""}${c.year ? `<span>· ${c.year}</span>` : ""}</div>
        <h2>${esc(c.title || c.name)}</h2>
        <div class="actions">
          <a class="btn primary small" href="/api/v1/items/${d.id}/file${isPdf ? "#page=" + this.page : ""}" target="_blank">${I.file} Open ${esc(ft(c.ftype, c.ext)[0])}${isPdf && this.page > 1 ? " at p." + this.page : ""}</a>
          ${c.landing ? `<a class="btn small" href="${esc(c.landing)}" target="_blank" rel="noopener">${I.ext} Source page</a>` : ""}
          ${c.loc ? `<button class="btn small" data-copy="${esc(c.loc)}" title="${esc(c.loc)}">${I.copy} Copy folder path</button>` : ""}
          ${c.lat != null && MapView.map == null ? `<a class="btn small" href="${buildUrl("/map", Object.assign({}, readState(), { sel: d.id, page: 1 }))}" data-nav>${I.map} On map</a>` : ""}
        </div>
      </div>
      <div class="pb">
        <div class="ptabs">
          <button data-tab="overview" class="${this.tab === "overview" ? "on" : ""}">Overview</button>
          ${c.pages ? `<button data-tab="text" class="${this.tab === "text" ? "on" : ""}">Text</button>` : ""}
          ${isPdf ? `<button data-tab="pdf" class="${this.tab === "pdf" ? "on" : ""}">PDF</button>` : ""}
          ${files.length > 1 ? `<button data-tab="files" class="${this.tab === "files" ? "on" : ""}">Files (${files.length})</button>` : ""}
        </div>
        <div id="ptab"></div>
      </div>`;
    $$(".ptabs button", host).forEach(b => b.addEventListener("click", () => { this.tab = b.dataset.tab; this.render(); }));
    $$("[data-copy]", host).forEach(b => b.addEventListener("click", () => copyText(b.dataset.copy)));
    const pane = $("#ptab", host);
    if (this.mini) { this.mini.remove(); this.mini = null; }
    if (this.tab === "overview") this.renderOverview(pane);
    else if (this.tab === "text") this.renderText(pane);
    else if (this.tab === "pdf") pane.innerHTML = `<iframe class="pdf-frame" style="height:calc(100vh - 290px)" src="/api/v1/items/${d.id}/file#page=${this.page}"></iframe>`;
    else if (this.tab === "files") this.renderFiles(pane);
  },
  renderOverview(pane) {
    const d = this.data, c = d.card || {};
    const row = (k, v) => v ? `<dt>${k}</dt><dd>${v}</dd>` : "";
    const pills = (arr, cls) => (arr || []).map(x => `<span class="pill ${cls || ""}">${esc(x)}</span>`).join("");
    const comm = (c.commodity || []).map(m => { const [s, col] = el(m); return `<span class="pill el" style="--el:${col}"><i>${esc(s)}</i>${esc(m)}</span>`; }).join("");
    const extra = (c.fields || []).filter(f => f.show !== false).map(f => row(esc(f.label || f.name), esc(Array.isArray(f.value) ? f.value.join(", ") : f.value))).join("");
    pane.innerHTML = `<div class="pane">
      ${c.summary ? `<p style="margin:0 0 14px;color:var(--ink-2)">${esc(c.summary)}</p>` : ""}
      <dl class="meta-grid">
        ${row("Report", c.coll ? esc(c.coll) + (c.ds ? " · " + esc(c.ds) : "") : "")}
        ${row("Company", esc((c.company || []).join(", ")))}
        ${row("Company now", c.owner ? esc(c.owner) + (c.asx ? ` <span class="pill">ASX: ${esc(c.asx)}</span>` : "") + (c.cstatus ? ` <span class="muted">${esc(c.cstatus)}</span>` : "") : "")}
        ${row("Year", c.year)}
        ${row("Type", esc(c.rtype))}
        ${row("Commodities", comm)}
        ${row("Tenements", pills(c.ten, "ten"))}
        ${row("Rock types", pills(c.lith))}
        ${row("Methods", pills(c.method))}
        ${row("State", esc(c.state))}
        ${extra}
        ${row("File", `${esc(c.name)} <span class="muted">· ${bytes(c.size)}${c.pages ? " · " + plural(c.pages, "page") : ""}${c.ocr ? " · OCR" : ""}</span>`)}
      </dl>
      ${c.lat != null || d.footprint ? `<div class="mini-map" id="minimap"></div><div class="loc-note">${esc(locBasis(c))}${c.raw ? ` · <span class="mono">${esc(c.raw)}</span>` : ""}</div>` : `<p class="loc-note" style="margin-top:14px">${I.pin.replace("<svg", '<svg width="12" height="12" style="vertical-align:-1px"')} No location found for this report yet.</p>`}
      ${c.attr || c.licence ? `<div class="attr">${c.licence ? `<b>${esc(c.licence)}</b> · ` : ""}${esc(c.attr || "")}</div>` : ""}
    </div>`;
    if (c.lat != null || d.footprint) setTimeout(() => this.drawMini(), 0);
  },
  drawMini() {
    const box = $("#minimap"); if (!box || !window.L) return;
    const c = this.data.card;
    const m = L.map(box, { zoomControl: true, attributionControl: false, scrollWheelZoom: false });
    this.mini = m;
    Basemap.add(m, true);
    const layers = [];
    if (this.data.footprint) layers.push(L.geoJSON(this.data.footprint, { style: { color: "#b5622a", weight: 2, fillOpacity: .15 } }).addTo(m));
    else if (c.bbox && (c.bbox[2] - c.bbox[0] > 0.01 || c.bbox[3] - c.bbox[1] > 0.01)) layers.push(L.rectangle([[c.bbox[1], c.bbox[0]], [c.bbox[3], c.bbox[2]]], { color: "#b5622a", weight: 2, fillOpacity: .12, dashArray: c.geo === "map sheet" ? "5 4" : null }).addTo(m));
    if (c.lat != null) layers.push(L.circleMarker([c.lat, c.lon], { radius: 6, color: "#fff", weight: 2, fillColor: "#b5622a", fillOpacity: 1 }).addTo(m));
    const g = L.featureGroup(layers);
    const b = g.getBounds();
    m.fitBounds(b.pad(0.6), { maxZoom: 9 });
    if (m.getZoom() > 9) m.setZoom(9);
    // Zoomed right in on a point, show some context.
    if (b.getNorthEast().equals(b.getSouthWest())) m.setView(b.getCenter(), 6);
  },
  async renderText(pane) {
    const d = this.data, c = d.card;
    pane.innerHTML = `<div class="pagebar"><button class="icon-btn" id="pprev" title="Previous page">${I.chevL}</button><span id="pnum">Page ${this.page} of ${c.pages || "?"}</span><button class="icon-btn" id="pnext" title="Next page">${I.chevR}</button><span class="sp"></span><span class="hits" id="phits"></span></div><div class="pagetext" id="ptext"><div class="skeleton" style="height:14px;width:90%;margin-bottom:8px"></div><div class="skeleton" style="height:14px;width:80%"></div></div>`;
    const load = async (n) => {
      this.page = n;
      let p;
      try { p = await api(`/items/${d.id}/pages/${n}?q=${encodeURIComponent(this.q)}`); } catch (e) { $("#ptext").textContent = e.message; return; }
      if (!$("#ptext")) return;
      $("#ptext").innerHTML = p.html || `<span class="muted">No text on this page${c.ocr === 0 && (c.ext || "") === ".pdf" ? " (it may be a scanned image; OCR runs on the Windows server)" : ""}.</span>`;
      $("#pnum").textContent = `Page ${p.page} of ${p.pages}`;
      $("#pprev").disabled = p.page <= 1; $("#pnext").disabled = p.page >= p.pages;
      const hits = p.matches || [];
      $("#phits").innerHTML = hits.length ? `<span class="muted" style="align-self:center;margin-right:4px">Matches on</span>` + hits.slice(0, 30).map(h => `<button data-pg="${h}" class="${h === p.page ? "on" : ""}">${h}</button>`).join("") : "";
      $$("#phits button").forEach(b => b.addEventListener("click", () => load(+b.dataset.pg)));
      // Scroll the preview (not the page) to the first match.
      const pb = $(".preview .pb"), first = $("#ptext mark");
      if (pb) pb.scrollTop = first ? Math.max(0, pb.scrollTop + first.getBoundingClientRect().top - pb.getBoundingClientRect().top - pb.clientHeight / 3) : 0;
      const open = $(".preview .actions a.primary");
      if (open && (c.ext || "") === ".pdf") { open.href = `/api/v1/items/${d.id}/file#page=${p.page}`; open.innerHTML = `${I.file} Open PDF${p.page > 1 ? " at p." + p.page : ""}`; }
    };
    $("#pprev").onclick = () => load(Math.max(1, this.page - 1));
    $("#pnext").onclick = () => load(this.page + 1);
    load(this.page || 1);
  },
  renderFiles(pane) {
    const d = this.data;
    pane.innerHTML = `<div class="pane"><div class="file-list">${(d.files || []).map(f => { const c = f.card || {}; const [t, col] = ft(c.ftype, c.ext); return `<div class="f ${f.id === d.id ? "on" : ""}" data-f="${f.id}"><span class="ic" style="background:${col}">${esc(t)}</span><span class="nm" title="${esc(c.name)}">${esc(c.name)}</span><span class="sz">${c.pages ? plural(c.pages, "p", "pp") + " · " : ""}${bytes(c.size)}</span></div>`; }).join("")}</div></div>`;
    $$(".file-list .f", pane).forEach(f => f.addEventListener("click", () => { this.data = null; this.open(+f.dataset.f, this.q, this.host, true); }));
  },
};
function copyText(t) {
  (navigator.clipboard ? navigator.clipboard.writeText(t) : Promise.reject()).then(() => toast("Copied: " + t), () => {
    const a = document.createElement("textarea"); a.value = t; document.body.appendChild(a); a.select(); try { document.execCommand("copy"); toast("Copied: " + t); } catch (e) { } a.remove();
  });
}

// ---------------------------------------------------------------- basemaps
const Basemap = {
  outline: null, mode: store.get("basemap", "streets"), online: null,
  // Approximate state borders (straight-line legal boundaries; NSW/QLD and NSW/VIC simplified).
  states: [
    [[-14.9, 129], [-31.7, 129]], [[-26, 129], [-26, 141]], [[-16.0, 138], [-26, 138]], [[-26, 141], [-29, 141]],
    [[-29, 141], [-29, 148.9], [-28.6, 150.5], [-28.6, 151.5], [-28.2, 152.5], [-28.17, 153.55]],
    [[-29, 141], [-38.06, 141]], [[-34.0, 141], [-34.6, 143.3], [-35.6, 144.7], [-36.1, 147.2], [-36.8, 148.1], [-37.5, 149.98]],
  ],
  async checkOnline() {
    if (this.online != null) return this.online;
    this.online = await new Promise(res => {
      const img = new Image(); const t = setTimeout(() => res(false), 3500);
      img.onload = () => { clearTimeout(t); res(true); }; img.onerror = () => { clearTimeout(t); res(false); };
      img.src = "https://a.basemaps.cartocdn.com/rastertiles/voyager/3/7/4.png?" + Date.now();
    });
    return this.online;
  },
  async loadOutline() {
    if (this.outline) return this.outline;
    try { this.outline = await (await fetch("/lib/geo/australia.json")).json(); } catch (e) { this.outline = { type: "FeatureCollection", features: [] }; }
    return this.outline;
  },
  add(map, small) {
    const dark = document.documentElement.getAttribute("data-theme") === "dark";
    const group = L.layerGroup().addTo(map);
    map._siloBase = group;
    const drawOutline = () => this.loadOutline().then(o => {
      L.geoJSON(o, { interactive: false, style: { color: dark ? "#5a6673" : "#a59d8c", weight: 1.2, fillColor: dark ? "#1f262e" : "#fbfaf6", fillOpacity: 1 } }).addTo(group);
      for (const s of this.states) L.polyline(s, { interactive: false, color: dark ? "#3c4652" : "#cfc7b6", weight: 1, dashArray: "4 4" }).addTo(group);
    });
    const mode = small ? (this.mode === "outline" ? "outline" : this.mode) : this.mode;
    if (mode === "outline" || this.online === false) { drawOutline(); return "outline"; }
    // Streets: CARTO's OpenStreetMap basemaps (free with attribution, light and dark styles; OSM's own
    // tile servers refuse apps). Topo and satellite: Esri.
    const src = {
      streets: [`https://{s}.basemaps.cartocdn.com/${dark ? "dark_all" : "rastertiles/voyager"}/{z}/{x}/{y}{r}.png`, "© OpenStreetMap contributors © CARTO", { subdomains: "abcd", maxZoom: 19 }],
      topo: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}", "Tiles © Esri, HERE, Garmin, USGS, OpenStreetMap contributors", { maxZoom: 18 }],
      satellite: ["https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}", "Imagery © Esri, Maxar, Earthstar Geographics", { maxZoom: 18 }],
    }[mode] || null;
    if (!src) { drawOutline(); return "outline"; }
    drawOutline();
    const tl = L.tileLayer(src[0], Object.assign({ attribution: src[1], referrerPolicy: "strict-origin-when-cross-origin" }, src[2]));
    let loaded = 0, failed = 0;
    tl.on("tileload", () => loaded++);
    tl.on("tileerror", () => { failed++; if (failed > 4 && loaded === 0) { this.online = false; group.removeLayer(tl); if (!small) MapView.showOffline(); } });
    tl.addTo(group);
    return mode;
  },
};
// Dark streets: invert the light OSM tiles so the map sits in the dark theme.
const darkTileCss = document.createElement("style");
darkTileCss.textContent = `.dark-tiles { filter: invert(1) hue-rotate(180deg) brightness(.92) contrast(.88) saturate(.6); }`;
document.head.appendChild(darkTileCss);

// ---------------------------------------------------------------- map view
const MapView = {
  map: null, st: null, layer: null, sel: null, rectLayer: null, drawing: false, list: [],
  async render(st) {
    const app = $("#app");
    this.st = st; Search.st = st;
    document.title = (st.q ? st.q + " — " : "") + "Map — Silo";
    if (!this.map || Search.view !== "map") {
      if (this.map) { this.map.remove(); this.map = null; }
      app.innerHTML = topbarHtml(st, "map") + `<div class="map-wrap">
        <aside class="map-list" id="maplist"><div class="head">Loading…</div></aside>
        <div class="map-main"><div id="map"></div>
          <div class="map-tools"><button class="btn hidden" id="searchArea">${I.target} Search this area</button><button class="btn" id="drawArea">${I.rect} Draw an area</button></div>
          <div class="map-side-tools"><div class="layer-switch" id="layers">
            <button data-l="streets">Streets</button><button data-l="topo">Topo</button><button data-l="satellite">Satellite</button><button data-l="outline">Outline</button></div></div>
          <div class="map-legend"><div class="r"><span class="sw" style="background:var(--accent)"></span>Report location</div><div class="r"><span class="sq"></span>Report extent</div><div class="r"><span class="sq" style="border-style:dashed"></span>Map sheet (approx.)</div></div>
        </div>
        <aside class="preview hidden" id="preview"></aside></div>`;
      Search.view = "map";
      await this.init();
    } else {
      const tmp = document.createElement("div"); tmp.innerHTML = topbarHtml(st, "map");
      $(".topbar", app).replaceWith(tmp.children[0]); $(".tabs", app).replaceWith(tmp.children[0]);
    }
    wireTopbar(app, st, "/map");
    this.refresh();
    if (st.sel) Preview.open(st.sel, st.q, $("#preview"), false);
  },
  async init() {
    const m = L.map("map", { zoomControl: false, worldCopyJump: true, minZoom: 3, preferCanvas: false });
    L.control.zoom({ position: "bottomright" }).addTo(m);
    L.control.scale({ position: "bottomright", imperial: false }).addTo(m);
    this.map = m;
    const saved = store.get("mapView", null);
    if (this.st.bbox) m.fitBounds([[this.st.bbox[1], this.st.bbox[0]], [this.st.bbox[3], this.st.bbox[2]]]);
    else if (saved) m.setView(saved.c, saved.z); else m.fitBounds([[-44, 112], [-10, 154]]);
    if (Basemap.mode !== "outline") await Basemap.checkOnline();
    this.applyTheme();
    m.on("moveend", () => {
      store.set("mapView", { c: [m.getCenter().lat, m.getCenter().lng], z: m.getZoom() });
      $("#searchArea").classList.remove("hidden");
    });
    m.on("zoomend", debounce(() => this.loadPoints(), 80));
    $("#searchArea").onclick = () => {
      const b = m.getBounds();
      go("/map", Object.assign({}, this.st, { bbox: [b.getWest(), b.getSouth(), b.getEast(), b.getNorth()], page: 1, sel: null }), true);
    };
    $("#drawArea").onclick = () => this.startDraw();
    $$("#layers button").forEach(b => b.addEventListener("click", () => { Basemap.mode = b.dataset.l; store.set("basemap", b.dataset.l); if (b.dataset.l !== "outline") Basemap.online = null; this.applyTheme(); }));
  },
  async applyTheme() {
    const m = this.map; if (!m) return;
    if (m._siloBase) m.removeLayer(m._siloBase);
    if (Basemap.mode !== "outline") await Basemap.checkOnline();
    const used = Basemap.add(m);
    if (m._siloBase) m._siloBase.eachLayer(l => l.bringToBack && l.bringToBack());
    $$("#layers button").forEach(b => b.classList.toggle("on", b.dataset.l === used));
    $(".offline-note")?.remove();
    if (Basemap.online === false && Basemap.mode !== "outline") this.showOffline();
  },
  showOffline() {
    if ($(".offline-note")) return;
    const n = document.createElement("div"); n.className = "offline-note"; n.textContent = "Offline: showing the built-in outline map";
    $(".map-main")?.appendChild(n);
    $$("#layers button").forEach(b => b.classList.toggle("on", b.dataset.l === "outline"));
  },
  startDraw() {
    const m = this.map; if (this.drawing) return;
    this.drawing = true; m.dragging.disable(); m.getContainer().style.cursor = "crosshair";
    const hint = document.createElement("div"); hint.className = "draw-hint"; hint.textContent = "Drag on the map to draw the area · Esc to cancel"; $(".map-main").appendChild(hint);
    let start = null, rect = null;
    const done = (bounds) => {
      this.drawing = false; m.dragging.enable(); m.getContainer().style.cursor = ""; hint.remove();
      m.off("mousedown", down); m.off("mousemove", move); m.off("mouseup", up); document.removeEventListener("keydown", key);
      if (rect) m.removeLayer(rect);
      if (bounds) go("/map", Object.assign({}, this.st, { bbox: [bounds.getWest(), bounds.getSouth(), bounds.getEast(), bounds.getNorth()], page: 1, sel: null }), true);
    };
    const down = e => { start = e.latlng; rect = L.rectangle([start, start], { color: "#b5622a", weight: 2, dashArray: "6 4", fillOpacity: .08 }).addTo(m); };
    const move = e => { if (start && rect) rect.setBounds([start, e.latlng]); };
    const up = e => { if (!start) return; const b = L.latLngBounds(start, e.latlng); done(b.getNorth() - b.getSouth() > 0.01 ? b : null); };
    const key = e => { if (e.key === "Escape") done(null); };
    m.on("mousedown", down); m.on("mousemove", move); m.on("mouseup", up); document.addEventListener("keydown", key);
  },
  async refresh() {
    const st = this.st, m = this.map;
    if (this.rectLayer) { m.removeLayer(this.rectLayer); this.rectLayer = null; }
    if (st.bbox) this.rectLayer = L.rectangle([[st.bbox[1], st.bbox[0]], [st.bbox[3], st.bbox[2]]], { color: "#2f7a78", weight: 2, dashArray: "6 4", fill: false, interactive: false }).addTo(m);
    $("#searchArea").classList.add("hidden");
    this.loadPoints();
    // The list: reports in the area (or everything matching), located ones first.
    $("#maplist").innerHTML = `<div class="head">Loading…</div>`;
    let r;
    try { r = await api("/search", request(st, { limit: 60, offset: 0 })); } catch (e) { $("#maplist").innerHTML = `<div class="head">${esc(e.message)}</div>`; return; }
    if (this.st !== st) return;
    Search.last = r;
    const groups = r.groups || [];
    const located = groups.filter(g => g.top.card?.lat != null), other = groups.filter(g => g.top.card?.lat == null);
    this.list = groups;
    $("#maplist").innerHTML = `<div class="head"><b>${plural(r.reports || r.total, "report")}</b>${st.bbox ? " in this area" : ""}${(r.chips || []).length || Object.keys(st.filters).length ? ` · <a href="#" id="mapclear">clear filters</a>` : ""}</div>
      ${chipsHtml(r, st) ? `<div style="padding:10px 16px 0">${chipsHtml(r, st)}</div>` : ""}
      ${located.map(g => this.itemHtml(g)).join("")}
      ${other.length ? `<div class="nl">${plural(other.length, "report")} below ${other.length === 1 ? "has" : "have"} no location yet</div>${other.map(g => this.itemHtml(g)).join("")}` : ""}
      ${(r.reports || r.total) > groups.length ? `<div class="nl">Showing the first ${groups.length}. Narrow the search or the area to see the rest.</div>` : ""}`;
    wireChips($("#maplist"), r, st, "/map");
    $("#mapclear")?.addEventListener("click", e => { e.preventDefault(); go("/map", Object.assign({}, st, { q: "", filters: {}, yf: null, yt: null, page: 1, sel: null })); });
    $$("#maplist .item").forEach(it => it.addEventListener("click", () => this.select(+it.dataset.id, true)));
  },
  itemHtml(g) {
    const c = g.top.card || {};
    const comm = (c.commodity || []).slice(0, 3).map(m => { const [s, col] = el(m); return `<span class="pill el" style="--el:${col}"><i>${esc(s)}</i>${esc(m)}</span>`; }).join("");
    return `<div class="item ${Preview.id === g.top.id ? "sel" : ""}" data-id="${g.top.id}" data-key="${esc(g.key)}">
      <div class="t">${esc(c.title || c.name)}</div>
      <div class="s"><b style="color:${DS_COLOR[c.ds] || "var(--teal)"}">${esc(c.ds || "")}</b> ${esc(c.coll || "")}${c.year ? " · " + c.year : ""}${c.lat != null ? ` · <span style="color:var(--teal)">${I.pin.replace("<svg", '<svg width="11" height="11" style="vertical-align:-1px"')}</span>` : ""} ${comm}</div>
    </div>`;
  },
  async loadPoints() {
    const m = this.map, st = this.st; if (!m) return;
    let r;
    try { r = await api("/map?zoom=" + m.getZoom(), request(st, { limit: 1 })); } catch (e) { return; }
    if (this.st !== st) return;
    if (this.layer) m.removeLayer(this.layer);
    const g = L.layerGroup();
    for (const [lat, lon, n, key, id, bbox] of r.points) {
      const size = n === 1 ? 18 : Math.min(54, 24 + Math.log2(n) * 6);
      const icon = L.divIcon({ className: "", html: `<div class="cluster ${n === 1 ? "one" : ""} ${id && id === Preview.id ? "sel" : ""}" style="width:${size}px;height:${size}px">${n > 1 ? (n > 999 ? Math.round(n / 1000) + "k" : n) : ""}</div>`, iconSize: [size, size], iconAnchor: [size / 2, size / 2] });
      const mk = L.marker([lat, lon], { icon, keyboard: false, riseOnHover: true });
      if (n === 1 && id) {
        mk.on("click", () => this.select(id, false));
        const g2 = this.list.find(x => x.top.id === id);
        const t = g2?.top.card?.title;
        mk.bindTooltip(esc(t || key || ""), { direction: "top", offset: [0, -8] });
      } else {
        mk.on("click", () => {
          if (bbox) m.fitBounds([[bbox[1], bbox[0]], [bbox[3], bbox[2]]], { padding: [40, 40], maxZoom: m.getZoom() + 3 });
          else m.setView([lat, lon], m.getZoom() + 2);
        });
        mk.bindTooltip(`${plural(n, "report")} · click to zoom in`, { direction: "top", offset: [0, -size / 2] });
      }
      g.addLayer(mk);
    }
    this.layer = g.addTo(m);
    const meta = $("#tabmeta");
    if (meta) meta.innerHTML = `${plural(r.located, "report")} on the map${r.total > r.located ? ` · ${num(r.total - r.located)} without a location` : ""}`;
  },
  async select(id, fly) {
    const st = Object.assign({}, this.st, { sel: id });
    history.replaceState(null, "", buildUrl("/map", st)); this.st.sel = id;
    $$("#maplist .item").forEach(i => i.classList.toggle("sel", +i.dataset.id === id));
    $(`#maplist .item[data-id="${id}"]`)?.scrollIntoView({ block: "nearest" });
    await Preview.open(id, this.st.q, $("#preview"), true);
    setTimeout(() => this.map.invalidateSize(), 50);
    this.drawSelection(fly);
    this.loadPoints();
  },
  drawSelection(fly) {
    const m = this.map, d = Preview.data; if (!d) return;
    if (this.sel) { m.removeLayer(this.sel); this.sel = null; }
    const c = d.card || {}, layers = [];
    if (d.footprint) layers.push(L.geoJSON(d.footprint, { style: { color: "#2f7a78", weight: 2, fillOpacity: .15 } }));
    else if (c.bbox && (c.bbox[2] - c.bbox[0] > 0.01 || c.bbox[3] - c.bbox[1] > 0.01)) layers.push(L.rectangle([[c.bbox[1], c.bbox[0]], [c.bbox[3], c.bbox[2]]], { color: "#2f7a78", weight: 2, fillOpacity: .1, dashArray: c.geo === "map sheet" ? "6 4" : null }));
    if (!layers.length) return;
    this.sel = L.featureGroup(layers).addTo(m);
    if (fly) m.flyToBounds(this.sel.getBounds().pad(0.8), { maxZoom: c.geo === "map sheet" ? 7 : 9, duration: .6 });
  },
  clearSelection() { if (this.sel) { this.map.removeLayer(this.sel); this.sel = null; } setTimeout(() => this.map && this.map.invalidateSize(), 50); this.loadPoints(); },
};

// ---------------------------------------------------------------- admin page
const Admin = { timer: null, logTimer: null };
async function renderAdmin() {
  const app = $("#app");
  document.title = "Server — Silo";
  Search.view = "admin";
  app.innerHTML = `<header class="topbar">${LOGO(32)}<div style="font-weight:600;color:var(--ink-2)">Server</div><div class="right"><a class="btn ghost" href="/" data-nav>${I.search} Search</a>${themeToggleHtml()}</div></header>
  <div class="admin">
    <h1>Silo Server</h1><p class="lead" id="lead">Loading…</p>
    <div class="cards" id="cards"></div>
    <div class="panel"><h2>Progress <span class="sp"></span><span class="muted" id="activity" style="font-weight:400;font-size:13px"></span></h2><div class="body"><div class="progress" id="prog"></div><div class="legend-row" id="progLegend"></div></div></div>
    <div class="panel"><h2>Actions</h2><div class="body" style="display:flex;gap:8px;flex-wrap:wrap">
      <button class="btn" data-a="scan">Rescan sources</button><button class="btn" data-a="pause" id="pauseBtn">Pause</button>
      <button class="btn" data-a="retry">Retry failed files</button><button class="btn" data-a="rules">Reload rules</button><button class="btn danger" data-a="reindex">Rebuild index</button></div></div>
    <div class="panel"><h2>Sources <span class="sp"></span><button class="btn small" id="addSrcBtn">Add a folder</button></h2>
      <div class="body hidden" id="addSrc"><div class="form-row">
        <label>Name<input id="srcName" placeholder="Perth projects share"></label>
        <label>Folder (as the server sees it)<input id="srcRoot" placeholder="\\\\fileserver\\projects or D:\\Data" style="min-width:320px"></label>
        <label>Access<select id="srcAccess"><option value="ntfs">Windows permissions (NTFS)</option><option value="public">Everyone who can use Silo</option></select></label>
        <button class="btn primary" id="srcSave">Add and scan</button></div></div>
      <table class="t" id="sources"></table></div>
    <div class="two-col">
      <div class="panel"><h2>File types</h2><table class="t" id="types"></table></div>
      <div class="panel"><h2>Recent problems</h2><table class="t" id="errors"></table></div>
    </div>
    <div class="panel"><h2>Plugins <span class="sp"></span><span class="muted" style="font-weight:400;font-size:12.5px">contract silo.plugin/1 · out of process</span></h2><table class="t" id="plugins"></table></div>
    <div class="panel"><h2>Log <span class="sp"></span><label class="muted" style="font-weight:400;font-size:13px"><input type="checkbox" id="logFollow" checked> follow</label></h2><div class="log" id="log"></div></div>
    <div class="panel"><h2>Settings</h2><div class="body"><div class="form-row">
      <label>OCR scanned pages<select id="setOcr"><option value="true">On</option><option value="false">Off</option></select></label>
      <label>Look up report locations online<select id="setEnrich"><option value="true">On</option><option value="false">Off</option></select></label>
      <label>Extraction workers<input id="setEw" type="number" min="1" max="64" style="min-width:90px"></label>
      <label>OCR workers<input id="setOw" type="number" min="1" max="32" style="min-width:90px"></label>
      <label>Sign-in<select id="setAuth"><option value="anonymous">None (public data)</option><option value="windows">Windows (domain accounts)</option></select></label>
      <label>Listen on<input id="setListen" style="min-width:260px"></label>
      <button class="btn primary" id="setSave">Save</button></div><p class="muted" style="font-size:12.5px;margin:10px 0 0">Worker counts, sign-in and listen addresses apply after the service restarts. Data folder: <span class="mono" id="dataDir"></span></p></div></div>
  </div>`;
  $$("[data-a]", app).forEach(b => b.addEventListener("click", () => adminAction(b.dataset.a)));
  $("#addSrcBtn").onclick = () => $("#addSrc").classList.toggle("hidden");
  $("#srcSave").onclick = async () => {
    try {
      await api("/admin/sources", { name: $("#srcName").value, plugin: "silo.source.folder", enabled: true, config: { root: $("#srcRoot").value, access: $("#srcAccess").value, sidecars: true } });
      toast("Source added: scanning"); $("#addSrc").classList.add("hidden"); adminStatus();
    } catch (e) { toast(e.message, 5000); }
  };
  $("#setSave").onclick = async () => {
    try {
      const r = await api("/admin/settings", { ocr: $("#setOcr").value === "true", enrich: $("#setEnrich").value === "true", extractWorkers: +$("#setEw").value, ocrWorkers: +$("#setOw").value, auth: $("#setAuth").value, listen: $("#setListen").value.split(/[;,\s]+/).filter(Boolean) });
      toast(r.note || "Saved", 4000);
    } catch (e) { toast(e.message, 5000); }
  };
  try {
    const s = await api("/admin/settings");
    $("#setOcr").value = String(s.ocr); $("#setEnrich").value = String(s.enrich); $("#setEw").value = s.extractWorkers || ""; $("#setOw").value = s.ocrWorkers || "";
    $("#setEw").placeholder = "auto"; $("#setOw").placeholder = "auto";
    $("#setAuth").value = s.auth || "anonymous"; $("#setListen").value = (s.listen || []).join(", ");
  } catch (e) {
    $("#lead").innerHTML = `<span style="color:var(--bad)">${esc(e.message)}</span>`; return;
  }
  adminStatus(); adminPlugins(); adminLog();
  clearInterval(Admin.timer); clearInterval(Admin.logTimer);
  Admin.timer = setInterval(() => { if (Search.view === "admin") adminStatus(); else clearInterval(Admin.timer); }, 3000);
  Admin.logTimer = setInterval(() => { if (Search.view === "admin") adminLog(); else clearInterval(Admin.logTimer); }, 4000);
}
async function adminAction(a) {
  try {
    if (a === "scan") await api("/admin/scan", {});
    if (a === "pause") { const p = $("#pauseBtn").dataset.paused === "1"; await api("/admin/pause?on=" + !p, {}); }
    if (a === "retry") { const r = await api("/admin/retry-failed", {}); toast(`Retrying ${plural(r.n, "file")}`); }
    if (a === "rules") { const r = await api("/admin/rules/reload", {}); toast(r.changed ? "Rules changed: reindexing" : "Rules unchanged"); }
    if (a === "reindex") { if (!confirm("Rebuild the search index from the catalog? Search keeps working while it rebuilds.")) return; await api("/admin/reindex", {}); toast("Rebuilding the index"); }
    if (a === "scan") toast("Scanning sources");
    adminStatus();
  } catch (e) { toast(e.message, 5000); }
}
async function adminStatus() {
  let s; try { s = await api("/admin/status"); } catch (e) { $("#lead").innerHTML = `<span style="color:var(--bad)">${esc(e.message)}</span>`; return; }
  const up = new Date(s.started);
  $("#lead").innerHTML = `Version ${esc(s.version)} on <b>${esc(s.machine)}</b> · ${s.cores} cores · ${num(s.memoryMb)} MB in use · running since ${up.toLocaleString()} · OCR ${esc(s.ocr)}`;
  $("#dataDir").textContent = s.dataDir;
  const sec = s.security || {};
  $("#lead").innerHTML += `<br>Runs as <b${sec.localSystem ? ' style="color:var(--bad)"' : ""}>${esc(sec.runAs || "?")}</b>${sec.localSystem ? " (run the installer again to give it its own account)" : ""} · HTTPS ${esc(sec.https || "off")} · sign-in ${esc(sec.auth || "?")} · plugins ${esc(sec.plugins || "?")}`;
  const card = (k, v, sub) => `<div class="card"><div class="k">${k}</div><div class="v">${v}</div>${sub ? `<div class="s">${sub}</div>` : ""}</div>`;
  $("#cards").innerHTML = card("Files", num(s.items), `${num(s.done)} read · ${num(s.metadataOnly)} name only`)
    + card("Searchable", num(s.indexDocs), `${num(s.indexPages)} pages indexed`)
    + card("Waiting", num(s.pending + s.needsOcr), `${num(s.pending)} to read · ${num(s.needsOcr)} need OCR`)
    + card("Failed", num(s.failed), s.failed ? "see Recent problems" : "none")
    + card("Index size", bytes(s.indexBytes), `catalog ${bytes(s.catalogBytes)}`)
    + card("Speed", num(s.rates.extractPerMin) + "<span style='font-size:14px;color:var(--ink-3)'>/min</span>", `${num(s.rates.ocrPagesPerMin)} OCR pages/min`)
    + card("Online lookups", num(s.totals.reportsLocated), `of ${num(s.totals.reportsEnriched)} reports checked`);
  const tot = Math.max(1, s.items);
  const seg = [[s.done, "var(--good)", "Read"], [s.metadataOnly, "var(--teal)", "Name and metadata only"], [s.needsOcr, "var(--ochre)", "Waiting for OCR"], [s.pending, "var(--line-2)", "Waiting to read"], [s.failed, "var(--bad)", "Failed"]];
  $("#prog").innerHTML = seg.map(([n, c]) => `<span style="width:${n / tot * 100}%;background:${c}"></span>`).join("");
  $("#progLegend").innerHTML = seg.map(([n, c, l]) => `<span><i style="background:${c}"></i>${l} ${num(n)}</span>`).join("");
  const act = Object.values(s.activity || {});
  $("#activity").textContent = s.paused ? "Paused" : act.length ? act.join(" · ") : s.dirty ? `Indexing ${num(s.dirty)}…` : "Idle";
  const pb = $("#pauseBtn"); pb.textContent = s.paused ? "Resume" : "Pause"; pb.dataset.paused = s.paused ? "1" : "0";
  $("#sources").innerHTML = `<tr><th>Source</th><th>Folder</th><th>Access</th><th class="num">Files</th><th>Last scan</th><th>Status</th><th></th></tr>` + s.sources.map(x => {
    const bad = (x.status || "").startsWith("error");
    return `<tr><td><b>${esc(x.name)}</b><div class="muted mono" style="font-size:11.5px">${esc(x.id)}</div></td><td class="mono" style="font-size:12.5px">${esc(x.config?.root || "")}</td><td>${x.config?.access === "public" ? "Everyone" : "NTFS"}</td><td class="num">${num(x.count)}</td><td>${x.lastScan ? new Date(x.lastScan).toLocaleString() : "never"}</td><td><span class="status-dot ${bad ? "bad" : ""}"></span>${esc(x.status || "")}</td>
      <td style="white-space:nowrap"><button class="btn small" data-scan="${esc(x.id)}">Rescan</button> <button class="btn small danger" data-del="${esc(x.id)}">Remove</button></td></tr>`;
  }).join("");
  $$("[data-scan]").forEach(b => b.onclick = async () => { try { await api("/admin/scan?source=" + encodeURIComponent(b.dataset.scan), {}); toast("Scanning"); } catch (e) { toast(e.message, 5000); } });
  $$("[data-del]").forEach(b => b.onclick = async () => { if (!confirm("Remove this source and its files from the index? The files themselves are not touched.")) return; try { await api("/admin/sources/" + encodeURIComponent(b.dataset.del), null, "DELETE"); adminStatus(); } catch (e) { toast(e.message, 5000); } });
  const types = (s.types || []).sort((a, b) => b.n - a.n);
  $("#types").innerHTML = `<tr><th>Type</th><th class="num">Files</th><th class="num">Size</th></tr>` + types.slice(0, 14).map(t => `<tr><td class="mono">${esc(t.ext || "(none)")}</td><td class="num">${num(t.n)}</td><td class="num">${bytes(t.bytes)}</td></tr>`).join("");
  $("#errors").innerHTML = (s.errors || []).length ? `<tr><th>File</th><th>Problem</th></tr>` + s.errors.slice(0, 12).map(e => `<tr><td style="max-width:220px;overflow-wrap:anywhere">${esc(e.name)}</td><td class="muted" style="font-size:12.5px">${esc(e.error)}</td></tr>`).join("") : `<tr><td class="muted">No problems.</td></tr>`;
}
async function adminPlugins() {
  let ps; try { ps = await api("/admin/plugins"); } catch (e) { return; }
  $("#plugins").innerHTML = `<tr><th>Plugin</th><th>Kind</th><th>Handles</th><th class="num">Calls</th><th class="num">Avg</th><th class="num">Fail / timeout</th><th>On</th></tr>` + ps.map(p => `<tr>
    <td><b>${esc(p.name)}</b> <span class="muted">${esc(p.version)}</span><div class="muted" style="font-size:12.5px">${esc(p.description || "")}</div><div class="muted mono" style="font-size:11px">${esc(p.id)}</div>${p.problem ? `<div style="color:var(--bad);font-size:12.5px">${esc(p.problem)}</div>` : ""}<div class="muted" style="font-size:11.5px${/FAILED|^off/.test(p.isolation || "") ? ";color:var(--bad)" : ""}">Sandbox: ${esc(p.isolation || "")}</div></td>
    <td>${esc(p.kind)}</td><td class="mono" style="font-size:12px;max-width:260px">${esc([...(p.extensions || []), ...(p.names || []), ...(p.datasets || []).map(d => "dataset:" + d)].join(" "))}</td>
    <td class="num">${num(p.calls)}</td><td class="num">${p.avgMs ? p.avgMs + " ms" : ""}</td><td class="num">${num(p.failures)} / ${num(p.timeouts)}</td>
    <td><button class="switch ${p.enabled ? "on" : ""}" data-plugin="${esc(p.id)}" title="${p.enabled ? "Turn off" : "Turn on"}"></button></td></tr>`).join("");
  $$("[data-plugin]").forEach(b => b.onclick = async () => {
    const on = !b.classList.contains("on");
    try { const r = await api(`/admin/plugins/${encodeURIComponent(b.dataset.plugin)}/enabled?on=${on}`, {}); b.classList.toggle("on", on); toast(r.note || "Saved", 4000); } catch (e) { toast(e.message); }
  });
}
async function adminLog() {
  let l; try { l = await api("/admin/log"); } catch (e) { return; }
  const box = $("#log"); if (!box) return;
  const lines = l.slice().reverse ? l : [];
  box.innerHTML = lines.map(x => { const lv = (x.level || x.Level || "").toLowerCase(); const t = x.time || x.Time || ""; return `<div class="${lv}">${esc(String(t).replace("T", " ").slice(0, 19))}  ${esc(lv.padEnd(5))}  ${esc(x.msg || x.message || x.Msg || "")}</div>`; }).join("");
  if ($("#logFollow")?.checked) box.scrollTop = box.scrollHeight;
}

// ---------------------------------------------------------------- help
function showHelp() {
  const back = document.createElement("div"); back.className = "dialog-back";
  const rows = [
    ["<code>gold RC drilling</code>", "Reports with all the words. Mining terms expand: RC, BIF, Au, sulphide/sulfide…"],
    ["<code>\"banded iron formation\"</code>", "The exact phrase."],
    ["<code>nickel -laterite</code>", "Leave out a word."],
    ["<code>lithium OR spodumene</code>", "Either word."],
    ["<code>E28/589</code> · <code>EPM 17533</code>", "Tenements, typed any common way. Exact tenement matches come first."],
    ["<code>A59033</code> · <code>CR089328</code>", "A report by its WAMEX or GSQ number."],
    ["<code>commodity:copper</code>", "Commodity (aliases work: <code>c:cu</code>)."],
    ["<code>type:annual</code>", "Report type: annual, surrender, drilling, geophysical, well completion…"],
    ["<code>year:1995</code> · <code>year:1990..2005</code> · <code>after:2015</code>", "Report year."],
    ["<code>company:\"Western Mining\"</code> · <code>asx:IGO</code>", "Company named on the report, or its current ASX listing."],
    ["<code>rock:komatiite</code> · <code>method:aircore</code>", "Rock types and exploration methods found in the text."],
    ["<code>source:wamex</code> · <code>state:qld</code>", "Where the report came from."],
    ["<code>filetype:spreadsheet</code> · <code>ext:dm</code>", "File kinds, including Datamine and Rocscience files."],
    ["<code>has:location</code> · <code>in:115,-32,122,-26</code>", "Reports with a location, or inside an area (west,south,east,north)."],
    ["<kbd>/</kbd> · <kbd>j</kbd> <kbd>k</kbd> · <kbd>Esc</kbd>", "Focus search · next and previous result · close the preview."],
  ];
  back.innerHTML = `<div class="dialog" role="dialog" aria-label="Search tips"><button class="icon-btn close">${I.x}</button><h2>Search tips</h2><p class="muted" style="margin:0">Type like you would in Google. These extras narrow things down, and can be combined.</p><table>${rows.map(([a, b]) => `<tr><td>${a}</td><td>${b}</td></tr>`).join("")}</table></div>`;
  document.body.appendChild(back);
  const close = () => back.remove();
  back.addEventListener("click", e => { if (e.target === back || e.target.closest(".close")) close(); });
  document.addEventListener("keydown", function k(e) { if (e.key === "Escape") { close(); document.removeEventListener("keydown", k); } });
}

// ---------------------------------------------------------------- router
function route() {
  const st = readState();
  if (st.path !== "/map" && MapView.map) { MapView.map.remove(); MapView.map = null; }
  if (st.path !== "/search" && st.path !== "/map") { Preview.id = null; Preview.data = null; }
  if (st.path === "/search") return renderSearch(st);
  if (st.path === "/map") return MapView.render(st);
  if (st.path === "/admin") return renderAdmin();
  Search.view = "home";
  return renderHome(st);
}
document.addEventListener("click", e => {
  const a = e.target.closest("a[data-nav]");
  if (a && !e.ctrlKey && !e.metaKey && !e.shiftKey && a.origin === location.origin) {
    e.preventDefault(); history.pushState(null, "", a.pathname + a.search); route(); scrollTo(0, 0); return;
  }
  const act = e.target.closest("[data-act]");
  if (act) {
    if (act.dataset.act === "theme") toggleTheme();
    if (act.dataset.act === "help") showHelp();
    if (act.dataset.act === "close-preview") Preview.close();
  }
});
document.addEventListener("keydown", e => {
  const typing = /INPUT|TEXTAREA|SELECT/.test(document.activeElement?.tagName || "");
  if (e.key === "/" && !typing) { const i = $(".searchbox input"); if (i) { e.preventDefault(); i.focus(); i.select(); } }
  else if (e.key === "Escape" && !typing && Preview.id && !$(".dialog-back")) Preview.close();
  else if ((e.key === "j" || e.key === "k") && !typing && Search.view === "search") {
    const rs = $$(".result"); if (!rs.length) return;
    let i = rs.findIndex(r => r.classList.contains("sel"));
    i = e.key === "j" ? Math.min(rs.length - 1, i + 1) : Math.max(0, i - 1);
    rs[i].scrollIntoView({ block: "nearest" }); selectResult(+rs[i].dataset.id, +rs[i].dataset.page || 0);
  }
});
window.addEventListener("popstate", route);
api("/labels").then(l => { LABELS = Object.assign(LABELS, l); }).catch(() => { });
route();
})();
