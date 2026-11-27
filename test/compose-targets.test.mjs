import test from 'node:test';
import assert from 'node:assert/strict';
import { Readable } from 'node:stream';
import { setTimeout as delay } from 'node:timers/promises';
import { OverlayCompose } from '../src/compose-targets.js';
import { PetHttpApi } from '../src/routes.js';

const requestId = '117823a4-1134-4c83-981c-53b9468884bc';
const item = (sessionId, fields) => ({ sessionId, agentAvailable: true, ...fields });

function fixture(options) {
  const log = [];
  const toasts = [];
  const state = { recentId: 'second' };
  const compose = new OverlayCompose({
    recentId: () => state.recentId,
    language: () => 'zh',
    onSent: (text) => toasts.push(text),
    log: (text) => log.push(text),
    ...options,
  });
  return { compose, state, log, toasts };
}

test('只列可用根会话，优先展示最近活动会话', async () => {
  const { compose, state } = fixture();
  compose.bind({
    list: async () => ({ items: [
      item('first', { projections: { values: { title: '  正文 = 标题  ' } } }),
      item('child', { parentSessionId: 'first' }),
      item('subagent', { origin: 'subagent' }),
      item('cold', { agentAvailable: false }),
      item('migration', { formatStatus: 'migration-required' }),
      item('second'),
    ] }),
    prompt: async () => ({ accepted: true }),
  });
  const { preferredId, targets } = await compose.targets();
  assert.equal(preferredId, 'second');
  assert.deepEqual(targets, [
    { id: 'first', title: '正文 = 标题' },
    { id: 'second', title: '未命名会话 · second' },
  ]);
  state.recentId = 'child';
  assert.equal((await compose.targets()).preferredId, 'first');
});

test('并行任务切换最近会话时仍发送到用户明确选中的会话', async () => {
  const { compose, state, log, toasts } = fixture();
  const sent = [];
  const text = '  草稿\n请保留换行  ';
  compose.bind({
    list: async () => ({ items: [item('first'), item('second')] }),
    prompt: async (body) => {
      sent.push(body);
      return { accepted: true };
    },
  });
  const { preferredId } = await compose.targets();
  state.recentId = 'first';

  const result = await compose.submit({ text, targetId: preferredId, requestId });
  assert.equal(result.status, 'sent');
  assert.deepEqual(sent, [{
    requestId,
    sessionId: 'second',
    mode: 'queue',
    content: [{ type: 'text', text }],
  }]);
  assert.equal(toasts.length, 1);
  assert.ok(log.every((line) => !line.includes('草稿')));
});

test('发送前拒绝已关闭、子会话、隐式目标和无效草稿', async () => {
  const { compose } = fixture();
  let closed = false;
  let attempts = 0;
  compose.bind({
    list: async () => ({ items: [item('first', { agentAvailable: !closed }), item('child', { parentSessionId: 'first' })] }),
    prompt: async () => { attempts++; return { accepted: true }; },
  });
  const { preferredId } = await compose.targets();
  closed = true;
  const valid = { text: '发一条', targetId: preferredId, requestId };
  for (const draft of [
    valid,
    { ...valid, targetId: 'child' },
    { ...valid, targetId: undefined },
    { ...valid, text: ' '.repeat(3) },
    { ...valid, text: '字'.repeat(6001) },
    { ...valid, requestId: 'bad' },
  ]) {
    assert.equal((await compose.submit(draft)).status, 'failed');
  }
  assert.equal(attempts, 0);
  assert.deepEqual(await compose.targets(), { preferredId: undefined, targets: [] });
});

test('超时报告结果待确认，重试保留同一 DSH 请求 ID', async () => {
  const { compose, toasts } = fixture({ submitTimeout: 10 });
  const hostReceipts = new Set();
  const attempts = [];
  const pending = delay(70);
  const draft = { text: '刚才那条', targetId: 'first', requestId };
  compose.bind({
    list: async () => ({ items: [item('first')] }),
    prompt: async ({ requestId, sessionId }) => {
      attempts.push(requestId);
      const key = sessionId + ':' + requestId;
      if (hostReceipts.has(key)) return { accepted: true };
      hostReceipts.add(key);
      await pending;
      return { accepted: true };
    },
  });

  assert.equal((await compose.submit(draft)).status, 'unknown');
  assert.equal((await compose.submit(draft)).status, 'sent');
  await pending;
  assert.deepEqual(attempts, [requestId, requestId]);
  assert.equal(hostReceipts.size, 1);
  assert.equal(toasts.length, 1);
});

