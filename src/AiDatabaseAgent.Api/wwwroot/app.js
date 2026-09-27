// AI Database Agent UI. No framework, no third-party code. Everything that comes from the model or the
// database is rendered with textContent / DOM nodes — never innerHTML — because it is untrusted.
"use strict";

const $ = (sel, root = document) => root.querySelector(sel);

const el = (tag, props = {}, ...children) => {
  const node = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null) continue;
    if (k === "class") node.className = v;
    else if (k === "text") node.textContent = v;
    else node.setAttribute(k, v);
  }
  for (const c of children) if (c !== null && c !== undefined) node.append(c);
  return node;
};

const form = $("#ask-form");
const questionBox = $("#question");
const askButton = $("#ask-button");
const output = $("#output");

let inFlight = null; // AbortController of the running request(s)

/* ------------------------------------------------------------------ status & samples */

async function loadHealth() {
  try {
    const res = await fetch("health", { cache: "no-store" });
    const body = await res.json();
    for (const [key, id] of [["database", "#status-database"], ["llm", "#status-llm"]]) {
      const check = body.checks?.[key];
      const item = $(id);
      item.classList.toggle("ok", check?.status === "Healthy");
      item.classList.toggle("down", check?.status !== "Healthy");
      item.title = check?.description ?? "unknown";
    }
    const llm = body.checks?.llm?.description ?? "";
    const models = llm.replace(/ (is|are) available\.$/, "");
    $("#model-name").textContent = models ? `model: ${models}` : "";
  } catch {
    $("#status-database").classList.add("down");
    $("#status-llm").classList.add("down");
  }
}

async function loadSamples() {
  try {
    const res = await fetch("api/examples");
    const samples = await res.json();
    const box = $("#samples");
    for (const q of samples) {
      const b = el("button", { type: "button", class: "sample", text: q });
      b.addEventListener("click", () => {
        questionBox.value = q;
        form.requestSubmit();
      });
      box.append(b);
    }
  } catch {
    /* samples are optional */
  }
}

/* ------------------------------------------------------------------ asking */

const selectedVersion = () => form.querySelector('input[name="version"]:checked').value;

function setBusy(busy) {
  askButton.textContent = busy ? "Cancel" : "Ask";
  askButton.classList.toggle("cancel", busy);
  askButton.type = busy ? "button" : "submit";
}

askButton.addEventListener("click", (e) => {
  if (inFlight) {
    e.preventDefault();
    inFlight.abort();
  }
});

questionBox.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
    e.preventDefault();
    form.requestSubmit();
  }
});

form.addEventListener("submit", async (e) => {
  e.preventDefault();
  if (inFlight) return;
  const question = questionBox.value.trim();
  if (!question) return;
  const mode = selectedVersion();
  updateUrl(question, mode);

  inFlight = new AbortController();
  setBusy(true);
  output.replaceChildren();
  output.classList.toggle("compare", mode === "compare");
  try {
    const versions = mode === "compare" ? ["V1", "V2", "V3", "V4", "V5"] : [mode];
    for (const v of versions) {
      const slot = el("div");
      output.append(slot);
      await askOne(question, v, slot, inFlight.signal);
      if (inFlight.signal.aborted) break;
    }
  } finally {
    inFlight = null;
    setBusy(false);
  }
});

async function askOne(question, version, slot, signal) {
  const started = performance.now();
  const loading = el("p", { class: "loading", text: `${version}: thinking…` });
  slot.replaceChildren(loading);
  const timer = setInterval(() => {
    loading.textContent = `${version}: thinking… ${((performance.now() - started) / 1000).toFixed(1)} s`;
  }, 100);

  try {
    const res = await fetch("api/ask", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ question, version }),
      signal,
    });
    if (!res.ok) {
      slot.replaceChildren(errorBox(await problemText(res)));
      return;
    }
    slot.replaceChildren(renderResult(await res.json()));
  } catch (err) {
    slot.replaceChildren(errorBox(err.name === "AbortError" ? "Cancelled." : "Could not reach the agent API."));
  } finally {
    clearInterval(timer);
  }
}

