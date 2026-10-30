// Host-side plugin for DSH: show a notice card when DSH stops to wait on a human
// (a tool approval or a question) and when a turn finishes.
//
// The card is lib/card.cs, compiled to a standalone executable the first time a
// card is needed. It is modelled on the Windows toast: Segoe UI, a thin border,
// small corner buttons, sliding in from the corner it is anchored to. It sits
// above other windows but never takes focus, so it never interrupts typing or a
// full-screen video, and it does not need Windows notifications switched on.
//
// One process per card. There is no resident helper and no shared queue: a card
// that misbehaves can only ever take itself down, and the next card starts clean.
// Compiling ahead of time is what makes that affordable - a card is on screen in
// ~60ms, against ~650ms for the PowerShell version it replaced.
//
// A card follows the Windows app theme light or dark, lands on the monitor under
// the pointer in whichever corner the config names, and scales with that
// monitor, so it neither covers a third of a small screen nor turns into a
// postage stamp on a 4K one.
//
// The card reports the session it belongs to and the permission mode that
// session is in, the latter under the same name the client's permission dropdown
// shows. Both are read from the host's own services rather than retyped here.
// Everything is optional: a host without a service, or a session without the
// projection yet, simply gets a card without that line. `ctx.get` is the
// inject-free service lookup, so this needs no dependency declaration.
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import z from "@deepseek-ai/schemastery";

const HERE = dirname(fileURLToPath(import.meta.url));
const CARD_SOURCE = join(HERE, "card.cs");

const IS_WINDOWS = process.platform === "win32";

// csc.exe ships with the .NET Framework on every Windows 10 and 11 install, so
// building the card needs nothing downloaded. The 64-bit compiler is preferred
// because the card is a 64-bit process on a 64-bit DSH; the 32-bit one is only a
// fallback for an odd environment.
const FRAMEWORK = `${process.env.SystemRoot ?? "C:/Windows"}\\Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe`;
const FRAMEWORK32 = `${process.env.SystemRoot ?? "C:/Windows"}\\Microsoft.NET\\Framework\\v4.0.30319\\csc.exe`;

// The window button on a card focuses the running DSH window, or launches it
// when it is not running. Both come from the process this plugin is loaded
// into, so nothing machine-specific is baked in: on the desktop app
// process.execPath is "…/DeepSeek Harness.exe" and the process name follows.
const APP_PATH = IS_WINDOWS && process.execPath && basename(process.execPath).toLowerCase() !== "node.exe"
  ? process.execPath
  : "";
const APP_NAME = APP_PATH ? basename(APP_PATH).replace(/\.exe$/i, "") : "";

// The card's own wording follows the language this DSH runs in, not a constant
// baked in here: a reader whose interface is English gets an English card. The
// locale comes from the runtime, which is the same ICU data the rest of the app
// formats with; anything not Chinese falls back to English.
const LOCALE = (() => {
  try {
    return Intl.DateTimeFormat().resolvedOptions().locale ?? "en";
  } catch {
    return "en";
  }
})();
const CHINESE = /^zh/i.test(LOCALE);

// The card's own wording, in both languages. The config picks one of these at
// send time instead of being frozen at load: flipping the language setting has to
// change the next card, not the next launch.
//
// A parenthesised tail on the approval body uses the full-width form in Chinese
// and the ASCII form in English; mixing them is the tell of a literal translated
// from the other language.
const TEXT_ZH = {
  turnEndTitle: "任务已结束",
  turnEndBody: "DSH 已完成本轮任务，等你下一步。",
  approvalTitle: "等你审批",
  approvalBody: "工具：{tool}{reason}",
  questionTitle: "等你回答",
  questionBody: "问题：{question}",
  permission: (name) => `权限 ${name}`,
  parens: (reason) => `（${reason}）`,
  fallbackTool: "工具",
  fallbackQuestion: "一个问题",
};

