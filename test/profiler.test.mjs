import assert from 'node:assert/strict';
import { mkdtemp, readdir, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';

import { startProfiler, topFrames } from '../src/profiler.js';

test('topFrames 按自采样数排序并折算成毫秒', () => {
  const profile = {
    startTime: 0,
    endTime: 10_000,
    samples: [1, 1, 1, 2],
    nodes: [
      { id: 1, callFrame: { functionName: 'busyLoop', url: 'x.js', lineNumber: 41 } },
      { id: 2, callFrame: { functionName: 'other', url: 'y.js', lineNumber: 6 } },
    ],
  };
  const rows = topFrames(profile, 2);
  assert.equal(rows.length, 2);
  assert.equal(rows[0].function, 'busyLoop');
  assert.equal(rows[0].self, 3);
  assert.equal(rows[0].line, 42);
  // 4 个采样覆盖 10ms ⇒ 每个 2.5ms，3 个约 7.5ms
  assert.equal(rows[0].ms, 8);
  assert.equal(rows[1].function, 'other');
});

test('topFrames 容忍空 profile', () => {
  assert.deepEqual(topFrames({}, 5), []);
  assert.deepEqual(topFrames(null, 5), []);
});

test('卡住超过阈值时落一份 cpuprofile', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'dsh-pet-prof-'));
  const lines = [];
  const profiler = startProfiler({
    dir,
    log: (line) => lines.push(line),
    thresholdMs: 60,
    resolution: 10,
  });
  try {
    await new Promise((resolve) => setTimeout(resolve, 250));
    const until = Date.now() + 220;
    let spin = 0;
    while (Date.now() < until) spin += Math.sqrt(spin + 1); // 故意同步阻塞
    assert.ok(spin > 0);

    const deadline = Date.now() + 4000;
    let files = [];
    while (Date.now() < deadline) {
      files = (await readdir(dir)).filter((name) => name.endsWith('.cpuprofile'));
      if (files.length > 0) break;
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
    assert.equal(files.length, 1, `expected one profile, got ${files.join(',')} / logs: ${lines.join(' | ')}`);
    const profile = JSON.parse(await readFile(join(dir, files[0]), 'utf8'));
    assert.ok(Array.isArray(profile.nodes) && profile.nodes.length > 0);
    assert.ok(lines.some((line) => line.startsWith('loop block ')));
    // 采样器会如实报告自己的观测值，/pet status 与文档都用它
    const info = profiler.info();
    assert.equal(info.profiles, 1);
    assert.equal(typeof info.maxLagMs, 'number');
  } finally {
    await profiler.stop();
    await rm(dir, { recursive: true, force: true });
  }
});