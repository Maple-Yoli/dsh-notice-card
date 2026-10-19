<div align="center">

# Notice Card

给 DeepSeek Harness 的通知卡。任务跑完、等你审批、等你回答的时候，在屏幕右下角弹一张卡片提醒你。

[![npm version](https://img.shields.io/npm/v/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![npm downloads](https://img.shields.io/npm/dm/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)

**简体中文** · [English](https://github.com/Maple-Yoli/dsh-notice-card/blob/main/README.en.md)

[反馈](#feedback) · [更新日志](#changelog) · [功能](#features) · [安装](#install) · [配置](#config) · [常见问题](#faq)

</div>

![卡片叠放示例](assets/card-deck.png)

![出现与消失动画](assets/card-animation.gif)

<a id="feedback"></a>

## 反馈：遇到问题或有建议，写这里

**有 Bug、有想法，请到 [Issues](https://github.com/Maple-Yoli/dsh-notice-card/issues) 提。**

提的时候附上：Windows 版本、DSH 版本、是哪种卡片（任务结束 / 审批 / 提问）、复现步骤。有截图或录屏最好。

<a id="changelog"></a>

## 更新日志：每个版本改了什么

**全部写在 [Releases](https://github.com/Maple-Yoli/dsh-notice-card/releases) 里，中文和英文都有。**

装之前可以先看那里，就知道这一版改了什么、修了什么。

<a id="features"></a>

## 功能

下面三件事发生时，屏幕右下角会出现一张卡片：

- 一轮任务跑完
- DSH 等你审批工具调用
- DSH 等你回答一个问题

卡片盖在其他窗口上面，但不会抢走你的焦点。你可以继续打字，也可以继续全屏看视频。

同时来了好几个事件，卡片会叠成一摞。最早出现的那张在最前面、完整显示，后面的只露出标题。

- 卡片自己画窗口，不用 Windows 通知中心，也不用开系统通知权限
- 卡片是独立进程。DSH 忙着的时候照弹，卡片崩了也不影响 DSH
- 不发任何网络请求，不收集任何数据

<a id="requirements"></a>

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows |
| DSH | 0.1.0-rc.1 或更高 |
| Node | 20 或更高（只有 DSH 加载插件时用得到） |
| .NET Framework | 4.x，Windows 自带，不用另外装 |

卡片的界面是一段 C# 代码，放在 `lib/card.cs`。第一次弹卡时，插件用系统自带的 `csc.exe` 把它编译成一个 exe，存到 `%LOCALAPPDATA%\dsh-notice-card\`，以后直接复用。仓库里没有编译好的文件，也不会下载任何东西。

<a id="install"></a>

## 安装

四种方式，按你的 DSH 是哪种形态选一种，不要同时用两种。

### 方式一：从 npm 装

```bash
npm i dsh-notice-card@0.1.8
```

装完之后，打开你的 profile 目录下的 `cordis.patch.yml`（默认在 `%USERPROFILE%\.dsh\profiles\<你的 profile 名>\`），在文件最后加上：

```yaml
- insert:
    - id: notice-card
      name: C:/你执行 npm i 的目录/node_modules/dsh-notice-card/lib/index.mjs
      config: {}
```

`name:` 那里要写完整的绝对路径，路径里的斜杠用 `/`。

> 包装到 npm 上之后，谁都能装，装的人不需要账号，也不需要令牌。作者的发布令牌只在推新版本时用，跟别人安装无关。

### 方式二：clone 源码

想改代码就用这种。

```bash
git clone --branch v0.1.8 https://github.com/Maple-Yoli/dsh-notice-card.git
```

然后在 profile 的 `cordis.patch.yml` 最后加上：

```yaml
- insert:
    - id: notice-card
      name: C:/你克隆的目录/dsh-notice-card/lib/index.mjs
      config: {}
```

新加的 `id` 会被 DSH 立即加载，不用重启。如果没有反应，完全退出 DSH 再打开一次。

### 方式三：官方桌面端

官方桌面端的 `desktop` profile 由应用自己管理，命令行的 `dsh plugin --profile desktop add` 会被拒绝。

在应用里打开 **设置 → 插件**，填仓库地址：

```
Maple-Yoli/dsh-notice-card
```

装完之后插件会自动挂上，不用改 patch 文件。

### 方式四：`dsh web` 或自己建的 profile

这些 profile 允许用命令行装插件。

```bash
dsh plugin --profile web add dsh-notice-card@0.1.8
```

也可以直接用 GitHub 源，不走 npm：

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.1.8
```

DSH 会读到你包里的 `dsh.bundle.patch`，自动把它加进 profile 的 `dsh.profile.bundles`。

> 同时用两种方式挂载，同一个插件会被加载两次，每件事会弹两张卡。

<a id="config"></a>

## 配置

配置写在 patch 的 `config:` 里。比如只关掉「任务结束」的提示：

```yaml
- insert:
    - id: notice-card
      name: C:/你克隆的目录/dsh-notice-card/lib/index.mjs
      config:
        onTurnEnd: false
        seconds: 20
```

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `onTurnEnd` | `true` | 一轮任务结束时弹卡 |
| `onApproval` | `true` | DSH 等你审批工具调用时弹卡 |
| `onQuestion` | `true` | DSH 等你回答问题时弹卡 |
| `seconds` | `10` | 任务结束卡停留几秒。审批卡和提问卡不看这个值，它们不会自己消失 |
| `turnEndTitle` | `任务已结束` | 任务结束卡的标题 |
| `turnEndBody` | `DSH 已完成本轮任务，等你下一步。` | 任务结束卡的正文 |
| `approvalTitle` | `等你审批` | 审批卡的标题 |
| `approvalBody` | `工具：{tool}{reason}` | 审批卡的正文模板。`{tool}` 是工具名，`{reason}` 是审批理由 |
| `questionTitle` | `等你回答` | 提问卡的标题 |
| `questionBody` | `问题：{question}` | 提问卡的正文模板。`{question}` 是问题内容 |
| `processName` | 自动推断 | 点卡片右上角窗口图标时，切到哪个进程 |
| `appPath` | 自动推断 | 那个进程没开时，启动哪个 exe |

`processName` 和 `appPath` 默认从 DSH 进程推出来，桌面端下就是 `DeepSeek Harness` 和它的 exe 路径。用别的壳跑 DSH 就改成对应的值。两个都留空，点窗口图标只会关掉卡片，不会切窗口。

## 卡片怎么工作

- **不抢焦点。** 卡片出现的时候，焦点还是留在你原来那个窗口。打字、放视频都不受影响。
- **独占全屏的游戏先等 2 秒。** 只有真正的独占全屏（D3D 全屏游戏、演示模式）才会等。在那种画面上画普通窗口，Windows 会切换显示模式，屏幕黑一下再回来，大约 0.7 秒。所以卡片先等 2 秒：游戏正好在关，或者你 alt-tab 出来，它就正常弹出；2 秒后还占着全屏就照画，宁可闪一下也不丢提示。无边框全屏播放器和 F11 浏览器不算独占，卡片直接盖上去，不会黑屏。
- **任务结束卡自己消失。** 停留 `seconds` 秒后淡出，不点也会消失。
- **审批卡和提问卡不会自己消失。** 请求有可能在你根本没看到卡片的时候就被答掉了：回合被中止、审批策略设成自动拒绝、或者远程客户端替你答了。如果卡片也跟着消失，你会以为对话卡住了。所以这种卡要同时满足两件事才淡出：请求已经答完，并且 DSH 窗口在这前后出现在过最前面。最少显示 1.2 秒，不会闪一下就没了。
- **点 ✕ 立即收起。** 点窗口图标则把 DSH 窗口切到前台。
- **全屏时不挡鼠标。** 有全屏窗口（视频、游戏、全屏浏览器）的时候，卡片自动变成鼠标穿透：看见卡片，但鼠标和点击都落到下面那个窗口上，不会在右下角形成一块点不到的死区。等退出全屏，卡片恢复可点，✕ 又能按了。
- **压在最上层。** 卡片会把自己固定在最上层。跟别的置顶窗口抢顶层时可能被挤到后面，这是正常的。

## 卸载

1. 把 `cordis.patch.yml` 里那段 `- insert:` 删掉（方式一、二、四），或者在 设置 → 插件 里卸载（方式三）
2. 删掉缓存目录 `%LOCALAPPDATA%\dsh-notice-card\`
3. 方式一可以顺带删掉 `node_modules/dsh-notice-card`，方式二可以顺带删掉克隆出来的目录

## 一次事件一张卡

卡片会同时收到三种事件：

```
一轮结束   → agent/turn-stopping
等你审批   → approval/request
等你回答   → user-questions/request
```

审批和提问这两个监听器只是旁听。它们挂在最前面，弹完卡立刻调用 `next()` 把请求交回给原本的应答方（应用里的审批面板）。插件不代替你回答，也不阻塞请求。弹卡失败只会被静默吞掉，不会影响 DSH。

每张卡都是一个独立的 `card-` 开头、后面跟一串编号的 exe 进程。没有常驻后台进程，没有共享队列，也没有请求文件。卡片上屏大约 60ms。一张卡崩了只崩它自己，下一张从干净的进程重新开始。

exe 只在 `card.cs` 改动时重新编译，所以平时弹卡不用等编译。编译结果放在 `%LOCALAPPDATA%\dsh-notice-card\`，卸载时整个删掉就行。

卡片会在这些时候自己退出：`seconds` 到点、被你点掉、所属请求被答掉、或者 DSH 进程关了。

### 单独试一下卡片

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "标题" --body "正文" --seconds 8
```

前提是 `%LOCALAPPDATA%\dsh-notice-card\` 里已经有一个 `card-*.exe`。没有的话，让它编译一次就行：随便跑一轮 DSH 任务。

<a id="faq"></a>

## 常见问题

**卡片不弹**

1. 先用上面那条命令单独跑一次卡片。能弹，就说明卡片本身是好的，问题在插件没挂上
2. 检查 `cordis.patch.yml` 里 `name:` 指向的路径是否真的存在，结尾是不是 `lib/index.mjs`
3. 改了 patch 但没反应：DSH 只对**新增的 `id`** 热加载。改一个已有的 `id` 不会重新加载，完全退出 DSH 再打开
4. 每次只弹一张、后面的不弹：检查是不是用两种方式挂了两次

**中文变成乱码**

`lib/card.cs` 是 UTF-8 无 BOM 的源码，编译时按文件里的字面量来。乱码一般是编辑器先按 GBK 读、又按 UTF-8 存回去，把中文压坏了。用 VS Code 以「UTF-8 without BOM」重新打开、保存，文件就恢复正常。

**想改卡片的样子**

颜色、字号、圆角、动画时长、叠放间距，都在 `lib/card.cs` 里。改完不用重装插件：文件内容一改，下次弹卡就会自动重新编译。

## 隐私

- 不发任何网络请求，不写任何统计
- 不收集你的路径、用户名、会话内容
- 跟 DSH 的交互只有上面三个事件。卡片上显示的只有事件自带的工具名和问题文本
- 编译出来的 exe 放在 `%LOCALAPPDATA%\dsh-notice-card\`，卸载时一起删掉

## License

[MIT](LICENSE)
