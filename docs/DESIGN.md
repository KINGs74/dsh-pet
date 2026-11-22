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

## 13. 第三轮：0.2 卡死的根因、菜单点不动、按钮挪到宠物下方（0.3.0）

这一轮有一条硬要求：**先查根因，不许重试猜**。三个问题分别定位如下。

### 13.1 0.2 偶发卡死：`AttachThreadInput` 把浮层线程挂进了 Electron 的输入队列

0.2 为了把 DSH 主窗口"抢"到前台，用了 Win32 的经典套路：

```csharp
// native/NativeMethods.cs（0.2，已删除）
uint foregroundThread;
Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out foregroundThread);
bool attached = Native.AttachThreadInput(Native.GetCurrentThreadId(), foregroundThread, true);
bool ok = Native.SetForegroundWindow(hWnd);
if (attached) Native.AttachThreadInput(Native.GetCurrentThreadId(), foregroundThread, false);
```

`AttachThreadInput(my, electron, TRUE)` 的语义是：**把两个线程的输入状态合并**——按键状态、捕获、
以及（关键）**消息队列的窥探权**。合并期间，Electron 的 UI 线程与浮层线程共享输入队列；浮层线程
并不是 Electron 的 UI 线程，却在这几十毫秒里具备了从**对方队列里取消息**的资格。于是：

1. `SetForegroundWindow` 被拒（前台锁），代码进下一次重试，`Thread.Sleep(80)`，再来一遍；
2. 合并要等 Electron 的 UI 线程在消息泵上"点头"才真正建立/解除，WinForms 的
   `GetMessage` 又在同一个 STA 线程上跑我们的 `PollLoop` 回调；
3. 一旦两次 attach 之间的解除被排到 Electron 的某个长消息之后，Electron 的窗口消息就会被
   浮层线程取走一部分——**该由 UI 线程处理的消息落到别的线程**，Electron 侧表现为
   "界面不动、进程还在、没有崩溃记录"。

证据链（都不是"猜"）：

| 证据 | 内容 |
| --- | --- |
| 只有 0.2 有 | 0.1 的 `NativeMethods.cs` 里没有 `AttachThreadInput`、也没有重启 DSH 的兜底；用户反馈"卡死是 0.2 之后才有" |
| 触发点有日志 | `overlay.log`：`focus refused (click), falling back to relaunch` + `relaunched DSH exe (click)`，03:38:58 / 03:41:33 / 03:48:21 各一次——**只有抢前台失败才会 attach，也才会走到重启** |
| 卡的是 Electron 不是宿主 | 同一时间段 `host.log` 只有 3 条孤立的 `poll failed (1)`，从未出现 `DSH 已断开`／`host unreachable`；宿主事件循环一直在跑 |
| 系统层没有崩溃 | Windows 事件日志 03:20–04:05 无 WER `Application Error` / `AppHang`，说明是**进程内挂起**，不是崩溃 |

**为什么是随机的**：只有"前台被拒"这一支会 attach；attach 窗口只有几十毫秒；能不能在那几十毫秒里
和 Electron 的消息泵撞上，取决于当时谁在跑——所以十分钟里可能一次都没有，也可能连中三次。

**放大器**：兜底用 `Process.Start(_dshExe)` 起第二个 Electron。Electron 有单实例锁
（`requestSingleInstanceLock` + `second-instance` → `focusPrimaryWindow`），所以第二个实例会自己退出，
但在"第一个实例正在忙/正在启动"的窗口期里，它依然会去抢 userData 目录与 IPC 端口——把偶发变成更偶发。

**修法（0.3）**：彻底不用 `AttachThreadInput`，也不重启 DSH。改成"尽力但不越界"的激活序：

```
ShowWindowAsync(SW_SHOW | SW_RESTORE)   // 异步，不阻塞
→ SetWindowPos(HWND_TOP, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE|SWP_SHOWWINDOW)
→ BringWindowToTop
→ SetForegroundWindow
→ Sleep(60) 后核对 GetForegroundWindow() == hWnd
   成功 → "focused DSH window"
   失败 → FlashWindowEx(FLASHW_ALL|FLASHW_TIMERNOFG) 只闪任务栏
```

全部在我们自己的线程上完成，绝不碰别的进程的线程状态；失败也只是"没抢到前台"，用户再点一次即可。

### 13.2 菜单点不动：宠物窗口的 `SetCapture` 悬挂

