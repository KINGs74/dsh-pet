// dsh-pet · 宠物安装 / 校验 / 目录列举
//
// petdex 的宠物包就是两件东西：`pet.json` + 一张雪碧图（webp 或 png）。
// petdex.dev 没有公开 JSON API（/api/* 在 robots 之外且需要鉴权），但它为每个
// 宠物提供一段安装脚本：GET https://petdex.dev/install/<slug> 返回 shell 脚本，
// 里面是两条 `curl -o "$PET_DIR/<name>" '<url>'`。我们解析这两条命令拿到
// 官方素材地址，再用同样的 Referer 下载 —— 与 Codex 侧完全一致的取包路径。
import { createWriteStream } from 'node:fs';
import { copyFile, mkdir, readdir, readFile, rename, rm, stat, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { basename, extname, join } from 'node:path';

export const PETDEX_ORIGIN = 'https://petdex.dev';
export const PETDEX_REFERER = 'https://petdex.dev/';
const MAX_ASSET_BYTES = 24 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 90_000;

/** petdex 雪碧图固定网格：8 列，至少 9 行；单帧 192×208 */
export const GRID = Object.freeze({ cols: 8, frameW: 192, frameH: 208, minRows: 9 });

const PS_CONVERT = `param([Parameter(Mandatory=$true)][string]$InPath, [Parameter(Mandatory=$true)][string]$OutPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
$stream = [System.IO.File]::OpenRead($InPath)
try {
  $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($stream, 'PreservePixelFormat', 'OnLoad')
  $frame = $decoder.Frames[0]
  $conv = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap
  $conv.BeginInit()
  $conv.Source = $frame
  $conv.DestinationFormat = [System.Windows.Media.PixelFormats]::Bgra32
  $conv.EndInit()
  $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
  $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($conv))
  $out = [System.IO.File]::Create($OutPath)
  try { $encoder.Save($out) } finally { $out.Close() }
  Write-Output ("ok " + $frame.PixelWidth + "x" + $frame.PixelHeight)
} finally {
  $stream.Close()
}
`;

export function sanitizeId(value) {
  return String(value ?? '')
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9._-]+/g, '-')
    // 顺带去掉首尾的 . 和 -，避免 '..' 这类目录名
    .replace(/^[.\-]+|[.\-]+$/g, '')
    .slice(0, 60);
}

/** 从「宠物名 / petdex 链接」里取出 slug；不是 petdex 来源则返回 '' */
export function petdexSlugFromSource(source) {
  const raw = String(source ?? '').trim();
  if (!raw) return '';
  if (/^[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(raw)) return raw.toLowerCase();
  try {
    const url = new URL(raw);
    if (!/(^|\.)petdex\.dev$/.test(url.hostname)) return '';
    const parts = url.pathname.split('/').filter(Boolean);
    // 支持 https://petdex.dev/pets/<slug>、/pets/<owner>/<slug>、/install/<slug>
    if (parts.length >= 2 && (parts[0] === 'pets' || parts[0] === 'install' || parts[0] === 'pet')) {
      return sanitizeId(parts[parts.length - 1]);
    }
    return '';
  } catch {
    return '';
  }
}

/** 解析 petdex 安装脚本里的 curl 行 → [{ name, url }] */
export function parseInstallScript(text) {
  const files = [];
  const re = /curl[^\n]*?-o\s+"?\$PET_DIR\/([^"\s]+)"?\s+'([^']+)'/g;
  let match;
  while ((match = re.exec(String(text ?? ''))) !== null) {
    files.push({ name: match[1], url: match[2] });
  }
  return files;
}

/** 读 PNG 头拿宽高（不依赖任何图像库） */
export async function readPngSize(path) {
  const handle = await readFile(path);
  if (handle.length < 24) return null;
  const signature = handle.subarray(0, 8).toString('hex');
  if (signature !== '89504e470d0a1a0a') return null;
  // IHDR: 8 字节签名 + 4 长度 + 4 类型 + 宽(4) + 高(4)
  const width = handle.readUInt32BE(16);
  const height = handle.readUInt32BE(20);
  return { width, height };
}

