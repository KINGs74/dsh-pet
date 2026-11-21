# DSH 智能伙伴（dsh-pet）实现方案与关键设计

> 目标：把 Codex 桌面的「迷你界面和智能伙伴」搬到 DeepSeek Harness（DSH）上——**最小化也能看见对话进展**、**任务结束立刻知道**、**能用 petdex.dev 上的宠物**、**能拖到屏幕任意位置**。

---

## 1. 先看 Codex 是怎么做的

Codex 侧的做法（也是 Petdex Desktop 的实现方式）可以归纳成四件事：

1. **独立的置顶透明窗体**，而不是主窗口里的一个 DOM 节点。Codex 的宠物窗是 Electron 的无边框、`alwaysOnTop`、`transparent` 窗口，尺寸跟着精灵走，位置由用户拖动决定，因此它不会被主面板的布局裁剪。
2. **精灵图集（spritesheet）驱动动画**。一张 `1536×1872` 或 `1536×2288` 的图，按 `8 列 × N 行` 切帧，每帧 `192×208`，每一行是一个状态（idle / run / waving / jumping / failed / waiting / running / review），用定时器换帧。
3. **一个本地小服务把「任务状态」喂给宠物**。Petdex Desktop 在 `127.0.0.1:7777` 起 hook 服务，agent 侧 `POST /state {"state":"waving"}`，并带上 `X-Petdex-Update-Token` 做本地鉴权；宠物窗轮询/接收后切换行号。
4. **对话气泡 + 系统通知**。气泡显示「正在做什么」，任务结束时切换成一句明确的「任务已经结束」，配合声音和托盘气球，避免用户一直等。

DSH 这一侧的功能映射：

| Codex 能力 | DSH 上的实现 |
| --- | --- |
| 置顶透明宠物窗 | 独立的 WinForms 分层窗口进程 `PetOverlay.exe` |
| 精灵图集动画 | 同一套 petdex 图集与行号约定 |
| 本地状态服务 | DSH 宿主插件在 Web 服务上挂 `/x-dsh-pet/*` HTTP 接口 |
| 任务结束提示 | `turn/end` 事件 → 气泡「✓ 任务已完成」+ `✔/✕` 区分成败 + 声音 + 托盘通知 |
| 拖到任意位置 | 窗口自身处理拖动，坐标夹取到虚拟屏幕 |

---

## 2. DSH 平台的硬约束（决定了架构）

这几条不是猜的，是从安装包 `app.asar` 与运行时探针里读出来的：

1. **Electron 主进程没有给插件任何窗口 API**。`lib/main.js` 里 `alwaysOnTop`、`transparent`、`setIgnoreMouseEvents` 出现次数都是 **0**；`setWindowOpenHandler` 8 处全部 `{action:'deny'}`。
2. **DSH Host 不是 Electron 主进程，而是 `spawn` 出来的子 Node 进程**（`@deepseek-ai/dsh-desktop-host/lib/index.js`），所以宿主插件 `require('electron')` 必然失败。
3. **Host 服务目录里没有 window / tray / screen 相关能力**（`cordis_inspect_query` 全量枚举确认）。
4. **客户端插件无法自救**：`window.open` 被拒；`shell.overlay` 插槽虽然"浮在整个 frame 之上"，但它仍在 DSH 窗口内——最小化窗口就一起消失，拖动也出不了窗口。

> 结论：**只要坚持"不可被裁剪、最小化仍在、可拖到全屏"，就必须拥有一个自己的 OS 窗口**。因此采用「DSH 宿主插件 + 独立原生浮层进程」的双体架构，两者用本机 HTTP 通信。

---

## 3. 总体架构