async function problemText(res) {
  if (res.status === 429) return "Too many questions in a short time — wait a minute and try again.";
  try {
    const p = await res.json();
    return p.detail || p.title || `Request failed (${res.status}).`;
  } catch {
    return `Request failed (${res.status}).`;
  }
}

const errorBox = (text) => el("div", { class: "error-box", role: "alert", text });

/* ------------------------------------------------------------------ rendering a result */

const OUTCOME_TEXT = {
  Answered: "Answered",
  NotAnswerable: "Not answerable",
  Rejected: "Rejected",
  Failed: "Failed",
};

function renderResult(r) {
  const node = $("#tpl-result").content.firstElementChild.cloneNode(true);
  const field = (name) => node.querySelector(`[data-field="${name}"]`);

  const badge = field("outcome");
  badge.textContent = OUTCOME_TEXT[r.outcome] ?? r.outcome;
  badge.classList.add(r.outcome.toLowerCase());
  field("version").textContent = r.version;
  field("question").textContent = r.question;

  setOrHide(field("explanation"), r.outcome === "Answered" ? r.explanation : null);
  const message = field("message");
  setOrHide(message, r.message);
  if (r.outcome === "Rejected" || r.outcome === "Failed") message.classList.add("bad");

  if (r.sql) {
    highlightSql(field("sql"), r.sql);
    node.querySelector('[data-action="copy-sql"]').addEventListener("click", (e) => copy(r.sql, e.currentTarget));
  } else {
    field("sql-block").hidden = true;
  }

  if (r.outcome === "Answered") {
    field("rowcount").textContent = `${r.rows.length.toLocaleString("en-US")} row${r.rows.length === 1 ? "" : "s"}${r.truncated ? " (truncated)" : ""}`;
    renderTable(field("table"), r.columns, r.rows);
    node.querySelector('[data-action="csv"]').addEventListener("click", () => downloadCsv(r));
  } else {
    field("table-block").hidden = true;
  }

  renderTools(field("tools-block"), field("tools-summary"), field("tools"), r.toolCalls);
  renderKnowledge(field("knowledge-block"), field("knowledge-summary"), field("knowledge"), r.knowledge);
  renderMetrics(field("metrics"), r);
  renderAttempts(field("attempts-summary"), field("attempts"), r.attempts);
  return node;
}

function setOrHide(node, text) {
  if (text) node.textContent = text;
  else node.hidden = true;
}

function renderTools(block, summary, list, calls) {
  if (!calls || calls.length === 0) {
    block.hidden = true;
    return;
  }
  const failed = calls.filter((c) => !c.succeeded).length;
  summary.textContent = `Agent tool calls · ${calls.length}${failed ? ` (${failed} failed)` : ""}`;
  for (const c of calls) {
    let args = c.arguments;
    try {
      const parsed = JSON.parse(c.arguments);
      args = Object.entries(parsed).map(([k, v]) => `${k}: ${typeof v === "string" ? v : JSON.stringify(v)}`).join(" · ");
    } catch {
      /* show raw */
    }
    list.append(el("li", { class: "attempt" },
      el("div", { class: "attempt-head" },
        el("span", { text: `#${c.number}` }),
        el("span", { class: "tool-name", text: c.name }),
        el("span", { class: c.succeeded ? "ok" : "bad", text: c.succeeded ? "ok" : "failed" }),
        el("span", { text: `${c.ms} ms` })),
      el("p", { class: "tool-args", text: args }),
      el("pre", { text: c.result })));
  }
}

const KIND_TEXT = { definition: "definition", "not-tracked": "not tracked", example: "example" };

function renderKnowledge(block, summary, list, knowledge) {
  if (!knowledge || knowledge.length === 0) {
    block.hidden = true;
    return;
  }
  summary.textContent = `Business knowledge used · ${knowledge.length}`;
  for (const k of knowledge) {
    const why = k.matchedTerm ? `term “${k.matchedTerm}”` : `similarity ${k.score.toFixed(2)}`;
    list.append(el("li", {},
      el("span", { class: `kind ${k.kind}`, text: KIND_TEXT[k.kind] ?? k.kind }),
      el("span", { class: "title", text: k.title }),
      el("span", { class: "why", text: why })));
  }
}