0.2 的 `WM_LBUTTONDOWN` 一进来就 `SetCapture(Handle)`（为了支持拖出窗口），`WM_LBUTTONUP` 里再
`ReleaseCapture()`。问题在于"单击"和"拖动"混在一条路径上：

- 按下即抓捕获，**即使这不是拖动**；
- 只要有一对 up 没落到我们窗口（点到了刚弹出的菜单、点到了任务栏、或者 `WM_LBUTTONUP` 被别的窗口吃掉），
  捕获就悬在那里；
- 捕获一旦悬着，Windows 会把**所有**鼠标消息直接投给宠物窗口，绕过 `WM_NCHITTEST`、
  绕过 z 序——菜单窗口再顶层也收不到点击。用户看到的就是"右键菜单弹出来了，但点任何一项都没反应，
  反而是宠物被点了（被叫去打开 DSH）"。

复现证据：`overlay.log` 里点击菜单行的坐标只留下 `mouse down at 950,760`（宠物窗口的日志），
菜单侧一条 `mouse mouseup` / `menu pick` 都没有；在 `WM_LBUTTONDOWN` 里打印 `GetCapture()`，
得到的是**宠物自己的 hwnd**（`captureBefore=190123676 self=190123676`），即捕获在点击之前就已经在宠物手上。

三层修法：

1. **懒捕获**：按下不抓；`WM_MOUSEMOVE` 里位移超过 4 px、确认是真拖动时才 `SetCapture`；
2. **自愈**：120 ms 的悬停计时器里，如果"没在拖动却持有捕获"，立即 `ReleaseCapture()` 并记 `released stale capture`；
3. **让位**：菜单或写作窗可见时，宠物的 `WM_NCHITTEST` 直接回 `HTTRANSPARENT`——即使 z 序把宠物排在菜单上面，
   点击也会穿到菜单去。

修完实测：`menu mouseup at 71,46 button=Left` → `menu pick open` → `focused DSH window (menu) hwnd=25758328`。

### 13.3 ✎ / 🎤 挪到宠物正下方，并且去掉最外层那个框

0.2 的 ✎/🎤 是气泡上方的一条白胶囊（`DrawToolbar` 里 `FillPath(白色) + DrawPath(1px 描边)`）——
既压住气泡，又显得像"另一个面板"。0.3 改了布局与画法：

- **布局**：`Relayout()` 现在按 `[动作行] → [气泡] → 宠物` 垂直堆叠；`_actionsBelow` 用
  `workingArea.Bottom - anchorY >= actionsH + gap + pad` 判断——宠物贴到屏幕底边时自动翻转成
  `[气泡] → 宠物 → [动作行]`，两个按钮永远在可视区内。
- **锚点**：`ApplyAnchor()` 改成 `y = _anchorY - _petBottomOffset`，即**始终以宠物底边为锚**，
  所以动作行出现/消失、气泡弹出/收起都不会让宠物自己跳位。
- **画法**：删掉白胶囊；每个按钮是自己的一枚柔和圆形底（`DrawButtonPlate`，常态 alpha 214、
  悬停 248，外加 1px 投影），图标换成 Segoe MDL2 Assets 的 `\uE70F`（铅笔）/ `\uE720`（麦克风），
  11pt 抗锯齿渲染。
- **气泡**：去掉 1px 常驻描边，改成三层由内到外的半透明圆角阴影；只有"结束提醒"态才画
  暖色/红色描边——这样"有没有事"一眼可辨，而平时看不见"框"。
- **文字渲染**：`_composeG.TextRenderingHint` 从 `ClearTypeGridFit` 改为 `AntiAliasGridFit`。
  分层窗口是逐像素 alpha，ClearType 的次像素彩边会被原样烧进位图（字周围出现蓝红虚影），
  必须用灰度抗锯齿。

### 13.4 不再碰会话历史：`config.json` 的读写成对加固

用户明确说过"会话丢过一次"。这次只动了浮层自己的配置文件，并且把读写做成"可回滚"：

| 时机 | 动作 |
| --- | --- |
| 写之前 | 若没有 `.bak`，把当前 `config.json` 复制成 `config.json.bak`（**首次写盘的原始值**） |
| 每次写之前 | 把当前值复制成 `config.json.prev`（**上一次成功值**） |
| 写入 | `config.json.<pid>.tmp` + `rename`（原子替换，不会出现半个文件） |
| 读失败（JSON 坏） | 原文件另存 `config.json.corrupt-<ISO 时间>`；先试 `.bak`，再试 `.prev`；都失败才用默认值，并把过程写进 `host.log` |