```
┌──────────────────────── DSH 进程（Node，Electron 子进程）────────────────────────┐
│  dsh-pet 宿主插件 (src/index.js)                                                │
│    ├─ 订阅 session/event / agent/status / approval/request                       │
│    │     → PetModel（纯函数状态机，src/pet-state.js）                            │
│    ├─ ConfigStore（~/.dsh/pets/config.json，tmp+rename 原子写）                  │
│    ├─ PetHttpApi（挂 /x-dsh-pet/*，校验 X-Pet-Token）                            │
│    └─ OverlaySupervisor（csc 编译 → 启动 → 看护 → 退出时清理）                    │
└───────────────────────────────────┬──────────────────────────────────────────────┘
                                    │ 本机 HTTP（127.0.0.1:14711，X-Pet-Token）
┌───────────────────────────────────┴──────────────────────────────────────────────┐
│  PetOverlay.exe（独立进程，C# / WinForms / GDI+）                                 │
│    ├─ 400ms 轮询 GET  /state  → 行协议 key=percentencoded                         │
│    ├─ 用户操作 → POST /config /ack /install /overlay                              │
│    ├─ 分层窗口渲染（UpdateLayeredWindow，预乘 alpha，逐像素命中测试）              │
│    └─ 托盘菜单（切换宠物 / 缩放 / 开关 / 测试提醒 / 退出）                          │
└──────────────────────────────────────────────────────────────────────────────────┘
```

为什么用 HTTP 轮询而不是 WebSocket / IPC：

- 宿主侧 `webServer.register()` 是**文档化**的能力，稳定；而 host↔client 的 `@Remote` RPC 在公开文档里没有授权第三方使用的说明，不赌。
- 轮询 400ms（忙碌时）/1200ms（空闲时）足够跟上"消息逐字追加"的观感，CPU 接近 0，实现简单、崩溃后自愈。
- 全部接口要求 `X-Pet-Token`，token 每次启动随机生成写在 `~/.dsh/pets/runtime.json`（仅本机）。

---

## 4. 宠物渲染与拖拽层

浮层是一个 **WS_EX_LAYERED + WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE** 的无边框置顶窗口（不进 Alt+Tab、不抢焦点）：

- **合成**：内部一张 32bpp ARGB `Bitmap`，所有绘制（气泡、圆角、文字、精灵帧）先画在这张图上。
- **上屏**：`PushLayered()` 手工把 BGRA **直通 alpha 转预乘 alpha**（`c*a/255`）后拷进 DIB，再 `UpdateLayeredWindow(..., ULW_ALPHA)`。**不能用 `Graphics.FromHdc` 直接 DrawImage**——GDI+ 走 DIB 会丢逐像素 alpha（早期就是这个 bug，宠物变成黑方块）。
- **点击穿透**：同一趟循环顺手把 alpha 存进 `byte[]`，`WM_NCHITTEST` 时逐像素判断，透明处返回 `HTTRANSPARENT`，于是"只有宠物和气泡吃鼠标，其余区域鼠标事件直接穿到桌面"。`WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`，点它不会把 DSH 顶掉。
- **拖动**：左键按下 → 4px 阈值后进入拖动；坐标夹取到 `SystemInformation.VirtualScreen`（**虚拟屏幕**，不是 DSH 窗口），所以可以拖出窗口、跨显示器；松手 `POST /config pos=1&posx&posy` 持久化。拖动时播 run-left / run-right 行，方向跟着鼠标走。
- **DPI**：`app.manifest` 声明 `PerMonitorV2`；行高不写死像素，而是 `font.GetHeight(g)+1` 现算，150% 缩放下文字不会互相压。
- **锚点**：窗口以"宠物脚底中心"为锚，气泡长高时窗口向上扩、宠物本身不动；扩完再 `ClampWindow()` 夹回当前显示器工作区，避免贴屏幕顶时气泡被推出屏幕。

---

## 5. 与对话流、任务结束事件的对接

宿主插件订阅的是 **durable session 事件**（`session/event`），这是 DSH 里最可靠的一层：

| 事件 | 用途 |
| --- | --- |
| `user/message` | 气泡摘要里的"你上次的请求"、标题推导、新一轮起点 |
| `assistant/message`（只取 `stream !== true`） | 气泡正文 = 最新一条**正文**（reasoning 段被过滤，避免把思考过程漏出去） |
| `tool/call` / `tool/result` | 活动行"正在执行 X"、工具报错时的失败文案 |
| `turn/start` | 清掉上一轮结论，进入 running |
| `turn/end` | **任务结束**：`reason.kind` 决定成功/失败/中断/max-tokens 文案，并生成 `alert` |
| `step/start`、`step/end` | 页脚"第 N 步" |
| `agent/status` | 兜底：agent 空闲时状态回落 |
| `approval/request`、`user-questions/request` | 进入 waiting（"在等你确认后继续…"），审批通过后恢复 |

三个容易踩的坑（都已处理）：

