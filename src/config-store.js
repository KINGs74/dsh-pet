// dsh-pet · 配置持久化
//
// 配置存放在 ~/.dsh/pets/config.json，与 DSH 自己的 profile 配置完全分开：
// 插件不去改 profile 的 cordis.patch.yml（那是 DSH 的所有物），只维护自己的
// 小型 JSON 文档，写入用「临时文件 + rename」保证原子性。
import { copyFile, mkdir, readFile, rename, stat, writeFile } from 'node:fs/promises';
import { homedir } from 'node:os';
import { dirname, join } from 'node:path';

/** 宠物数据根目录：~/.dsh/pets */
export const PETS_ROOT = join(homedir(), '.dsh', 'pets');

/** 可持久化的配置项与默认值 */
export const DEFAULT_CONFIG = Object.freeze({
  visible: true, // 是否显示宠物浮层
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
    visible: toBool(input.visible, DEFAULT_CONFIG.visible),
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

  async load(report = () => {}) {
    try {
      const text = await readFile(this.file, 'utf8');
      this.config = normalizeConfig(JSON.parse(text));
      this._corrupt = false;
    } catch (error) {
      if (error?.code === 'ENOENT') {
        this._corrupt = false;
        return this.config;
      }
      // 坏文件不能让插件启动失败，也不能被"默认值"直接覆盖掉用户原来的内容：
      // 先把坏文件留档成 config.json.corrupt-<时间戳>，再从 .bak / .prev 回滚。
      this._corrupt = true;
      const stamp = new Date().toISOString().replace(/[:.]/g, '-');
      const keep = `${this.file}.corrupt-${stamp}`;
      try {
        await copyFile(this.file, keep);
        report(`配置解析失败（${error?.message ?? error}），原文件已留档为 ${keep}`);
      } catch (copyError) {
        report(`配置解析失败，留档也失败：${copyError?.message ?? copyError}`);
      }
      for (const fallback of [`${this.file}.bak`, `${this.file}.prev`]) {
        try {
          const text = await readFile(fallback, 'utf8');
          this.config = normalizeConfig(JSON.parse(text));
          report(`已从 ${fallback} 恢复配置`);
          // 立刻把恢复结果写回原文件：否则下次启动又要再走一遍回滚，
          // 而且磁盘上一直留着一份坏 JSON。这里绕开 .bak/.prev 轮转，
          // 避免把损坏内容写进备份。
          await this._writeRecovered();
          return this.config;
        } catch {
          // 下一个候选
        }
      }
      report('没有可用的备份，使用默认配置（原文件已留档）');
      this.config = { ...DEFAULT_CONFIG };
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

  /**
   * 落盘。三步保证"任何时刻磁盘上都有一份能读的配置"：
   *   1. 首次写入前把现有文件复制成 config.json.bak（只在不存在时做，不覆盖更早的备份）
   *   2. 写临时文件 → rename 覆盖（rename 是原子的，不会出现半截 JSON）
   *   3. 更新前先把当前文件留成 config.json.prev
   * 影响面：只碰 ~/.dsh/pets/config.json 及其 .bak/.prev 兄弟文件，从不写会话数据。
   */
  async _write() {
    await mkdir(dirname(this.file), { recursive: true });
    await this._backupOnce();
    await this._rotatePrev();
    const tmp = `${this.file}.${process.pid}.tmp`;
    await writeFile(tmp, `${JSON.stringify(this.config, null, 2)}\n`, 'utf8');
    await rename(tmp, this.file);
  }

  /** 回滚后直接把恢复结果写回原文件（不动 .bak / .prev） */
  async _writeRecovered() {
    try {
      await mkdir(dirname(this.file), { recursive: true });
      const tmp = `${this.file}.${process.pid}.tmp`;
      await writeFile(tmp, `${JSON.stringify(this.config, null, 2)}\n`, 'utf8');
      await rename(tmp, this.file);
    } catch {
      // 写不回去也不影响本次运行，内存里的配置已经是恢复过的
    }
  }

  async _backupOnce() {
    const bak = `${this.file}.bak`;
    try {
      await stat(bak);
      return;
    } catch {
      // 还没有 .bak，继续
    }
    try {
      await stat(this.file);
    } catch {
      return; // 还没有配置文件，没什么可备份
    }
    try {
      await copyFile(this.file, bak);
    } catch {
      // 备份失败不阻断写入
    }
  }

  async _rotatePrev() {
    try {
      await stat(this.file);
    } catch {
      return;
    }
    try {
      await copyFile(this.file, `${this.file}.prev`);
    } catch {
      // 同上，尽力而为
    }
  }
}