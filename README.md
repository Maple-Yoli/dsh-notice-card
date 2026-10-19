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

下面三件事发生时，屏幕的某个角落会出现一张卡片：

- 一轮任务跑完
- DSH 等你审批工具调用
- DSH 等你回答一个问题

卡片盖在其他窗口上面，但不会抢走你的焦点。你可以继续打字，也可以继续全屏看视频。

卡片跟着 Windows 的浅色/深色主题走：系统切深色，卡片就是深底浅字；切浅色就是浅底深字。每张卡在弹出那一刻读一次主题，所以切换主题之后弹出的卡立刻是新配色。开了**高对比度**时这套配色整个让位给系统配色，卡片用系统自己的窗口色和文字色，不猜。

卡片默认落在**鼠标所在那块屏幕**的角上，不是写死主屏。`corner` 默认是 `auto`，跟系统通知同一个角落：任务栏在下面就在右下，任务栏在上面就在右上，任务栏在左边就跟到左下。想钉死某一个角就显式写 `top-left` / `top-right` / `bottom-left` / `bottom-right`。上边两个是顶格贴屏幕顶，下边两个贴在任务栏上沿（用工作区算，所以任务栏摆在左边、上边也一样对齐）。

卡片大小按那块屏幕的实际宽度按比例缩放，小屏笔记本上不会是盖掉三分之一桌面的巨卡，4K 上也不会缩成一张邮票。字号有下限，再小也保持可读。**「设置 → 辅助功能 → 文本大小」**调过就跟着放大，整张卡一起长，不把文字挤进原来的框里。

卡片上的文字用**系统自己的文字引擎**（GDI ClearType）绘制，跟任务栏、资源管理器、设置界面同一套渲染：亚像素抗锯齿，字形落在像素网格上。所以字是清的，不是那种灰蒙蒙、边缘发胖的样子。字号只取整数像素（`18px`、`14px`），行高按 GDI 自己的行距取整，换行宽度也用画字的那套引擎去量，不会出现「量的时候放得下、画的时候挤掉了」这种一边缺一块的不均匀。

卡片的字体跟着界面语言换：中文用微软雅黑 UI，日文用 Yu Gothic UI，韩文用 Malgun Gothic，其它用 Segoe UI。阿拉伯语、希伯来语这类从右往左的语言，右上角两个按钮会镜像到左上角，「右边」的会话名也跟着换边。

系统里关掉「在 Windows 中显示动画」、或者机器进了省电模式，卡片就不再滑入淡出，直接落到位置上。这个判断每张卡做一次，所以中途改变设置，下一张卡就换行为。

每张卡底部有一行灰字：左边是弹出时刻（按系统的短时间格式，12 小时制写 `2:35 PM`，24 小时制写 `14:35`），右边是这个会话的名字。会话还没生成标题时显示会话 id 的前 6 位，照样能区分是哪个会话。再下面一行是当前权限范围（比如「权限 工作区内修改」），跟应用里的权限下拉条同名。权限只显示、不能点：切换权限会改变沙箱模式，正在跑的那次工具调用会被中止，所以这个开关只留在应用里。宿主的服务拿不到时对应那行就不显示，卡片也照原样正常弹。

同时来了好几个事件，卡片会叠成一摞。最早出现的那张在最前面、完整显示，后面的只露出标题。**叠几张由屏幕高度决定**：矮屏上只放得下两三张，高屏上能多放几张，绝不会有一张落到屏幕外面。`maxStack` 只能把上限往下调，调不到屏幕放不下的数量。超出上限时最早的那张被关掉。

卡片是**触摸屏**上的时候，右上角两个按钮放大到能用手指点中（鼠标 28px，手指 36px），并且不做悬停高亮——手指没有悬停这回事。滚轮滚动行数也读系统的鼠标设置，不是写死三行。

卡片的分层动画是 4ms 一帧的私有消息泵，不是 `WM_TIMER`，实测在 180Hz 屏上也是连续滑动，不会一跳一跳。

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
npm i dsh-notice-card@0.2.0
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
git clone --branch v0.2.0 https://github.com/Maple-Yoli/dsh-notice-card.git
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
dsh plugin --profile web add dsh-notice-card@0.2.0
```

也可以直接用 GitHub 源，不走 npm：

```bash
dsh plugin --profile web add github:Maple-Yoli/dsh-notice-card#v0.2.0
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
| `corner` | `auto` | 卡片落在哪一角：`auto` 跟任务栏走，或显式 `top-left` / `top-right` / `bottom-left` / `bottom-right` |
| `maxStack` | `3` | 最多同时显示几张卡，超出就把最老的关掉。只能往下调（1–8），实际张数还受屏幕高度限制 |

`processName` 和 `appPath` 默认从 DSH 进程推出来，桌面端下就是 `DeepSeek Harness` 和它的 exe 路径。用别的壳跑 DSH 就改成对应的值。两个都留空，点窗口图标只会关掉卡片，不会切窗口。

改 `corner` 和 `maxStack` 属于改已有插件的 `config`，要完全退出 DSH 再启动才生效。

## 卡片怎么工作

