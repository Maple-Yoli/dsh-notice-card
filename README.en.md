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

Please include your Windows version, DSH version, which card it was (turn end / approval / question) and the steps to reproduce. A screenshot or recording helps most.

<a id="changelog"></a>

## Changelog: what changed in each version

**Everything is in [Releases](https://github.com/Maple-Yoli/dsh-notice-card/releases), in both Chinese and English.**

Check it before you install or upgrade, so you know what this version changed and fixed.

<a id="features"></a>

## Features

A card appears in the bottom-right corner when one of three things happens:

- A turn finishes
- DSH waits for you to approve a tool call
- DSH waits for you to answer a question

The card sits above other windows but never takes focus. You can keep typing, and you can keep watching a fullscreen video.

When several events arrive together the cards stack. The oldest one sits in front and fully visible; the rest show only their title.

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

The card's interface is a piece of C# code in `lib/card.cs`. On the first card, the plugin compiles it into an exe with the `csc.exe` that ships with Windows and stores it in `%LOCALAPPDATA%\dsh-notice-card\`, then reuses it. The repository contains no prebuilt file and nothing is ever downloaded.

<a id="install"></a>

## Install

There are four ways. Pick the one that matches your DSH setup; do not use two at once.

### Option 1: from npm

```bash
npm i dsh-notice-card@0.1.7
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
git clone --branch v0.1.7 https://github.com/Maple-Yoli/dsh-notice-card.git
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
dsh plugin --profile web add dsh-notice-card@0.1.7
```

You can also use the GitHub source instead of npm:

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.1.7
```

DSH reads the `dsh.bundle.patch` field from the package and adds it to the profile's `dsh.profile.bundles` by itself.

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

`processName` and `appPath` are inferred from the DSH process; on the desktop app they are `DeepSeek Harness` and its exe path. If you run DSH under a different shell, set the matching values. Leave both empty and clicking the window icon only closes the card, without switching windows.

## How a card works

- **It never takes focus.** When a card appears, focus stays in the window you were already using. Typing and video playback are unaffected.
- **A true exclusive-fullscreen game makes it wait 2 seconds.** Only real exclusive fullscreen (a D3D-fullscreen game, presentation mode) triggers the wait. Drawing a normal window over such a surface makes Windows switch display mode and the screen goes black for a moment, about 0.7 seconds. So the card waits up to 2 seconds: if the game is closing or you alt-tab out, it appears normally; if fullscreen is still held after 2 seconds it draws anyway, preferring a brief flash over a missed notice. Borderless fullscreen video players and F11 browsers are not exclusive, so cards cover them directly with no black screen.
- **The turn-end card closes itself.** It fades out after `seconds`, with no click needed.
- **Approval and question cards never expire on their own.** A request can be settled while you never saw the card: a stopped turn, an approval policy set to auto-deny, or a remote client answering for you. If the card disappeared too, you would think the conversation was stuck. So this kind of card fades out only when both things are true: the request has been answered, and the DSH window has been in front of you around that moment. Minimum display is 1.2 seconds, so it never flashes away.
- **Click ✕ to dismiss immediately.** Click the window icon to bring the DSH window to the front.
- **Over fullscreen it does not block the mouse.** While a fullscreen window is up (video, game, fullscreen browser), the card becomes click-through: you still see it, but the cursor and every click land on the window underneath, so no dead patch forms in the corner. Leave fullscreen and the card becomes clickable again, ✕ included.
- **Always on top.** The card pins itself to the top. When competing with another always-on-top window it may be pushed behind; that is normal.

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

The approval and question listeners only observe. They sit at the front, draw the card, and immediately call `next()` to hand the request back to whoever would answer it anyway (the approval panel in the app). The plugin never answers for you and never blocks the request. A failure to draw is silently swallowed and cannot affect DSH.

Every card is its own `card-`-prefixed exe process with a number after it. There is no resident background process, no shared queue and no request files. A card reaches the screen in about 60ms. A card that crashes takes only itself down; the next one starts from a clean process.

The exe is recompiled only when `card.cs` changes, so drawing a card never waits for a compile. The build output lives in `%LOCALAPPDATA%\dsh-notice-card\` and can be deleted wholesale on uninstall.

A card exits by itself when `seconds` is up, when you click it away, when the request it belongs to is answered, or when the DSH process exits.

### Try a card on its own

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "Title" --body "Body" --seconds 8
```

This requires a `card-*.exe` to already exist in `%LOCALAPPDATA%\dsh-notice-card\`. If there is none, let it compile once: just run one DSH turn.

<a id="faq"></a>

## FAQ

**No card appears**

1. Run the command above to draw a card on its own. If that works, the card is fine and the plugin is not mounted
2. Check that the path in `cordis.patch.yml` `name:` really exists and ends in `lib/index.mjs`
3. You edited the patch and nothing happened: DSH hot-loads only **newly added `id`s**. Changing an existing `id` does not reload it; quit DSH completely and open it again
4. Only one card appears and later ones do not: check whether you mounted the plugin two ways

**Chinese text turns into garbage**

`lib/card.cs` is UTF-8 source without a BOM, and the compiler takes literals from the file as they are. Garbage usually means an editor read the file as GBK and wrote it back as UTF-8, corrupting the Chinese. Reopen and save it in VS Code as "UTF-8 without BOM".

**I want to change how the card looks**

Colors, font sizes, corner radius, animation length and stacking gap are all in `lib/card.cs`. No reinstall needed: once the file content changes, the next card recompiles automatically.

## Privacy

- No network requests, no statistics
- No collection of your paths, user name or conversation content
- The only interaction with DSH is the three events above. A card shows only the tool name and question text those events carry
- The compiled exe lives in `%LOCALAPPDATA%\dsh-notice-card\`; delete it on uninstall

## License

[MIT](LICENSE)