const TEXT_EN = {
  turnEndTitle: "Turn finished",
  turnEndBody: "DSH has finished this turn and is waiting on you.",
  approvalTitle: "Waiting for approval",
  approvalBody: "Tool: {tool}{reason}",
  questionTitle: "Waiting for your answer",
  questionBody: "Question: {question}",
  permission: (name) => `Permission ${name}`,
  parens: (reason) => ` (${reason})`,
  fallbackTool: "tool",
  fallbackQuestion: "a question",
};

// "auto" is the language this DSH runs in.
const TEXT = CHINESE ? TEXT_ZH : TEXT_EN;
const textOf = (language) => (language === "zh" ? TEXT_ZH : language === "en" ? TEXT_EN : TEXT);

const DEFAULTS = {
  // A card for each event kind. Turn these off individually if they are noisy.
  onTurnEnd: true,
  onApproval: true,
  onQuestion: true,
  // How long a turn-end card lives when nothing dismisses it sooner. A card that
  // waits for an answer ignores it: it has no timeout at all.
  seconds: 10,
  // Headline and body text. `{tool}` and `{reason}` are filled in per approval.
  turnEndTitle: TEXT.turnEndTitle,
  turnEndBody: TEXT.turnEndBody,
  approvalTitle: TEXT.approvalTitle,
  approvalBody: TEXT.approvalBody,
  questionTitle: TEXT.questionTitle,
  questionBody: TEXT.questionBody,
  // Focus target for the card's first button, and what to launch if it is gone.
  processName: APP_NAME,
  appPath: APP_PATH,
  // Which corner the deck lives in, and how many cards may be on screen at once.
  // "auto" follows the taskbar the way the shell's own toasts do, so the cards
  // land where the notification area is instead of a corner the user moved away
  // from. The stack is capped by what the screen can actually show; `maxStack`
  // only lowers that.
  corner: "auto",
  maxStack: 3,
  // Type size as a percentage of the card as designed. It moves the glyphs only:
  // the card keeps its width, its height and its bands, so bigger words show
  // fewer lines in the same body box rather than growing the card. A card that
  // grew would be `scale`, and would shift every card below it.
  fontSize: 100,
  // Appearance. Every one of these has an "auto"/"follow" answer that means
  // "whatever Windows is already doing", so a config that never touches them
  // behaves exactly like the card before these existed.
  //
  // `theme` picks the light or dark preset; `scale` sizes the card against the
  // screen; `language` picks the card's own wording and its font; `contrast`
  // nudges the text and the border up or down one notch. `animations` only skips
  // the enter/exit motion - position and dwell time are unchanged.
  theme: "auto",
  animations: "follow",
  scale: "auto",
  language: "auto",
  contrast: "auto",
};

function fill(template, values) {
  return String(template).replace(/\{(\w+)\}/g, (match, key) =>
    values[key] === undefined || values[key] === null ? "" : String(values[key]));
}

// Volatile config fields reach the plugin as a live box, not as a bare value:
// the host wraps every field declared `.volatile()` in `{ get() }` so a change
// can be observed without reloading the plugin. Unwrap once, here, and every
// reader downstream can keep treating a setting as a plain value.
function read(value) {
  return value !== null && typeof value === "object" && typeof value.get === "function"
    ? value.get()
    : value;
}

// Cards stack, so several can be waiting at once: a shared dismiss path would
// make answering one approval dismiss every other card on screen. The path is
// unique per request and removed again once the request is over.
let dismissSeq = 0;
function dismissPath() {
  dismissSeq += 1;
  return join(tmpdir(), `dsh-notify-dismiss-${process.pid}-${dismissSeq}.txt`);
}

// A card reports the permission mode the session is in, under the same name the
// client's permission dropdown shows. The name is read from the host's own
// catalogue rather than retyped here; only the "权限"/"Permission" prefix is this
// plugin's and it follows the UI language. Everything is optional: a host
// without the service, or a session without the projection yet, simply gets a
// card without that line. `ctx.get` is the inject-free service lookup, so this
// needs no dependency declaration.
function presetLabel(ctx, session, language) {
  try {
    const presets = ctx.get("permissionPresets");
    if (!presets || !session) return "";
    const option = presets.catalog().options
      .find((item) => item.value === presets.current(session));
    return option?.name ? textOf(language).permission(option.name) : "";
  } catch {
    return "";
  }
}

