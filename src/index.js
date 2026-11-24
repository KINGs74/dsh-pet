// dsh-pet · 宿主插件入口
//
// 职责链：DSH 会话事件 → PetModel（纯状态机）→ /x-dsh-pet/state 行协议 →
//        PetOverlay.exe（独立置顶分层窗口，托盘可控）。
//
// 为什么要有独立进程，见 src/overlay.js 顶部注释：DSH 桌面端不向插件开放窗口
// 能力，任何"在页面里"的方案都会被面板裁剪，也就不满足"拖到全屏任意位置"。
import { randomUUID } from 'node:crypto';
import { existsSync } from 'node:fs';
import { appendFile, mkdir } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import { ConfigStore, PETS_ROOT } from './config-store.js';
import { OverlayCompose } from './compose-targets.js';
import { OverlaySupervisor, buildOverlay, writeRuntimeFile } from './overlay.js';
import { PetModel, excerpt, textOfMessage } from './pet-state.js';
import { installPet, listPets, powerShellPath } from './pets.js';
import { startProfiler } from './profiler.js';
import { PetHttpApi, PREFIX, VISIBILITY_PATH, visibilityHandler } from './routes.js';

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

function profileRequested() {
  if (process.env.DSH_PET_PROFILE === '1') return true;
  if (process.env.DSH_PET_PROFILE === '0') return false;
  return existsSync(join(PETS_ROOT, 'profile.on'));
}

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

