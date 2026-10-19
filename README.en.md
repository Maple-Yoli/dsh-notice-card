<div align="center">

# Notice Card

Notification cards for DeepSeek Harness. When a turn finishes, when DSH waits for an approval, or when it waits for an answer, a card appears in the bottom-right corner of your screen.

[![npm version](https://img.shields.io/npm/v/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![npm downloads](https://img.shields.io/npm/dm/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)

[简体中文](https://github.com/Maple-Yoli/dsh-notice-card/blob/main/README.md) · **English**

[Feedback](#feedback) · [Changelog](#changelog) · [Features](#features) · [Install](#install) · [Config](#config) · [FAQ](#faq)

</div>

![Cards stacked](assets/card-deck.png)

![Appear and disappear animation](assets/card-animation.gif)

<a id="feedback"></a>

## Feedback: report a bug or suggest something

**Found a bug or have an idea? Open an issue at [Issues](https://github.com/Maple-Yoli/dsh-notice-card/issues).**

Please include your Windows version, DSH version, which card it was (turn end / approval / question) and the steps to reproduce. A screenshot or recording is the fastest way to figure it out.

<a id="changelog"></a>

## Changelog: what changed in each version

**Everything is in [Releases](https://github.com/Maple-Yoli/dsh-notice-card/releases), in both Chinese and English.**

Check it before you install or upgrade, so you know what this version changed and fixed.

<a id="features"></a>

## Features

A card appears in one corner of the screen when one of three things happens:

- A turn finishes
- DSH waits for you to approve a tool call
- DSH waits for you to answer a question

The card sits above other windows but never takes focus. You can keep typing, and you can keep watching a fullscreen video.

The card follows the Windows light/dark app theme: dark surface with light text on dark, the other way round on light. The theme is read once when a card is raised, so a card raised after you switch already has the new colours. Turn on **high contrast** and the theme stands aside entirely: the card borrows the system's own window and text colours instead of guessing at a variant.

By default the card lands in the corner of **the monitor under the pointer**, not a hard-coded primary one. `corner` defaults to `auto`, which copies the shell: a bottom taskbar keeps the cards bottom-right, a top one moves them top-right, a left one sends them bottom-left. Pin a corner explicitly with `top-left` / `top-right` / `bottom-left` / `bottom-right`; the two top corners sit flush with the top of the screen, the two bottom ones rest on the taskbar (computed from the working area, so a taskbar on the left or top lines up too).

The card scales with that monitor's actual width: no card covering a third of a small laptop screen, and no postage stamp on a 4K wall. Text has a floor so it stays readable. Bump **Settings → Accessibility → Text size** and the whole card grows with the text instead of squeezing it into the old box.

The text is drawn with **the system's own text engine** (GDI ClearType), the same renderer the taskbar, Explorer and Settings use: sub-pixel antialiasing with the glyphs sitting on the pixel grid, so the type is crisp instead of grey and swollen. Font sizes are whole pixels (`18px`, `14px`), the line height comes from GDI's own line spacing, and the wrap width is measured with the same engine that paints, so a line never measures as fitting and then gets clipped.

The typeface follows the interface language: Microsoft YaHei UI for Chinese, Yu Gothic UI for Japanese, Malgun Gothic for Korean, Segoe UI otherwise. In a right-to-left language (Arabic, Hebrew, Persian, Urdu) the two corner buttons mirror to the top-left and the session name swaps ends of the footer line.

Turn off **Show animations in Windows**, or let the machine drop into battery saver, and the card stops sliding and fading: it lands in place instead. That is decided per card, so changing the setting affects the next card.

Every card carries a grey footer: the moment it was raised on the left, written in your own short-time pattern (`2:35 PM` on a 12-hour region, `14:35` on a 24-hour one), and the session name on the right. A session that has no generated title yet shows the first six characters of its id, which still tells two sessions apart. The row below names the session's current permission mode, the same name the app's permission dropdown shows, read from the host's own catalogue. It only displays it: switching permission changes the sandbox mode and aborts the tool call that is running, so that switch stays in the app. When a host service is unavailable its row is simply absent and the card keeps its normal height.

When several events arrive together the cards stack. The oldest one sits in front and fully visible; the rest show only their title. **How many fit is up to the screen**: a short laptop shows two or three, a tall monitor more, and a card that would land off the edge is retired instead. `maxStack` can only lower that; it cannot raise it past what fits.

On a **touch screen** the two corner buttons grow to fingertip size (28px for a mouse, 36px for a finger) and drop the hover highlight, since a finger has no hover. Wheel scrolling follows your mouse settings rather than a hard-coded three lines.

The motion is a 4ms private-message pump, not `WM_TIMER`, so it stays a continuous glide on a 180Hz display instead of stepping.

- The card draws its own window. No Windows notification center, no system notification permission.
- Each card is a separate process. It still appears while DSH is busy, and a card that crashes cannot affect DSH.
- No network requests, no data collection.

<a id="requirements"></a>

## Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows |
| DSH | 0.1.0-rc.1 or newer |
| Node | 20 or newer (only used when DSH loads the plugin) |
| .NET Framework | 4.x, ships with Windows, nothing to install |

The card's interface is a piece of C# code in `lib/card.cs`. On the first card, the plugin compiles it into an exe with the `csc.exe` that ships with Windows and stores it in `%LOCALAPPDATA%\dsh-notice-card\`, then reuses it. The repository holds no prebuilt file, and nothing is downloaded at runtime.

<a id="install"></a>

## Install

There are four ways. Pick the one that matches your DSH setup; do not use two at once.

### Option 1: from npm

```bash
npm i dsh-notice-card@0.2.0
```

Then open `cordis.patch.yml` in your profile directory (by default `%USERPROFILE%\.dsh\profiles\<your profile name>\`) and add this at the end of the file:

```yaml
- insert:
    - id: notice-card
      name: C:/the directory where you ran npm i/node_modules/dsh-notice-card/lib/index.mjs
      config: {}
```

`name:` must be a full absolute path, and the slashes must be `/`.

> Once a package is on npm, anyone can install it. People who install it need no account and no token. The author's publish token is only used when releasing a new version, and has nothing to do with anyone's installation.

### Option 2: clone the source

Use this one if you want to change the code.

```bash
git clone --branch v0.2.0 https://github.com/Maple-Yoli/dsh-notice-card.git
```

Then add this at the end of your profile's `cordis.patch.yml`:

```yaml
- insert:
    - id: notice-card
      name: C:/the directory you cloned into/dsh-notice-card/lib/index.mjs
      config: {}
```

DSH loads a newly added `id` immediately, no restart needed. If nothing happens, quit DSH completely and open it again.

### Option 3: the official desktop app

The desktop app manages its own `desktop` profile, so the command-line `dsh plugin --profile desktop add` is refused there.

In the app, open **Settings → Plugins** and enter the repository:

```
Maple-Yoli/dsh-notice-card
```

The plugin is mounted automatically afterwards; you do not edit the patch file.

### Option 4: `dsh web` or a profile you created yourself

These profiles accept command-line plugin installs.

```bash
dsh plugin --profile web add dsh-notice-card@0.2.0
```

You can also use the GitHub source instead of npm:

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.2.0
```

DSH reads the `dsh.bundle.patch` field from the package and adds it to the profile's `dsh.profile.bundles`, so you do not edit that file.

> If you mount the plugin two ways at once, it is loaded twice and every event draws two cards.

<a id="config"></a>

## Config

Configuration goes in the patch's `config:`. For example, to turn off only the "turn finished" notice:

```yaml
- insert:
    - id: notice-card
      name: C:/the directory you cloned into/dsh-notice-card/lib/index.mjs
      config:
        onTurnEnd: false
        seconds: 20
```

| Key | Default | Meaning |
| --- | --- | --- |
| `onTurnEnd` | `true` | Draw a card when a turn finishes |
| `onApproval` | `true` | Draw a card when DSH waits for a tool approval |
| `onQuestion` | `true` | Draw a card when DSH waits for an answer |
| `seconds` | `10` | How many seconds the turn-end card stays. Approval and question cards ignore this value; they never expire on their own |
| `turnEndTitle` | `任务已结束` | Title of the turn-end card |
| `turnEndBody` | `DSH 已完成本轮任务，等你下一步。` | Body of the turn-end card |
| `approvalTitle` | `等你审批` | Title of the approval card |
| `approvalBody` | `工具：{tool}{reason}` | Body template of the approval card. `{tool}` is the tool name, `{reason}` is the approval reason |
| `questionTitle` | `等你回答` | Title of the question card |
| `questionBody` | `问题：{question}` | Body template of the question card. `{question}` is the question text |
| `processName` | inferred | Which process to switch to when you click the window icon on the card |
| `appPath` | inferred | Which exe to launch when that process is not running |
| `corner` | `auto` | Which corner the cards land in: `auto` follows the taskbar, or pin `top-left` / `top-right` / `bottom-left` / `bottom-right` |
| `maxStack` | `3` | How many cards may be up at once; the oldest is closed past that. Only lowers the cap (1–8); the screen height still decides what fits |

`processName` and `appPath` are inferred from the DSH process; on the desktop app they are `DeepSeek Harness` and its exe path. If you run DSH under a different shell, set the matching values. Leave both empty and clicking the window icon only closes the card, without switching windows.

Changing `corner` or `maxStack` edits the config of an already-loaded plugin, so it takes a full DSH restart, not a reload.

## How a card works

- **It follows the system theme.** Dark surface with light text on dark, the other way round on light. The theme is read once when a card is raised, from `AppsUseLightTheme`, so a card raised just after you switch already has the new colours. High contrast replaces the palette with the system's own.
- **It lands in the corner of the monitor under the pointer.** On two screens the card appears on the one your cursor is on, never on the other. `corner: auto` copies the shell (a bottom taskbar keeps them bottom-right, a top one moves them top, a left one sends them left); pin one of the four explicitly instead. Each monitor's size and DPI are measured separately, and raising the system text size grows the whole card with the type.
- **How deep the deck goes is up to the screen.** As many cards as fit, never one off the edge. `maxStack` only lowers that.
- **It never takes focus.** When a card appears, focus stays in the window you were already using. Typing and video playback are unaffected.
- **A true exclusive-fullscreen game makes it wait 2 seconds.** Only real exclusive fullscreen (a D3D-fullscreen game, presentation mode) triggers the wait. Drawing a normal window over such a surface makes Windows switch display mode and the screen goes black for a moment, about 0.7 seconds. So the card waits up to 2 seconds: if the game is closing or you alt-tab out, it appears normally; if fullscreen is still held after 2 seconds it draws regardless, accepting a brief flash rather than a missed notice. Borderless fullscreen video players and F11 browsers are not exclusive, so cards cover them directly with no black screen.
- **Quiet hours suppress the interruption.** While Windows is in a "do not disturb" state (focus assist, presentation mode) a turn-end card is simply not drawn. Approval and question cards still appear: that is the conversation waiting on you, not an interruption.
- **The turn-end card closes itself.** It fades out after `seconds`, with no click needed.
- **Approval and question cards never expire on their own.** A request can be settled while you never saw the card: a stopped turn, an approval policy set to auto-deny, or a remote client answering for you. If the card disappeared too, you would think the conversation was stuck. So this kind of card fades out only when both things are true: the request has been answered, and the DSH window has been in front of you around that moment. Minimum display is 1.2 seconds, so it never flashes away.
- **Click ✕ to dismiss immediately.** Click the window icon to bring the DSH window to the front.
- **Every card names the moment, the session and the permission mode.** A short time plus the session name (the first six characters of the id when it has no title), then the session's current permission mode below, taken from the host's own preset catalogue so it reads exactly like the dropdown in the app. Read-only: neither can be changed from the card.
- **Over fullscreen it does not block the mouse.** While a fullscreen window is up (video, game, fullscreen browser), the card becomes click-through: you still see it, but the cursor and every click land on the window underneath, so no dead patch forms in the corner. Leave fullscreen and the card becomes clickable again, ✕ included.
- **Always on top.** The card pins itself to the top. When another always-on-top window competes with it, Windows may put the card behind.

## Uninstall

1. Delete the `- insert:` block from `cordis.patch.yml` (options 1, 2, 4), or uninstall from Settings → Plugins (option 3)
2. Delete the cache directory `%LOCALAPPDATA%\dsh-notice-card\`
3. Optionally delete `node_modules/dsh-notice-card` (option 1) or the cloned directory (option 2)

## One card per event

The card listens to three events:

```
turn finished    → agent/turn-stopping
waiting approval → approval/request
waiting answer   → user-questions/request
```

Both listeners sit at the front, but they only draw: the card appears and then calls `next()` right away, handing the request back to the panel that answers it in the app, so the answer is still yours to give there. The card watches that request and leaves as soon as it is answered.

The session name and the permission name both come from the host's own services: the name from `sessionTitle` (falling back to the first six characters of the id), the permission from the preset catalogue behind `permissionPresets`. If either service is unavailable its line is simply not written and the card still appears.

Everything else is read from the system at the moment a card is raised, with nothing hard-coded: the app theme (`AppsUseLightTheme`), high contrast, the animation setting (`SPI_GETCLIENTAREAANIMATION`), battery saver (`GetSystemPowerStatus`), the text scale (`TextScaleFactor`), touch input (`SM_DIGITIZER`), the wheel scroll lines (`SPI_GETWHEELSCROLLLINES`), which edge the taskbar is on (the difference between the working area and the screen bounds), the interface language (which picks the typeface and the reading direction) and the do-not-disturb state (`SHQueryUserNotificationState`). Any of them failing to read falls back to a safe default rather than costing you the card.

Each corner is **its own deck**: the card file name carries a corner tag (`-br`, `-tl` and so on), so cards at different corners never count each other. Change `corner` and the two ends do not fight over the same slot numbers.

Every card is its own `card-`-prefixed exe process with a number after it. There is no resident background process, no shared queue and no request files. A card reaches the screen in about 60ms. A card that crashes takes only itself down; the next one starts from a clean process.

The exe is recompiled only when `card.cs` changes, so drawing a card never waits for a compile. The build output lives in `%LOCALAPPDATA%\dsh-notice-card\` and you can delete the whole thing on uninstall.

A card exits on its own when `seconds` is up, when you click it away, when the request it belongs to is answered, or when the DSH process exits. Past the stack cap the oldest card is closed by the arriving one.

### Try a card on its own

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "Title" --body "Body" --seconds 8 --corner top-left --session "demo" --preset "workspace-write"
```

This requires a `card-*.exe` to already exist in `%LOCALAPPDATA%\dsh-notice-card\`. If there is none, run one DSH turn to trigger the first compile. `--corner`, `--max-stack`, `--session` and `--preset` are all optional; leaving them out means the `auto` corner, the default cap, and no session or permission line.

<a id="faq"></a>

## FAQ

**No card appears**

1. Run the command above to draw a card on its own. If that works, the card is fine and the plugin is not mounted
2. Check that the path in `cordis.patch.yml` `name:` exists and ends in `lib/index.mjs`
3. You edited the patch and nothing happened: DSH hot-loads only **newly added `id`s**. Changing an existing `id` does not reload it; quit DSH completely and open it again
4. Only one card appears and later ones do not: check whether you mounted the plugin two ways

**Chinese text turns into garbage**

`lib/card.cs` is UTF-8 source without a BOM, and the compiler takes literals from the file as they are. Garbage usually means an editor read the file as GBK and wrote it back as UTF-8, corrupting the Chinese. Reopen the file in VS Code, save it as "UTF-8 without BOM", and it is back to normal.

**I want to change how the card looks**

Colors, font sizes, corner radius, animation length and stacking gap are all in `lib/card.cs`. No reinstall needed: once the file content changes, the next card recompiles automatically.

## Privacy

- No network requests, no statistics
- No collection of your paths, user name or conversation content
- The only interaction with DSH is the three events above. A card shows only the tool name and question text those events carry
- The compiled exe lives in `%LOCALAPPDATA%\dsh-notice-card\`, deleted along with it on uninstall

## License

[MIT](LICENSE)