// A card names the session it belongs to, so two cards raised by two sessions
// can be told apart at a glance. The title is the human label; a session that
// has not been titled yet falls back to the first characters of its id, which is
// still unique. Both are optional: a host without the service gets no name.
function sessionLabel(ctx, session) {
  try {
    if (!session) return "";
    const titles = ctx.get("sessionTitle");
    const title = titles?.get(session)?.title;
    if (typeof title === "string" && title.trim().length !== 0) return title.trim();
    const id = session.id;
    return typeof id === "string" ? id.slice(0, 6) : "";
  } catch {
    return "";
  }
}

// The card is built once per source revision, into a directory the user can
// write to (the plugin itself lives in a node_modules tree that may be
// read-only). The file name carries a hash of card.cs, so editing the source
// rebuilds instead of running a stale card that no longer matches this plugin.
const BUILD_DIR = join(process.env.LOCALAPPDATA ?? tmpdir(), "dsh-notice-card");
// The build is checked again for every card rather than cached for the life of
// the process. A cached path is not safe here: building a newer revision prunes
// the old exe, and a plugin that still remembers the pruned path fails its spawn
// silently — no card at all, which looks exactly like the plugin being uninstalled.
let built = null;
// The stamp whose build already failed. Kept per revision rather than as a plain
// flag: card.cs is edited while a host is running, so a build attempted during an
// intermediate broken state must not cost the process every card it will ever
// raise. A permanent latch is exactly that bug - one bad compile and the plugin
// looks uninstalled until DSH restarts.
let failedStamp = null;

function cardExe() {
  try {
    const source = readFileSync(CARD_SOURCE);
    const stamp = createHash("sha256").update(source).digest("hex").slice(0, 16);
    if (built !== null && built.stamp === stamp && existsSync(built.exe)) return built.exe;
    const exe = join(BUILD_DIR, `card-${stamp}.exe`);
    if (existsSync(exe)) { built = { stamp, exe }; return exe; }
    if (failedStamp === stamp) return null;
    mkdirSync(BUILD_DIR, { recursive: true });
    const compiler = existsSync(FRAMEWORK) ? FRAMEWORK : FRAMEWORK32;
    if (!existsSync(compiler)) { failedStamp = stamp; return null; }
    // Compiled beside the target and renamed over it. A compiler killed mid-write
    // would otherwise leave a truncated exe that passes every existence check and
    // then fails to start, which reads as a card that silently never appears.
    const temporary = `${exe}.${process.pid}.tmp`;
    const build = spawnSync(compiler, [
      "/nologo", "/target:winexe", "/optimize+",
      `/out:${temporary}`,
      "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
      CARD_SOURCE,
    ], { stdio: "ignore", windowsHide: true, timeout: 60000 });
    if (build.status !== 0 || !existsSync(temporary)) {
      try { rmSync(temporary, { force: true }); } catch {}
      failedStamp = stamp;
      return null;
    }
    renameSync(temporary, exe);
    // Older revisions are left where they are. Deleting them looks tidy and is
    // how this plugin lost its cards once: another process (an earlier DSH, or a
    // second window) can hold a path into this directory, and a file that
    // vanishes under it turns into a silently missing card. One revision is
    // 24KB; the whole directory is removed on uninstall anyway.
    failedStamp = null;
    built = { stamp, exe };
    return exe;
  } catch {
    return null;
  }
}

