// dsh-pet · 配置持久化
//
// 配置存放在 ~/.dsh/pets/config.json，与 DSH 自己的 profile 配置完全分开：
// 插件不去改 profile 的 cordis.patch.yml（那是 DSH 的所有物），只维护自己的
// 小型 JSON 文档，写入用「临时文件 + rename」保证原子性。
import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

/** 宠物数据根目录：~/.dsh/pets */
export const PETS_ROOT = join(homedir(), '.dsh', 'pets');

/** 可持久化的配置项与默认值 */
export const DEFAULT_CONFIG = Object.freeze({
  pet: '', // 当前宠物 id；空串表示「第一个可用宠物」
  scale: 0.55, // 宠物缩放，0.3 ~ 1.5
  bubble: true, // 是否显示气泡
  sound: true, // 任务结束提示音
  bubbleMs: 5000, // 气泡停留毫秒；0 = 一直显示（未确认的结束提醒另外算 alertMs）
  alertMs: 30000, // 结束提醒至少停留多久（毫秒）
  lang: 'zh', // 气泡文案语言：zh | en
  pos: null, // 宠物锚点（屏幕物理像素）{x, y}
});

const SCALE_MIN = 0.3;
const SCALE_MAX = 1.5;

function clampNumber(value, min, max, fallback) {
  const n = typeof value === 'number' ? value : Number.parseFloat(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

function toBool(value, fallback) {
  if (typeof value === 'boolean') return value;
  if (value === '1' || value === 'true') return true;
  if (value === '0' || value === 'false') return false;
  return fallback;
}

/** 把任意输入规整成合法配置（越界值收敛而不是报错） */
export function normalizeConfig(raw) {
  const input = raw && typeof raw === 'object' ? raw : {};
  const pos =
    input.pos && Number.isFinite(Number(input.pos.x)) && Number.isFinite(Number(input.pos.y))
      ? { x: Math.round(Number(input.pos.x)), y: Math.round(Number(input.pos.y)) }
      : null;
  return {
    pet: typeof input.pet === 'string' ? input.pet.trim() : DEFAULT_CONFIG.pet,
    scale: clampNumber(input.scale, SCALE_MIN, SCALE_MAX, DEFAULT_CONFIG.scale),
    bubble: toBool(input.bubble, DEFAULT_CONFIG.bubble),
    sound: toBool(input.sound, DEFAULT_CONFIG.sound),
    bubbleMs: clampNumber(input.bubbleMs, 0, 600000, DEFAULT_CONFIG.bubbleMs),
    alertMs: clampNumber(input.alertMs, 0, 600000, DEFAULT_CONFIG.alertMs),
    lang: input.lang === 'en' ? 'en' : 'zh',
    pos,
  };
}

export class ConfigStore {
  constructor(root = PETS_ROOT) {
    this.root = root;
    this.file = join(root, 'config.json');
    this.config = { ...DEFAULT_CONFIG };
    this._pending = null;
  }

  async load() {
    try {
      const text = await readFile(this.file, 'utf8');
      this.config = normalizeConfig(JSON.parse(text));
    } catch (error) {
      if (error?.code !== 'ENOENT') {
        // 坏文件不应让插件启动失败：退回默认值，下一次写入时覆盖
        this.config = { ...DEFAULT_CONFIG };
      }
    }
    return this.config;
  }

  get() {
    return this.config;
  }

  /** 合并补丁并落盘，返回新配置 */
  async patch(patch) {
    this.config = normalizeConfig({ ...this.config, ...(patch ?? {}) });
    await this.save();
    return this.config;
  }

  save() {
    // 串行化写入，避免连续拖动时 rename 竞争
    const next = this._pending ? this._pending.then(() => this._write()) : this._write();
    this._pending = next.catch(() => {});
    return next;
  }

  async _write() {
    await mkdir(dirname(this.file), { recursive: true });
    const tmp = `${this.file}.${process.pid}.tmp`;
    await writeFile(tmp, `${JSON.stringify(this.config, null, 2)}\n`, 'utf8');
    await rename(tmp, this.file);
  }
}