function renderMetrics(dl, r) {
  const tokensIn = r.attempts.reduce((n, a) => n + (a.inputTokens ?? 0), 0);
  const tokensOut = r.attempts.reduce((n, a) => n + (a.outputTokens ?? 0), 0);
  const items = [
    ["total", `${(r.timings.totalMs / 1000).toFixed(2)} s`],
    ["model", `${(r.attempts.reduce((n, a) => n + a.modelMs, 0) / 1000).toFixed(2)} s`],
    r.timings.queryMs !== null ? ["query", `${r.timings.queryMs} ms`] : null,
    r.estimate ? ["est. cost", String(r.estimate.cost)] : null,
    tokensIn ? ["tokens", `${tokensIn.toLocaleString("en-US")} in / ${tokensOut.toLocaleString("en-US")} out`] : null,
    ["model id", r.model],
  ];
  for (const item of items.filter(Boolean)) dl.append(el("div", {}, el("dt", { text: item[0] }), el("dd", { text: item[1] })));
}

function renderAttempts(summary, list, attempts) {
  const repairs = attempts.length - 1;
  summary.textContent = `Model trace · ${attempts.length} attempt${attempts.length === 1 ? "" : "s"}${repairs ? ` (${repairs} repair${repairs === 1 ? "" : "s"})` : ""}`;
  for (const a of attempts) {
    const ok = a.issues.length === 0;
    const verdict = !ok ? "not accepted" : a.sql ? "accepted" : "declined";
    const head = el("div", { class: "attempt-head" },
      el("span", { text: `#${a.number}` }),
      el("span", { class: ok ? "ok" : "bad", text: verdict }),
      el("span", { text: `${(a.modelMs / 1000).toFixed(2)} s` }),
      a.inputTokens ? el("span", { text: `${a.inputTokens} → ${a.outputTokens} tokens` }) : null);
    const issues = a.issues.length ? el("ul", {}, ...a.issues.map((i) => el("li", { text: i }))) : null;
    list.append(el("li", { class: "attempt" }, head, issues, el("pre", { text: a.modelResponse })));
  }
}

/* ------------------------------------------------------------------ table & CSV */

const NUMERIC = /^(tinyint|smallint|int|bigint|decimal|numeric|money|smallmoney|float|real)$/i;
const DECIMAL = /^(decimal|numeric|money|smallmoney|float|real)$/i;

function formatCell(value, type) {
  if (value === null || value === undefined) return { text: "NULL", cls: "null" };
  if (typeof value === "number" && NUMERIC.test(type)) {
    return {
      text: DECIMAL.test(type)
        ? value.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 4 })
        : value.toLocaleString("en-US"),
      cls: "num",
    };
  }
  if (typeof value === "boolean") return { text: value ? "true" : "false", cls: "" };
  if (typeof value === "string" && /^\d{4}-\d{2}-\d{2}T/.test(value)) {
    return { text: value.endsWith("T00:00:00") ? value.slice(0, 10) : value.replace("T", " "), cls: "" };
  }
  return { text: String(value), cls: "" };
}

function renderTable(table, columns, rows) {
  const thead = el("thead", {}, el("tr", {}, ...columns.map((c) => el("th", { scope: "col" }, c.name, el("small", { text: c.type })))));
  const tbody = el("tbody");
  for (const row of rows) {
    tbody.append(el("tr", {}, ...row.map((v, i) => {
      const f = formatCell(v, columns[i]?.type ?? "");
      return el("td", { class: f.cls || null, text: f.text });
    })));
  }
  table.replaceChildren(thead, tbody);
}

