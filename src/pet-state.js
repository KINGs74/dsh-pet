// dsh-pet · 会话 → 宠物状态的纯函数归约器
//
// 这一层不碰文件、不碰网络、不认识 DSH 的运行时对象：它只吃「会话事件」这种
// 普通对象，产出宠物窗要展示的状态。因此可以脱离宿主单独单测。
//
// 状态语义：
//   running —— 正在干活（有进行中的 turn）
//   waiting —— 在等你（提问 / 审批挂起）
//   done    —— 最近一次 turn 正常结束（触发"任务已结束"提醒）
//   failed  —— 最近一次 turn 异常结束（同样提醒，但图标/文案不同）
//   idle    —— 没有活动会话，或从未开始
export const PetStatus = Object.freeze({
  unknown: 'unknown',
  idle: 'idle',
  running: 'running',
  waiting: 'waiting',
  failed: 'failed',
  done: 'done',
});

/** 把事件对象里可能嵌套在 data 下的字段摊平（不同版本事件包装不同） */
function flat(event) {
  if (!event || typeof event !== 'object') return {};
  if (event.data && typeof event.data === 'object' && !Array.isArray(event.data)) {
    return { ...event, ...event.data };
  }
  return event;
}

/** 一条消息里不代表"最终回答"的分片：思考过程、工具调用、图片等 */
const NON_TEXT_PARTS = new Set([
  'reasoning',
  'thinking',
  'analysis',
  'thought',
  'tool-call',
  'tool_call',
  'toolcall',
  'tool-result',
  'tool_result',
  'tooluse',
  'tool-use',
  'image',
  'file',
  'attachment',
]);

function partText(part) {
  if (!part) return '';
  if (typeof part === 'string') return part;
  const type = String(part.type ?? part.kind ?? '').toLowerCase();
  // 思考过程绝不能出现在宠物气泡里（对用户是噪音，也可能包含不该外泄的推理）
  if (type && NON_TEXT_PARTS.has(type)) return '';
  if (typeof part.text === 'string') return part.text;
  if (type === 'text' && typeof part.value === 'string') return part.value;
  return '';
}

/** 从各种形态的消息对象里尽力取出纯文本 */
export function textOfMessage(message) {
  if (!message) return '';
  if (typeof message === 'string') return message;
  if (Array.isArray(message)) return message.map(partText).filter(Boolean).join('\n');
  const parts = Array.isArray(message.parts)
    ? message.parts
    : Array.isArray(message.content)
      ? message.content
      : null;
  if (parts) return parts.map(partText).filter(Boolean).join('\n');
  if (typeof message.content === 'string') return message.content;
  if (typeof message.text === 'string') return message.text;
  return '';
}

