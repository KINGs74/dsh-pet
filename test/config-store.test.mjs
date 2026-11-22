// dsh-pet · 配置持久化的安全性
//
// 这一组测试对应 0.3 的一条硬要求：改配置的代码不能把用户的现场弄坏。
//   · 正常读写要能往返；
//   · 配置文件被写坏时，必须留档 + 回滚到备份，而不是静默用默认值覆盖；
//   · 写入前要有 .bak，写入后要有 .prev，任何时候磁盘上都有一份可读配置。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { ConfigStore, DEFAULT_CONFIG, normalizeConfig } from '../src/config-store.js';

async function tempRoot() {
  return mkdtemp(join(tmpdir(), 'dsh-pet-config-'));
}

test('默认配置和越界值都会收敛到合法范围', () => {
  const config = normalizeConfig({ scale: 99, bubbleMs: -5, alertMs: 10 ** 9, lang: 'fr' });
  assert.equal(config.scale, 1.5);
  assert.equal(config.bubbleMs, 0);
  assert.equal(config.alertMs, 600000);
  assert.equal(config.lang, 'zh');
  assert.equal(config.bubble, DEFAULT_CONFIG.bubble);
});

test('patch 之后写盘，重新 load 能读回同样的值', async () => {
  const root = await tempRoot();
  const store = new ConfigStore(root);
  await store.load();
  await store.patch({ scale: 0.85, bubbleMs: 8000, pos: { x: 12, y: 34 } });

  const reopened = new ConfigStore(root);
  await reopened.load();
  assert.equal(reopened.get().scale, 0.85);
  assert.equal(reopened.get().bubbleMs, 8000);
  assert.deepEqual(reopened.get().pos, { x: 12, y: 34 });
});

test('写入时留下 .bak 与 .prev 两份可读备份', async () => {
  const root = await tempRoot();
  const file = join(root, 'config.json');
  // 模拟"磁盘上已经有一份用户配置"（比如从 0.2 升级上来的现场）
  await writeFile(`${file}`, `${JSON.stringify({ ...DEFAULT_CONFIG, scale: 0.55 }, null, 2)}\n`, 'utf8');

  const store = new ConfigStore(root);
  await store.load();
  await store.patch({ scale: 0.6 });
  await store.patch({ scale: 0.7 });

  assert.ok(existsSync(`${file}.bak`), '应有 config.json.bak');
  assert.ok(existsSync(`${file}.prev`), '应有 config.json.prev');
  const bak = JSON.parse(await readFile(`${file}.bak`, 'utf8'));
  const prev = JSON.parse(await readFile(`${file}.prev`, 'utf8'));
  const current = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(bak.scale, 0.55, '.bak 是最早那份（默认值）');
  assert.equal(prev.scale, 0.6, '.prev 是上一次的值');
  assert.equal(current.scale, 0.7);
});

test('配置文件被写坏时留档原文件并从备份回滚', async () => {
  const root = await tempRoot();
  const file = join(root, 'config.json');
  const good = { ...DEFAULT_CONFIG, scale: 0.8, pet: 'boba' };
  await writeFile(`${file}.bak`, `${JSON.stringify(good, null, 2)}\n`, 'utf8');
  await writeFile(file, '{"scale": 0.8, oops', 'utf8');

  const messages = [];
  const store = new ConfigStore(root);
  await store.load((message) => messages.push(message));

  assert.equal(store.get().scale, 0.8, '应回滚到 .bak 里的值');
  assert.equal(store.get().pet, 'boba');
  assert.ok(
    messages.some((message) => message.includes('留档')),
    `应报告留档：${messages.join(' | ')}`,
  );
  const corruptLeft = messages.join(' ').includes('.corrupt-');
  assert.ok(corruptLeft, '留档提示里应带上 .corrupt- 文件名');
});

test('没有备份时坏文件仍然不会让 load 抛错，并留档原件', async () => {
  const root = await tempRoot();
  const file = join(root, 'config.json');
  await writeFile(file, 'not json at all', 'utf8');

  const messages = [];
  const store = new ConfigStore(root);
  const config = await store.load((message) => messages.push(message));

  assert.equal(config.scale, DEFAULT_CONFIG.scale);
  assert.ok(messages.some((message) => message.includes('默认配置')));
  const { readdir } = await import('node:fs/promises');
  const files = await readdir(root);
  assert.ok(
    files.some((name) => name.includes('.corrupt-')),
    `应该留下坏文件副本：${files.join(', ')}`,
  );
});

test('坏文件回滚后再次写入，磁盘上恢复成合法 JSON', async () => {
  const root = await tempRoot();
  const file = join(root, 'config.json');
  await writeFile(file, '{ broken', 'utf8');
  const store = new ConfigStore(root);
  await store.load();
  await store.patch({ pet: 'deepseek' });
  const current = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(current.pet, 'deepseek');
});
test('回滚之后立刻把文件修好，不必等到下一次写入', async () => {
  const root = await tempRoot();
  const file = join(root, 'config.json');
  await writeFile(`${file}.bak`, JSON.stringify({ pet: 'boba', scale: 0.7 }), 'utf8');
  await writeFile(file, '{ "pet": "boba", "scale": 0.', 'utf8');

  const messages = [];
  const store = new ConfigStore(root);
  const config = await store.load((message) => messages.push(message));

  assert.equal(config.pet, 'boba');
  assert.equal(config.scale, 0.7);
  const onDisk = JSON.parse(await readFile(file, 'utf8'));
  assert.equal(onDisk.pet, 'boba', '磁盘上的坏文件应被恢复结果覆盖');
  assert.equal(onDisk.scale, 0.7);
  // 备份保持原样，没有被损坏内容污染
  const bak = JSON.parse(await readFile(`${file}.bak`, 'utf8'));
  assert.equal(bak.pet, 'boba');
});
