// The desktop frontend owns the selected chat. The host can enumerate eligible chats.
const REQUEST_ID = /^(?:[0-9a-f]{32}|[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12})$/i;

function canonicalRequestId(id) {
  const hex = id.replaceAll('-', '').toLowerCase();
  return [hex.slice(0, 8), hex.slice(8, 12), hex.slice(12, 16), hex.slice(16, 20), hex.slice(20)].join('-');
}
const MAX_DRAFT_LENGTH = 6000;

function eligible(item) {
  return typeof item?.sessionId === 'string' && item.sessionId.length > 0
    && item.agentAvailable === true && !item.parentSessionId && item.origin !== 'subagent'
    && item.formatStatus !== 'migration-required';
}

// The SDK observes cancellation before admission. The caller must also stop
// waiting at its deadline if admission is already in progress.
async function within(signal, operation) {
  signal.throwIfAborted();
  let onAbort;
  const cancelled = new Promise((_, reject) => {
    onAbort = () => reject(signal.reason);
    signal.addEventListener('abort', onAbort, { once: true });
  });
  try {
    const pending = Promise.resolve().then(() => {
      signal.throwIfAborted();
      return operation(signal);
    });
    return await Promise.race([pending, cancelled]);
  } finally {
    signal.removeEventListener('abort', onAbort);
  }
}

export class OverlayCompose {
  #binding;

  constructor({ recentId, language, onSent, log, listTimeout = 4000, submitTimeout = 12_000 }) {
    this.recentId = recentId;
    this.language = language;
    this.onSent = onSent;
    this.log = log;
    this.listTimeout = listTimeout;
    this.submitTimeout = submitTimeout;
  }

  get controller() {
    return this.#binding?.controller;
  }

  bind(controller) {
    this.dispose();
    const binding = { controller, lifetime: new AbortController(), newSessions: new Map() };
    this.#binding = binding;
    return () => {
      if (this.#binding === binding) this.dispose();
    };
  }

  dispose() {
    const binding = this.#binding;
    this.#binding = undefined;
    binding?.lifetime.abort();
  }

  localized(zh, en) {
    return this.language() === 'en' ? en : zh;
  }

  async #list(binding) {
    const signal = AbortSignal.any([binding.lifetime.signal, AbortSignal.timeout(this.listTimeout)]);
    const result = await within(signal, (signal) => binding.controller.list({}, signal));
    if (!Array.isArray(result?.items)) throw new Error('Invalid DSH session list');
    return result.items.filter(eligible);
  }

  async targets() {
    const binding = this.#binding;
    if (typeof binding?.controller?.list !== 'function' || typeof binding.controller.prompt !== 'function') {
      throw new Error('DSH session controller unavailable');
    }
    const items = await this.#list(binding);
    const recentId = this.recentId();
    const targets = items.map(({ sessionId, projections }) => {
      const title = projections?.values?.title;
      return {
        id: sessionId,
        title: typeof title === 'string' && title.trim()
          ? title.trim()
          : this.localized('未命名会话', 'Untitled session') + ' · ' + sessionId.slice(-8),
      };
    });
    const preferredId = targets.find(({ id }) => id === recentId)?.id ?? targets[0]?.id;
    return { preferredId, targets };
  }

  async submit({ text, targetId, requestId }) {
    const reply = (status, zh, en) => ({ status, message: this.localized(zh, en) });
    if (typeof text !== 'string' || !text.trim()) {
      return reply('failed', '请输入要发送的内容', 'Enter a message to send.');
    }
    if (text.length > MAX_DRAFT_LENGTH) {
      return reply('failed', '草稿过长，请缩短后重试', 'Shorten the draft and try again.');
    }
    if (typeof targetId !== 'string' || !targetId || typeof requestId !== 'string' || !REQUEST_ID.test(requestId)) {
      return reply('failed', '发送目标或请求编号无效，请重新选择会话。', 'The target or request ID is invalid. Select a session again.');
    }

    const binding = this.#binding;
    if (typeof binding?.controller?.list !== 'function' || typeof binding.controller.prompt !== 'function') {
      return reply('failed', 'DSH 会话接口暂不可用', 'The DSH session controller is unavailable.');
    }
    if (targetId === '@new') {
      if (typeof binding.controller.create !== 'function') {
        return reply('failed', '当前 DSH 无法新建会话', 'This DSH version cannot create a session.');
      }
      const id = canonicalRequestId(requestId);
      let pending = binding.newSessions.get(id);
      if (!pending) {
        pending = Promise.resolve().then(() => binding.controller.create({}));
        binding.newSessions.set(id, pending);
        pending.catch(() => binding.newSessions.delete(id));
      }
      try {
        const signal = AbortSignal.any([binding.lifetime.signal, AbortSignal.timeout(this.listTimeout)]);
        const created = await within(signal, () => pending);
        if (typeof created?.sessionId !== 'string' || !created.sessionId) throw new Error('Missing session ID');
        targetId = created.sessionId;
      } catch {
        return reply('failed', '新建会话未完成，可重试同一草稿', 'The new session could not be confirmed. Retry the same draft.');
      }
    } else {
      let items;
      try {
        items = await this.#list(binding);
      } catch {
        return reply('failed', '无法确认发送目标，请重试', 'Could not verify the target session. Try again.');
      }
      if (!items.some((item) => item.sessionId === targetId)) {
        return reply('failed', '所选会话已不可用，请重新选择', 'The selected session is unavailable. Choose another session.');
      }
    }
    if (binding.lifetime.signal.aborted) {
      return reply('failed', '插件正在重载，请重试', 'The plugin is reloading. Try again.');
    }

    const signal = AbortSignal.any([binding.lifetime.signal, AbortSignal.timeout(this.submitTimeout)]);
    try {
      const result = await within(signal, (signal) => binding.controller.prompt({
        requestId: canonicalRequestId(requestId),
        sessionId: targetId,
        mode: 'queue',
        content: [{ type: 'text', text }],
      }, signal));
      if (result?.accepted === true) {
        const sent = reply('sent', '已发送', 'Sent.');
        if (!binding.lifetime.signal.aborted) {
          this.onSent(sent.message);
          this.log('浮层消息已提交');
        }
        return sent;
      }
    } catch (error) {
      if (!signal.aborted && error?.name !== 'TimeoutError' && error?.name !== 'AbortError') {
        this.log('DSH 拒绝了浮层消息');
        return reply('failed', 'DSH 未接受消息，请检查目标会话后重试', 'DSH rejected the message. Check the target session and try again.');
      }
    }
    this.log('浮层消息发送结果待确认');
    return reply('unknown', '发送结果待确认。可查看目标会话或重试同一草稿。', 'Delivery is unconfirmed. Check the target session or retry the same draft.');
  }
}