1. **`turn/end` 不是 listener 事件**，它是 `session/event` 的一个 payload；只能读 `event.type === 'turn/end'`。
2. **`approval/request` 是 waterfall 事件**：监听器必须 `return next()`，否则审批面板会静默失踪。dsh-pet **不监听**这两个事件，而是用 `agent/status` + 会话事件推导 waiting，彻底避开这个坑（见 §9 的取舍说明）。
3. **只有根会话结束才算"任务结束"**：`session.header.parentSession` 为空才触发 alert，否则每个子 agent 转身都会弹一次"任务已经结束"。

---

## 6. 状态 ↔ 精灵行映射

petdex 的九行约定与 DSH 状态对应：

| DSH 状态 | 含义 | 精灵行 | 帧数 | fps |
| --- | --- | --- | --- | --- |
| `idle` | 没有进行中的任务 | 0 Idle | 6 | 6 |
| `running` | 正在跑回合 | 7 Running | 6 | 8 |
| `waiting` | 等你确认/回答 | 6 Waiting | 6 | 5 |
| `failed` | 本轮失败/中断 | 5 Failed | 8 | 8 |
| `done` | 本轮完成（未确认提醒） | 8 Review | 6 | 5 |
| 拖动中 | 跟随鼠标方向 | 1 RunRight / 2 RunLeft | 8 | 14 |
| 新提醒 2.2s | 庆祝跳跃 | 4 Jumping | 5 | 8 |
| 有未确认提醒 | 招手等你 | 3 Waving | 4 | 6 |

`SetRow()` 只在**行真的变化**时才重置帧号——早期版本每次轮询都 `_frame = 0`，动画每 400ms 卡一下。

---

## 7. petdex.dev 宠物适配

petdex 的宠物包 = `pet.json` + `spritesheet.webp`（或 `.png`）。校验规则：宽能被 8 整除、帧宽 ≥64、帧高 208、行数 ≥9；社区 v2 图集是 `1536×2288`（8×11）。

**素材获取走 petdex 自己的安装脚本**，这是最稳的接口（站点没有公开 JSON API，`/api/pets/*` 返回 401）：

```
GET https://petdex.dev/install/<slug>          → text/x-shellscript
   curl -fsSL -e "https://petdex.dev/" -o "$PET_DIR/pet.json"      'https://assets.petdex.dev/.../petjson.json'
   curl -fsSL -e "https://petdex.dev/" -o "$PET_DIR/spritesheet.webp" 'https://assets.petdex.dev/.../sprite.webp'
```

宿主用正则把这行 `curl` 解析成 `文件名 → URL`，自己带 `Referer: https://petdex.dev/` 下载（90s 超时、最多 24MB、重试 2 次）。

**关键坑：webp 转 PNG 会丢 alpha**。WIC 直接 `BitmapDecoder → PngBitmapEncoder` 得到的是 RGB、黑底。正确做法是中间插一层 `FormatConvertedBitmap`：

```powershell
$dec = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream,'PreservePixelFormat','OnLoad')
$conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($dec.Frames[0],$null,$null,0)
$conv.DestinationFormat = [System.Windows.Media.PixelFormats]::Bgra32
$enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder; $enc.Save(...)
```

配色上：气泡 `#FCFCFE` + `#CED2DE` 描边（普通态），提醒态 `#FFFAF0` + 琥珀色描边 + `✔`；失败态 `#FEF4F2` + 砖红描边 + `✕`。**成功和失败必须一眼能分开**——只改描边颜色不够。

支持三种来源：petdex 名称（`boba`）、petdex 链接（`https://petdex.dev/pets/boba`）、本地目录 / zip。

---

## 8. 配置持久化

- `~/.dsh/pets/config.json`：`{pet, scale, bubble, sound, bubbleMs, alertMs, lang, pos}`，tmp + rename 原子写，串行化避免并发写坏。
- `~/.dsh/pets/<pet>/{pet.json, spritesheet.png, spritesheet.webp}`。
- `~/.dsh/pets/bin/PetOverlay.exe`、`~/.dsh/pets/logs/host.log`、`~/.dsh/pets/logs/overlay.log`、`~/.dsh/pets/runtime.json`（token + 前缀）。
- 浮层位置以**物理像素**存成「底边中点」锚点（宠物变大、气泡出现时锚点不动，窗口只向上长），重启/换宠物/换缩放都回到原位；0.2 起取消了「重置位置」，想去哪儿拖到哪儿。

