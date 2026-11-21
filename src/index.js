// dsh-pet · 宿主插件入口
//
// 职责链：DSH 会话事件 → PetModel（纯状态机）→ /x-dsh-pet/state 行协议 →
//        PetOverlay.exe（独立置顶分层窗口，托盘可控）。
//
// 为什么要有独立进程，见 src/overlay.js 顶部注释：DSH 桌面端不向插件开放窗口
// 能力，任何"在页面里"的方案都会被面板裁剪，也就不满足"拖到全屏任意位置"。
import { randomUUID } from 'node:crypto';
import { appendFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { ConfigStore, PETS_ROOT } from './config-store.js';
import { OverlaySupervisor, buildOverlay, writeRuntimeFile } from './overlay.js';
import { PetModel, excerpt, textOfMessage } from './pet-state.js';
import { installPet, listPets, powerShellPath } from './pets.js';
import { PetHttpApi, PREFIX } from './routes.js';

export const name = 'dsh-pet';
// webServer 就绪后才加载本插件：路由要挂在它的 127.0.0.1 端口上
export const inject = ['webServer'];

const here = dirname(fileURLToPath(import.meta.url));
const pluginDir = join(here, '..');

const LOG_DIR = join(PETS_ROOT, 'logs');
const HOST_LOG = join(LOG_DIR, 'host.log');
const OVERLAY_LOG = join(LOG_DIR, 'overlay.log');

const TOAST_MS = 20_000;
// 全新安装时先给一只默认宠物，用户打开就有东西可看（失败则保持占位图形）
const DEFAULT_PET_SLUG = 'deepseek';

function stamp() {
  const d = new Date();
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`;
}

function createLogger() {
  return (message) => {
    const line = `[${stamp()}] ${message}\n`;
    appendFile(HOST_LOG, line, 'utf8').catch(() => {});
    if (process.env.DSH_PET_DEBUG) process.stdout.write(line);
  };
}

function asBool(value, fallback) {
  if (value === undefined || value === null || value === '') return fallback;
  if (typeof value === 'boolean') return value;
  const text = String(value).toLowerCase();
  if (text === '1' || text === 'true' || text === 'yes' || text === 'on') return true;
  if (text === '0' || text === 'false' || text === 'no' || text === 'off') return false;
  return fallback;
}

class PetService {
  constructor({ store, model, log, psPath }) {
    this.store = store;
    this.model = model;
    this.log = log;
    this.psPath = psPath;
    this.pets = [];
    this.toastText = '';
    this.toastAt = 0;
    this.supervisor = null;
  }

  async refreshPets() {
    this.pets = await listPets(PETS_ROOT);
    return this.pets;
  }

  currentPet() {
    const { pet } = this.store.get();
    const found = this.pets.find((item) => item.id === pet);
    if (found) return found;
    return this.pets[0] ?? null;
  }

  setToast(text) {
    this.toastText = text;
    this.toastAt = Date.now();
  }

  state() {
    const config = this.store.get();
    const snapshot = this.model.snapshot();
    const pet = this.currentPet();
    const toastAlive = this.toastText && Date.now() - this.toastAt < TOAST_MS;
    return {
      ...snapshot,
      pet: pet
        ? { id: pet.id, name: pet.name, sheet: pet.sheet, grid: pet.grid }
        : null,
      pets: this.pets.map((item) => ({ id: item.id, name: item.name })),
      scale: config.scale,
      bubble: config.bubble,
      sound: config.sound,
      toast: config.toast,
      lang: config.lang,
      toastText: toastAlive ? this.toastText : '',
      overlay: this.supervisor?.status().state ?? 'stopped',
      pos: config.pos,
    };
  }

  ack(alertId) {
    this.model.ack(alertId);
    this.toastText = '';
    this.log(`浮层确认提醒 ${alertId || '(全部)'}`);
    return 'ok';
  }

  async configure(body = {}) {
    const patch = {};
    if (body.pet !== undefined) {
      const id = String(body.pet);
      if (id && !this.pets.some((item) => item.id === id)) throw new Error(`没有名为 ${id} 的宠物`);
      patch.pet = id;
      if (id) {
        const pet = this.pets.find((item) => item.id === id);
        this.setToast(`已切换到 ${pet?.name ?? id}`);
      }
    }
    if (body.scale !== undefined && body.scale !== '') {
      const raw = Number(body.scale);
      if (Number.isFinite(raw) && raw > 0) {
        // 浮层菜单用百分比（40–125），这里统一折算成 0.3–1.5 的倍数
        patch.scale = raw > 2 ? raw / 100 : raw;
      }
    }
    const config = this.store.get();
    if (body.bubble !== undefined) patch.bubble = asBool(body.bubble, config.bubble);
    if (body.sound !== undefined) patch.sound = asBool(body.sound, config.sound);
    if (body.notify !== undefined) patch.toast = asBool(body.notify, config.toast);
    if (body.toast !== undefined && body.pos === undefined) {
      // 兼容旧字段名：toast 在这里表示"结束气泡提醒"开关
      if (body.toast === '0' || body.toast === '1') patch.toast = asBool(body.toast, config.toast);
    }
    if (body.lang !== undefined && body.lang) patch.lang = String(body.lang);
    if (String(body.pos ?? '') === '1') {
      const x = Number(body.posx);
      const y = Number(body.posy);
      if (Number.isFinite(x) && Number.isFinite(y)) patch.pos = { x: Math.round(x), y: Math.round(y) };
    }
    if (String(body.reset ?? '') === '1') {
      this.model.ack();
      this.setToast('');
    }
    if (Object.keys(patch).length > 0) {
      await this.store.patch(patch);
      this.model.setLang?.(this.store.get().lang);
      this.log(`配置更新 ${JSON.stringify(patch)}`);
    }
    return 'ok';
  }

  async install(source) {
    const text = String(source ?? '').trim();
    if (!text) return '请填写宠物名称、链接或本地路径';
    this.log(`开始安装宠物：${text}`);
    try {
      const pet = await installPet(text, { root: PETS_ROOT, psPath: this.psPath, log: this.log });
      await this.refreshPets();
      await this.store.patch({ pet: pet.id });
      this.setToast(`已安装 ${pet.name}`);
      return `已安装 ${pet.name}\n${pet.grid.cols} 列 × ${pet.grid.rows} 行 · ${pet.grid.frameW}×${pet.grid.frameH}`;
    } catch (error) {
      const message = error?.message ?? String(error);
      this.log(`安装失败：${message}`);
      return `安装失败：${message}`;
    }
  }

  /** 演练结束提醒：不依赖真实任务，托盘菜单里可随时触发确认效果 */
  test(kind = 'completed') {
    const allowed = new Set(['completed', 'failed', 'error', 'aborted', 'blocked', 'max-tokens', 'interrupted']);
    this.model.previewAlert(allowed.has(String(kind)) ? String(kind) : 'completed');
    this.log('已触发结束提醒演练');
    return 'ok';
  }

  overlayEvent(body = {}) {
    const event = String(body.event ?? '');
    if (event === 'ready') {
      const dims = body.width && body.height ? `${body.width}x${body.height}` : '';
      this.log(['浮层已就绪', dims, body.scale ? `scale=${body.scale}` : ''].filter(Boolean).join(' '));
    }
    else if (event === 'focus-dsh') this.log('浮层请求聚焦 DSH 窗口');
    else if (event === 'log') this.log(`浮层：${body.message ?? ''}`);
  }
}

/** 会话标题：优先用会话自带头衔，否则用第一条用户消息截断 */
function createTitleTracker() {
  const titles = new Map();
  return (session, event) => {
    const id = session?.id;
    if (!id) return '';
    let title = titles.get(id) ?? '';
    if (!title) {
      const own = session?.header?.title;
      if (typeof own === 'string' && own.trim()) title = excerpt(own, 40);
    }
    if (!title && event?.type === 'user/message') {
      const data = event.data ?? event;
      const text = excerpt(textOfMessage(data.message ?? data.userMessage), 40);
      if (text) title = text.split('\n')[0];
    }
    if (title) titles.set(id, title);
    return title;
  };
}

export function apply(ctx, pluginConfig = {}) {
  const log = createLogger();

  if (process.platform !== 'win32') {
    log('dsh-pet 仅支持 Windows，本次不加载浮层');
    return;
  }

  const store = new ConfigStore(PETS_ROOT);
  const model = new PetModel();
  const psPath = powerShellPath();
  const service = new PetService({ store, model, log, psPath });
  const titleOf = createTitleTracker();
  const token = randomUUID();

  ctx.effect(() => {
    const api = new PetHttpApi({ token, service, log });
    // 注意：prefix 匹配是 `pathname === prefix || pathname.startsWith(prefix + '/')`，
    // 所以这里不能带结尾斜杠，否则 /x-dsh-pet/state 会漏到静态资源的 fallback 上。
    //
    // 重载时若旧实例的路由没被摘掉（webserver 对重复 prefix 直接抛错），
    // 退到 /x-dsh-pet-2 继续跑，而不是让整只宠物消失；浮层跟着 --prefix 走。
    let prefix = PREFIX;
    let unregister = null;
    for (let attempt = 1; attempt <= 5; attempt += 1) {
      const candidate = attempt === 1 ? PREFIX : `${PREFIX}-${attempt}`;
      try {
        unregister = ctx.webServer.register({ kind: 'prefix', path: candidate, handler: api.handler });
        prefix = candidate;
        if (attempt > 1) log(`路由 ${PREFIX} 已被占用，改用 ${candidate}`);
        break;
      } catch (error) {
        if (attempt === 5) throw error;
        log(`挂载 ${candidate} 失败：${error?.message ?? error}`);
      }
    }
    api.prefix = prefix;
    service.prefix = prefix;
    log(`已挂载 ${prefix}/* 到 ${safeOrigin(ctx)}`);

    const tick = setInterval(() => {
      service.refreshPets().catch(() => {});
    }, 15_000);
    tick.unref?.();

    const onSessionEvent = (session, event) => {
      try {
        if (!session?.id) return;
        const isRoot = !session.header?.parentSession;
        model.applyEvent({
          sessionId: session.id,
          isRoot,
          title: titleOf(session, event),
          event,
        });
        if (event?.type === 'turn/end' && isRoot) {
          const kind = event.data?.reason?.kind ?? 'completed';
          log(`回合结束 kind=${kind} → 提醒=${kind === 'completed' ? '已完成' : kind}`);
        }
      } catch (error) {
        log(`处理会话事件失败：${error?.message ?? error}`);
      }
    };
    const onAgentStatus = (payload) => {
      try {
        const sessionId = payload?.agent?.session?.id ?? payload?.agent?.sessionId ?? payload?.sessionId;
        if (sessionId) model.setAgentStatus({ sessionId, status: payload?.status });
      } catch (error) {
        log(`处理 agent/status 失败：${error?.message ?? error}`);
      }
    };
    const offSession = ctx.on('session/event', onSessionEvent);
    const offStatus = ctx.on('agent/status', onAgentStatus);

    // 这两条是 waterfall 事件：只观察、不抢答，必须 next() 交回给真正的处理者，
    // 否则审批面板/提问卡片会被我们静默吞掉。
    const observeWaiting = (pickSession) => async (request, next) => {
      const sessionId = pickSession(request);
      try {
        model.setWaiting(sessionId);
      } catch (error) {
        log(`等待态观察失败：${error?.message ?? error}`);
      }
      try {
        return await next();
      } finally {
        try {
          model.resumeFromWaiting(sessionId);
        } catch {}
      }
    };
    const sessionIdOf = (request) => request?.agent?.session?.id ?? request?.agent?.sessionId ?? '';
    const offApproval = ctx.on('approval/request', observeWaiting(sessionIdOf));
    const offQuestions = ctx.on('user-questions/request', observeWaiting(sessionIdOf));

    // `/pet`：DSH 内的查看/操作入口。浮层本身在窗口外，出问题时用户需要一个
    // 不依赖浮层的自检入口（csc 编译失败、宠物装失败都能在这里看到原因）。
    const offCommand = ctx.inject(['commands'], (scope) => {
      log('注册 /pet 命令（DSH 内的自检入口）');
      return scope.commands.register({
        name: 'pet',
        description: '智能伙伴浮层：查看状态 / 切换宠物 / 安装 petdex 宠物',
        input: { hint: 'status | pets | pet <名称> | scale <40-125> | install <petdex 名称或链接> | test' },
        handler: async (invocation) => {
          try {
            const [verb = 'status', ...rest] = String(invocation?.rawInput ?? '').trim().split(/\s+/).filter(Boolean);
            const arg = rest.join(' ');
            const text = await runPetCommand(verb.toLowerCase(), arg);
            return { kind: 'success', text };
          } catch (error) {
            return { kind: 'error', text: String(error?.message ?? error) };
          }
        },
      });
    });

    const runPetCommand = async (verb, arg) => {
      if (!service) return '智能伙伴还在启动中，请稍后再试';
      const config = store.get();
      if (verb === 'status' || verb === '') {
        const st = service.state();
        const overlay = st.overlay === 'running' ? '运行中' : st.overlay === 'unavailable' ? '不可用' : st.overlay;
        const petName = st.pet?.name ?? '未选择';
        return [
          `浮层：${overlay}`,
          `宠物：${petName}${st.pet ? `（${st.pet.id}）` : ''}`,
          `气泡：${config.bubble ? '开' : '关'} · 提示音：${config.sound ? '开' : '关'} · 系统通知：${config.toast ? '开' : '关'}`,
          `缩放：${Math.round(config.scale * 100)}%`,
          `已装宠物：${st.pets.length} 只`,
          st.alert ? `待确认提醒：${st.alert.text}` : '没有待确认的提醒',
          `日志：${HOST_LOG}`,
        ].join('\n');
      }
      if (verb === 'pets') {
        const list = service.state().pets;
        if (!list.length) return '还没有安装宠物。用 `/pet install boba` 或桌面右键 ⋯ 安装 petdex 宠物。';
        return list
          .map((item) => `${item.id === config.pet ? '· ' : '  '}${item.id} — ${item.name}`)
          .join('\n');
      }
      if (verb === 'pet') {
        if (!arg) throw new Error('用法：/pet pet <宠物 id>，先用 /pet pets 查看');
        await service.configure({ pet: arg });
        return `已切换到 ${arg}`;
      }
      if (verb === 'scale') {
        const pct = Number(arg);
        if (!Number.isFinite(pct) || pct < 40 || pct > 125) throw new Error('用法：/pet scale 40-125');
        await service.configure({ scale: String(pct) });
        return `缩放已设为 ${pct}%`;
      }
      if (verb === 'install') {
        if (!arg) throw new Error('用法：/pet install <petdex 名称 | petdex.dev 链接 | 本地 zip/文件夹>');
        return await service.install(arg);
      }
      if (verb === 'test') {
        service.test();
        return '已触发一次"任务结束"演练，看看浮层上的提醒';
      }
      if (verb === 'sound' || verb === 'bubble' || verb === 'toast') {
        const on = arg !== 'off' && arg !== '0' && arg !== 'false';
        await service.configure({ [verb]: on ? '1' : '0' });
        return `${verb} 已${on ? '开启' : '关闭'}`;
      }
      throw new Error(`未知子命令 ${verb}，可用：status / pets / pet / scale / install / test / sound / bubble / toast`);
    };

    let supervisor = null;
    const boot = async () => {
      try {
        await mkdir(LOG_DIR, { recursive: true });
        await store.load();
        model.setLang?.(store.get().lang);
        const config = store.get();
        if (config.pet) model.setLang?.(config.lang);

        const csPath = join(pluginDir, 'native', 'PetOverlay.cs');
        const manifestPath = join(pluginDir, 'native', 'app.manifest');
        const exePath = join(PETS_ROOT, 'bin', 'PetOverlay.exe');

        await service.refreshPets().catch(() => {});
        if (service.pets.length === 0) {
          // 空目录：后台装一只默认宠物，不阻塞浮层启动
          service
            .install(DEFAULT_PET_SLUG)
            .then((message) => log(`默认宠物：${message}`))
            .catch(() => {});
        }
        const build = await buildOverlay({ csPath, manifestPath, exePath, log });
        if (!build.ok) log(`浮层不可用：${build.message}`);

        supervisor = new OverlaySupervisor({
          exePath,
          host: safeOrigin(ctx),
          token,
          prefix,
          logFile: OVERLAY_LOG,
          log,
        });
        service.supervisor = supervisor;

        await writeRuntimeFile(join(PETS_ROOT, 'runtime.json'), {
          token,
          prefix,
          host: safeOrigin(ctx),
          hostPid: process.pid,
          startedAt: new Date().toISOString(),
        }).catch(() => {});

        if (build.ok) await supervisor.start();
      } catch (error) {
        log(`启动失败：${error?.message ?? error}`);
      }
    };
    boot().then(async () => {
      // 启动后跑一次 /pet status，证明不依赖浮层的自检入口真的可用
      try {
        const first = String(await runPetCommand('status', '')).split('\n')[0];
        log(`/pet 自检：${first}`);
      } catch (error) {
        log(`/pet 自检失败：${error?.message ?? error}`);
      }
    });

    return async () => {
      clearInterval(tick);
      // 单个 disposer 失败不能连坐：之前 offCommand 抛出过 TypeError，
      // 结果 unregister 被跳过，路由永久留在 webserver 上，重载就撞 duplicate。
      const dispose = (fn, label) => {
        if (typeof fn !== 'function') return;
        try {
          fn();
        } catch (error) {
          log(`释放 ${label} 失败：${error?.message ?? error}`);
        }
      };
      dispose(unregister, '路由');
      dispose(offSession, 'session/event');
      dispose(offStatus, 'agent/status');
      dispose(offApproval, 'approval/request');
      dispose(offQuestions, 'user-questions/request');
      dispose(offCommand, '/pet 命令');
      try {
        await supervisor?.stop();
      } catch {}
      log('dsh-pet 已卸载');
    };
  });
}

function safeOrigin(ctx) {
  try {
    return ctx.webServer.getOrigin();
  } catch {
    return 'http://127.0.0.1:14711';
  }
}