test('停用时结束等待并忽略旧请求的最终回执', async () => {
  const { compose, toasts } = fixture();
  const pending = Promise.withResolvers();
  const started = Promise.withResolvers();
  const controller = {
    list: async () => ({ items: [item('first')] }),
    prompt: async () => {
      started.resolve();
      return pending.promise;
    },
  };
  const unbind = compose.bind(controller);
  const send = compose.submit({ text: '停止前提交', targetId: 'first', requestId });
  await started.promise;
  compose.dispose();
  assert.equal((await send).status, 'unknown');
  pending.resolve({ accepted: true });
  await pending.promise;
  assert.equal(toasts.length, 0);
  await assert.rejects(() => compose.targets(), /unavailable/);
  compose.bind(controller);
  unbind();
  assert.equal((await compose.targets()).preferredId, 'first');
});

test('浮层接口保持鉴权和行协议，转发所选目标与请求 ID', async () => {
  const submitted = [];
  const service = { compose: {
    targets: async () => ({ preferredId: '会话=1', targets: [{ id: '会话=1', title: '中文 + 日文\n第二行' }] }),
    submit: async (body) => {
      submitted.push(body);
      return { status: 'unknown', message: '结果待确认' };
    },
  } };
  const api = new PetHttpApi({ token: 'local', service });
  const get = async (method, endpoint, fields, token = 'local') => {
    const req = Readable.from(fields ? [Buffer.from(new URLSearchParams(fields).toString())] : []);
    req.method = method;
    req.url = '/x-dsh-pet/' + endpoint;
    req.headers = { 'x-pet-token': token };
    const response = {};
    await api.handler(req, {
      writeHead: (status) => { response.status = status; },
      end: (body) => { response.body = body; },
    });
    return response;
  };
  const lines = (body) => Object.fromEntries(new URLSearchParams(body.replaceAll('\n', '&')));
  assert.equal((await get('GET', 'compose-targets', null, 'wrong')).status, 403);
  const list = await get('GET', 'compose-targets');
  assert.equal(list.status, 200);
  assert.deepEqual(lines(list.body), {
    preferredId: '会话=1',
    'target.0.id': '会话=1',
    'target.0.title': '中文 + 日文\n第二行',
  });
  const draft = { targetId: '会话=1', requestId, text: '请保留+号\n谢谢' };
  const sent = await get('POST', 'say', draft);
  assert.equal(sent.status, 200);
  assert.deepEqual(lines(sent.body), { status: 'unknown', message: '结果待确认' });
  assert.deepEqual(submitted, [draft]);
  service.compose.targets = async () => { throw new Error('hidden sdk error'); };
  const failed = await get('GET', 'compose-targets');
  assert.equal(failed.status, 503);
  assert.equal(failed.body, 'session list unavailable');
});

test('本地 WinForms 两种 GUID 格式映射到同一个投递编号', async () => {
  const { compose } = fixture();
  const seen = [];
  compose.bind({
    list: async () => ({ items: [item('first')] }),
    prompt: async ({ requestId }) => {
      seen.push(requestId);
      return { accepted: true };
    },
  });
  for (const id of [requestId.replaceAll('-', '').toUpperCase(), requestId]) {
    const result = await compose.submit({ text: '桌面草稿', targetId: 'first', requestId: id });
    assert.equal(result.status, 'sent');
  }
  assert.deepEqual(seen, [requestId, requestId]);
});

test('桌面草稿发送到新会话，重试沿用同一新会话', async () => {
  const { compose } = fixture();
  const received = [];
  let created = 0;
  compose.bind({
    list: async () => ({ items: [item('existing')] }),
    create: async () => ({ sessionId: 'new-' + ++created }),
    prompt: async (request) => {
      received.push(request.sessionId);
      return { accepted: received.length > 1 };
    },
  });
  const draft = { targetId: '@new', requestId, text: 'draft' };
  assert.equal((await compose.submit(draft)).status, 'unknown');
  assert.equal((await compose.submit(draft)).status, 'sent');
  assert.equal((await compose.submit({ ...draft, requestId: '217823a4-1134-4c83-981c-53b9468884bc' })).status, 'sent');
  assert.deepEqual(received, ['new-1', 'new-1', 'new-2']);
});