export function createNotifier(config = {}) {
  const raw = { ...DEFAULTS, ...config };
  // Settings the user changes from the settings page arrive as volatile wrappers
  // (`{ get() }`, see `Config` below), not as plain values: the host hands out a
  // live box so a field can change without the plugin being reloaded. Reading
  // through a getter is what makes a change take effect on the next card rather
  // than the next launch. A plain value - the fallback for anything the host did
  // not wrap, including every DEFAULTS entry - passes through untouched.
  const options = {};
  for (const key of Object.keys(raw)) {
    Object.defineProperty(options, key, {
      enumerable: true,
      get: () => read(raw[key]),
    });
  }

  // One spawn path for both kinds of card, so a preview and a real notice cannot
  // drift apart: the same arguments, read through the same getters, differing
  // only in the two flags at the end. `values` is anything with those fields -
  // the live options for a notice, a merged snapshot for a preview.
  let previewHost = null;

  function launch(values, spec) {
    if (!IS_WINDOWS) return null;
    try {
      const exe = cardExe();
      if (!exe) return null;
      if (spec.dismiss) rmSync(spec.dismiss, { force: true });
      const args = [
        "--title", spec.title,
        "--body", spec.body,
        "--seconds", String(values.seconds),
        "--owner-pid", String(process.pid),
      ];
      if (spec.dismiss) args.push("--dismiss", spec.dismiss);
      if (values.processName) args.push("--process", values.processName);
      if (values.appPath) args.push("--app", values.appPath);
      if (values.corner) args.push("--corner", values.corner);
      args.push("--max-stack", String(values.maxStack));
      args.push("--font-size", String(values.fontSize || 100));
      // Appearance. Passed on every card rather than only when off-default: a card
      // is its own process, so it has no other way to learn the current settings,
      // and each of these is a single short string. "auto" and "follow" mean
      // "read the system", which is what card.cs does with them.
      args.push("--theme", String(values.theme));
      args.push("--animations", String(values.animations));
      args.push("--scale", String(values.scale));
      args.push("--language", String(values.language));
      args.push("--contrast", String(values.contrast));
      if (spec.host) args.push("--preview-host", spec.host);
      if (spec.meta?.preset) args.push("--preset", spec.meta.preset);
      if (spec.meta?.session) args.push("--session", spec.meta.session);
      const child = spawn(exe, args, { stdio: "ignore", windowsHide: true, detached: true });
      child.on("error", () => {});
      // Detached, so the card outlives the hook that raised it and DSH does not
      // wait on it. The card exits by itself: after `seconds`, when it is
      // clicked, or when the request it belongs to is answered.
      child.unref();
      return child;
    } catch {
      return null;
    }
  }

  // A missed notice must never block or fail an approval, so nothing escapes.
  function notify(title, body, dismiss, meta) {
    launch(options, { title, body, dismiss, meta });
  }

  // A card that waits for an answer, plus the two details that end it: the
  // request resolving, and DSH being back in front. Both live here because the
  // request must settle exactly once no matter which side answered.
  const MIN_VISIBLE_MS = 1200;
  const DISMISS_TTL_MS = 30 * 60 * 1000;

  function openCard(title, body, meta) {
    const dismiss = dismissPath();
    notify(title, body, dismiss, meta);
    const started = Date.now();
    const close = () => {
      const wait = MIN_VISIBLE_MS - (Date.now() - started);
      const write = () => { try { writeFileSync(dismiss, ""); } catch {} };
      if (wait > 0) setTimeout(write, wait); else write();
      setTimeout(() => { try { rmSync(dismiss, { force: true }); } catch {} }, DISMISS_TTL_MS).unref();
    };
    return { close };
  }

  // Resolve the request first (the in-app UI answers it), then dismiss the card.
  // `next()` returns the whole remaining waterfall, so the card stays up exactly
  // as long as DSH is waiting. The floor stops a flash when nothing actually
  // waited on a human (no answerer composed, request resolved instantly).
  //
  // The answer is only half of what ends such a card: the card also waits for
  // DSH to be back in front, because a request can be settled with nobody
  // looking at the app. So the file is left in place long enough that a card
  // which starts late still reads it, and the card that consumed it deletes it.
  // This timer is only the backstop for a card that never started at all.
  function notifyUntilAnswered(title, body, next, meta) {
    const card = openCard(title, body, meta);
    try {
      return Promise.resolve(next()).finally(card.close);
    } catch (error) {
      card.close();
      throw error;
    }
  }

  // A preview is a stack, not a card: "cards per corner" is a setting whose
  // effect is the pile itself, so one card could never show it.
  //
  // It is also the one thing here that cannot pay a process start per edit. A
  // card takes ~300ms to reach the screen, and about 250ms of that is just a
  // process coming up; rebuilding a stack inside a process that is already warm
  // takes ~40ms. So the page never starts a card: it writes a small spec file and
  // a single long-lived host, started once per settings session, redraws the whole
  // stack whenever that file changes.
  const PREVIEW_SPEC = join(tmpdir(), "dsh-notice-cards", "preview.spec");
  const PREVIEW_LEAD_MS = 60;

  // The preview is one card per event kind, so the stack the page shows is the
  // stack a reader actually gets: turn-end, approval, question, one each. The
  // list is empty when every kind is switched off, and the cards then draw blank
  // rather than dropping the stack - a preview that vanished would look like the
  // setting had broken something.
  function previewKinds(values) {
    const kinds = [];
    if (values.onTurnEnd) kinds.push("turnEnd");
    if (values.onApproval) kinds.push("approval");
    if (values.onQuestion) kinds.push("question");
    return kinds.join(",");
  }

  function writeSpec(values, language) {
    const lines = [
      `stamp=${Date.now()}`,
      `maxStack=${Number(values.maxStack) || 0}`,
      `fontSize=${Number(values.fontSize) || 100}`,
      `kinds=${previewKinds(values)}`,
      `corner=${escapeSpec(String(values.corner ?? "auto"))}`,
      `theme=${escapeSpec(String(values.theme ?? "auto"))}`,
      `animations=${escapeSpec(String(values.animations ?? "follow"))}`,
      `scale=${escapeSpec(String(values.scale ?? "auto"))}`,
      `language=${escapeSpec(String(values.language ?? "auto"))}`,
      `contrast=${escapeSpec(String(values.contrast ?? "auto"))}`,
      `turnEndTitle=${escapeSpec(wording("turnEndTitle", values, language))}`,
      `turnEndBody=${escapeSpec(wording("turnEndBody", values, language))}`,
      `approvalTitle=${escapeSpec(wording("approvalTitle", values, language))}`,
      `approvalBody=${escapeSpec(fill(wording("approvalBody", values, language), {
        tool: textOf(language).fallbackTool, reason: textOf(language).parens("…"),
      }))}`,
      `questionTitle=${escapeSpec(wording("questionTitle", values, language))}`,
      `questionBody=${escapeSpec(fill(wording("questionBody", values, language), {
        question: textOf(language).fallbackQuestion,
      }))}`,
      `title=${escapeSpec(wording("turnEndTitle", values, language))}`,
      `body=${escapeSpec(wording("turnEndBody", values, language))}`,
    ];
    mkdirSync(dirname(PREVIEW_SPEC), { recursive: true });
    // Written beside the target and renamed over it, so the host can never read a
    // half-written spec and redraw from numbers that were never a state.
    const temporary = `${PREVIEW_SPEC}.${process.pid}.tmp`;
    writeFileSync(temporary, `${lines.join("\n")}\n`, "utf8");
    renameSync(temporary, PREVIEW_SPEC);
  }

  function escapeSpec(text) {
    // One field per line, so the two characters that could break the shape have
    // to go: the newline that would end the field early, and the backslash that
    // would let a body forge an escape.
    return String(text).replace(/\\/g, "\\\\").replace(/\r/g, "").replace(/\n/g, "\\n");
  }

  function preview(values) {
    if (!IS_WINDOWS) return;
    const language = values.language;
    try { writeSpec(values, language); } catch { return; }
    const alive = (() => {
      if (!previewHost) return false;
      if (previewHost.exitCode !== null || previewHost.signalCode !== null) return false;
      try { process.kill(previewHost.pid, 0); return true; } catch { return false; }
    })();
    if (alive) return;
    previewHost = launch(values, {
      title: wording("turnEndTitle", values, language),
      body: wording("turnEndBody", values, language),
      host: PREVIEW_SPEC,
    });
  }

  function closePreview() {
    // The host shuts itself down when the spec disappears, which is also what
    // makes a host orphaned by a crash die on its own terms.
    try { rmSync(PREVIEW_SPEC, { force: true }); } catch {}
    const child = previewHost;
    previewHost = null;
    if (!child) return;
    // Giving it a moment to notice the missing spec is enough for the cards to
    // close in order; the kill only covers a host that somehow missed it.
    setTimeout(() => {
      try { child.kill(); } catch {}
    }, PREVIEW_LEAD_MS);
  }

  return { notify, notifyUntilAnswered, preview, closePreview, options };
}

