<div align="center">

# Notice Card

给 DeepSeek Harness 的通知卡：任务跑完、等你审批、等你回答时，在屏幕角落弹一张卡片。

[![npm version](https://img.shields.io/npm/v/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![npm downloads](https://img.shields.io/npm/dm/dsh-notice-card?color=2ea043)](https://www.npmjs.com/package/dsh-notice-card)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows-blue)](#requirements)

**简体中文** · [English](https://github.com/Maple-Yoli/dsh-notice-card/blob/main/README.en.md)

[功能](#features) · [安装](#install) · [配置](#config) · [实现](#how) · [常见问题](#faq) · [卸载](#uninstall)

</div>

![卡片叠放示例](assets/card-deck.png)

![出现与消失动画](assets/card-animation.gif)

<a id="features"></a>

## 功能

三件事发生时弹一张卡：

- 一轮任务跑完
- 等你审批工具调用
- 等你回答一个问题

卡片盖在其他窗口上，但不抢焦点。你可以继续打字，也可以继续全屏看视频。

### 外观

- **跟随系统主题** —— 深色模式深底浅字，浅色模式反过来。每次弹卡读一次主题，刚切过之后弹出的卡立刻是新配色。
- **高对比度让位** —— 开了高对比度就用系统自己的窗口色和文字色，不猜变体。
- **文字用系统引擎画** —— GDI ClearType，跟任务栏、资源管理器、设置界面同一套渲染，字形落在像素网格上。字号只取整数像素，行高和换行宽度都用画字的那套引擎算，不会一边缺一块。
- **字体跟界面语言走** —— 中文微软雅黑 UI、日文 Yu Gothic UI、韩文 Malgun Gothic、其它 Segoe UI。从右往左的语言（阿拉伯语、希伯来语）右上角两个按钮镜像到左上角。

### 位置与大小

- **落在鼠标那块屏幕** —— 副屏不跑丢。`corner: auto` 跟系统通知同一个角：任务栏在下面就在右下，在上面就在右上，在左边就跟到左下。
- **也可以钉死四角** —— `top-left` / `top-right` / `bottom-left` / `bottom-right`。上边两个顶格贴屏幕顶，下边两个贴任务栏上沿，按工作区算，任务栏在左边或上边也一样对齐。
- **按屏幕宽度缩放** —— 小屏笔记本上不会盖掉三分之一桌面，4K 上也不会缩成邮票。字号有下限。
- **跟文本大小一起长** —— 「设置 → 辅助功能 → 文本大小」调过，整张卡放大，不把文字挤进原来的框。
- **叠成一摞** —— 最早那张在最前、完整显示，后面的只露标题。放得下几张叠几张，绝不会有一张落到屏幕外。

### 底部信息

- **左边** —— 弹出时刻，按系统的短时间格式（12 小时制 `2:35 PM`，24 小时制 `14:35`）。
- **右边** —— 会话名。会话还没生成标题时显示会话 id 前 6 位，照样能区分。右到左的语言里这一项换到另一边。
- **再下面一行** —— 当前权限范围，跟应用里那个下拉条同名。只显示不能点：切权限会改沙箱模式，正在跑的工具调用会被中止，所以开关只留在应用里。

### 行为

- **不抢焦点** —— 焦点留在你原来那个窗口，打字、放视频都不受影响。
- **独占全屏的游戏先等 2 秒** —— 只有真正的独占全屏（D3D 全屏游戏、演示模式）才等。在那种画面上画普通窗口，Windows 会切显示模式，屏幕黑一下约 0.7 秒。2 秒后还占着就照画，宁可闪一下也不丢提示。无边框全屏播放器和 F11 浏览器不算独占，直接盖上去。
- **免打扰时不打扰** —— 系统进「请勿打扰」（专注模式、演示模式）时任务结束卡不弹；审批卡和提问卡照弹，那是对话在等你。
- **全屏时不挡鼠标** —— 卡片变鼠标穿透，点击落到下面那个窗口，不在角落形成点不到的死区。退出全屏后恢复可点，✕ 又能按。
- **关动画就真不动** —— 系统关掉「在 Windows 中显示动画」或进省电模式，卡片直接落到位置，不滑入淡出。每张卡判断一次，中途改设置下一张就换行为。
- **触摸屏按钮放大** —— 鼠标 28px、手指 36px，并且不做悬停高亮。滚轮行数读系统鼠标设置，不是写死三行。
- **压在最上层** —— 跟别的置顶窗口抢顶层时可能被挤到后面，这是正常的。

### 什么时候消失

- **任务结束卡** —— 停留 `seconds` 秒后淡出，不点也会消失。
- **审批卡和提问卡** —— 不自己消失。请求可能在你没看见时就被答掉了（回合中止、审批策略自动拒绝、远程客户端替你答），卡片跟着消失会让你以为对话卡住。所以必须同时满足「请求已答完」和「DSH 窗口在这前后出现在过最前面」才淡出，最少显示 1.2 秒，不会闪一下就没了。
- **点 ✕** —— 立即收起。点窗口图标则把 DSH 切到前台。
- **超出叠放上限** —— 最早那张被新卡关掉。

<a id="requirements"></a>

## 环境要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows |
| DSH | 0.1.0-rc.1 或更高 |
| Node | 20 或更高（只在 DSH 加载插件时用得到） |
| .NET Framework | 4.x，Windows 自带，不用另外装 |

<a id="install"></a>

## 安装

四种方式，按你的 DSH 是哪种形态选一种。**不要同时用两种**，否则每件事弹两张卡。

### 方式一：从 npm 装

```bash
npm i dsh-notice-card@0.2.1
```

在 profile 目录的 `cordis.patch.yml`（默认 `%USERPROFILE%\.dsh\profiles\<profile 名>\`）末尾加上：

```yaml
- insert:
    - id: notice-card
      name: C:/你执行 npm i 的目录/node_modules/dsh-notice-card/lib/index.mjs
      config: {}
```

`name:` 要写完整绝对路径，斜杠用 `/`。

### 方式二：clone 源码

想改代码就用这种。

```bash
git clone --branch v0.2.1 https://github.com/Maple-Yoli/dsh-notice-card.git
```

```yaml
- insert:
    - id: notice-card
      name: C:/你克隆的目录/dsh-notice-card/lib/index.mjs
      config: {}
```

新加的 `id` 会被 DSH 立即加载，不用重启。没有反应就完全退出 DSH 再打开。

### 方式三：官方桌面端

桌面端的 `desktop` profile 由应用自己管理，命令行的 `dsh plugin --profile desktop add` 会被拒绝。

在应用里打开 **设置 → 插件**，填仓库地址：

```
Maple-Yoli/dsh-notice-card
```

装完自动挂上，不用改 patch 文件。

### 方式四：`dsh web` 或自己建的 profile

```bash
dsh plugin --profile web add dsh-notice-card@0.2.1
```

也可以直接走 GitHub 源，不经 npm：

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.2.1
```

包里的 `dsh.bundle.patch` 会被自动加进 profile 的 `dsh.profile.bundles`。

<a id="config"></a>

## 配置

配置写在 patch 的 `config:` 里。比如只关掉「任务结束」提示、把停留时间改成 20 秒：

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
| `onApproval` | `true` | 等你审批工具调用时弹卡 |
| `onQuestion` | `true` | 等你回答问题时弹卡 |
| `seconds` | `10` | 任务结束卡停留几秒。审批卡和提问卡不看这个值 |
| `turnEndTitle` | `任务已结束` | 任务结束卡的标题 |
| `turnEndBody` | `DSH 已完成本轮任务，等你下一步。` | 任务结束卡的正文 |
| `approvalTitle` | `等你审批` | 审批卡的标题 |
| `approvalBody` | `工具：{tool}{reason}` | 审批卡正文模板。`{tool}` 工具名，`{reason}` 审批理由 |
| `questionTitle` | `等你回答` | 提问卡的标题 |
| `questionBody` | `问题：{question}` | 提问卡正文模板。`{question}` 问题内容 |
| `processName` | 自动推断 | 点窗口图标时切到哪个进程 |
| `appPath` | 自动推断 | 那个进程没开时启动哪个 exe |
| `corner` | `auto` | `auto` 跟任务栏走，或钉死 `top-left` / `top-right` / `bottom-left` / `bottom-right` |
| `maxStack` | `3` | 最多同时显示几张卡（1–8），超出关掉最老的。实际张数还受屏幕高度限制 |

`processName` 和 `appPath` 默认从 DSH 进程推出来，桌面端下就是 `DeepSeek Harness` 和它的 exe 路径。两个都留空，点窗口图标只关卡片、不切窗口。

改 `corner`、`maxStack` 属于改已有插件的 `config`，要**完全退出 DSH 再启动**才生效。

<a id="how"></a>

## 实现

- **三个事件** —— `agent/turn-stopping`（一轮结束）、`approval/request`（等你审批）、`user-questions/request`（等你回答）。
- **只弹卡，不代替你回答** —— 审批和提问的监听器挂在最前面，卡片弹出后立刻调 `next()`，把请求交回原本的应答方（应用里的面板）。你还是在应用里作答，卡片盯着请求，答完就收走。
- **底部两行从宿主读** —— 会话名走 `sessionTitle`（没标题退回会话 id 前 6 位），权限名走 `permissionPresets` 的预设目录。任一服务读不到就不写那行，卡片照常弹。
- **其余全是当场读的系统状态** —— 主题 `AppsUseLightTheme`、高对比度、动画开关 `SPI_GETCLIENTAREAANIMATION`、省电 `GetSystemPowerStatus`、文本大小 `TextScaleFactor`、触摸 `SM_DIGITIZER`、滚轮行数 `SPI_GETWHEELSCROLLLINES`、任务栏在哪边、界面语言、免打扰 `SHQueryUserNotificationState`。任何一项读不到都退回安全默认值，不会因此不弹卡。
- **每个角是独立队列** —— 卡片文件名带角标签（`-br` / `-tl`），不同角的卡互不计数。
- **一次一进程** —— 每张卡是独立的 `card-<编号>.exe`。没有常驻进程、没有共享队列、没有请求文件。一张卡崩了只崩它自己。上屏约 60ms。
- **动画** —— 4ms 一帧的私有消息泵，不是 `WM_TIMER`，180Hz 屏上也是连续滑动。
- **编译一次就复用** —— `lib/card.cs` 第一次弹卡时用系统自带的 `csc.exe` 编成 exe，存到 `%LOCALAPPDATA%\dsh-notice-card\`。源码内容变了才重编。

### 单独试一下卡片

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "标题" --body "正文" --seconds 8 --corner top-left --session "演示" --preset "工作区内修改"
```

前提是缓存目录里已经有一个 `card-*.exe`，没有就先随便跑一轮 DSH 任务让它编一次。`--corner`、`--max-stack`、`--session`、`--preset` 都可以省略。

<a id="faq"></a>

## 常见问题

**卡片不弹**

1. 用上面那条命令单独跑一次。能弹说明卡片本身是好的，问题在插件没挂上
2. 检查 `cordis.patch.yml` 里 `name:` 的路径是否存在，结尾是不是 `lib/index.mjs`
3. 改了 patch 没反应：DSH 只对**新增的 `id`** 热加载，改已有的 `id` 要完全退出 DSH 再打开
4. 只弹一张、后面的不弹：检查是不是用两种方式挂了两次

**中文变成乱码**

`lib/card.cs` 是 UTF-8 无 BOM 的源码，编译时按文件里的字面量来。乱码一般是编辑器先按 GBK 读、又按 UTF-8 存回去，把中文压坏了。用 VS Code 以「UTF-8 without BOM」重新打开、保存即可。

**想改卡片的样子**

颜色、字号、圆角、动画时长、叠放间距都在 `lib/card.cs` 里。改完不用重装，文件内容一改，下次弹卡自动重编。

<a id="uninstall"></a>

## 卸载

1. 删掉 `cordis.patch.yml` 里那段 `- insert:`（方式一、二、四），或在 **设置 → 插件** 里卸载（方式三）
2. 删掉缓存目录 `%LOCALAPPDATA%\dsh-notice-card\`
3. 顺带删掉 `node_modules/dsh-notice-card`（方式一）或克隆出来的目录（方式二）

## 隐私

- 不发网络请求，不写统计
- 不收集你的路径、用户名、会话内容
- 跟 DSH 的交互只有那三个事件。卡片上显示的只有事件自带的工具名和问题文本
- 编译出来的 exe 放在 `%LOCALAPPDATA%\dsh-notice-card\`，卸载时一起删掉

<a id="changelog"></a>

## 更新日志

每个版本改了什么、修了什么，全部写在 [Releases](https://github.com/Maple-Yoli/dsh-notice-card/releases)，中文和英文都有。装之前可以先看一眼。

<a id="feedback"></a>

## 反馈

有 Bug、有想法，到 [Issues](https://github.com/Maple-Yoli/dsh-notice-card/issues) 提。

附上：Windows 版本、DSH 版本、是哪种卡片（任务结束 / 审批 / 提问）、复现步骤。有截图或录屏最好。

## License

[MIT](LICENSE)
