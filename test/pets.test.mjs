import test from 'node:test';
import assert from 'node:assert/strict';

import { gridFor, parseInstallScript, petdexSlugFromSource, sanitizeId } from '../src/pets.js';

// petdex 安装脚本的真实片段（boba v2）
const BOBA_SCRIPT = `#!/bin/sh
set -e
PET_DIR="$HOME/.codex/pets/boba"
mkdir -p "$PET_DIR"
curl -fsSL -e "$PETDEX_REFERER" -o "$PET_DIR/pet.json" 'https://assets.petdex.dev/curated/boba/petjson-v2.json'
curl -fsSL -e "$PETDEX_REFERER" -o "$PET_DIR/spritesheet.webp" 'https://assets.petdex.dev/curated/boba/sprite-v2.webp'
echo "Boba is ready in $PET_DIR"
`;

test('petdex 安装脚本能解析出两个资源地址', () => {
  const files = parseInstallScript(BOBA_SCRIPT);
  assert.equal(files.length, 2);
  assert.deepEqual(files[0], {
    name: 'pet.json',
    url: 'https://assets.petdex.dev/curated/boba/petjson-v2.json',
  });
  assert.deepEqual(files[1], {
    name: 'spritesheet.webp',
    url: 'https://assets.petdex.dev/curated/boba/sprite-v2.webp',
  });
});

test('安装脚本里没有 curl 时返回空数组', () => {
  assert.deepEqual(parseInstallScript('echo nothing here'), []);
  assert.deepEqual(parseInstallScript(''), []);
});

test('petdex 来源识别：名字 / 页面链接 / 安装链接', () => {
  assert.equal(petdexSlugFromSource('boba'), 'boba');
  assert.equal(petdexSlugFromSource('  Boba  '), 'boba');
  assert.equal(petdexSlugFromSource('https://petdex.dev/pets/boba'), 'boba');
  assert.equal(petdexSlugFromSource('https://petdex.dev/pets/crafter/boba'), 'boba');
  assert.equal(petdexSlugFromSource('https://petdex.dev/install/boba'), 'boba');
  assert.equal(petdexSlugFromSource('https://example.com/pets/boba'), '');
  assert.equal(petdexSlugFromSource('C:/Users/KING/pets/boba'), '');
  assert.equal(petdexSlugFromSource(''), '');
});

test('sanitizeId 只保留安全字符', () => {
  assert.equal(sanitizeId('My Pet 01!'), 'my-pet-01');
  assert.equal(sanitizeId('../../etc/passwd'), 'etc-passwd');
  assert.equal(sanitizeId(''), '');
});

test('网格推导：v1 九行 / v2 十一行都识别，形状不对拒绝', () => {
  assert.deepEqual(gridFor(1536, 1872), { cols: 8, rows: 9, frameW: 192, frameH: 208 });
  assert.deepEqual(gridFor(1536, 2288), { cols: 8, rows: 11, frameW: 192, frameH: 208 });
  // 宽不是 8 的整数倍
  assert.equal(gridFor(1001, 2288), null);
  // 行数不足 9
  assert.equal(gridFor(1536, 1040), null);
  // 高度不是整行
  assert.equal(gridFor(1536, 2290), null);
  // 帧宽过小
  assert.equal(gridFor(480, 2288), null);
});