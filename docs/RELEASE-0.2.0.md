## 0.2.0 —— 常驻浮层的第二轮：像 Codex 那样用

这一版把「宠物能不能用起来」变成了「宠物好不好用」。三个真 bug 修完，交互补齐，托盘彻底去掉。

### 三个 bug 的根因（都定位到具体代码行）

- **退出 DSH 宠物不消失**：旧版只有「45 秒拿不到状态就退出」的看门狗，宿主没了宠物还在屏幕上干站 45 秒。
  现在宿主把 `process.ppid` / `process.execPath` 交给浮层，浮层每轮 `OpenProcess` + `GetExitCodeProcess` 判断，
  宿主一没立刻退出（实测 **0.22 秒**）。
- **「打开 DSH」有时完全没反应**：两个原因叠加。① 旧实现 `if (!IsWindowVisible(h)) return;`，而 DSH 点关闭走的是
  `mainWindow.hide()`（只有 `isQuitting()` 才 `close()`），窗口隐藏后就永远「找不到窗口」；② 浮层是
  `WS_EX_NOACTIVATE` 工具窗、从不拥有前台，`SetForegroundWindow` 被系统静默拒绝，而旧代码没检查返回值、日志照样打「focused」。
  修法：按 pid 枚举**不看可见性**、按标题/可见/非最小化打分选主窗口；`ShowWindow` + `AttachThreadInput` 借前台输入队列重试 3 次
  并校验 `GetForegroundWindow()`；主窗口真被销毁时 `Process.Start(dshExe)` 兜底，Electron 的 `second-instance` 会自己 restore/show/focus。
- **中文正文永远单行省略**：换行按空格切，中文整段是一个「词」，于是溢出成一行 `…`。改成 CJK 逐字断行 + 词边界。

### 新交互

- **点一下宠物就打开 DSH**，和 Codex 一致；有未确认的结束提醒时，这一下同时算「我知道了」。
- **悬停浮出 ✎ / 🎤 工具条**（离开 300ms 收起）：
  - ✎ 就地写字，Enter 发送 → 走 `sessionController.prompt(...)`，**作为一条用户消息**投进当前会话（不是旁路注入）；
  - 🎤 语音听写（SAPI，本机中英两个引擎），草稿灰字实时上屏，停 3 秒自动结束；没有引擎的机器只降级提示，不影响其它功能。
- **右键菜单重绘**：分层窗口自绘，圆角 + 半透明 + 真 `✓`/`▸`，深浅色跟随系统；子菜单贴近屏幕右缘时**整体左翻**，
  悬停高亮用 90ms 轮询（置顶无激活窗口不一定收得到 `WM_MOUSEMOVE`）。
- **气泡不再永久显示**：`bubbleMs` 默认 5 秒（菜单可选 3 / 5 / 8 秒 / 一直显示 / 关闭气泡），结束提醒 `alertMs` 默认 30 秒。
- **位置只认你拖到的地方**：去掉「重置位置」，拖完就记进 `config.json`；DPI 变化、插拔显示器都会夹回可见区。
- **托盘图标彻底移除**：原本托盘只承担「系统通知 / 菜单入口 / 退出入口」，现在分别由气泡+提示音、右键菜单、菜单里的「退出宠物窗」接管。

### 其它

- 深色主题、失败时红字可见、提醒超过 1 分钟补「N 分钟前结束」、`/pet` 新增 `bubble <秒>` / `say <内容>` / `test failed`。
- 新图标：蓝色徽章 + 对话气泡脸（`tools/make-icon.py` 生成多尺寸 `app.ico`）。

### 安装

```bash
# 在 DSH 里（推荐，从仓库装最新代码）
plugin_manager  action: install_bundle  target: C:\\path\\to\\dsh-pet
# 或别人的机器上，直接用这一版的 tgz
plugin_manager  action: install_bundle  target: https://github.com/KINGs74/dsh-pet/releases/download/v0.2.0/dsh-pet-0.2.0.tgz
```

要求：Windows 10/11、DSH ≥ `0.2.1-rc.3`、Node ≥ 22.6（系统自带 .NET Framework 4 的 `csc.exe` 即可编译浮层，无需装 SDK）。

### 已知限制

- 只有 Windows 有浮层（宿主仍可装，只是不启动浮层）。
- 位置按「底边中点」记录，混合 DPI 的多屏之间切换可能偏几像素，拖一下即定。
- 热重载插件时，旧实例的前缀路由可能残留在当前宿主进程里，插件会自动落到 `/x-dsh-pet-2` 之类的备用路径；重启 DSH 即回到 `/x-dsh-pet`。

完整设计见 [docs/DESIGN.md](https://github.com/KINGs74/dsh-pet/blob/main/docs/DESIGN.md) 与 [docs/DESIGN.html](https://github.com/KINGs74/dsh-pet/blob/main/docs/DESIGN.html)。