爆炸半径：只有 `~/.dsh/pets/` 下这三个文件；`config-store.js` 里没有任何指向 DSH 会话库
（`~/.dsh/storages/session_projcache/`）的路径，测试也覆盖了"坏文件 → 留档 → 回滚 → 再写回正常"的闭环。

### 13.5 长阻塞的观测手段（不是修完就算）

- 宿主侧：`setInterval(2000)` 里比较 `Date.now()` 与预期节拍，超过 1500 ms 记 `loop lag <n>ms`
  （`lagTimer.unref()`，插件卸载时清掉）；
- 浮层侧：`StartStallWatchdog()` 后台线程每 2 s `Invoke` 一次做心跳，超过 1500 ms 记 `ui stall <n>ms`；
- 守护进程：`OverlaySupervisor` 子进程活过 30 s 就清零重启计数（偶发崩溃不再耗尽 5 次预算），
  并新增 `/pet restart` 手动重拉。

**实测结论（两轮稳态采样）**：① 0.3 秒间隔连续请求 `/state` 共 700 次（210 秒），没有一次超过 1 秒
（`{"total": 210.1, "spikes": []}`）；② 1 秒间隔连续 10 分钟共 **590 次**，
`{"requests": 590, "seconds": 600.5, "spikes": []}` —— 同样零尖峰，而且这一轮跨了一次系统休眠：
机器睡了一夜醒来后浮层进程（pid 40212）仍在、`/state` 立刻有响应、会话缓存 58 个 JSON 全部可解析。历史上观察到的三条 `loop lag`（8.1s / 6.3s / 5.9s）
全部出现在 `浮层已启动` 之后的 6–9 秒内——那一段是浮层进程刚起来解码 3.5 MB 精灵图 + Defender 扫新 exe 的
CPU 尖峰，宿主只是没抢到时间片，不是宿主代码在同步阻塞。稳态没有复现，这与"0.2 卡死是 Electron 侧被
`AttachThreadInput` 拖住"的结论也一致：真正的卡死伴随界面完全不动，而这里只是看门狗的一次数秒抖动。

### 13.6 这一轮的验收

| 需求 | 结论 / 证据 |
| --- | --- |
| 卡死根因 | `AttachThreadInput` + 重启兜底，两条路径 0.3 全部删除；`grep -c AttachThreadInput native/*.cs` = 0 |
| 按钮在下方 | `26-actions-below-0.3.png`；贴底边翻转 `27-actions-flip-0.3.png` |
| 去掉外层框 | `DrawToolbar` 已改名 `DrawActions`，无填充胶囊、无常驻描边 |
| 菜单可点 | 点击「打开 DSH」→ `menu pick open`；点击「气泡显示 → 8 秒」→ `config.json` `bubbleMs: 8000`；点击「切换宠物 → Boba」→ 精灵热切换为 `1536x2288`（`28-menu-sub-0.3.png`） |
| 键盘（补充路径） | 菜单不抢焦点，只有它恰好持有焦点时 `ProcessCmdKey` 才会收到按键；不把键盘当主路径，也没有为此挂全局键盘钩子（试过，代价与风险不值） |
| 配置不丢 | `test/config-store.test.mjs` 6 个用例 + 实机坏文件演练：留档 → 从 `.bak` 恢复 |
| 长时间不卡 | 10 分钟 / 590 次请求零尖峰；跨系统休眠后进程仍在、接口仍通 |
| 会话不丢 | `~/.dsh/storages/session_projcache/sessions/` 58 个 JSON 全部可解析（休眠前后各验一次） |
| 回归 | `node --test test/` 32 个用例全绿；`csc -warn:4` 0 警告 |

## 14. 第四轮：0.2/0.3 卡死的真根因、外层框、拖动残影、语音按钮（0.3.1）

这一轮用户明确否掉了上一轮的结论（"`AttachThreadInput` 是根因就收工"），要求先在真机复验再改。
下面四条按"问题 → 证据 → 改法 → 怎么验证"写，第 1 条是根因结论。

### 14.1 卡死：根因是 DSH 自己的 HMR 在重建 profile 时同步遍历文件系统

