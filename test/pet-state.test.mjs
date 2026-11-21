import test from 'node:test';
import assert from 'node:assert/strict';

import { PetModel, PetStatus, excerpt, textOfMessage } from '../src/pet-state.js';

function makeModel() {
  let clock = 1_000_000;
  const model = new PetModel({ now: () => clock });
  return {
    model,
    advance(ms) {
      clock += ms;
    },
  };
}

const root = { sessionId: 's-root', isRoot: true, title: '重构 provider 路由' };
const child = { sessionId: 's-child', isRoot: false, title: '子代理' };

test('textOfMessage 兼容多种消息形态', () => {
  assert.equal(textOfMessage('纯文本'), '纯文本');
  assert.equal(textOfMessage({ text: '带 text 字段' }), '带 text 字段');
  assert.equal(textOfMessage({ content: '带 content 字符串' }), '带 content 字符串');
  assert.equal(
    textOfMessage({ content: [{ type: 'text', text: '第一段' }, { type: 'text', text: '第二段' }] }),
    '第一段\n第二段',
  );
  assert.equal(textOfMessage(null), '');
});

test('excerpt 去掉代码围栏并压平空白', () => {
  const text = excerpt('前文\n\n```js\nconst a = 1;\n```\n\n后文', 80);
  assert.ok(!text.includes('```'));
  assert.ok(text.includes('前文'));
  assert.ok(text.length <= 81);
});

test('一轮任务：运行中 → 完成 → 产生提醒', () => {
  const { model, advance } = makeModel();
  model.observeSession(root);

  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  assert.equal(model.snapshot().status, PetStatus.running);

  model.applyEvent({ ...root, event: { type: 'step/start', data: { turn: 1, step: 2 } } });
  model.applyEvent({
    ...root,
    event: { type: 'tool/call', data: { turn: 1, step: 2, callId: 'c1', name: 'ops_bash', arguments: {} } },
  });
  assert.match(model.snapshot().line2, /第 1 轮/);
  assert.match(model.snapshot().line2, /第 2 步/);

  advance(4_000);
  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 1, step: 2, message: { text: '已经改完并跑过测试' }, stream: false } } });
  assert.equal(model.snapshot().line1, '已经改完并跑过测试');

  model.applyEvent({ ...root, event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'completed' } } } });
  const done = model.snapshot();
  assert.equal(done.status, PetStatus.done);
  assert.ok(done.alert, 'turn/end 必须产生提醒');
  assert.equal(done.alert.kind, 'completed');
  assert.equal(done.alert.text, '任务已完成，可以回来看了');
  assert.ok(done.elapsed >= 4, `elapsed 应至少 4s，实际 ${done.elapsed}`);
});

test('工具调用不污染最新消息，只体现在活动行与页脚', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'tool/call', data: { turn: 1, step: 1, callId: 'c1', name: 'ops_bash' } } });
  const snapshot = model.snapshot();
  assert.equal(snapshot.line1, '正在执行 ops_bash');
  // 正文已经写了工具名，页脚就别重复；有正文时才在页脚补工具名
  assert.ok(!/ops_bash/.test(snapshot.line2), `页脚不应重复工具名：${snapshot.line2}`);

  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 1, step: 1, message: { text: '改完了，准备跑测试' }, stream: false } } });
  const withText = model.snapshot();
  assert.equal(withText.line1, '改完了，准备跑测试');
  assert.match(withText.line2, /ops_bash/);
});

test('流式片段不覆盖最新一条消息', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 1, step: 1, message: { text: '完整回答' }, stream: false } } });
  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 1, step: 1, message: { text: '半句' }, stream: true } } });
  assert.equal(model.snapshot().line1, '完整回答');
});

test('确认提醒后清空；错误 id 不会误清', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'completed' } } } });
  const alertId = model.snapshot().alert.id;

  model.ack('别的 id');
  assert.ok(model.snapshot().alert, '错误 id 不应清除提醒');
  model.ack(alertId);
  assert.equal(model.snapshot().alert, null);
  assert.equal(model.ackedId, alertId);
});

test('子代理会话不抢占根会话的展示状态，也不单独提醒', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });

  model.applyEvent({ ...child, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...child, event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'completed' } } } });

  const snapshot = model.snapshot();
  assert.equal(snapshot.status, PetStatus.running, '根会话仍在跑，不该被子代理的结束改状态');
  assert.equal(snapshot.alert, null);
  assert.equal(snapshot.sessionId, 's-root');
});

