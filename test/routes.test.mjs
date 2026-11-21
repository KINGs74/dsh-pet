import test from 'node:test';
import assert from 'node:assert/strict';

import { serializeState } from '../src/routes.js';

function toMap(text) {
  const map = new Map();
  for (const line of text.split('\n')) {
    if (!line) continue;
    const eq = line.indexOf('=');
    map.set(line.slice(0, eq), decodeURIComponent(line.slice(eq + 1)));
  }
  return map;
}

test('状态行协议包含浮层需要的全部字段', () => {
  const text = serializeState({
    status: 'running',
    title: '重构 provider 路由',
    line1: '正在读取 src/routes.js',
    line2: '第 2 轮 · 第 4 步 · 12s · ops_bash',
    elapsed: 12,
    alert: { id: 'a1', kind: 'completed', text: '任务已完成，可以回来看了' },
    pet: {
      id: 'boba',
      name: 'Boba',
      sheet: 'C:\\Users\\KING\\.dsh\\pets\\boba\\spritesheet.png',
      grid: { cols: 8, rows: 11, frameW: 192, frameH: 208 },
    },
    pets: [
      { id: 'boba', name: 'Boba' },
      { id: 'deepseek', name: 'DeepSeek' },
    ],
    scale: 0.55,
    bubble: true,
    sound: false,
    toast: true,
    lang: 'zh',
    toastText: '已切换到 Boba',
    overlay: 'running',
    pos: { x: 1200, y: 640 },
  });

  const map = toMap(text);
  assert.equal(map.get('status'), 'running');
  assert.equal(map.get('title'), '重构 provider 路由');
  assert.equal(map.get('alert.id'), 'a1');
  assert.equal(map.get('alert.kind'), 'completed');
  assert.equal(map.get('pet.id'), 'boba');
  assert.equal(map.get('frame.w'), '192');
  assert.equal(map.get('frame.rows'), '11');
  assert.equal(map.get('scale'), '0.55');
  assert.equal(map.get('bubble'), '1');
  assert.equal(map.get('sound'), '0');
  assert.equal(map.get('notify'), '1');
  assert.equal(map.get('toast'), '已切换到 Boba');
  assert.equal(map.get('pos.x'), '1200');
  assert.equal(map.get('petlist.0.id'), 'boba');
  assert.equal(map.get('petlist.1.name'), 'DeepSeek');
  // 空值不占行，浮层才不会把旧值读回来
  assert.ok(!text.includes('alert.text=undefined'));
});

test('没有提醒、没有宠物时不下发对应键', () => {
  const map = toMap(
    serializeState({
      status: 'idle',
      title: 'DSH 智能伙伴',
      line1: '暂时没有进行中的任务',
      line2: '',
      elapsed: 0,
      alert: null,
      pet: null,
      pets: [],
      scale: 0.55,
      bubble: true,
      sound: true,
      toast: true,
      lang: 'zh',
      toastText: '',
      overlay: 'unavailable',
      pos: null,
    }),
  );
  assert.equal(map.has('alert.id'), false);
  assert.equal(map.has('pet.id'), false);
  assert.equal(map.has('pos.x'), false);
  assert.equal(map.has('petlist.0.id'), false);
  assert.equal(map.get('overlay'), 'unavailable');
});

test('含换行与中文的值经过 URL 编码后可原样还原', () => {
  const raw = '第一行\n第二行 · 含空格';
  const map = toMap(serializeState({ status: 'running', line1: raw, pets: [] }));
  assert.equal(map.get('line1'), raw);
});
test('挂载前缀变化后，路由仍能正确解析端点（重载撞车退到 -2 的情况）', async () => {
  const { PetHttpApi } = await import('../src/routes.js');
  const calls = [];
  const api = new PetHttpApi({
    token: 't0',
    prefix: '/x-dsh-pet-2',
    service: {
      state: () => ({ status: 'running', line1: 'x' }),
      ack: (id) => calls.push(['ack', id]),
      configure: () => {},
      install: async () => '',
      overlayEvent: () => {},
      test: (kind) => calls.push(['test', kind]),
    },
  });

  const makeRes = () => {
    const out = { status: 0, body: '' };
    return {
      out,
      writeHead(code) { out.status = code; },
      end(text) { out.body = String(text ?? ''); },
    };
  };

  const res = makeRes();
  const fakeReq = (method, url) => {
    const handlers = new Map();
    return {
      method,
      url,
      headers: { 'x-pet-token': 't0' },
      on(name, fn) { handlers.set(name, fn); return this; },
      destroy() {},
      // 空 body：读完立刻 end
      emit() {
        queueMicrotask(() => handlers.get('end')?.());
      },
    };
  };
  const req = fakeReq('POST', '/x-dsh-pet-2/test');
  req.emit();
  await api.handler(req, res);
  assert.equal(res.out.status, 200);
  assert.deepEqual(calls[0], ['test', undefined]);

  const wrong = makeRes();
  await api.handler({ method: 'GET', url: '/x-dsh-pet/state', headers: { 'x-pet-token': 't0' } }, wrong);
  assert.equal(wrong.out.status, 404, '旧前缀不应再被这个实例处理');
});
