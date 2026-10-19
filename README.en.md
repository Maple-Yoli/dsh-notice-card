<div align="center">

# Notice Card

Notification cards for DeepSeek Harness: when a turn ends, when DSH waits for an approval, or when it waits for an answer, a card appears in a corner of the screen.

[![npm version](https://img.shields.io/npm/v/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![npm downloads](https://img.shields.io/npm/dm/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)

[简体中文](https://github.com/Maple-Yoli/dsh-notice-card/blob/main/README.md) · **English**

[Features](#features) · [Install](#install) · [Config](#config) · [How it works](#how) · [FAQ](#faq) · [Uninstall](#uninstall)

</div>

![Cards stacked](assets/card-deck.png)

![Appear and disappear animation](assets/card-animation.gif)

<a id="features"></a>

## Features

A card appears when one of three things happens:

- A turn finishes
- DSH waits for you to approve a tool call
- DSH waits for you to answer a question

The card sits above other windows but never takes focus. You can keep typing, and you can keep watching a fullscreen video.

### Appearance

- **Follows the system theme** — dark surface with light text on dark, the other way round on light. The theme is read once per card, so a card raised after you switch already has the new colours.
- **High contrast takes over** — with high contrast on, the card borrows the system's own window and text colours instead of guessing at a variant.
- **Text drawn by the system engine** — GDI ClearType, the same renderer as the taskbar, Explorer and Settings, with glyphs sitting on the pixel grid. Sizes are whole pixels, and line height and wrap width come from the same engine that paints, so a line never measures as fitting and then gets clipped.
- **Typeface follows the interface language** — Microsoft YaHei UI for Chinese, Yu Gothic UI for Japanese, Malgun Gothic for Korean, Segoe UI otherwise. In a right-to-left language (Arabic, Hebrew) the two corner buttons mirror to the top-left.

### Position and size

- **Lands on the monitor under the pointer** — a second screen does not lose the card. `corner: auto` copies the shell: a bottom taskbar keeps cards bottom-right, a top one moves them top-right, a left one sends them bottom-left.
- **Or pin one of four corners** — `top-left` / `top-right` / `bottom-left` / `bottom-right`. The top two sit flush with the screen edge, the bottom two rest on the taskbar, computed from the working area so a left or top taskbar lines up too.
- **Scales with the monitor width** — no card covering a third of a small laptop, no postage stamp on a 4K wall. Text has a floor.
- **Grows with the text size** — bump **Settings → Accessibility → Text size** and the whole card grows instead of squeezing the text into the old box.
- **Stacks into a deck** — the oldest card sits in front and fully visible, the rest show only their title. As many as fit, and never one off the edge of the screen.

### Footer

- **Left** — the moment the card was raised, in your own short-time pattern (`2:35 PM` on a 12-hour region, `14:35` on a 24-hour one).
- **Right** — the session name. A session with no generated title yet shows the first six characters of its id, which still tells two sessions apart. Right-to-left languages swap this end.
- **Row below** — the session's current permission mode, the same name the app's permission dropdown shows, display only.

### Behaviour

- **Never takes focus** — focus stays in the window you were using. Typing and video playback are unaffected.
- **A true exclusive-fullscreen game makes it wait 2 seconds** — only real exclusive fullscreen (a D3D-fullscreen game, presentation mode) triggers the wait. Drawing a normal window over such a surface makes Windows switch display mode and the screen goes black for about 0.7 seconds. After 2 seconds it draws regardless, accepting a brief flash rather than a missed notice. Borderless players and F11 browsers are not exclusive, so cards cover them directly.
- **Quiet hours suppress the interruption** — while Windows is in a do-not-disturb state (focus assist, presentation mode) a turn-end card is not drawn. Approval and question cards still appear: that is the conversation waiting on you.
- **Over fullscreen it does not block the mouse** — the card turns click-through, so clicks land on the window underneath instead of forming a dead patch in the corner. Leave fullscreen and it becomes clickable again, ✕ included.
- **Animations off means no motion** — with **Show animations in Windows** off, or the machine in battery saver, the card lands in place instead of sliding and fading. Decided per card, so changing the setting affects the next one.
- **Bigger buttons on a touch screen** — 28px for a mouse, 36px for a finger, with the hover highlight dropped. Wheel scrolling follows your mouse settings rather than a hard-coded three lines.
- **Always on top** — when another always-on-top window competes, Windows may put the card behind.

### When a card goes away

- **Turn-end card** — fades out after `seconds`, no click needed.
- **Approval and question cards** — never expire on their own. A request can be settled while you never saw the card (a stopped turn, an auto-deny policy, a remote client answering for you), and a card that vanished with it would make the conversation look stuck. So it fades out only when the request has been answered **and** the DSH window has been in front around that moment, with a 1.2 second minimum so it never flashes away.
- **Click ✕** — dismiss immediately. Click the window icon to bring DSH to the front.
- **Past the stack cap** — the oldest card is closed by the arriving one.

<a id="requirements"></a>

## Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows |
| DSH | 0.1.0-rc.1 or newer |
| Node | 20 or newer (only used when DSH loads the plugin) |
| .NET Framework | 4.x, ships with Windows, nothing to install |

<a id="install"></a>

## Install

Four ways. Pick the one that matches your DSH setup, and **do not use two at once**, or every event draws two cards.

### Option 1: from npm

```bash
npm i dsh-notice-card@0.2.1
```

Then add this at the end of `cordis.patch.yml` in your profile directory (by default `%USERPROFILE%\.dsh\profiles\<profile name>\`):

```yaml
- insert:
    - id: notice-card
      name: C:/the directory where you ran npm i/node_modules/dsh-notice-card/lib/index.mjs
      config: {}
```

`name:` must be a full absolute path, with `/` as the separator.

### Option 2: clone the source

Use this one if you want to change the code.

```bash
git clone --branch v0.2.1 https://github.com/Maple-Yoli/dsh-notice-card.git
```

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

It mounts automatically afterwards; you do not edit the patch file.

### Option 4: `dsh web` or a profile you created yourself

```bash
dsh plugin --profile web add dsh-notice-card@0.2.1
```

You can also use the GitHub source instead of npm:

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.2.1
```

The `dsh.bundle.patch` field in the package is added to the profile's `dsh.profile.bundles` for you.

<a id="config"></a>

## Config

Configuration goes in the patch's `config:` block. For example, to turn off the turn-end notice and keep it up for 20 seconds:

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
| `seconds` | `10` | How many seconds the turn-end card stays. Approval and question cards ignore it |
| `turnEndTitle` | `任务已结束` | Title of the turn-end card |
| `turnEndBody` | `DSH 已完成本轮任务，等你下一步。` | Body of the turn-end card |
| `approvalTitle` | `等你审批` | Title of the approval card |
| `approvalBody` | `工具：{tool}{reason}` | Body template. `{tool}` is the tool name, `{reason}` the approval reason |
| `questionTitle` | `等你回答` | Title of the question card |
| `questionBody` | `问题：{question}` | Body template. `{question}` is the question text |
| `processName` | inferred | Which process to switch to when you click the window icon |
| `appPath` | inferred | Which exe to launch when that process is not running |
| `corner` | `auto` | `auto` follows the taskbar, or pin `top-left` / `top-right` / `bottom-left` / `bottom-right` |
| `maxStack` | `3` | How many cards may be up at once (1–8); the oldest closes past that. The screen height still decides what fits |

`processName` and `appPath` are inferred from the DSH process; on the desktop app they are `DeepSeek Harness` and its exe path. Leave both empty and clicking the window icon only closes the card.

Changing `corner` or `maxStack` edits the config of an already-loaded plugin, so it takes a **full DSH restart**, not a reload.

<a id="how"></a>

## How it works

- **Three events** — `agent/turn-stopping` (turn finished), `approval/request` (waiting for approval), `user-questions/request` (waiting for an answer).
- **It draws, it does not answer for you** — the approval and question listeners sit at the front, and the card calls `next()` right away, handing the request back to the panel that answers it in the app. The answer stays yours to give there; the card watches the request and leaves once it is answered.
- **The footer reads from the host** — the session name comes from `sessionTitle` (falling back to the first six characters of the id), the permission name from the preset catalogue behind `permissionPresets`. If a service is unavailable its line is simply not written and the card still appears.
- **Everything else is read from the system per card** — the theme (`AppsUseLightTheme`), high contrast, the animation setting (`SPI_GETCLIENTAREAANIMATION`), battery saver (`GetSystemPowerStatus`), the text scale (`TextScaleFactor`), touch input (`SM_DIGITIZER`), the wheel scroll lines (`SPI_GETWHEELSCROLLLINES`), which edge the taskbar is on, the interface language and the do-not-disturb state (`SHQueryUserNotificationState`). Any of them failing to read falls back to a safe default rather than costing you the card.
- **Each corner is its own deck** — the card file name carries a corner tag (`-br`, `-tl`), so cards at different corners never count each other.
- **One process per card** — each card is its own `card-<hash>.exe`. No resident process, no shared queue, no request files. A card that crashes takes only itself down. A card reaches the screen in about 60ms.
- **The motion** — a 4ms private-message pump, not `WM_TIMER`, so it stays a continuous glide on a high-refresh display.
- **Compiled once, then reused** — the first card compiles `lib/card.cs` with the `csc.exe` that ships with Windows and stores the exe in `%LOCALAPPDATA%\dsh-notice-card\`. It recompiles only when the source content changes.

### Try a card on its own

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "Title" --body "Body" --seconds 8 --corner top-left --session "demo" --preset "workspace-write"
```

This needs a `card-*.exe` to already exist in the cache directory; if there is none, run one DSH turn to trigger the first compile. `--corner`, `--max-stack`, `--session` and `--preset` are all optional.

<a id="faq"></a>

## FAQ

**No card appears**

1. Run the command above to draw a card on its own. If that works, the card is fine and the plugin is not mounted
2. Check that the path in `cordis.patch.yml` `name:` exists and ends in `lib/index.mjs`
3. You edited the patch and nothing happened: DSH hot-loads only **newly added `id`s**. Changing an existing `id` takes a full restart
4. Only one card appears and later ones do not: check whether you mounted the plugin two ways

**Chinese text turns into garbage**

`lib/card.cs` is UTF-8 source without a BOM, and the compiler takes literals from the file as they are. Garbage usually means an editor read the file as GBK and wrote it back as UTF-8. Reopen it in VS Code, save as "UTF-8 without BOM", and it is back to normal.

**I want to change how the card looks**

Colours, font sizes, corner radius, animation length and stacking gap are all in `lib/card.cs`. No reinstall needed: once the file content changes, the next card recompiles automatically.

<a id="uninstall"></a>

## Uninstall

1. Delete the `- insert:` block from `cordis.patch.yml` (options 1, 2, 4), or uninstall from **Settings → Plugins** (option 3)
2. Delete the cache directory `%LOCALAPPDATA%\dsh-notice-card\`
3. Optionally delete `node_modules/dsh-notice-card` (option 1) or the cloned directory (option 2)

## Privacy

- No network requests, no statistics
- No collection of your paths, user name or conversation content
- The only interaction with DSH is the three events above. A card shows only the tool name and question text those events carry
- The compiled exe lives in `%LOCALAPPDATA%\dsh-notice-card\`, deleted along with it on uninstall

<a id="changelog"></a>

## Changelog

What each version changed and fixed is in [Releases](https://github.com/Maple-Yoli/dsh-notice-card/releases), in both Chinese and English. Worth a look before you upgrade.

<a id="feedback"></a>

## Feedback

Found a bug or have an idea? Open an issue at [Issues](https://github.com/Maple-Yoli/dsh-notice-card/issues).

Please include your Windows version, DSH version, which card it was (turn end / approval / question) and the steps to reproduce. A screenshot or recording is best.

## License

[MIT](LICENSE)