function downloadCsv(r) {
  const esc = (v) => {
    if (v === null || v === undefined) return "";
    const s = String(v);
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const lines = [r.columns.map((c) => esc(c.name)).join(","), ...r.rows.map((row) => row.map(esc).join(","))];
  const blob = new Blob([lines.join("\r\n")], { type: "text/csv;charset=utf-8" });
  const a = el("a", { href: URL.createObjectURL(blob), download: "foundrytrade-result.csv" });
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

async function copy(text, button) {
  try {
    await navigator.clipboard.writeText(text);
    button.textContent = "Copied";
  } catch {
    button.textContent = "Copy failed";
  }
  setTimeout(() => (button.textContent = "Copy"), 1500);
}

/* ------------------------------------------------------------------ SQL highlighting (DOM only) */

const KEYWORDS = new Set(("select from where join inner left right full outer cross on and or not in is null as group by order having " +
  "top distinct with union all except intersect case when then else end asc desc between like exists over partition " +
  "cast convert insert update delete into values set drop create alter table exec execute declare offset fetch next rows only").split(" "));
const TOKEN = /(--[^\n]*|\/\*[\s\S]*?\*\/)|('(?:[^']|'')*')|(\b\d+(?:\.\d+)?\b)|([A-Za-z_@#][\w@#$]*)(?=\s*\()|([A-Za-z_][\w$]*)|([\s\S])/g;

function highlightSql(code, sql) {
  code.replaceChildren();
  let plain = "";
  const flush = () => {
    if (plain) code.append(plain);
    plain = "";
  };
  for (const m of sql.matchAll(TOKEN)) {
    const [text, comment, str, num, fn, word] = m;
    let cls = null;
    if (comment) cls = "cmt";
    else if (str) cls = "str";
    else if (num) cls = "num";
    else if (fn) cls = KEYWORDS.has(fn.toLowerCase()) ? "kw" : "fn";
    else if (word && KEYWORDS.has(word.toLowerCase())) cls = "kw";
    if (cls) {
      flush();
      code.append(el("span", { class: cls, text }));
    } else plain += text;
  }
  flush();
}

/* ------------------------------------------------------------------ schema browser */

let schemaLoaded = false;
$("#schema").addEventListener("toggle", async (e) => {
  if (!e.currentTarget.open || schemaLoaded) return;
  schemaLoaded = true;
  const grid = $("#schema-grid");
  try {
    const res = await fetch("api/schema");
    const schema = await res.json();
    $("#schema-count").textContent = `${schema.database} · ${schema.tables.length} tables and views`;
    for (const t of schema.tables) {
      const fks = new Map(t.foreignKeys.filter((k) => k.columns.length === 1).map((k) => [k.columns[0], `${k.referencedSchema}.${k.referencedTable}`]));
      const cols = t.columns.map((c) => el("li", {},
        el("span", { class: t.primaryKey.includes(c.name) ? "pk" : null, text: c.name }),
        el("span", { class: "type", text: c.sqlType + (c.isNullable ? "?" : "") }),
        fks.has(c.name) ? el("span", { class: "fk", text: `→ ${fks.get(c.name)}` }) : null,
        c.allowedValues.length ? el("span", { class: "type", text: `{${c.allowedValues.join(", ")}}` }) : null));
      grid.append(el("section", { class: "schema-table" },
        el("h3", {}, `${t.schema}.${t.name}`, el("small", { text: t.kind === "View" ? "view" : "" })),
        t.description ? el("p", { text: t.description }) : null,
        el("ul", {}, ...cols)));
    }
  } catch {
    grid.append(errorBox("Could not load the schema."));
    schemaLoaded = false;
  }
});

/* ------------------------------------------------------------------ shareable URLs */

function updateUrl(question, mode) {
  const url = new URL(location.href);
  url.searchParams.set("q", question);
  url.searchParams.set("v", mode);
  history.replaceState(null, "", url);
}

function restoreFromUrl() {
  const params = new URLSearchParams(location.search);
  const v = params.get("v");
  const radio = v && form.querySelector(`input[name="version"][value="${CSS.escape(v)}"]`);
  if (radio) radio.checked = true;
  const q = params.get("q");
  if (q) {
    questionBox.value = q.slice(0, 500);
    form.requestSubmit();
  }
}

loadHealth();
loadSamples();
restoreFromUrl();