/** 由雪碧图尺寸推出网格；不符合 petdex 规范返回 null */
export function gridFor(width, height) {
  if (!Number.isFinite(width) || !Number.isFinite(height)) return null;
  if (width % GRID.cols !== 0) return null;
  const frameW = width / GRID.cols;
  if (frameW < 64) return null;
  const rows = height / GRID.frameH;
  if (!Number.isInteger(rows)) return null;
  if (rows < GRID.minRows) return null;
  return { cols: GRID.cols, rows, frameW, frameH: GRID.frameH };
}

async function exists(path) {
  try {
    await stat(path);
    return true;
  } catch {
    return false;
  }
}

function run(cmd, args, { timeoutMs = 120_000 } = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(cmd, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let out = '';
    let err = '';
    const timer = setTimeout(() => {
      try {
        child.kill();
      } catch {}
      reject(new Error(`${cmd} 超时（${timeoutMs}ms）`));
    }, timeoutMs);
    child.stdout?.on('data', (chunk) => (out += chunk));
    child.stderr?.on('data', (chunk) => (err += chunk));
    child.on('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(out.trim());
      else reject(new Error(`${cmd} 退出码 ${code}${err ? `：${err.trim()}` : ''}`));
    });
  });
}

async function download(url, dest, { log = () => {}, timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
  log(`下载 ${url}`);
  const response = await fetch(url, {
    headers: {
      referer: PETDEX_REFERER,
      'user-agent': 'dsh-pet/0.1 (+https://petdex.dev)',
      accept: '*/*',
    },
    signal: AbortSignal.timeout(timeoutMs),
  });
  if (!response.ok) throw new Error(`下载失败 ${response.status} ${url}`);
  const size = Number(response.headers.get('content-length') ?? 0);
  if (size > MAX_ASSET_BYTES) throw new Error(`素材过大：${size} 字节`);
  const body = Buffer.from(await response.arrayBuffer());
  if (body.byteLength > MAX_ASSET_BYTES) throw new Error(`素材过大：${body.byteLength} 字节`);
  await mkdir(join(dest, '..'), { recursive: true });
  await writeFile(dest, body);
  log(`已保存 ${dest}（${body.byteLength} 字节）`);
  return body.byteLength;
}

/** Windows 上可用的 PowerShell；非 Windows 返回 '' */
export function powerShellPath(platform = process.platform) {
  if (platform !== 'win32') return '';
  return 'powershell.exe';
}

export async function convertSpriteToPng({ input, output, scriptPath, psPath, log = () => {} }) {
  if (!psPath) throw new Error('webp 转换需要 Windows PowerShell');
  await mkdir(join(scriptPath, '..'), { recursive: true });
  await writeFile(scriptPath, PS_CONVERT, 'utf8');
  const out = await run(psPath, [
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    scriptPath,
    '-InPath',
    input,
    '-OutPath',
    output,
  ]);
  log(`webp→png ${out}`);
}

const SPRITE_NAMES = ['spritesheet.webp', 'spritesheet.png', 'sprite.webp', 'sprite.png'];

async function findSprite(dir, petJson) {
  const declared = typeof petJson?.spritesheetPath === 'string' ? petJson.spritesheetPath : '';
  if (declared) {
    const candidate = join(dir, basename(declared));
    if (await exists(candidate)) return candidate;
  }
  for (const name of SPRITE_NAMES) {
    const candidate = join(dir, name);
    if (await exists(candidate)) return candidate;
  }
  const entries = await readdir(dir, { withFileTypes: true });
  for (const entry of entries) {
    if (!entry.isFile()) continue;
    const ext = extname(entry.name).toLowerCase();
    if (ext === '.webp' || ext === '.png') return join(dir, entry.name);
  }
  return '';
}

/**
 * 把暂存目录整理成最终宠物目录：校验 pet.json + 雪碧图，webp 转 png。
 * @returns {Promise<{id, name, description, dir, sheet, grid}>}
 */
export async function finalizePetDir({
  stagingDir,
  root,
  fallbackId = 'pet',
  psPath = powerShellPath(),
  scriptPath = join(root, 'bin', 'convert.ps1'),
  log = () => {},
}) {
  const petJsonPath = join(stagingDir, 'pet.json');
  let petJson;
  try {
    petJson = JSON.parse(await readFile(petJsonPath, 'utf8'));
  } catch (error) {
    throw new Error(`pet.json 缺失或无法解析：${error.message}`);
  }

  const sprite = await findSprite(stagingDir, petJson);
  if (!sprite) throw new Error('宠物包里找不到雪碧图（spritesheet.webp / .png）');

  const id = sanitizeId(petJson.id || petJson.displayName || fallbackId) || sanitizeId(fallbackId) || 'pet';
  let sheet = sprite;
  if (extname(sprite).toLowerCase() === '.webp') {
    const png = join(stagingDir, 'spritesheet.png');
    await convertSpriteToPng({ input: sprite, output: png, scriptPath, psPath, log });
    sheet = png;
  }

  const size = await readPngSize(sheet);
  if (!size) throw new Error('雪碧图无法读取（不是合法 PNG）');
  const grid = gridFor(size.width, size.height);
  if (!grid) {
    throw new Error(
      `雪碧图网格不符合 petdex 规范：${size.width}×${size.height}，需要 8 列、单帧 ${GRID.frameW}×${GRID.frameH}、至少 ${GRID.minRows} 行`,
    );
  }

  const targetDir = join(root, id);
  await rm(targetDir, { recursive: true, force: true });
  await mkdir(root, { recursive: true });
  await rename(stagingDir, targetDir);

  return {
    id,
    name: typeof petJson.displayName === 'string' && petJson.displayName ? petJson.displayName : id,
    description: typeof petJson.description === 'string' ? petJson.description : '',
    dir: targetDir,
    sheet: join(targetDir, 'spritesheet.png'),
    grid,
  };
}

async function stagingUnder(root) {
  const dir = join(root, '.staging', `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`);
  await mkdir(dir, { recursive: true });
  return dir;
}

async function extractZip(zipPath, dest, psPath) {
  if (!psPath) throw new Error('解压 zip 需要 Windows PowerShell');
  await run(psPath, [
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy',
    'Bypass',
    '-Command',
    `Expand-Archive -LiteralPath '${zipPath.replace(/'/g, "''")}' -DestinationPath '${dest.replace(/'/g, "''")}' -Force`,
  ]);
  // 有些 zip 把一个目录套在外面：向下找一层含 pet.json 的目录
  const entries = await readdir(dest, { withFileTypes: true });
  if (!entries.some((entry) => entry.isFile() && entry.name === 'pet.json')) {
    const dirs = entries.filter((entry) => entry.isDirectory());
    if (dirs.length === 1) return join(dest, dirs[0].name);
  }
  return dest;
}

/**
 * 安装宠物。source 支持四种形态：
 *   1. petdex 名称：`boba`
 *   2. petdex 链接：https://petdex.dev/pets/boba
 *   3. 任意 zip 的 http(s) 链接
 *   4. 本地 zip 文件或包含 pet.json 的目录
 */
export async function installPet(source, options = {}) {
  const {
    root,
    fetchImpl = fetch,
    psPath = powerShellPath(),
    scriptPath = join(root, 'bin', 'convert.ps1'),
    log = () => {},
  } = options;
  const raw = String(source ?? '').trim();
  if (!raw) throw new Error('请填写宠物名称、链接或本地路径');
  if (!root) throw new Error('缺少宠物目录');

  const stagingDir = await stagingUnder(root);
  try {
    const slug = petdexSlugFromSource(raw);
    if (slug) {
      const script = await fetchImpl(`${PETDEX_ORIGIN}/install/${encodeURIComponent(slug)}`, {
        headers: { referer: PETDEX_REFERER, 'user-agent': 'dsh-pet/0.1' },
        signal: AbortSignal.timeout(30_000),
      });
      if (!script.ok) throw new Error(`petdex 没有名为 ${slug} 的宠物（HTTP ${script.status}）`);
      const files = parseInstallScript(await script.text());
      if (files.length === 0) throw new Error(`无法解析 petdex 安装脚本（${slug}）`);
      for (const file of files) {
        const name = basename(file.name) || 'spritesheet.webp';
        await download(file.url, join(stagingDir, name), { log });
      }
      // petdex 脚本把 spritesheet 存成 spritesheet.webp，但 pet.json 里写的是
      // spritesheetPath；两者不一致时补一份规范名，保证校验走的路径确定
      const petJsonPath = join(stagingDir, 'pet.json');
      const petJson = JSON.parse(await readFile(petJsonPath, 'utf8'));
      const declared = typeof petJson.spritesheetPath === 'string' ? basename(petJson.spritesheetPath) : '';
      if (declared && !(await exists(join(stagingDir, declared)))) {
        const sprite = await findSprite(stagingDir, {});
        if (sprite) await copyFile(sprite, join(stagingDir, declared));
      }
    } else if (/^https?:\/\//i.test(raw)) {
      const zipPath = join(stagingDir, 'pet.zip');
      await download(raw, zipPath, { log });
      const unpacked = await extractZip(zipPath, join(stagingDir, 'unzipped'), psPath);
      if (unpacked !== join(stagingDir, 'unzipped')) {
        // 目录被套了一层：整体挪上来
        const entries = await readdir(unpacked);
        for (const name of entries) {
          await rename(join(unpacked, name), join(stagingDir, name));
        }
      }
    } else {
      const info = await stat(raw);
      if (info.isDirectory()) {
        const entries = await readdir(raw);
        for (const name of entries) {
          if (name === 'pet.json' || /\.(webp|png)$/i.test(name)) {
            await copyFile(join(raw, name), join(stagingDir, name));
          }
        }
      } else if (extname(raw).toLowerCase() === '.zip') {
        await extractZip(raw, join(stagingDir, 'unzipped'), psPath);
        const unpacked = join(stagingDir, 'unzipped');
        for (const name of await readdir(unpacked)) {
          await rename(join(unpacked, name), join(stagingDir, name));
        }
      } else {
        throw new Error(`不支持的本地路径：${raw}`);
      }
    }

    const pet = await finalizePetDir({ stagingDir, root, fallbackId: slug || 'pet', psPath, scriptPath, log });
    log(`安装完成 ${pet.id} → ${pet.dir}（${pet.grid.frameW}×${pet.grid.frameH} · ${pet.grid.rows} 行）`);
    return pet;
  } catch (error) {
    await rm(stagingDir, { recursive: true, force: true }).catch(() => {});
    throw error;
  }
}

/** 列举已安装宠物（按名称排序） */
export async function listPets(root) {
  let entries;
  try {
    entries = await readdir(root, { withFileTypes: true });
  } catch {
    return [];
  }
  const pets = [];
  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name.startsWith('.')) continue;
    const dir = join(root, entry.name);
    try {
      const petJson = JSON.parse(await readFile(join(dir, 'pet.json'), 'utf8'));
      const sheet = join(dir, 'spritesheet.png');
      if (!(await exists(sheet))) continue;
      const size = await readPngSize(sheet);
      const grid = size ? gridFor(size.width, size.height) : null;
      pets.push({
        id: entry.name,
        name: typeof petJson.displayName === 'string' && petJson.displayName ? petJson.displayName : entry.name,
        description: typeof petJson.description === 'string' ? petJson.description : '',
        dir,
        sheet,
        grid,
      });
    } catch {
      // 半个宠物目录直接跳过，不影响其他宠物
    }
  }
  pets.sort((a, b) => a.name.localeCompare(b.name));
  return pets;
}

/** 下载用的默认 Referer，导出便于测试断言 */
export const PETDEX_HEADERS = { referer: PETDEX_REFERER };