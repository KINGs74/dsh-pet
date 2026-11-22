## 0.3.1 —— 卡死的真根因找到了：不是宠物，是 DSH 的 HMR 在重建 profile

上一版把卡死归给了 `AttachThreadInput`，这一版先在真机复验：那个结论**不成立**——0.3 已经把
`AttachThreadInput` 与"重启 DSH"兜底全删了，卡死仍然复现。加了事件循环采样器之后，根因拿到了。

### ① 卡死根因：`dsh-hmr` 的 `reconcileProfile()` 同步遍历文件系统

四份 CPU profile 的 inclusive 栈完全一致：

```text
apply                                    …dsh-hmr/lib/index.js:120
  reconcileProfile                       …dsh-hmr/lib/index.js:1324
    mappedDirectories                    …dsh-hmr/lib/index.js:1432
      realSpellings                      …dsh-hmr/lib/index.js:1445
        realpathSync                     node:fs:2781
          lstat                          (native)  1.0–1.8 s
```

- **什么时候触发**：`dsh-hmr` 的 `refresh()`（`lib/index.js:852`）只在 `profile.patchPath`、
  `profile.home/cordis.patch.yml`、`profile.dir/package.json`（且 `dsh.profile.bundles` 变了）变化时重建 profile
  ——也就是**启用/停用/安装插件**，以及 DSH 启动。这就是"随机"的来源：平时对话不触发，一动插件必触发。
- **阻塞多长**：单次 `realpathSync`/`lstat` 遍历 1.0–1.8 秒，累计 2.3–8.2 秒（四份 profile：2287 / 2578 / 6355 / 5034 ms）。
- **为什么是"进程在、界面不动"**：同步文件系统调用占满 Node 事件循环，DSH 宿主无法处理 IPC，Electron 渲染进程等不到响应；
  Windows 事件日志同期没有 WER AppHang/AppError，不是崩溃。
- **对照组**：稳态 240 秒 / 250 ms 探针 937 次，DSH UI 线程最长 17 ms、`/state` 最长 862 ms、0 失败；
  往 `~/.dsh/pets/`、工作区根、插件包目录写文件均无 `loop lag`；`plugin_manager set_bundle` 重载必现 3.2–5.5 秒阻塞。

> 完整证据与两条"DSH 侧才能改"的建议（profile 换非符号链接布局、把 `~/.dsh/profiles` 加进 Defender 排除）
> 见 [`lag-analysis.md`](lag-analysis.md)。

### ② 最外面的框：是气泡的三层阴影 + 1px 常驻描边，不是窗口边框

`GetWindowLongPtr` 显示浮层窗口 `style=0x16010000`（`caption=False border=False thick=False`）、
`ex=0x08090088`；沿窗口下边界取像素，内外差 **0**——窗口本身没有边框、没有矩形填充。
真正被看见的是气泡外圈的三层圆角阴影加一条常驻描边。现在换成单层扁平半透明填充，
提醒态靠填充色 + `✔/✕` 区分；文字渲染也从 `ClearTypeGridFit` 改为 `AntiAliasGridFit`
（逐像素 alpha 的分层窗口不能做子像素抗锯齿，否则彩边会烧进位图）。

同一位置、同一份配置，0.3.0 与 0.3.1 两个二进制各拍一张：`docs/img/34-frame-compare-0.3.1.png`。

### ③ 拖动残影：位置变了，合成帧还等 80 ms 的动画定时器

拖动的处理只改 `Location`，`PushLayered()` 仍由动画定时器驱动，窗口挪走后屏幕上留着上一帧内容。
现在位置一变就在 8 ms 内补推一帧（`_dragPushAt` 节流），松手再 `Relayout` 收尾。

验证脚本 `tools/ghostcheck.py` 可复跑：200 帧连拍里单帧最多 2 块（气泡 + 宠物），从未出现第三块；
松手 1.5 秒后的画面与干净基准**逐像素一致**（`bbox=None`，最大通道差 0）。

### ④ 麦克风点了没反应

`overlay.log` 里攒了 8 次
`InvalidOperationException: 在创建窗口句柄之前，不能在控件上调用 Invoke 或 BeginInvoke。`，
栈是 `ComposeWindow..ctor ← PetForm.OpenCompose ← PetForm.WndProc`——正是点 🎤 的路径：
构造函数在窗口句柄还没创建时就 `BeginInvoke(ToggleVoice)`。现在构造里只记 `_voicePending`，
`ToggleVoice()` 挪到 `OnShown`；引擎不可用时按钮变灰并给红字原因。

### 新增的取证工具

- `src/profiler.js`：`DSH_PET_PROFILE=1` 或标记文件 `~/.dsh/pets/profile.on` 时启用。
  `monitorEventLoopDelay` 20 ms 采样 + `setTimeout` 心跳，单次阻塞超 1500 ms 就停表写
  `~/.dsh/pets/logs/lag-<ISO>.cpuprofile`，并记 `loop block <n>ms top=<函数@文件:行>`。
  V8 采样线程独立于事件循环，所以"卡死那几秒"的栈照样能记下来——这一轮的根因就是它抓到的。
- `tools/probe.ps1`：用 `SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG)` 分别量 DSH 主窗口与浮层窗口的 UI 响应，
  叠加 `/state` 的 HTTP 时延，落 CSV。
- `tools/soak.ps1`：固定操作序列（悬停 / 菜单 / 写作 / 语音 / 拖动 / `/say` / 最小化）跑长跑。
- `tools/ghostcheck.py`：拖动残影检测（连拍帧分块统计 + 松手后逐像素比对）。

> 两个 PowerShell 脚本里都按 pid 用 `EnumWindows` 找窗口：浮层是 LAYERED|TOOLWINDOW|NOACTIVATE，
> `Process.MainWindowHandle` 恒为 0。脚本本身是 UTF-8 with BOM，否则 Windows PowerShell 5 会按 GBK 解析报错。

### 回归

- `node --test test/` → **35 个用例全绿**（新增 `profiler.test.mjs` 3 例 + 配置回滚写回 1 例）。
- `csc -warn:4` → **0 警告**，浮层正常编译并启动。
- DSH 最小化时宠物与气泡照常；配置只走 `~/.dsh/pets/config.json`（`.bak`/`.prev` + 坏文件留档），
  全程没有写 DSH 的会话库。

### 安装

```text
plugin_manager action=install_bundle target=<本 tgz 路径或下载链接>
```

或本地开发直接指向包目录：

```text
plugin_manager action=install_bundle target=C:\path\to\dsh-pet
```