test('失败原因映射到 failed 与对应文案', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({
    ...root,
    event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'error', error: { message: 'provider 502' } } } },
  });
  const snapshot = model.snapshot();
  assert.equal(snapshot.status, PetStatus.failed);
  assert.equal(snapshot.alert.kind, 'error');
  assert.match(snapshot.alert.text, /失败/);
  assert.match(snapshot.line1, /provider 502/);
});

test('waiting 状态会改写第一行文案', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.setWaiting();
  const snapshot = model.snapshot();
  assert.equal(snapshot.status, PetStatus.waiting);
  assert.match(snapshot.line1, /等/);
  model.resumeFromWaiting();
  assert.equal(model.snapshot().status, PetStatus.running);
});

test('英文模式下提醒文案切换', () => {
  const { model } = makeModel();
  model.setLang('en');
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'completed' } } } });
  assert.equal(model.snapshot().alert.text, 'Task finished — come take a look');
});
test('思考过程不会进入气泡，只取正文', () => {
  const message = {
    parts: [
      { type: 'reasoning', text: '这里是我的推理，不该出现在气泡里' },
      { type: 'text', text: '已经改完并跑过测试' },
      { type: 'tool-call', name: 'ops_bash' },
    ],
  };
  assert.equal(textOfMessage(message), '已经改完并跑过测试');
});

test('新一轮开始清掉上一轮的结论', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 1, step: 1, message: { text: '上一轮的结论' }, stream: false } } });
  assert.equal(model.snapshot().line1, '上一轮的结论');

  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 2 } } });
  assert.notEqual(model.snapshot().line1, '上一轮的结论', '新的一轮不应继续显示上一轮的答案');
});

test('后台会话的审批不会抢走当前气泡的等待态', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });

  model.setWaiting('child-session');
  assert.equal(model.snapshot().status, PetStatus.running, '子会话审批不应影响当前会话');
  assert.equal(model.snapshot().waiting, 0);

  model.setWaiting(root.sessionId);
  assert.equal(model.snapshot().status, PetStatus.waiting);
  assert.equal(model.snapshot().waiting, 1);
  assert.match(model.snapshot().line1, /等你确认/);

  model.setWaiting(root.sessionId);
  assert.equal(model.snapshot().waiting, 2, '两项待确认应累计');
  model.resumeFromWaiting(root.sessionId);
  assert.equal(model.snapshot().status, PetStatus.waiting, '还剩一项时仍是等待态');
  model.resumeFromWaiting(root.sessionId);
  assert.equal(model.snapshot().status, PetStatus.running);
});

test('失败演练与成功演练的文案和状态不同', () => {
  const { model } = makeModel();
  model.previewAlert('completed');
  const ok = model.snapshot();
  assert.equal(ok.status, PetStatus.done);
  assert.equal(ok.alert.kind, 'completed');

  model.ack();
  model.previewAlert('failed');
  const bad = model.snapshot();
  assert.equal(bad.status, PetStatus.failed, '失败不能显示成完成');
  assert.equal(bad.alert.kind, 'failed');
  assert.match(bad.line1, /失败|出错|中断/);
});

test('结束提醒的标题和正文不重复', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'user/message', data: { turn: 1, message: { text: '帮我把宠物插件装上' } } } });
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 1 } } });
  model.applyEvent({ ...root, event: { type: 'turn/end', data: { turn: 1, reason: { kind: 'completed' } } } });
  const snap = model.snapshot();
  assert.equal(snap.alert.text, '任务已完成，可以回来看了');
  assert.notEqual(snap.line1, snap.alert.text, '正文不该把标题重复一遍');
  assert.match(snap.line1, /帮我把宠物插件装上/);
});

test('演练文案不会污染正在进行的对话气泡', () => {
  const { model } = makeModel();
  model.observeSession(root);
  model.applyEvent({ ...root, event: { type: 'turn/start', data: { turn: 3 } } });
  model.previewAlert('failed');
  assert.match(model.snapshot().line1, /退出码 1（演练）/, '演练时正文要说明这是演练');
  model.ack();
  // 真实对话继续：新的 step 事件之后，气泡不该还停在演练的"退出码 1"上
  model.applyEvent({ ...root, event: { type: 'step/start', data: { turn: 3, step: 4 } } });
  model.applyEvent({ ...root, event: { type: 'assistant/message', data: { turn: 3, step: 4, message: { text: '已把 provider 路由改好' } } } });
  const snap = model.snapshot();
  assert.ok(!snap.line1.includes('退出码 1'), `气泡被演练文案污染了：${snap.line1}`);
  assert.equal(snap.line1, '已把 provider 路由改好');
});