- **跟系统主题走。** 深色模式下卡片是深底浅字，浅色模式下反过来。主题在卡片弹出时读一次 Windows 的 `AppsUseLightTheme`，所以刚切过主题之后弹出的卡片立刻是新配色。开了高对比度就用系统配色，不猜。
- **落在鼠标那块屏幕的角上。** 双屏时哪块屏幕上有鼠标就弹哪块，不会跑到另一块屏去。`corner: auto` 跟系统通知同一个角（任务栏在哪边就跟到哪边），也可以钉死四个角之一，上边顶格、下边贴任务栏上沿（按工作区算，任务栏在左边或上边也一样对齐）。每块屏幕的大小和 DPI 各自算，卡片跟着那块屏幕缩放；「文本大小」调过的话整张卡一起放大。
- **叠几张看屏幕。** 屏幕放得下几张就叠几张，绝不会有卡片落到屏幕外；`maxStack` 只能把上限往下压。
- **不抢焦点。** 卡片出现的时候，焦点还是留在你原来那个窗口。打字、放视频都不受影响。
- **独占全屏的游戏先等 2 秒。** 只有真正的独占全屏（D3D 全屏游戏、演示模式）才会等。在那种画面上画普通窗口，Windows 会切换显示模式，屏幕黑一下再回来，大约 0.7 秒。所以卡片先等 2 秒：游戏正好在关，或者你 alt-tab 出来，它就正常弹出；2 秒后还占着全屏就照画，宁可闪一下也不丢提示。无边框全屏播放器和 F11 浏览器不算独占，卡片直接盖上去，不会黑屏。
- **专注助手/免打扰时不打扰。** 系统进入「请勿打扰」状态（专注模式、演示模式）时，任务结束卡直接不弹；审批卡和提问卡照弹，因为那是对话在等你。
- **任务结束卡自己消失。** 停留 `seconds` 秒后淡出，不点也会消失。
- **审批卡和提问卡不会自己消失。** 请求有可能在你根本没看到卡片的时候就被答掉了：回合被中止、审批策略设成自动拒绝、或者远程客户端替你答了。如果卡片也跟着消失，你会以为对话卡住了。所以这种卡要同时满足两件事才淡出：请求已经答完，并且 DSH 窗口在这前后出现在过最前面。最少显示 1.2 秒，不会闪一下就没了。
- **点 ✕ 立即收起。** 点窗口图标则把 DSH 窗口切到前台。
- **卡片上标着时刻、会话和权限。** 一行短时间加会话名（没有标题时是会话 id 前 6 位），下面一行是这个会话当前的权限范围，名字取自宿主自己的预设目录，应用里那个下拉条叫什么它就写什么。只读：从卡片上既切不了会话也改不了权限。
- **全屏时不挡鼠标。** 有全屏窗口（视频、游戏、全屏浏览器）的时候，卡片自动变成鼠标穿透：看见卡片，但鼠标和点击都落到下面那个窗口上，不会在角落形成一块点不到的死区。等退出全屏，卡片恢复可点，✕ 又能按了。
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

审批和提问这两个监听器都挂在最前面，但它们只负责弹卡，不代替你回答：卡片弹出来之后立刻调用 `next()`，把请求交回给原本的应答方（应用里的审批面板、提问面板），你还是在应用里作答。卡片自己盯着这个请求，请求一被答掉就收走。

卡片上的会话名和权限名都从宿主自己的服务里读：会话名走 `sessionTitle`（没有标题时退回会话 id 前 6 位），权限名走 `permissionPresets` 的预设目录。两个服务任何一个读不到，对应的那行就不写，卡片照常弹。

另外这些也全是当场读的系统状态，没有写死的值：应用主题（`AppsUseLightTheme`）、高对比度、动画开关（`SPI_GETCLIENTAREAANIMATION`）、省电模式（`GetSystemPowerStatus`）、文本大小（`TextScaleFactor`）、触摸屏（`SM_DIGITIZER`）、滚轮行数（`SPI_GETWHEELSCROLLLINES`）、任务栏在哪一边（工作区和屏幕边界的差）、界面语言（决定字体族和阅读方向）、免打扰状态（`SHQueryUserNotificationState`）。任何一项读不到都退回一个安全默认值，不会因为读不到就不弹卡。

每个角是**一个独立的队列**：卡片文件名里带角标签（`-br` / `-tl` 这样），不同角的卡片互不计数。所以把 `corner` 改成别的角之后，两边的卡不会混在一起算位置。

每张卡都是一个独立的 `card-` 开头、后面跟一串编号的 exe 进程。没有常驻后台进程，没有共享队列，也没有请求文件。卡片上屏大约 60ms。一张卡崩了只崩它自己，下一张从干净的进程重新开始。

exe 只在 `card.cs` 改动时重新编译，所以平时弹卡不用等编译。编译结果放在 `%LOCALAPPDATA%\dsh-notice-card\`，卸载时整个删掉就行。

卡片会在这些时候自己退出：`seconds` 到点、被你点掉、所属请求被答掉、或者 DSH 进程关了。超出 `maxStack` 时最老的那张被新卡关掉。

### 单独试一下卡片

```powershell
$card = (Get-ChildItem "$env:LOCALAPPDATA\dsh-notice-card\card-*.exe" | Select-Object -First 1).FullName
& $card --title "标题" --body "正文" --seconds 8 --corner top-left --session "演示" --preset "工作区内修改"
```

前提是 `%LOCALAPPDATA%\dsh-notice-card\` 里已经有一个 `card-*.exe`。没有的话，让它编译一次就行：随便跑一轮 DSH 任务。`--corner`、`--max-stack`、`--session`、`--preset` 都可以省略，省略时就是 `auto` 位置、默认上限、不写那两行。

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