/** 单行摘要：合并空白、截断，保留可读的句首 */
export function excerpt(text, max = 160) {
  const clean = String(text ?? '')
    .replace(/\r/g, '')
    .replace(/```[^\n]*\n?/g, ' ')
    .replace(/\s+/g, ' ')
    .trim();
  if (clean.length <= max) return clean;
  return `${clean.slice(0, max - 1).trimEnd()}…`;
}

function reasonKind(reason) {
  if (!reason) return 'completed';
  if (typeof reason === 'string') return reason;
  if (typeof reason.kind === 'string') return reason.kind;
  if (typeof reason.type === 'string') return reason.type;
  return 'completed';
}

function reasonDetail(reason) {
  if (!reason || typeof reason !== 'object') return '';
  const error = reason.error ?? reason.reason ?? null;
  if (!error) return '';
  if (typeof error === 'string') return error;
  return error.message ?? error.reason ?? error.name ?? '';
}

const ALERT_TEXT = {
  completed: { zh: '任务已完成，可以回来看了', en: 'Task finished — come take a look' },
  aborted: { zh: '任务被中止了', en: 'Task was aborted' },
  blocked: { zh: '任务被阻塞了', en: 'Task is blocked' },
  error: { zh: '任务执行失败', en: 'Task failed' },
  'max-tokens': { zh: '达到 token 上限，提前结束', en: 'Stopped at the token limit' },
  failed: { zh: '任务执行失败，可以回来看原因了', en: 'Task failed — come back for details' },
  interrupted: { zh: '任务被中断', en: 'Task was interrupted' },
  forked: { zh: '会话已分叉', en: 'Session was forked' },
};

export class PetModel {
  constructor({ now = () => Date.now() } = {}) {
    this.now = now;
    /** @type {Map<string, {title: string, isRoot: boolean, lastAt: number, turn: number, step: number, tool: string}>} */
    this.sessions = new Map();
    this.activeId = '';
    this.status = PetStatus.unknown;
    this.lastText = '';
    this.lastUser = '';
    // 演练文案只挂在提醒上，绝不写进 lastText —— 否则演练完气泡会一直停在"退出码 1"上
    this.rehearsal = '';
    this.alert = null;
    this.ackedId = '';
    this.turnStartedAt = 0;
    this.endedAt = 0;
    this.updatedAt = 0;
    this.lang = 'zh';
    /** 每个会话挂起中的审批/提问数：@type {Map<string, number>} */
    this.pending = new Map();
  }

  setLang(lang) {
    this.lang = lang === 'en' ? 'en' : 'zh';
  }

  /** 记录一个会话存在，并把它设为"当前关注"（最近活动的根会话优先） */
  observeSession({ sessionId, isRoot = false, title = '' } = {}) {
    if (!sessionId) return;
    const entry = this._entry(sessionId);
    entry.isRoot = Boolean(isRoot);
    if (title) entry.title = title;
    entry.lastAt = this.now();
    this.activeId = this._pickActive(sessionId);
    this.updatedAt = entry.lastAt;
  }

  _entry(sessionId) {
    let entry = this.sessions.get(sessionId);
    if (!entry) {
      entry = { title: '', isRoot: false, lastAt: 0, turn: 0, step: 0, tool: '' };
      this.sessions.set(sessionId, entry);
    }
    return entry;
  }

  /** 根会话优先；同为根会话时取最近活动者 */
  _pickActive(fallbackId) {
    let best = '';
    let bestAt = -1;
    for (const [id, entry] of this.sessions) {
      if (!entry.isRoot) continue;
      if (entry.lastAt > bestAt) {
        bestAt = entry.lastAt;
        best = id;
      }
    }
    if (best) return best;
    const fallback = this.sessions.get(fallbackId);
    if (fallback) return fallbackId;
    return this.activeId;
  }

  /** 处理一条 session/event */
  applyEvent({ sessionId, isRoot = false, title = '', event } = {}) {
    if (!sessionId || !event) return this.snapshot();
    const entry = this._entry(sessionId);
    entry.isRoot = Boolean(isRoot) || entry.isRoot;
    if (title) entry.title = title;
    entry.lastAt = this.now();
    this.activeId = this._pickActive(sessionId);
    // 非当前关注会话只用来刷新"谁最近活过"，不改展示状态
    const isActive = this.activeId === sessionId || !this._hasActiveRoot();

    const e = flat(event);
    const type = String(e.type ?? e.kind ?? '');

    switch (type) {
      case 'turn/start': {
        entry.turn = Number(e.turn ?? entry.turn ?? 0) || entry.turn;
        entry.step = 0;
        entry.tool = '';
        if (isActive) {
          this.turnStartedAt = this.now();
          this.endedAt = 0;
          this.alert = null;
          this.status = PetStatus.running;
          // 新一轮开始时清掉上一轮的结论，否则气泡会一直停在上一轮的答案上
          this.lastText = '';
          this.rehearsal = '';
        }
        break;
      }
      case 'step/start': {
        entry.step = Number(e.step ?? entry.step ?? 0) || entry.step;
        if (isActive && this.status !== PetStatus.waiting) this.status = PetStatus.running;
        break;
      }
      case 'tool/call': {
        entry.tool = String(e.name ?? entry.tool ?? '');
        if (isActive) this.status = PetStatus.running;
        break;
      }
      case 'tool/result': {
        if (isActive && e.error) {
          const prefix = this.lang === 'en' ? `Tool ${entry.tool} failed: ` : `工具 ${entry.tool} 报错：`;
          this.lastText = prefix + excerpt(reasonDetail(e.error), 80);
        }
        break;
      }
      case 'user/message': {
        entry.turn = Number(e.turn ?? entry.turn ?? 0) || entry.turn;
        const text = excerpt(textOfMessage(e.message ?? e.userMessage), 200);
        if (text) this.lastUser = text;
        if (isActive) {
          this.status = PetStatus.running;
          this.turnStartedAt = this.now();
          this.endedAt = 0;
          this.alert = null;
          this.rehearsal = '';
        }
        break;
      }
      case 'assistant/message': {
        const streaming = e.stream === true;
        const text = excerpt(textOfMessage(e.message), 200);
        if (text && !streaming) this.lastText = text;
        if (isActive && !streaming) this.status = PetStatus.running;
        break;
      }
      case 'turn/end': {
        const kind = reasonKind(e.reason);
        if (isActive) {
          this.endedAt = this.now();
          const success = kind === 'completed';
          this.status = success ? PetStatus.done : PetStatus.failed;
          const detail = reasonDetail(e.reason);
          if (!success && detail) {
            const label = (ALERT_TEXT[kind] ?? ALERT_TEXT.completed)[this.lang] ?? (ALERT_TEXT[kind] ?? ALERT_TEXT.completed).zh;
            this.lastText = `${label}：${excerpt(detail, 90)}`;
          }
          this.alert = {
            id: `${sessionId}:${entry.turn}:${kind}`,
            kind,
            text: '',
          };
          this.updatedAt = this.endedAt;
        }
        break;
      }
      default:
        break;
    }

    this.ensureTiming(this.now());
    this.updatedAt = this.now();
    return this.snapshot();
  }

  _hasActiveRoot() {
    const entry = this.sessions.get(this.activeId);
    return Boolean(entry && entry.isRoot);
  }

  /**
   * 挂起等待（审批 / 提问）。带 sessionId 时只认当前关注会话的挂起，
   * 否则任何后台子会话弹审批都会把气泡抢成"在等你确认"。
   */
  setWaiting(sessionId = '') {
    const id = sessionId || this.activeId;
    if (!id) return;
    this.pending.set(id, (this.pending.get(id) ?? 0) + 1);
    this._refreshWaiting();
  }

  /** 用户回来处理了挂起项 */
  resumeFromWaiting(sessionId = '') {
    const id = sessionId || this.activeId;
    if (!id) return;
    const count = (this.pending.get(id) ?? 0) - 1;
    if (count > 0) this.pending.set(id, count);
    else this.pending.delete(id);
    this._refreshWaiting();
  }

  /** 当前关注会话还有几项在等用户 */
  get pendingCount() {
    return this.pending.get(this.activeId) ?? 0;
  }

  _refreshWaiting() {
    if (this.pendingCount > 0) {
      if (this.status === PetStatus.running || this.status === PetStatus.unknown) {
        this.status = PetStatus.waiting;
      }
    } else if (this.status === PetStatus.waiting) {
      this.status = PetStatus.running;
      // 挂起期间没有 turn/start，补一个计时起点，避免 elapsed 从 0 重来
      this.ensureTiming(this.now());
    }
    this.updatedAt = this.now();
  }

  setAgentStatus({ sessionId, status } = {}) {
    if (!sessionId) return;
    const entry = this._entry(sessionId);
    entry.lastAt = this.now();
    if (sessionId !== this.activeId) return;
    if (status === 'running' && this.status === PetStatus.idle) this.status = PetStatus.running;
    if (status === 'idle' && this.status === PetStatus.running && !this.turnStartedAt) this.status = PetStatus.idle;
  }

  ack(alertId = '') {
    if (!this.alert) return;
    if (alertId && this.alert.id !== alertId) return;
    this.ackedId = this.alert.id;
    this.alert = null;
    this.rehearsal = '';
    this.updatedAt = this.now();
  }

  /**
   * 产生一条"演练用"的结束提醒（托盘菜单「测试结束提醒」用到）。
   * 不经过真实 turn/end，但走完提醒的全部下游链路，方便用户确认效果。
   */
  previewAlert(kind = 'completed') {
    const at = this.now();
    const success = kind === 'completed';
    this.status = success ? PetStatus.done : PetStatus.failed;
    this.endedAt = at;
    if (!success) {
      const table = ALERT_TEXT[kind] ?? ALERT_TEXT.error;
      const label = table[this.lang] ?? table.zh;
      this.rehearsal = this.lang === 'en'
        ? `${label}: step 42 exited with code 1 (rehearsal)`
        : `${label}：第 42 步 执行 ops_bash 时退出码 1（演练）`;
    }
    this.alert = { id: `preview:${at}`, kind, text: '' };
    this.updatedAt = at;
  }

  get elapsedSeconds() {
    if (!this.turnStartedAt) return 0;
    const end = this.endedAt || this.now();
    return Math.max(0, Math.round((end - this.turnStartedAt) / 1000));
  }

  /** 插件在某轮进行到一半时才挂载（或刚重启）时，错过 turn/start 也要有个计时起点 */
  ensureTiming(at) {
    if (!this.turnStartedAt && this.status === PetStatus.running) this.turnStartedAt = at;
  }

  get alertTitle() {
    if (!this.alert) return '';
    const table = ALERT_TEXT[this.alert.kind] ?? ALERT_TEXT.completed;
    return table[this.lang] ?? table.zh;
  }

  snapshot() {
    const entry = this.sessions.get(this.activeId) ?? { title: '', turn: 0, step: 0, tool: '' };
    const elapsed = this.elapsedSeconds;
    const en = this.lang === 'en';

    let line1 = this.lastText;
    const waiting = this.pendingCount;
    if (this.status === PetStatus.waiting) {
      line1 = waiting > 1
        ? (en ? `${waiting} confirmations waiting…` : `${waiting} 项待你确认…`)
        : (en ? 'Waiting for your confirmation…' : '在等你确认后继续…');
    } else if (!line1) {
      if (this.alert) {
        // 标题已经写了"任务已完成"这类结论，正文再重复一遍就是噪音；
        // 用你上次的请求补一句上下文更有用。
        // 演练文案优先（它自带原因），否则用你上次的请求补上下文，别把标题重复一遍
        line1 = this.rehearsal
          || (this.lastUser
            ? (en ? `Your last request: “${this.lastUser}”` : `你上次的请求：「${this.lastUser}」`)
            : this.alertTitle);
      } else if (this.status === PetStatus.running) {
        line1 = entry.tool
          ? en ? `Running ${entry.tool}…` : `正在执行 ${entry.tool}`
          : en ? 'Thinking…' : '正在思考…';
      } else if (this.status === PetStatus.done) {
        line1 = en ? 'Everything is up to date' : '当前没有进行中的任务';
      } else {
        line1 = en ? 'Nothing running right now' : '暂时没有进行中的任务';
      }
    }

    const parts = [];
    if (entry.turn) parts.push(en ? `turn ${entry.turn}` : `第 ${entry.turn} 轮`);
    if (entry.step) parts.push(en ? `step ${entry.step}` : `第 ${entry.step} 步`);
    if (elapsed) parts.push(`${elapsed}s`);
    // 正文里已经写了工具名就别在页脚再念一遍
    if (entry.tool && this.status === PetStatus.running && line1.indexOf(entry.tool) === -1) {
      parts.push(entry.tool);
    }

    const alert = this.alert
      ? { id: this.alert.id, kind: this.alert.kind, text: this.alertTitle }
      : null;

    return {
      status: this.status,
      title: entry.title || (this.lang === 'en' ? 'DSH companion' : 'DSH 智能伙伴'),
      line1,
      line2: parts.join(' · '),
      elapsed,
      turn: entry.turn || 0,
      step: entry.step || 0,
      tool: entry.tool || '',
      waiting: this.pendingCount,
      alert,
      sessionId: this.activeId,
      updatedAt: this.updatedAt,
    };
  }
}