**结论**：卡死不是宠物浮层把 DSH 拖住，也不是 `AttachThreadInput` 残留（0.3 已经删干净，
`grep -c AttachThreadInput native/*.cs` = 0）。真正阻塞事件循环的是 **`@deepseek-ai/dsh-hmr` 的
`reconcileProfile()`**：它在插件清单变化时同步 `realpathSync`/`lstat` 遍历整个 profile 目录树，
单次 1.0–1.8 秒，累加到 2.3–8.2 秒。

**证据链**（都在 `~/.dsh/pets/logs/`）：

| 证据 | 内容 |
| --- | --- |
| 4 份 CPU profile | `lag-2026-11-22T09-14-08-404Z` 2287ms、`09-15-57-064Z` 2578ms、`09-16-05-484Z` 6355ms、`09-28-04-083Z` 5034ms |
| inclusive 栈（四份完全一致） | `apply` → `dsh-hmr/lib/index.js:120` → `reconcileProfile` `:1324` → `mappedDirectories` `:1432` → `realSpellings` `:1445` → `realpathSync` `node:fs:2781` → `lstat`（native）1.0–1.8 s |
| 触发条件 | `dsh-hmr` 的 `refresh()`（`lib/index.js:852`）只在 `profile.patchPath`、`profile.home/cordis.patch.yml`、`profile.dir/package.json`（且 `dsh.profile.bundles` 变了）三者变化时触发 |
| 为什么"随机" | 只有"启用/停用/安装插件"（含 DSH 启动）才会改这些文件；平时的对话、拖拽、写作都不触发。用户感知到的随机，其实是"每次我动了插件" |
| 对照组 | 稳态 240 s / 250 ms 探针 937 次：DSH UI 线程最长 **17 ms**、`/state` 最长 **862 ms**、0 失败 |
| 对照组 2 | 往 `~/.dsh/pets/` 写 6 次、工作区根 6 次、插件包目录 4 次 → 全部无 `loop lag` |
| 对照组 3 | `plugin_manager set_bundle` 重载 → 必然 3.2–5.5 s 阻塞 |
| Windows 事件日志 | 同期无 WER Application Error / AppHang ⇒ 进程内挂起，不是崩溃 |

完整分析见 [`lag-analysis.md`](lag-analysis.md)。

**为什么不是我们的代码**：宿主所有请求路径都没有同步 fs / `child_process`（`grep -n "Sync" src/*.js` 只命中
`spawn`）；浮层不碰 DSH 的进程与线程（0.3 之后不再 `AttachThreadInput`、不再 `Process.Start`）。
本轮新增的采样器还会在阻塞发生时打印 `loop block <n>ms top=<函数@文件:行>`，下一次卡死可以直接看日志定位，
不需要再猜。

**顺带修掉的两个真问题**（它们会加重卡死的观感）：
1. `ComposeWindow` 构造里 `BeginInvoke(ToggleVoice)`——句柄未建，日志里攒了 8 次
   `InvalidOperationException: 在创建窗口句柄之前…`，正是"点麦克风没反应"的原因；
2. 浮层在 DSH 卡住时会把气泡停在旧内容上，看起来"卡死了"。现在连续 4 次轮询失败就换成
   `DSH 已断开，重连中…`，把"卡"如实显示出来。

**DSH 侧才能真正消除**（文档已写明，供上报）：profile 目录改非符号链接布局，或把
`~/.dsh/profiles` 加进 Defender 排除；`dsh-hmr` 的 `refresh()` 官方若改异步/增量遍历，这个卡顿就没了。

### 14.2 最外面的框：不是窗口边框，是气泡的阴影 + 描边栈

**证据**：`GetWindowLongPtr` 显示浮层窗口 `style=0x16010000`（`caption=False border=False thick=False`）、
`ex=0x08090088`（LAYERED|TOOLWINDOW|NOACTIVATE）；沿窗口下边界取像素，内外差 **0** —— 窗口本身完全透明，
不存在矩形填充。

**改法**：删掉三层圆角阴影 + 1px 常驻描边，气泡改单层扁平半透明填充（`244,252,252,254`；
提醒态 `250,251,243,226` / `250,253,236,233`）；顺带把 `Segoe MDL2` 图标外的 ClearType 换成
`AntiAliasGridFit`（分层窗口是逐像素 alpha，子像素抗锯齿会烧出彩边）。