// The wording a card should use right now. `language` is a live setting, so the
// text cannot be chosen once at load: it is resolved per card. A user-supplied
// headline or body wins, unless it is still the built-in string of either
// language - then it is treated as "not customised" and follows the setting,
// so flipping the language actually flips the card instead of leaving a stale
// translated literal behind.
const BUILTIN = new Map(
  ["turnEndTitle", "turnEndBody", "approvalTitle", "approvalBody", "questionTitle", "questionBody"]
    .map((field) => [field, new Set([TEXT_ZH[field], TEXT_EN[field]])]),
);

function wording(field, options, language) {
  const custom = options[field];
  if (typeof custom === "string" && custom.length !== 0 && !BUILTIN.get(field).has(custom)) return custom;
  return textOf(language)[field];
}

// ---- preview bridge -------------------------------------------------------
//
// The settings page cannot spawn a process, so the card it needs to show lives
// on this side. The page POSTs the values it is showing; the host merges them
// over the live config and raises one preview card. Only the loopback interface
// is served, and only the fields below are read from the body: the bridge is a
// window onto a local desktop card, not a general-purpose launcher.
const PREVIEW_PATH = "/api/dsh-notice-card-preview";
const PREVIEW_FIELDS = [
  "onTurnEnd", "onApproval", "onQuestion", "corner", "maxStack", "fontSize", "seconds",
  "theme", "animations", "scale", "language", "contrast",
];
const MAX_JSON_BODY_BYTES = 64 * 1024;

