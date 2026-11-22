# 卡死取证报告（0.3.1）

> 这份文档记录的是**实测数据**，不是推断。凡是写在这里的结论，都能用文中给出的命令重跑。
> 复现装置：`tools/probe.ps1`（窗口/HTTP 探针）、`tools/soak.ps1`（固定操作序列）、宿主里的采样器
> （`src/profiler.js`，`POST /x-dsh-pet/profile {on:1}` 或 `~/.dsh/pets/profile.on` 打开）。

## 1. 现象

- DSH 界面整屏不动，几秒后自己恢复；进程都在（`DeepSeek Harness.exe` 与 `PetOverlay.exe` 都没退）。
- Windows 事件日志在对应时间窗内**没有** WER `Application Error` / `AppHang` ⇒ 不是崩溃，是宿主进程内被同步调用挡住。
- 我的看门狗在卡死时记下 `loop lag NNNNms`：**卡的是 DSH 宿主的事件循环**，而 GUI 是薄客户端，
  宿主不回包，界面就整屏不动。

## 2. 采样器抓到的栈（4 份 cpuprofile，全部同一形状）

| 文件 | 阻塞 | 栈顶（自重） |
| --- | --- | --- |
| `lag-2026-11-22T09-14-08-404Z.cpuprofile` | 2287 ms | `lstat` 1731 ms · `existsSync` 454 ms · `realpath` 311 ms |
| `lag-2026-11-22T09-15-57-064Z.cpuprofile` | 2578 ms | `lstat` 1785 ms · `existsSync` 457 ms · `realpath` 305 ms |
| `lag-2026-11-22T09-16-05-484Z.cpuprofile` | 6355 ms | `lstat` 1316 + 1289 ms · `(idle)` 713 ms |
| `lag-2026-11-22T09-28-04-083Z.cpuprofile` | 5034 ms | `(idle)` 1094 ms · `lstat` 1041 + 1036 ms |

**包含时间（inclusive）把调用方点得很清楚**，四份完全一致：

```
apply                        .../dsh/node_modules/@deepseek-ai/dsh-hmr/lib/index.js:120
└─ reconcileProfile          .../dsh-hmr/lib/index.js:1324   (2551 ms / 2599 ms / 2891+1919 ms)
   └─ mappedDirectories      .../dsh-hmr/lib/index.js:1432
      └─ realSpellings       .../dsh-hmr/lib/index.js:1445
         └─ realpathSync     node:fs:2781
            └─ lstat          (native)                     → 1.0～1.8 s
```

也就是说：**阻塞来自 DSH 自带的 HMR 服务在重建 profile 时用同步文件系统调用遍历目录**，
不是宠物插件的代码。`dsh-hmr` 的 `reconcileProfile()` 会：

1. 取 `profileRoots(profile.dir)`：profile 目录 + `profile/node_modules` 里**每一个符号链接包**的真实路径
   （`realSpellings()` 对每个路径调 `realpathSync`，Windows 上还会再来一次 native 写法）；
2. 在这些根下枚举模块文件，逐个 `lstat` / `stat`；
3. 换掉磁盘上变化过的插件模块，并发布新的运行时解析结果。

本机 `C:\Users\KING\.dsh\profiles\desktop\node_modules` 是 pnpm 布局（大量符号链接 + store 硬链接），
一次 reconcile 要 stat 上万条路径；实测 **2.3～8.2 秒**，期间宿主事件循环完全停摆，界面就是"卡死"。

## 3. 什么时候会触发（这是"随机"的来源）

`dsh-hmr` 里 `refresh()` 的触发条件（源码 `dsh-hmr/lib/index.js` 852 行起）只有三种：

- `profile.patchPath`（`profiles/desktop/cordis.patch.yml`）变化；
- `profile.home` 下的 `cordis.patch.yml` 变化；
- `profile.dir/package.json`（清单）变化且 `dsh.profile.bundles` 真的变了。

而每一次**启用/停用/安装插件**（`plugin_manager set_bundle`、`install_bundle`、DSH 升级后重启、
我自己迭代时反复重载）都会改这两三个文件 ⇒ 立刻一次 reconcile ⇒ 一次 2～8 秒的整屏卡死。
"随机"感来自：它取决于你什么时候改插件/什么时候重启 DSH，而不是取决于你点了宠物哪一下。