---

## 9. 可靠性与边界（以及为什么这么选）

- **单实例**：浮层用命名互斥量 `Local\DshPetOverlay.SingleInstance`；宿主启动前先 `taskkill` 旧进程，避免"两个宠物"。
- **编译失败不影响 DSH**：csc 缺失/编译报错只写日志并把 `overlay` 状态标成 `unavailable`，DSH 照常工作。
- **浮层只看护不失控**：异常退出自动重启（最多 5 次）；连续 45s 拿不到状态就自行退出，**宿主没了不会留孤儿**。
- **插件卸载干净**：`ctx.effect` 的清理函数逐个 `try/catch`（早期版本一个 TypeError 就会跳过路由注销，把一个前缀永久占住）；停用时 `SIGTERM` 子进程并确认进程消失。
- **路由前缀回退**：如果 `/x-dsh-pet` 已被占用（比如热重载残留），自动改用 `/x-dsh-pet-2`… 并把实际前缀写进 `runtime.json`、通过 `--prefix` 传给浮层，**前端和探针都不会写死路径**。
- **不监听 approval waterfall**：用 `agent/status` + 会话事件推导 waiting，宁可少一点精确度，也不冒"吞掉审批面板"的风险。
- **非 Windows 主机**：插件加载后写一条日志即返回，不做任何事。

- **不依赖浮层的自检入口**：`/pet status`（以及 `pets` / `pet <id>` / `scale` / `install` / `test` / `bubble|sound|toast on|off`）直接在 DSH 里查状态；插件启动后会自动跑一次 `status` 并把首行写进 `host.log`（`/pet 自检：浮层：运行中`），这样"命令注册成功"也有据可查。

已知限制：

1. 只在 Windows 上提供浮层（渲染层是 WinForms/GDI+）。
2. 气泡宽度固定 300px（缩放 40% 时视觉偏宽，快捷键/菜单里可关气泡）。
3. 位置按物理像素存；混合 DPI 多屏切换时可能偏一点，拖一下即可。
4. 热重载插件会让旧实例残留在 `/x-dsh-pet` 前缀上（DSH 进程内无法反注册别的实例的路由）；新实例自动占用 `-2`，**重启 DSH 后回到单一 `/x-dsh-pet`**。

---

## 10. 验收对照

| 验收标准 | 证据 |
| --- | --- |
| 最小化状态下能看到当前对话内容 | `docs/img/03-minimized.png`：DSH 窗口 `IsIconic=True`，宠物与气泡仍在桌面，气泡内容随会话更新 |
| 任务结束时收到明确提示 | `docs/img/02-alert.png`、`12-alert-success.png`、`13-alert-failed.png`；overlay 日志 `alert shown id=… kind=…`；声音 + 托盘气球 + 点击气泡确认 |
| 能加载 petdex.dev 上的宠物 | `07-boba.png`；`~/.dsh/pets/boba/spritesheet.png` 为 `1536×2288` RGBA；安装日志 `已安装 Boba / 8 列 × 11 行 · 192×208` |
| 可拖到全屏任意位置、不被裁剪 | `04-dragged.png`；拖动后 `config.json` 记录 `pos`；日志 `mouse down … / drag to …` |
| 配置可保存并复现 | `14-restart.png`：杀掉浮层后宿主自动重启，宠物、缩放、位置全部复原 |

回归：`node --test test/`（24 条用例，覆盖状态机、路由、宠物解析）。
---

## 11. 第二轮：交互、生命周期与那三个 bug（0.2.0）

这一轮把「像 Codex 那样用」补齐，也修掉了第一轮遗留的三个真 bug。下面按需求逐条给结论。

### 11.1 退出 DSH 时宠物会不会跟着消失？——不会，这是个 bug，已修

第一轮只靠「连续 45 秒拿不到状态就自己退出」看门，宿主退出后宠物会**在屏幕上干站 45 秒**。

修法：宿主把自己的 pid 与 exe 路径交给浮层（`--dsh-pid` = `process.ppid`，`--dsh-exe` = `process.execPath`），
浮层轮询里先 `OpenProcess(pid)` + `GetExitCodeProcess` 判断 `STILL_ACTIVE`，宿主一没就 `CloseFromThread()`。
实测 `PetOverlay.exe --dsh-pid 999999` → 0.22 秒后日志 `DSH 已退出，浮层退出`，退出码 0。