function isLoopbackRequest(request) {
  const address = request.socket.remoteAddress;
  if (address !== "127.0.0.1" && address !== "::1" && address !== "::ffff:127.0.0.1") return false;
  const host = request.headers.host;
  if (typeof host !== "string") return false;
  let hostUrl;
  try {
    hostUrl = new URL("http://" + host);
  } catch {
    return false;
  }
  if (hostUrl.hostname !== "127.0.0.1" && hostUrl.hostname !== "localhost" && hostUrl.hostname !== "[::1]") return false;
  if (request.headers["sec-fetch-site"] === "cross-site") return false;
  const origin = request.headers.origin;
  if (origin === undefined) return true;
  try {
    return new URL(origin).host === hostUrl.host;
  } catch {
    return false;
  }
}

function writeJson(res, status, body) {
  const payload = JSON.stringify(body);
  res.writeHead(status, { "content-type": "application/json; charset=utf-8", "referrer-policy": "no-referrer" });
  res.end(payload);
}

async function readJsonBody(req) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > MAX_JSON_BODY_BYTES) return undefined;
    chunks.push(chunk);
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    return undefined;
  }
}

export function apply(ctx, config = {}) {
  const notifier = createNotifier(config);
  const options = notifier.options;

  // Everything a card needs beyond its two lines of text. Read once per card,
  // and every field degrades to an empty string rather than throwing.
  const metaOf = (session, language) => ({
    preset: presetLabel(ctx, session, language),
    session: sessionLabel(ctx, session),
  });

  // The three switches are read inside each listener, per event, not here. That
  // is the whole point of them being live settings: turning one off has to stop
  // the next card without a restart, which is impossible if the subscription
  // itself was decided at load time.
  ctx.on("approval/request", (req, next) => {
    if (!options.onApproval) return next();
    const language = options.language;
    const text = textOf(language);
    const body = fill(wording("approvalBody", options, language), {
      tool: req?.toolName ?? text.fallbackTool,
      reason: req?.reason ? text.parens(req.reason) : "",
    });
    return notifier.notifyUntilAnswered(wording("approvalTitle", options, language), body, next,
      metaOf(req?.agent?.session, language));
  }, { prepend: true });

  ctx.on("user-questions/request", (req, next) => {
    if (!options.onQuestion) return next();
    const language = options.language;
    const question = req?.questions?.[0]?.label;
    const body = fill(wording("questionBody", options, language), {
      question: question ?? textOf(language).fallbackQuestion,
    });
    return notifier.notifyUntilAnswered(wording("questionTitle", options, language), body, next,
      metaOf(req?.agent?.session, language));
  }, { prepend: true });

  // Fires once per finished turn, after the agent has stopped producing work.
  ctx.on("agent/turn-stopping", ({ agent } = {}) => {
    if (!options.onTurnEnd) return;
    const language = options.language;
    notifier.notify(wording("turnEndTitle", options, language), wording("turnEndBody", options, language),
      undefined, metaOf(agent?.session, language));
  });

  // The preview bridge. The settings page has no way to start a process, so it
  // sends the values it is showing and this side raises the card. The body wins
  // over the live config field by field, which is what makes an edit visible
  // before the host has stored it: the page's optimistic value is already the
  // one the card is drawn from.
  //
  // Registered inside `ctx.inject` because webServer is not present on every
  // host build, and torn down with the plugin so a disabled plugin leaves no
  // route behind.
  ctx.inject(["webServer"], (sctx) => {
    sctx.effect(() => {
      const dispose = sctx.webServer.register({
        kind: "exact",
        path: PREVIEW_PATH,
        handler: async (req, res) => {
          if (!isLoopbackRequest(req)) return writeJson(res, 403, { ok: false, error: "loopback requests only" });
          if (req.method !== "POST") return writeJson(res, 405, { ok: false, error: `method not allowed: ${req.method ?? ""}` });
          const body = await readJsonBody(req);
          if (body === undefined) return writeJson(res, 400, { ok: false, error: "malformed JSON body" });
          try {
            if (body.close === true) {
              notifier.closePreview();
              return writeJson(res, 200, { ok: true });
            }
            const values = {};
            for (const field of PREVIEW_FIELDS) {
              const sent = body[field];
              values[field] = sent === undefined || sent === null ? options[field] : sent;
            }
            notifier.preview(values);
            return writeJson(res, 200, { ok: true });
          } catch (error) {
            return writeJson(res, 500, { ok: false, error: String(error?.message ?? error) });
          }
        },
      });
      return () => {
        try { dispose(); } catch {}
        notifier.closePreview();
      };
    }, "dsh-notice-card: preview bridge");
  });
}