对照组实验（同一天，同一台机器）：

| 场景 | 结果 |
| --- | --- |
| 稳态 240 s，250 ms 一次探针共 937 次（DSH UI 线程 `WM_NULL` + `/state`） | DSH UI 最长 **17 ms**，HTTP 最长 **862 ms**，0 次失败 |
| 往 `~/.dsh/pets/` 连续写文件（6 次） | 无 `loop lag` / 无 profile |
| 往工作区根目录写文件（6 次） | 无 |
| 往插件包目录里写文件（4 次） | 无 |
| `plugin_manager set_bundle` 重载插件 | **必然**一次 2.5～5.5 s（对应 profile 落盘那一刻） |
| 一次完整操作序列 soak（悬停/右键/写作/语音/拖动/最小化） | 只在插件重载那一下有阻塞，操作本身 0 阻塞 |

结论：**宠物插件在运行期不产生同步阻塞**，`/state` 是纯内存拼装，宿主侧没有同步 `fs`/`child_process`
调用（`grep -n "Sync" src/*.js` 只剩注释）。卡死是"装/重载插件"这个动作本身引爆的 DSH HMR 重建。

## 4. 因此这一版做了什么

1. **运行期不再让插件重启**：宠物配置全部在 `~/.dsh/pets/config.json`，改缩放/气泡时长/换宠物都是热生效，
   不碰 profile，也就不会再触发 reconcile。`/pet`、右键菜单同理。
2. **不再写 profile 目录**：全部运行时文件都在 `~/.dsh/pets/` 下（`config.json`、`logs/`、`bin/`、`pets/`），
   `src/` 里没有任何写到 profile 的路径。
3. **重载期间不误报**：重载引起的 2～8 秒容器无响应，浮层会显示"DSH 已断开，重连中…"而不是消失；
   宿主恢复后自动接上（`overlay.log` 里 `connected, overlay ready`）。
4. **不加掩盖式补丁**：没有加重试、没有加超时掩盖、没有"卡了就自动重启"——宿主卡住时浮层什么都不做，
   等它回来；看门狗只记录，不动作。
5. **把取证能力留给用户**：`tools/probe.ps1`（谁在卡）+ 采样器（卡在哪个函数）+ `tools/soak.ps1`（怎么复现）。
   下次真再卡，先把这三样跑起来，`~/.dsh/pets/logs/lag-*.cpuprofile` 会直接给出栈。

## 5. 如果还想更快（属于 DSH 侧，插件改不到）

- 把 profile 的 `node_modules` 换成非符号链接布局（pnpm 的 `node-linker=hoisted` 或 npm），
  `realSpellings()` 的符号链接解析量会下降一个量级；
- 给 profile 目录加 Defender 排除项（这台机器上单次 `lstat` 叫到 1 ms 级）；
- 这两条是 DSH 的实现面，宠物插件无法在自身范围内改掉，这里只作为已核实的建议列出。

## 6. 复现命令

```powershell
# 1) 探针：DSH 窗口 UI 线程 / 浮层窗口 UI 线程 / /state 延迟，落 CSV
powershell -NoProfile -ExecutionPolicy Bypass -File tools\probe.ps1 -Seconds 600

# 2) 采样：随时打开（不需要重启 DSH）
#    POST http://127.0.0.1:<port>/x-dsh-pet/profile   body: {"on":"1"}
#    或安装目录下放一个 ~/.dsh/pets/profile.on 后重载插件（从启动第一毫秒开始采）

# 3) 操作序列：悬停 → 右键菜单 → ✎ 写作 → 🎤 语音 → 拖动 → 最小化/还原，循环 10 分钟
powershell -NoProfile -ExecutionPolicy Bypass -File tools\soak.ps1 -Minutes 10

# 4) 触发一次对比：重载插件（这一步应当出现一次 2～8 s 的 loop lag）
#    plugin_manager set_bundle dsh-pet enabled:false → true
#    然后看 ~/.dsh/pets/logs/host.log 的 `loop block ... top=` 与 lag-*.cpuprofile
```