function config_lang(lang) {
  return lang === 'en' ? 'en' : 'zh';
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
    this.compose = new OverlayCompose({
      recentId: () => this.model.snapshot().sessionId,
      language: () => this.store.get().lang,
      onSent: (message) => this.setToast(message),
      log,
    });
    this.profiler = null;
    this._alertSince = 0;
    this._alertIdSeen = '';
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
      visible: config.visible,
      bubble: config.bubble,
      sound: config.sound,
      bubbleMs: config.bubbleMs,
      alertMs: config.alertMs,
      lang: config.lang,
      toastText: toastAlive ? this.toastText : '',
      overlay: this.supervisor?.status().state ?? 'stopped',
      pos: config.pos,
      dshPid: process.ppid,
      dshExe: process.execPath,
      alertAge: this.alertAge(snapshot),
    };
  }

  /** 结束提醒从第一次被看到算起，给气泡一个「多久之前」的说法 */
  alertAge(snapshot) {
    const id = snapshot?.alert?.id ?? '';
    if (!id) {
      this._alertIdSeen = '';
      this._alertSince = 0;
      return '';
    }
    if (id !== this._alertIdSeen) {
      this._alertIdSeen = id;
      this._alertSince = Date.now();
      return '';
    }
    const age = Date.now() - this._alertSince;
    if (age < 60_000) return '';
    const minutes = Math.round(age / 60_000);
    return config_lang(this.store.get().lang) === 'en' ? `${minutes} min ago` : `${minutes} 分钟前结束`;
  }

  /** 兼容 DSH 中的 /pet say 命令，其会话选择维持原有行为。 */
  async say(text) {
    const content = String(text ?? '').trim();
    if (!content) return '失败：消息是空的';
    const snapshot = this.model.snapshot();
    const sessionId = snapshot?.sessionId;
    if (!sessionId) return '失败：还没有进行中的对话，先在 DSH 里开一个会话';
    const controller = this.compose.controller;
    if (!controller || typeof controller.prompt !== 'function') {
      return '失败：当前 DSH 版本没有开放 sessionController，无法从浮层发消息';
    }
    try {
      const requestId = `pet-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
      await controller.prompt(
        { requestId, sessionId, mode: 'queue', content: [{ type: 'text', text: content }] },
        AbortSignal.timeout(15_000),
      );
      this.setToast('已发送');
      this.log('/pet say 消息已提交');
      return '已发送';
    } catch (error) {
      const message = error?.message ?? String(error);
      this.log('/pet say 消息发送失败');
      return `失败：${message}`;
    }
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
    if (body.visible !== undefined) patch.visible = asBool(body.visible, config.visible);
    if (body.bubble !== undefined) patch.bubble = asBool(body.bubble, config.bubble);
    if (body.sound !== undefined) patch.sound = asBool(body.sound, config.sound);
    if (body.bubbleMs !== undefined && body.bubbleMs !== '') {
      const raw = Number(body.bubbleMs);
      if (Number.isFinite(raw) && raw >= 0) patch.bubbleMs = Math.round(raw);
    }
    if (body.alertMs !== undefined && body.alertMs !== '') {
      const raw = Number(body.alertMs);
      if (Number.isFinite(raw) && raw >= 0) patch.alertMs = Math.round(raw);
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

  // 压测/误触时清空排队中的用户消息。updateQueue 只接受单条 itemId，
  // 所以先 state() 拿到 queue，再逐条 remove（kind 必须是 {kind:'remove'}）。
  async clearQueue() {
    const controller = this.compose.controller;
    const sessionId = this.model.snapshot()?.sessionId;
    if (!controller || !sessionId) return '失败：没有可用的会话';
    try {
      // state()/updateQueue() 的第二个参数是必填的 AbortSignal，少传就会在内部
      // throwIfAborted 上炸（'Cannot read properties of undefined'）。
      const state = await controller.state({ sessionId }, AbortSignal.timeout(10_000));
      const queue = Array.isArray(state?.queue) ? state.queue : [];
      let removed = 0;
      for (const item of queue) {
        const itemId = item?.itemId ?? item?.id ?? item?.messageId;
        if (!itemId) continue;
        await controller.updateQueue(
          { sessionId, itemId, action: { kind: 'remove' } },
          AbortSignal.timeout(10_000),
        );
        removed += 1;
      }
      this.log(`清空排队消息 ${removed} 条`);
      return `已清空 ${removed} 条排队消息`;
    } catch (error) {
      return `失败：${error?.message ?? error}`;
    }
  }

  // 运行时开关采样：卡死是随机发生的，不能为了取证让用户重启 DSH，
  // 所以给一条带 token 的 HTTP 通道（POST /profile {on:1}）随时开。
  profile(body = {}) {
    const on = String(body.on ?? '').trim();
    if (on === '1' || on === 'true') {
      if (this.profiler) return '采样已经开着';
      this.profiler = startProfiler({ dir: LOG_DIR, log: this.log, thresholdMs: 1500 });
      this.log('已开启事件循环采样（运行时开关）');
      return '采样已开启';
    }
    if (!this.profiler) return '采样本来就没开';
    const info = this.profiler.info();
    void this.profiler.stop();
    this.profiler = null;
    return `采样已停止（profile ${info.profiles} 份，最大阻塞 ${info.maxLagMs}ms，p99 ${info.p99Ms}ms）`;
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

    const configReady = mkdir(LOG_DIR, { recursive: true })
      .then(() => store.load((message) => log(`配置：${message}`)));
    const offVisibility = ctx.inject(['connection'], (scope) => {
      const routes = scope.connection.fetch;
      if (typeof routes?.register !== 'function') return;
      return routes.register({
        path: VISIBILITY_PATH,
        methods: ['GET', 'PUT'],
        requestBody: 'buffered',
        fetch: visibilityHandler({ service, ready: configReady }),
      });
    });

    const offController = ctx.inject(['sessionController'], (scope) => {
      const unbind = service.compose.bind(scope.sessionController);
      log('已接上 sessionController（浮层可以选择会话并发消息）');
      return unbind;
    });

    const tick = setInterval(() => {
      service.refreshPets().catch(() => {});
    }, 15_000);
    tick.unref?.();

    // 宿主事件循环看门狗：每 2 秒对一次表，超时说明这一侧的 JS 被同步操作拖住了。
    // 卡死排查时 overlay.log 的 ui stall 与这里的 loop lag 一对照，立刻能分清是
    // 浮层 UI 线程还是 DSH 宿主卡住（0.2 那次两个进程都有嫌疑，但没有证据可查）。
    let lagExpected = Date.now() + 2000;
    const lagTimer = setInterval(() => {
      const now = Date.now();
      const lag = now - lagExpected;
      if (lag > 1500) log(`loop lag ${lag}ms`);
      lagExpected = now + 2000;
    }, 2000);
    lagTimer.unref?.();

    // 只有 DSH_PET_PROFILE=1 才开采样：常开会有采样线程的固定开销，
    // 默认用户不该为排查付这笔钱。开了之后卡死会自己落一份 cpuprofile。
    // 开关优先级：环境变量 > 标记文件。标记文件是为了"不重启 DSH 也能从启动那一刻开始采"，
    // 卡死往往发生在启动后几秒，等插件起来再开采样就采不到了。
    if (!service.profiler && profileRequested()) {
      service.profiler = startProfiler({ dir: LOG_DIR, log, thresholdMs: 1500 });
      log(`已开启事件循环采样（${process.env.DSH_PET_PROFILE === '1' ? 'DSH_PET_PROFILE=1' : '标记文件 profile.on'}）`);
    }

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
        input: { hint: 'status | show | hide | pets | pet <名称> | scale <40-125> | bubble <秒> | say <内容> | install <petdex 名称或链接> | test | restart' },
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
        const overlay = st.overlay === 'running' ? '运行中' : st.overlay === 'unavailable' ? '不可用（可用 /pet restart 重试）' : st.overlay;
        const petName = st.pet?.name ?? '未选择';
        return [
          `浮层：${overlay}`,
          `宠物：${petName}${st.pet ? `（${st.pet.id}）` : ''} · ${config.visible ? '显示' : '隐藏'}`,
          `气泡：${config.bubble ? '开' : '关'}（${config.bubbleMs === 0 ? '一直显示' : `${Math.round(config.bubbleMs / 1000)} 秒`}）· 提示音：${config.sound ? '开' : '关'}`,
          `缩放：${Math.round(config.scale * 100)}%`,
          `已装宠物：${st.pets.length} 只`,
          st.alert ? `待确认提醒：${st.alert.text}` : '没有待确认的提醒',
          `日志：${HOST_LOG}`,
        ].join('\n');
      }
      if (verb === 'show' || verb === 'hide') {
        await service.configure({ visible: verb === 'show' });
        return verb === 'show' ? '已设置为显示宠物' : '已设置为隐藏宠物。输入 /pet show 可恢复。';
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
        service.test(arg === 'failed' ? 'failed' : 'completed');
        return '已触发一次"任务结束"演练，看看浮层上的提醒';
      }
      if (verb === 'bubble') {
        if (!arg) return `气泡停留 ${Math.round(config.bubbleMs / 1000)} 秒（0 = 一直显示）`;
        const seconds = Number(arg);
        if (!Number.isFinite(seconds) || seconds < 0 || seconds > 600) throw new Error('用法：/pet bubble 3（秒，0 = 一直显示）');
        await service.configure({ bubbleMs: String(Math.round(seconds * 1000)) });
        return seconds === 0 ? '气泡改为一直显示' : `气泡改为停留 ${Math.round(seconds)} 秒`;
      }
      if (verb === 'say') {
        if (!arg) throw new Error('用法：/pet say 你好');
        return await service.say(arg);
      }
      if (verb === 'restart') {
        const status = await supervisor?.restart();
        return status?.state === 'running' ? `浮层已重启 pid=${status.pid}` : `浮层重启失败：${status?.error || status?.state}`;
      }
      if (verb === 'sound') {
        const on = arg !== 'off' && arg !== '0' && arg !== 'false';
        await service.configure({ sound: on ? '1' : '0' });
        return `提示音已${on ? '开启' : '关闭'}`;
      }
      throw new Error(`未知子命令 ${verb}，可用：status / show / hide / pets / pet / scale / bubble / say / install / test / sound / restart`);
    };

    let supervisor = null;
    const lifetime = new AbortController();
    const signal = lifetime.signal;
    const boot = async () => {
      try {
        await configReady;
        signal.throwIfAborted();
        model.setLang?.(store.get().lang);
        const config = store.get();
        if (config.pet) model.setLang?.(config.lang);

        const nativeDir = join(pluginDir, 'native');
        const exePath = join(PETS_ROOT, 'bin', 'PetOverlay.exe');

        await service.refreshPets().catch(() => {});
        signal.throwIfAborted();
        if (service.pets.length === 0) {
          // 空目录：后台装一只默认宠物，不阻塞浮层启动
          service
            .install(DEFAULT_PET_SLUG)
            .then((message) => log(`默认宠物：${message}`))
            .catch(() => {});
        }
        const build = await buildOverlay({ nativeDir, exePath, log, signal });
        signal.throwIfAborted();
        if (!build.ok) log(`浮层不可用：${build.message}`);

        supervisor = new OverlaySupervisor({
          exePath,
          host: safeOrigin(ctx),
          token,
          prefix,
          dshPid: process.ppid,
          dshExe: process.execPath,
          logFile: OVERLAY_LOG,
          log,
        });
        service.supervisor = supervisor;

        await writeRuntimeFile(join(PETS_ROOT, 'runtime.json'), {
          token,
          prefix,
          host: safeOrigin(ctx),
          hostPid: process.pid,
          dshPid: process.ppid,
          dshExe: process.execPath,
          startedAt: new Date().toISOString(),
        }).catch(() => {});

        signal.throwIfAborted();
        if (build.ok) await supervisor.start();
      } catch (error) {
        if (!signal.aborted) log(`启动失败：${error?.message ?? error}`);
      }
    };
    boot().then(async () => {
      if (signal.aborted) return;
      // 启动后跑一次 /pet status，证明不依赖浮层的自检入口真的可用
      try {
        const first = String(await runPetCommand('status', '')).split('\n')[0];
        log(`/pet 自检：${first}`);
      } catch (error) {
        log(`/pet 自检失败：${error?.message ?? error}`);
      }
    });

    return async () => {
      lifetime.abort();
      service.compose.dispose();
      clearInterval(tick);
      clearInterval(lagTimer);
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
      dispose(offController, 'sessionController');
      await Promise.allSettled([
        typeof offVisibility === 'function' ? Promise.resolve().then(offVisibility) : undefined,
        supervisor?.stop(),
        service.profiler?.stop(),
      ]);
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