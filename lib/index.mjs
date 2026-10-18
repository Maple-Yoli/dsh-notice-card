// Host-side plugin for DSH: show a notice card when DSH stops to wait on a human
// (a tool approval or a question) and when a turn finishes.
//
// The card is lib/card.cs, compiled to a standalone executable the first time a
// card is needed. It is modelled on the Windows toast: system colours, Segoe UI,
// a thin border, small corner buttons, pinned to the bottom-right corner, sliding
// up as it fades in. It sits above other windows but never takes focus, so it
// never interrupts typing or a full-screen video, and it does not need Windows
// notifications switched on.
//
// One process per card. There is no resident helper and no shared queue: a card
// that misbehaves can only ever take itself down, and the next card starts clean.
// Compiling ahead of time is what makes that affordable - a card is on screen in
// ~60ms, against ~650ms for the PowerShell version it replaced.
//
// The approval and question listeners never answer the request themselves; they
// only notice it and delegate with next(), so the built-in UI still decides.
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
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

const DEFAULTS = {
  // A card for each event kind. Turn these off individually if they are noisy.
  onTurnEnd: true,
  onApproval: true,
  onQuestion: true,
  // How long a turn-end card lives when nothing dismisses it sooner. A card that
  // waits for an answer ignores it: it has no timeout at all.
  seconds: 10,
  // Headline and body text. `{tool}` and `{reason}` are filled in per approval.
  turnEndTitle: "任务已结束",
  turnEndBody: "DSH 已完成本轮任务，等你下一步。",
  approvalTitle: "等你审批",
  approvalBody: "工具：{tool}{reason}",
  questionTitle: "等你回答",
  questionBody: "问题：{question}",
  // Focus target for the card's first button, and what to launch if it is gone.
  processName: APP_NAME,
  appPath: APP_PATH,
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

// The card is built once per source revision, into a directory the user can
// write to (the plugin itself lives in a node_modules tree that may be
// read-only). The file name carries a hash of card.cs, so editing the source
// rebuilds instead of running a stale card that no longer matches this plugin.
const BUILD_DIR = join(process.env.LOCALAPPDATA ?? tmpdir(), "dsh-notice-card");
let builtExe = null;
let buildFailed = false;

function cardExe() {
  if (builtExe !== null) return builtExe;
  if (buildFailed) return null;
  try {
    const source = readFileSync(CARD_SOURCE);
    const stamp = createHash("sha256").update(source).digest("hex").slice(0, 16);
    const exe = join(BUILD_DIR, `card-${stamp}.exe`);
    if (existsSync(exe)) { builtExe = exe; return exe; }
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
    // Only the current revision is kept: a stale build is never used again and
    // each one is 24KB of somebody's disk.
    try {
      for (const name of readdirSync(BUILD_DIR)) {
        if (name.startsWith("card-") && name.endsWith(".exe") && name !== basename(exe)) {
          rmSync(join(BUILD_DIR, name), { force: true });
        }
      }
    } catch {}
    builtExe = exe;
    return exe;
  } catch {
    buildFailed = true;
    return null;
  }
}

export function createNotifier(config = {}) {
  const options = { ...DEFAULTS, ...config };

  // A missed notice must never block or fail an approval, so nothing escapes.
  function notify(title, body, dismiss) {
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
      const child = spawn(exe, args, { stdio: "ignore", windowsHide: true, detached: true });
      child.on("error", () => {});
      // Detached, so the card outlives the hook that raised it and DSH does not
      // wait on it. The card exits by itself: after `seconds`, when it is
      // clicked, or when the request it belongs to is answered.
      child.unref();
    } catch {}
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
  const MIN_VISIBLE_MS = 1200;
  const DISMISS_TTL_MS = 30 * 60 * 1000;

  function notifyUntilAnswered(title, body, next) {
    const dismiss = dismissPath();
    notify(title, body, dismiss);
    const started = Date.now();
    const close = () => {
      const wait = MIN_VISIBLE_MS - (Date.now() - started);
      const write = () => { try { writeFileSync(dismiss, ""); } catch {} };
      if (wait > 0) setTimeout(write, wait); else write();
      setTimeout(() => { try { rmSync(dismiss, { force: true }); } catch {} }, DISMISS_TTL_MS).unref();
    };
    try {
      return Promise.resolve(next()).finally(close);
    } catch (error) {
      close();
      throw error;
    }
  }

  return { notify, notifyUntilAnswered, options };
}

export function apply(ctx, config = {}) {
  const notifier = createNotifier(config);
  const options = notifier.options;

  if (options.onApproval) {
    ctx.on("approval/request", (req, next) => {
      const body = fill(options.approvalBody, {
        tool: req?.toolName ?? "工具",
        reason: req?.reason ? `（${req.reason}）` : "",
      });
      return notifier.notifyUntilAnswered(options.approvalTitle, body, next);
    }, { prepend: true });
  }

  if (options.onQuestion) {
    ctx.on("user-questions/request", (req, next) => {
      const question = req?.questions?.[0]?.label;
      const body = fill(options.questionBody, { question: question ?? "一个问题" });
      return notifier.notifyUntilAnswered(options.questionTitle, body, next);
    }, { prepend: true });
  }

  if (options.onTurnEnd) {
    // Fires once per finished turn, after the agent has stopped producing work.
    ctx.on("agent/turn-stopping", () => {
      notifier.notify(options.turnEndTitle, options.turnEndBody);
    });
  }
}

export const name = "dsh-notice-card";
export { DEFAULTS };