// Every setting the settings page shows, and nothing else. `.volatile()` is what
// makes a field editable there without restarting the host: the host hands the
// plugin a live box per field (`{ get() }`, see `read`) instead of a value frozen
// at load. Fields must sit flat on the root object for that to be allowed.
//
// Only the fourteen settings the page owns live here. The card's own wording
// (turnEndTitle and friends) stays outside: it is translated, not configured, and
// a form field per phrase would be noise.
export const Config = z.object({
  onTurnEnd: z.boolean().default(DEFAULTS.onTurnEnd).volatile(),
  onApproval: z.boolean().default(DEFAULTS.onApproval).volatile(),
  onQuestion: z.boolean().default(DEFAULTS.onQuestion).volatile(),
  corner: z.string().default(DEFAULTS.corner).volatile(),
  maxStack: z.number().default(DEFAULTS.maxStack).min(1).max(8).volatile(),
  fontSize: z.number().default(DEFAULTS.fontSize).min(80).max(160).volatile(),
  seconds: z.number().default(DEFAULTS.seconds).min(3).max(120).volatile(),
  theme: z.string().default(DEFAULTS.theme).volatile(),
  animations: z.string().default(DEFAULTS.animations).volatile(),
  scale: z.string().default(DEFAULTS.scale).volatile(),
  language: z.string().default(DEFAULTS.language).volatile(),
  contrast: z.string().default(DEFAULTS.contrast).volatile(),
});

export const name = "dsh-notice-card";
export { DEFAULTS };