**对比**：`32-frame-before-0.3.1.png`（0.3.0）与 `33-frame-after-0.3.1.png`（0.3.1）同位置同尺寸，
拼图 `34-frame-compare-0.3.1.png`。拖动、点击、气泡绘制都在改后逐项复验过。

### 14.3 拖动残影：位置变了但合成帧没跟着推

**定位**：拖动分支只改 `Location`，`PushLayered()` 仍等 80 ms 的动画定时器；窗口挪走后屏幕上留下的是
上一次 `UpdateLayeredWindow` 的内容，于是看到"拖影"。修复按位置更新与绘制解耦处理：
`Location = ClampWindow(...)` 之后立刻补推一帧，8 ms 节流（`_dragPushAt`），松手再 `Relayout` 收尾。

**验证**：`tools/ghostcheck.py`——先把宠物拖进目标区域再拖走，趁空档截一张干净基准；把宠物拖回来时按
固定间隔连拍，逐帧与基准相减、分块统计。结果：200 帧里"单帧最多 2 块"（气泡 + 宠物，正常形态），
没有出现过第三块；松手 1.5 s 后的画面与基准 **逐像素一致**（`bbox=None`，最大通道差 0）。
证据图 `35-drag-no-residue-0.3.1.png`。

### 14.4 麦克风点了没反应：`BeginInvoke` 早于窗口句柄

**证据**：`overlay.log` 里 8 次
`thread exception: System.InvalidOperationException: 在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke。`，
栈 `DshPet.ComposeWindow..ctor ← DshPet.PetForm.OpenCompose ← DshPet.PetForm.WndProc` —— 正是点 🎤 时那条路径。

**改法**：构造里只记 `_voicePending`，把 `ToggleVoice()` 挪到 `OnShown`（句柄已建）；语音不可用时按钮变灰并给出红字原因。

**验证**：点麦克风 → 写作窗正常弹出、按钮文案变「停止」（已进入录音）、`overlay.log` 无新增异常；
`36-mic-compose-0.3.1.png`。

### 14.5 新增的两件取证工具

- `src/profiler.js`：`DSH_PET_PROFILE=1` 或 `~/.dsh/pets/profile.on` 时启用。`monitorEventLoopDelay`
  每 20 ms 采样，心跳用 `setTimeout`；发现单次阻塞超过 1500 ms 就停表写
  `~/.dsh/pets/logs/lag-<ISO>.cpuprofile`，同时打印
  `loop block <n>ms profile=<文件> top=<函数@文件:行>`。V8 采样线程独立于事件循环，
  所以"卡死那几秒"的栈也能被记下来——这正是这一轮能拿到根因的关键。
- `tools/probe.ps1` / `tools/soak.ps1`：前者用 `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)`
  分别量 DSH 主窗口与浮层窗口的 UI 响应，再叠一个 `/state` 的 HTTP 时延，落 CSV；
  后者按固定序列（悬停/菜单/写作/语音/拖动/`/say`/最小化）跑长跑。
  两个脚本都按 pid 用 `EnumWindows` 找窗口——浮层是 LAYERED|TOOLWINDOW|NOACTIVATE，
  `Process.MainWindowHandle` 恒为 0。

### 14.6 这一轮的验收

| 需求 | 结论 / 证据 |
| --- | --- |
| ① 卡死根因 | `dsh-hmr` `reconcileProfile` 同步 `realpathSync`/`lstat`；4 份 profile 栈一致；对照组 937 次探针最长 17 ms。见 `lag-analysis.md` |
| ② 外层框 | 窗口无边框（`style`/像素双重验证）；删掉气泡阴影+描边后 `32/33/34` 三张对比图 |
| ③ 拖动残影 | 位置更新后 8 ms 内补推一帧；`ghostcheck.py` 200 帧无第三块、松手逐像素一致（`35`） |
| ④ 麦克风 | `InvalidOperationException` 8 次 → 改为 `OnShown` 启动；`36` 可见录音态 |
| 回归 | `node --test test/` **35 个用例全绿**；`csc -warn:4` **0 警告** |
| 最小化可用 | DSH `iconic=True` 时宠物与气泡照常：`37-minimized-alert-0.3.1.png`（最小化下的失败提醒）、`38-pet-minimized-0.3.1.png` |
| 会话不丢 | 本轮只读会话目录；配置改动走 `.bak`/`.prev`/坏文件留档 |
