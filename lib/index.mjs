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
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

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

// A parenthesised tail on the approval body. The full-width form is the correct
// one in Chinese text and the ASCII form in English; mixing them is the tell of a
// literal translated from the other language.
const wrapReason = (reason) => CHINESE ? `（${reason}）` : ` (${reason})`;

const TEXT = CHINESE
  ? {
      turnEndTitle: "任务已结束",
      turnEndBody: "DSH 已完成本轮任务，等你下一步。",
      approvalTitle: "等你审批",
      approvalBody: "工具：{tool}{reason}",
      questionTitle: "等你回答",
      questionBody: "问题：{question}",
      permission: (name) => `权限 ${name}`,
      fallbackTool: "工具",
      fallbackQuestion: "一个问题",
    }
  : {
      turnEndTitle: "Turn finished",
      turnEndBody: "DSH has finished this turn and is waiting on you.",
      approvalTitle: "Waiting for approval",
      approvalBody: "Tool: {tool}{reason}",
      questionTitle: "Waiting for your answer",
      questionBody: "Question: {question}",
      permission: (name) => `Permission ${name}`,
      fallbackTool: "tool",
      fallbackQuestion: "a question",
    };

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
};

function fill(template, values) {
  return String(template).replace(/\{(\w+)\}/g, (match, key) =>
    values[key] === undefined || values[key] === null ? "" : String(values[key]));
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
function presetLabel(ctx, session) {
  try {
    const presets = ctx.get("permissionPresets");
    if (!presets || !session) return "";
    const option = presets.catalog().options
      .find((item) => item.value === presets.current(session));
    return option?.name ? TEXT.permission(option.name) : "";
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
let buildFailed = false;

function cardExe() {
  if (buildFailed) return null;
  try {
    const source = readFileSync(CARD_SOURCE);
    const stamp = createHash("sha256").update(source).digest("hex").slice(0, 16);
    if (built !== null && built.stamp === stamp && existsSync(built.exe)) return built.exe;
    const exe = join(BUILD_DIR, `card-${stamp}.exe`);
    if (existsSync(exe)) { built = { stamp, exe }; return exe; }
    mkdirSync(BUILD_DIR, { recursive: true });
    const compiler = existsSync(FRAMEWORK) ? FRAMEWORK : FRAMEWORK32;
    if (!existsSync(compiler)) { buildFailed = true; return null; }
    const build = spawnSync(compiler, [
      "/nologo", "/target:winexe", "/optimize+",
      `/out:${exe}`,
      "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll",
      CARD_SOURCE,
    ], { stdio: "ignore", windowsHide: true, timeout: 60000 });
    // A failed build leaves no card at all, which is worse than a slow one, so
    // the failure is remembered rather than retried on every turn.
    if (build.status !== 0 || !existsSync(exe)) { buildFailed = true; return null; }
    // Older revisions are left where they are. Deleting them looks tidy and is
    // how this plugin lost its cards once: another process (an earlier DSH, or a
    // second window) can hold a path into this directory, and a file that
    // vanishes under it turns into a silently missing card. One revision is
    // 24KB; the whole directory is removed on uninstall anyway.
    built = { stamp, exe };
    return exe;
  } catch {
    buildFailed = true;
    return null;
  }
}

export function createNotifier(config = {}) {
  const options = { ...DEFAULTS, ...config };

  // A missed notice must never block or fail an approval, so nothing escapes.
  function notify(title, body, dismiss, meta) {
    if (!IS_WINDOWS) return;
    try {
      const exe = cardExe();
      if (!exe) return;
      if (dismiss) rmSync(dismiss, { force: true });
      const args = [
        "--title", title,
        "--body", body,
        "--seconds", String(options.seconds),
        "--owner-pid", String(process.pid),
      ];
      if (dismiss) args.push("--dismiss", dismiss);
      if (options.processName) args.push("--process", options.processName);
      if (options.appPath) args.push("--app", options.appPath);
      if (options.corner) args.push("--corner", options.corner);
      args.push("--max-stack", String(options.maxStack));
      if (meta?.preset) args.push("--preset", meta.preset);
      if (meta?.session) args.push("--session", meta.session);
      const child = spawn(exe, args, { stdio: "ignore", windowsHide: true, detached: true });
      child.on("error", () => {});
      // Detached, so the card outlives the hook that raised it and DSH does not
      // wait on it. The card exits by itself: after `seconds`, when it is
      // clicked, or when the request it belongs to is answered.
      child.unref();
    } catch {}
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

  return { notify, notifyUntilAnswered, options };
}

export function apply(ctx, config = {}) {
  const notifier = createNotifier(config);
  const options = notifier.options;

  // Everything a card needs beyond its two lines of text. Read once per card,
  // and every field degrades to an empty string rather than throwing.
  const metaOf = (session) => ({ preset: presetLabel(ctx, session), session: sessionLabel(ctx, session) });

  if (options.onApproval) {
    ctx.on("approval/request", (req, next) => {
      const body = fill(options.approvalBody, {
        tool: req?.toolName ?? TEXT.fallbackTool,
        reason: req?.reason ? wrapReason(req.reason) : "",
      });
      return notifier.notifyUntilAnswered(options.approvalTitle, body, next, metaOf(req?.agent?.session));
    }, { prepend: true });
  }

  if (options.onQuestion) {
    ctx.on("user-questions/request", (req, next) => {
      const question = req?.questions?.[0]?.label;
      const body = fill(options.questionBody, { question: question ?? TEXT.fallbackQuestion });
      return notifier.notifyUntilAnswered(options.questionTitle, body, next, metaOf(req?.agent?.session));
    }, { prepend: true });
  }

  if (options.onTurnEnd) {
    // Fires once per finished turn, after the agent has stopped producing work.
    ctx.on("agent/turn-stopping", ({ agent } = {}) => {
      notifier.notify(options.turnEndTitle, options.turnEndBody, undefined, metaOf(agent?.session));
    });
  }
}

export const name = "dsh-notice-card";
export { DEFAULTS };
