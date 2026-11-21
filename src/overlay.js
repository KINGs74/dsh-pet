// dsh-pet · 原生浮层进程的编译与托管
//
// 为什么是独立进程：DSH 桌面端不向插件开放任何窗口 / 置顶 / 托盘能力
// （Electron 主进程里 alwaysOnTop、transparent、setIgnoreMouseEvents 均为 0 处，
//  window.open 全部被 deny，且宿主本身是 Electron spawn 出来的 Node 子进程），
// 所以"能拖到屏幕任何角落、不被面板裁剪、最小化后仍然可见"只能由一个自己的
// 顶层窗口实现。这里用 .NET Framework 自带的 csc.exe 现场编译一个几百行的
// WinForms 分层窗口，不需要用户装编译环境。
import { spawn } from 'node:child_process';
import { mkdir, readdir, readFile, stat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const CSC_PATHS = [
  'C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe',
  'C:/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe',
];

async function fileExists(path) {
  try {
    const info = await stat(path);
    return info.isFile();
  } catch {
    return false;
  }
}

// System.Speech 只在 GAC 里，csc 需要完整路径才能引用；找不到就编译无语音版本
const SPEECH_GAC_ROOTS = [
  'C:/Windows/Microsoft.NET/assembly/GAC_MSIL/System.Speech',
  'C:/Windows/assembly/GAC_MSIL/System.Speech',
];

export async function findSpeechDll() {
  if (process.platform !== 'win32') return '';
  for (const root of SPEECH_GAC_ROOTS) {
    try {
      const versions = await readdir(root);
      for (const version of versions) {
        const candidate = join(root, version, 'System.Speech.dll');
        if (await fileExists(candidate)) return candidate;
      }
    } catch {}
  }
  return '';
}

export async function findCsc() {
  if (process.platform !== 'win32') return '';
  for (const path of CSC_PATHS) {
    if (await fileExists(path)) return path;
  }
  return '';
}

export function run(cmd, args, { timeoutMs = 120_000, cwd } = {}) {
  return new Promise((resolve) => {
    let out = '';
    let err = '';
    let child;
    try {
      child = spawn(cmd, args, { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    } catch (error) {
      resolve({ ok: false, code: -1, out: '', err: String(error?.message ?? error) });
      return;
    }
    const timer = setTimeout(() => {
      try {
        child.kill();
      } catch {}
    }, timeoutMs);
    child.stdout?.on('data', (chunk) => (out += chunk));
    child.stderr?.on('data', (chunk) => (err += chunk));
    child.on('error', (error) => {
      clearTimeout(timer);
      resolve({ ok: false, code: -1, out, err: String(error?.message ?? error) });
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ ok: code === 0, code: code ?? -1, out, err });
    });
  });
}

/** 源码比 exe 新（或 exe 缺失）时才重新编译 */
export async function needsBuild({ sources, exePath }) {
  if (!(await fileExists(exePath))) return { needed: true, reason: '缺少可执行文件' };
  const exeInfo = await stat(exePath);
  for (const source of sources) {
    if (!(await fileExists(source))) return { needed: false, reason: `源码缺失：${source}`, missing: true };
    const info = await stat(source);
    if (info.mtimeMs > exeInfo.mtimeMs + 1000) return { needed: true, reason: '源码已更新' };
  }
  return { needed: false, reason: '已是最新' };
}

/** 枚举 native/ 下的全部 .cs（含 manifest 与图标），返回值参与 mtime 比较 */
export async function collectSources(nativeDir) {
  const sources = [];
  try {
    const files = await readdir(nativeDir);
    for (const file of files.sort()) {
      // 只有 .cs 是编译输入；manifest / 图标是资源，只参与"要不要重编"的比较
      if (file.endsWith('.cs')) sources.push(join(nativeDir, file));
    }
  } catch {
    return { sources: [], manifestPath: '', iconPath: '', error: `无法读取 ${nativeDir}` };
  }
  const iconPath = join(nativeDir, 'app.ico');
  const hasIcon = await fileExists(iconPath);
  return {
    sources,
    manifestPath: join(nativeDir, 'app.manifest'),
    iconPath: hasIcon ? iconPath : '',
    error: '',
  };
}

/**
 * 编译原生浮层。失败不抛异常：返回 { ok:false, message }，由调用方把
 * overlay=unavailable 发给浮层/日志，绝不影响 DSH 本体。
 */
export async function buildOverlay({ nativeDir, exePath, log = () => {}, force = false }) {
  const csc = await findCsc();
  if (!csc) return { ok: false, message: '找不到 csc.exe（需要 .NET Framework 4.x）' };
  const collected = await collectSources(nativeDir);
  if (collected.error) return { ok: false, message: collected.error };
  if (collected.sources.length === 0) return { ok: false, message: `native 目录里没有源码：${nativeDir}` };
  const watched = [...collected.sources];
  if (collected.manifestPath) watched.push(collected.manifestPath);
  if (collected.iconPath) watched.push(collected.iconPath);
  const check = await needsBuild({ sources: watched, exePath });
  if (check.missing) return { ok: false, message: check.reason };
  if (!check.needed && !force) return { ok: true, message: '已是最新' };

  const manifestPath = collected.manifestPath;
  const speechDll = await findSpeechDll();
  const args = [
    '/nologo',
    '/target:winexe',
    '/optimize+',
    `/out:${exePath}`,
    `/win32manifest:${manifestPath}`,
    '/r:System.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
  ];
  if (speechDll) {
    args.push(`/r:${speechDll}`, '/define:VOICE');
  } else {
    log('没有找到 System.Speech.dll，浮层将以无语音模式编译');
  }
  if (collected.iconPath) args.push(`/win32icon:${collected.iconPath}`);
  for (const source of collected.sources) args.push(source);

  await mkdir(join(exePath, '..'), { recursive: true });
  const result = await run(csc, args);
  if (!result.ok) {
    const message = `编译失败：${(result.err || result.out || '').trim().split('\n').slice(0, 4).join(' ')}`;
    log(message);
    return { ok: false, message };
  }
  log(`浮层已编译 → ${exePath}`);
  return { ok: true, message: '已编译' };
}

/**
 * 浮层进程监护：启动、退出后自动重启（有上限）、随插件卸载一起清理。
 */
export class OverlaySupervisor {
  constructor({ exePath, host, token, prefix = '/x-dsh-pet', dshPid = 0, dshExe = '', logFile, log = () => {}, maxRestarts = 5 }) {
    this.exePath = exePath;
    this.host = host;
    this.token = token;
    this.prefix = prefix;
    // 浮层靠这两个值判断「DSH 是否还活着」：主进程没了就自己退出，不留下孤儿窗口
    this.dshPid = dshPid;
    this.dshExe = dshExe;
    this.logFile = logFile;
    this.log = log;
    this.maxRestarts = maxRestarts;
    this.child = null;
    this.restarts = 0;
    this.stopping = false;
    this.timer = null;
    this.state = 'stopped'; // stopped | starting | running | unavailable
    this.lastError = '';
  }

  status() {
    return { state: this.state, pid: this.child?.pid ?? 0, restarts: this.restarts, error: this.lastError };
  }

  async available() {
    return fileExists(this.exePath);
  }

  async start() {
    if (this.child) return;
    this.stopping = false;
    if (!(await this.available())) {
      this.state = 'unavailable';
      this.lastError = '缺少 PetOverlay.exe';
      this.log('浮层不可用：缺少可执行文件');
      return;
    }
    await mkdir(join(this.logFile, '..'), { recursive: true });
    this.state = 'starting';
    const args = ['--host', this.host, '--token', this.token, '--log', this.logFile, '--prefix', this.prefix];
    if (this.dshPid) args.push('--dsh-pid', String(this.dshPid));
    if (this.dshExe) args.push('--dsh-exe', this.dshExe);
    const child = spawn(this.exePath, args, {
      windowsHide: false,
      stdio: 'ignore',
      detached: false,
    });
    this.child = child;
    this.state = 'running';
    this.log(`浮层已启动 pid=${child.pid} host=${this.host}`);

    child.on('error', (error) => {
      this.lastError = String(error?.message ?? error);
      this.log(`浮层启动失败：${this.lastError}`);
      this.state = 'unavailable';
      this.child = null;
    });

    child.on('exit', (code, signal) => {
      this.child = null;
      this.log(`浮层退出 code=${code} signal=${signal ?? ''}`);
      if (this.stopping) {
        this.state = 'stopped';
        return;
      }
      if (code === 0) {
        // 正常退出（例如宿主长时间不可达后自清理）：不重启，等下一次宿主事件
        this.state = 'stopped';
        return;
      }
      if (this.restarts >= this.maxRestarts) {
        this.state = 'unavailable';
        this.lastError = `连续重启 ${this.restarts} 次后放弃`;
        this.log(this.lastError);
        return;
      }
      this.restarts += 1;
      this.state = 'starting';
      this.timer = setTimeout(() => {
        this.timer = null;
        this.start().catch((error) => this.log(`浮层重启失败：${error?.message ?? error}`));
      }, 1500);
      this.timer.unref?.();
    });
  }

  async stop() {
    this.stopping = true;
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
    const child = this.child;
    this.child = null;
    this.state = 'stopped';
    if (!child?.pid) return;
    const pid = child.pid;
    try {
      child.kill();
    } catch {}
    // Windows 上 Node 的 kill 对 GUI 进程并不总是立即生效，补一刀 taskkill
    if (process.platform === 'win32') {
      await run('taskkill', ['/PID', String(pid), '/T', '/F'], { timeoutMs: 8000 });
    }
    this.log(`浮层已停止 pid=${pid}`);
  }
}

export async function writeRuntimeFile(path, payload) {
  await mkdir(join(path, '..'), { recursive: true });
  await writeFile(path, `${JSON.stringify(payload, null, 2)}\n`, 'utf8');
}

export async function readJsonFile(path) {
  try {
    return JSON.parse(await readFile(path, 'utf8'));
  } catch {
    return null;
  }
}