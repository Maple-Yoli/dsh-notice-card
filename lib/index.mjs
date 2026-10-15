// Host-side plugin for DSH: show a notice card when DSH stops to wait on a human
// (a tool approval or a question) and when a turn finishes.
//
// The card is drawn by lib/notify.ps1 with plain WinForms, modelled on the
// Windows toast: system colours, Segoe UI, a thin border, small corner buttons,
// pinned to the bottom-right corner, sliding up as it fades in. It sits above
// other windows but never takes focus, so it never interrupts typing or a
// full-screen video, and it does not need Windows notifications switched on.
//
// The approval and question listeners never answer the request themselves; they
// only notice it and delegate with next(), so the built-in UI still decides.
import { spawn } from "node:child_process";
import { rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = dirname(fileURLToPath(import.meta.url));
const NOTIFY = join(HERE, "notify.ps1");

const IS_WINDOWS = process.platform === "win32";
const POWERSHELL = `${process.env.SystemRoot ?? "C:/Windows"}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe`;

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
  // How long a card lives when nothing dismisses it sooner.
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

// Cards stack now, so several can be waiting at once: a shared dismiss path
// would make answering one approval dismiss every other card on screen. The
// path is unique per request and removed again once the request is over.
let dismissSeq = 0;
function dismissPath() {
  dismissSeq += 1;
  return join(tmpdir(), `dsh-notify-dismiss-${process.pid}-${dismissSeq}.txt`);
}

export function createNotifier(config = {}) {
  const options = { ...DEFAULTS, ...config };
  const script = options.scriptPath ?? NOTIFY;

  // A missed notice must never block or fail an approval, so nothing escapes.
  // -Popup makes this child the card itself; without it the script would only
  // re-spawn a grandchild and exit, and that extra PowerShell start (~250ms) is
  // the bulk of the delay between the in-app prompt and the card.
  function notify(title, body, dismiss) {
    if (!IS_WINDOWS) return;
    try {
      if (dismiss) rmSync(dismiss, { force: true });
      const args = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
        "-Popup", "-Title", title, "-Body", body, "-Seconds", String(options.seconds)];
      if (dismiss) args.push("-DismissFile", dismiss);
      if (options.processName) args.push("-ProcessName", options.processName);
      if (options.appPath) args.push("-AppPath", options.appPath);
      const child = spawn(POWERSHELL, args, { stdio: "ignore", windowsHide: true });
      child.on("error", () => {});
      child.unref();
    } catch {}
  }

  // Resolve the request first (the in-app UI answers it), then dismiss the card.
  // `next()` returns the whole remaining waterfall, so the card stays up exactly
  // as long as DSH is waiting. The floor stops a flash when nothing actually
  // waited on a human (no answerer composed, request resolved instantly). The
  // card's own life timer stays as a safety net for a lost signal.
  const MIN_VISIBLE_MS = 1200;

  function notifyUntilAnswered(title, body, next) {
    const dismiss = dismissPath();
    notify(title, body, dismiss);
    const started = Date.now();
    const close = () => {
      const wait = MIN_VISIBLE_MS - (Date.now() - started);
      const write = () => { try { writeFileSync(dismiss, ""); } catch {} };
      if (wait > 0) setTimeout(write, wait); else write();
      // Give the card a moment to notice the file before the file goes away.
      setTimeout(() => { try { rmSync(dismiss, { force: true }); } catch {} }, 5000);
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
