## 0.3.0 —— 修掉 0.2 引入的随机卡死，把动作区从气泡上摘下来

这一版没有新玩法，主要回答三个问题：**为什么 0.2 会卡死**、**为什么菜单点了没反应**、**最外面那圈框是什么**。
三个都定位到了具体代码，并且都带证据行。

### ① 随机卡死：`AttachThreadInput` 把浮层线程缝进了 Electron 的输入队列

0.2 为了让「打开 DSH」在被系统拒绝抢前台时也能成功，用了 `AttachThreadInput(myThread, foregroundThread, true)`
→ `SetForegroundWindow` → 解挂的写法。`foregroundThread` 往往就是 Electron 的 UI 线程：**Attach 之后两条线程
共享输入队列，Electron 的窗口消息可能被浮层线程取走并派发**，Electron 的界面于是离开了自己的 UI 线程——
表现就是「随机卡死、界面完全不动、进程还在」。随机的原因：只有「抢前台被拒」这条分支才 Attach、只挂几十毫秒，
只有恰好撞上 Electron 的消息处理才出事。旁边还有个放大器：兜底的 `Process.Start(dshExe)` 会在同一份 userData 上再拉一个实例。

证据：

| 层面 | 事实 |
| --- | --- |
| 代码 | 0.1 没有 `AttachThreadInput`、没有 relaunch 兜底；卡死也是 0.2 才出现 |
| `overlay.log` | `focus refused (click), falling back to relaunch` + `relaunched DSH exe`，03:38:58 / 03:41:33 / 03:48:21 |
| `host.log` | 卡死期间宿主一直健康，只有 3 条孤立 `poll failed`，从没 `DSH 已断开` / `host unreachable` ⇒ 卡的是 Electron，不是 Node 宿主 |
| Windows 事件日志 | 03:20–04:05 无 WER AppHang / AppError ⇒ 不是崩溃，是进程内挂住 |

修法：两条路径全删。`Activate()` = `ShowWindowAsync` → `SetWindowPos(HWND_TOP, SWP_NOACTIVATE)` → `BringWindowToTop`
→ `SetForegroundWindow` → 60ms 后校验 `GetForegroundWindow()`；失败只让任务栏闪一下（`FlashWindowEx`），
**不再重启 DSH、不再 Attach**。`grep -c AttachThreadInput native/*.cs` = 0。

### ② 「菜单点了没反应」：鼠标捕获悬挂，点击被宠物窗口吃掉

现场探针很反直觉——`WindowFromPoint()` 返回的明明是菜单窗口，z 序也正常。决定性的一行是：

```
WM_LBUTTONDOWN: captureBefore=190123676  capture now=190123676  self=190123676
```

宠物窗口在点击**之前**就已经握着鼠标捕获。持有捕获的窗口直接收后续鼠标消息，`WM_NCHITTEST` 不会再发出去，
所以菜单永远等不到 mouse-up。根因：旧代码每次 `WM_LBUTTONDOWN` 就 `SetCapture`，却只在 `if (_dragging)`
分支里 `ReleaseCapture`——一次 up 丢失，捕获就永久挂住。

三层修法：**懒捕获**（移动超过 4px 才开始抢）、**自愈**（每 120ms 检查，捕获在手上但没在拖动就释放，
日志 `released stale capture`）、**让位**（菜单或写作窗开着时，宠物窗口的命中测试直接返回 `HTTRANSPARENT`）。

### ③ 动作区移到宠物下方，去掉最外面的框

- 布局从 `[气泡] → 宠物` 变成 `[动作行] → [气泡] → 宠物`；动作行是宠物自己的区域，不再浮在气泡上。
- 窗口贴到底边时动作行自动翻到气泡上方（`_actionsBelow` 判定），四种边都夹回可见区。
- 「最外面的框」就是那枚白胶囊，已删除：每个按钮只画自己的柔和圆底（常态 alpha 214 / 悬停 248 + 1px 投影），
  图标换成 Segoe MDL2 Assets 的铅笔 / 麦克风，11pt 抗锯齿。
- 气泡也去掉常驻 1px 描边，改成三层半透明圆角阴影；只有「结束提醒」态才画暖色（成功）/ 红色（失败）描边。
- 顺手修掉一个渲染坑：`TextRenderingHint` 从 `ClearTypeGridFit` 改为 `AntiAliasGridFit`。分层窗口是逐像素 alpha，
  ClearType 的次像素彩边会被原样烧进位图（字周围出现蓝红虚影）。

### ④ 配置读写成对加固（不碰会话历史）

写之前留 `.bak`（首次原始值）与 `.prev`（上一次成功值），写入走 `tmp + rename` 原子替换；读失败时把坏文件
另存为 `config.json.corrupt-<ISO>`，先试 `.bak` 再试 `.prev`，并**立刻把盘上文件写回合法 JSON**。
爆炸半径只有 `~/.dsh/pets/` 下这三个文件——代码里没有任何指向会话库 `~/.dsh/storages/session_projcache/` 的路径。
实机演练：把 `config.json` 截断成非法 JSON → `host.log` 记下留档路径并从 `.bak` 恢复，重启后配置/位置/宠物原样。

### ⑤ 观测手段与「不是所有 loop lag 都是 bug」

- 宿主：`setInterval(2000)` 比对预期节拍，超过 1500ms 记 `loop lag <n>ms`。
- 浮层：后台线程每 2s `Invoke` 一次心跳，超过 1500ms 记 `ui stall <n>ms`。
- 守护：子进程活过 30s 就清零重启计数（偶发崩溃不再耗尽 5 次预算），另加 `/pet restart` 手动重拉。

**实测**：0.3 秒间隔连续请求 `/state` 共 700 次（210 秒），**没有一次超过 1 秒**（`{"total": 210.1, "spikes": []}`）。
历史上看到的 8.1s / 6.3s / 5.9s 三条 `loop lag` 全部落在 `浮层已启动` 之后 6–9 秒：浮层刚起来解码 3.5MB 精灵图、
Defender 在扫新 exe，宿主只是没抢到时间片。稳态不复现。

### 其它

- 菜单里新增「复制最近一条」，把气泡正文送进剪贴板。
- 菜单以鼠标为主（悬停展开子菜单、点一下生效）。浮层是「不抢焦点」的窗口，键盘事件只有菜单恰好持有焦点时才到得了，
  所以 ↑↓←→ / Enter / Esc 只作为补充路径——这一轮试过全局键盘钩子，代价与风险不值，已经撤掉。
- `/pet` 新增 `restart`；`/pet bubble <秒>`、`/pet say <内容>`、`/pet test failed` 沿用。

### 安装

```bash
# 在 DSH 里（推荐，从仓库装最新代码）
plugin_manager  action: install_bundle  target: C:\\path\\to\\dsh-pet
# 或别人的机器上，直接用这一版的 tgz
plugin_manager  action: install_bundle  target: <本页 dsh-pet-0.3.0.tgz 的下载地址>
```

### 要求

- Windows 10/11（浮层是 C#/WinForms 原生进程，用系统自带的 `csc.exe` 编译，无 npm 依赖）
- DSH ≥ 0.2.1-rc.3，Node ≥ 22.6
- 语音听写需要系统里装有 SAPI 识别引擎（中文/英文各一），没有会降级提示，不影响其它功能

### 已知限制

- 只支持 Windows（浮层是原生窗口）。
- 一台机器同时只跑一个浮层实例（命名互斥量），多开会被拒绝。
- 声音/位置等设置存在 `~/.dsh/pets/config.json`，删掉插件不会删这些文件。