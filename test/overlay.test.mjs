import test from 'node:test';
import assert from 'node:assert/strict';
import { OverlaySupervisor, buildOverlay } from '../src/overlay.js';

function fixture() {
  const logs = [];
  const supervisor = new OverlaySupervisor({
    exePath: './missing-overlay-test-exe',
    logFile: './unused-overlay-test.log',
    log: (line) => logs.push(line),
  });
  return { supervisor, logs };
}

test('合并待启动请求，停用后不得继续拉起浮层', async () => {
  const { supervisor, logs } = fixture();
  const gate = Promise.withResolvers();
  let checks = 0;
  supervisor.available = () => {
    checks += 1;
    return gate.promise;
  };

  const first = supervisor.start();
  const second = supervisor.start();
  assert.equal(checks, 1);
  await supervisor.stop();
  gate.resolve(true);
  await Promise.all([first, second]);

  assert.equal(supervisor.status().state, 'stopped');
  assert.equal(supervisor.status().pid, 0);
  assert.deepEqual(logs, []);
});

test('稍后的停用会取消正在等待的重启', async () => {
  const { supervisor, logs } = fixture();
  supervisor.available = () => {
    assert.fail('停用后不应重启');
  };
  const restarting = supervisor.restart();
  await supervisor.stop();

  assert.equal((await restarting).state, 'stopped');
  assert.deepEqual(logs, []);
});

test('已停用的插件不能开始编译', async () => {
  const abort = new AbortController();
  abort.abort();
  await assert.rejects(
    () => buildOverlay({ signal: abort.signal }),
    { name: 'AbortError' },
  );
});