反过来，**DSH 关窗口 ≠ 退出**：Electron 主进程里点关闭走的是 `mainWindow.hide()`（只有 `isQuitting()` 才 `close()`），
所以窗口隐藏后宠物必须留着——这条顺带解释了 11.3 的根因。

### 11.2 能不能不要托盘图标？——能，已去掉

托盘原本只承担三件事，都有替代：

| 托盘职责 | 替代 |
| --- | --- |
| 系统气球通知 | 浮层自己的暖色气泡（`✔ 任务已经结束`）+ 提示音 + 宠物跳跃/招手 |
| 菜单入口 | 宠物右键菜单（同一份 `BuildMenuEntries`） |
| 退出入口 | 菜单最后一项「退出宠物窗」 |

于是 `NotifyIcon` 相关代码整体删除（`grep -c NotifyIcon native/*.cs` = 0），`notify/toast` 配置项一并退役。

### 11.3 「打开 DSH」有时没反应——是真 bug，两个根因叠加

| 根因 | 细节 | 修法 |
| --- | --- | --- |
| 只认可见窗口 | 旧 `FocusDsh()` 在 `EnumWindows` 回调里 `if (!IsWindowVisible(h)) return;`；而 DSH 点关闭是 **hide 到后台**，窗口从此不可见 → 永远走「找不到窗口」分支 | 按 pid 找窗口、**不看可见性**，用打分选主窗口（有标题 +10、可见 +5、非最小化 +2） |
| 抢前台被系统拒绝 | 浮层是 `WS_EX_NOACTIVATE` 工具窗，从不拥有前台，`SetForegroundWindow` 被 Windows 静默拒绝；旧代码**没检查返回值**，日志照样打「focused」 | 先 `ShowWindow(SW_SHOW/SW_RESTORE/SW_SHOWNORMAL)`，再 `AttachThreadInput` 借前台线程输入队列，`SetForegroundWindow` 重试 3 次并**校验 `GetForegroundWindow()==hWnd`** |
| 兜底 | 主窗口真被销毁（崩溃/重启）时无处可聚焦 | `Process.Start(dshExe)` → Electron 的 `second-instance` 回调会 `restore/show/focus` 主窗口 |

实测：正常、最小化（`IsIconic=True`）、`ShowWindow(SW_HIDE)` 隐藏三态下，单击宠物都能把 DSH 拉到前台，
日志 `focused DSH window (click) hwnd=…`，`GetForegroundWindow()` 确认就是 DSH 主窗口。

### 11.4 点击宠物直接打开 DSH

`WM_LBUTTONUP` 且没有拖动时：先 `PostAck()`（有提醒就当成「我知道了」），再 `FocusDsh("click")`。
单击不再用于展开/收起气泡——气泡按时间自动收起（11.7），菜单里的开关才管显示/隐藏。

### 11.5 就地写字 / 语音 / 鼠标离开收起

- **悬停**：`_hoverTimer`（120ms）在指针进入窗口矩形时置 `_hover`，窗口顶部浮出 78×34 胶囊工具条（✎ / 🎤）；
  指针离开 300ms 收起（`HoverVisible = _hover || IsComposing`）。
- **✎ 写字**：`ComposeWindow` 贴在宠物上方的圆角输入框（宠物贴屏幕顶时自动翻到下方），
  Enter 发送 / Shift+Enter 换行 / Esc 关闭 / 失焦且为空自动关。发送走 `POST /say`，宿主用
  `sessionController.prompt({requestId, sessionId, mode:'queue', content:[{type:'text', text}]}, AbortSignal.timeout(15s))`
  把这句**作为一条用户消息**投进当前会话——和你在输入框里打字是同一条链路，不是旁路注入。
- **🎤 语音**：`SpeechRecognitionEngine` + `DictationGrammar`（本机两个引擎，优先中文）。
  `Hypothesized` 把草稿灰字实时填进输入框，`Recognized` 定稿，停 3 秒自动停。
  编译期用 `#define:VOICE` + GAC 的 `System.Speech.dll`；找不到就退化成「这台机器没有可用的语音识别引擎」，
  不会让整个浮层编译失败。

### 11.6 右键菜单：自绘圆角半透明

不用系统 `ContextMenuStrip`（方角、灰底、没有真悬停态）。`OverlayMenu` 是一个**分层窗口**自己画：

- 主列 + 子菜单是**同一个窗口的两列**（子菜单开成子窗口的话，父窗口一失活菜单就关）；
- 底色高不透明度 + 1px 描边 + 三层递减阴影，深浅色跟随
  `HKCU\…\Themes\Personalize\AppsUseLightTheme`；
- 子菜单靠近屏幕右缘时**整体翻到主列左侧**（`_contentShift` 平移，窗口左边界反向抵消）；
- 悬停高亮用 90ms 定时器**轮询**光标位置——置顶无激活窗口不一定收得到 `WM_MOUSEMOVE`，
  纯事件驱动会出现「鼠标停在子菜单项上却不展开」；
- Esc 逐级关闭、失焦 140ms 后关，真 `✓`/`▸` 字形而非文字符号。

> 调试记录：翻到左侧后主列文字整段消失，根因是 `DrawString` 的矩形宽度写成 `rect.Width - textLeft`，
> 而 `textLeft` 是**绝对坐标**，`_contentShift` 一大宽度就变负数。改成 `rect.Right - textLeft` 后正常。

### 11.7 气泡停留时长可设置

`bubbleMs`（默认 5000，菜单里 3 / 5 / 8 秒 / 一直显示 / 关闭气泡）、`alertMs`（默认 30000，结束提醒更久）。
两个值宿主下发、浮层执行；`bubbleMs = 0` 表示常显。

内容「变没变」用指纹判断，指纹里**剔掉了 `第 N 步 · 45s` 那个每秒跳动的计时**（`StripElapsed`），
否则计时一刷新就把气泡重新点亮、永远收不起来。

### 11.8 位置持久（并去掉「重置位置」）

松手 → `POST /config {pos:1, posx, posy}` → 写 `config.json`；启动时用保存的锚点还原，
窗口尺寸变化按「底边中点」重新摆位，再用 `Screen.FromPoint(...).WorkingArea` 夹回可见区；
`WM_DPICHANGED` / `WM_DISPLAYCHANGE` 触发重新布局与夹取，插拔显示器不会把宠物丢到屏幕外。

### 11.9 顺手补的（原 UX 评审里未落地的 P1）

| 项 | 说明 |
| --- | --- |
| 深色主题 | 菜单、气泡、工具条按系统深浅色切换调色板 |
| 错误可见 | 编译失败 / 网络失败 / 发送失败以红字写进气泡或输入框，不再只进日志 |
| 结束时长提示 | `alert.age`：提醒超过 1 分钟，正文追加「N 分钟前结束」 |
| `/pet` 补全 | 新增 `bubble <秒>`、`say <内容>`、`test failed` |
| 中文换行 | 正文按 CJK 逐字断行（旧版按空格切，中文整段溢出成一行省略号） |
| 断开可见 | 宿主失联 4 次后气泡写 `DSH 已断开，重连中…`，不再拿旧进度装没事 |
| 图标重绘 | 蓝色徽章 + 对话气泡脸，`tools/make-icon.py` 出多尺寸 `app.ico` |

### 11.10 这一轮的验收

| 需求 | 结论 / 证据 |
| --- | --- |
| 宠物随 DSH 生死 | `--dsh-pid 999999` → 0.22s 退出并记 `DSH 已退出，浮层退出`；重新启用插件自动重启并回到原位置（`22-restored-0.2.png`） |
| 去托盘图标 | `NotifyIcon` 计数 0，通知区无残留 |
| 「打开 DSH」bug | 两处根因定位并修复；正常 / 最小化 / 隐藏三态实测均能拉起 |
| 点击宠物打开 DSH | 同上，单击即拉起 |
| 就地写字 | `overlay.log` `say result: 已发送`，宿主日志 `浮层消息 → <sessionId>：…` |
| 语音 | SAPI 两个引擎可用，`#if VOICE` 编译通过；无引擎时降级提示 |
| 菜单美化 | `20-menu-0.2.png`：圆角半透明、真 ✓、子菜单左翻 |
| 气泡时长 | `bubbleMs=1000` 时 4 秒后气泡与工具条消失、宠物留在原地 |
| 位置持久 | 拖到左下后写 `pos`，禁用→启用后原位复原（`22-restored-0.2.png`） |
| 最小化仍可用 | `23-minimized-alert-0.2.png`：`IsIconic=True` 时宠物与失败提醒气泡都在桌面 |
