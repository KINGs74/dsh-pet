// 卡死取证用的采样器（只有设置 DSH_PET_PROFILE=1 才启用，默认完全不加载）。
//
// 为什么不是"发现卡了再开始采样"：事件循环被同步代码挡住时，采样器本身也跑不起来，
// 事后再采只能看到卡完之后的世界，对定位毫无价值。所以这里是**常开采样 + 事后落盘**：
// V8 的采样线程独立于 JS 主线程，卡死期间的栈也会被记进 profile，等主线程恢复后
// 再把这段 profile 停下来写文件，profile 里就包含了"卡住那一刻在跑什么"。
import { Session } from 'node:inspector/promises';
import { mkdir, writeFile } from 'node:fs/promises';
import { monitorEventLoopDelay } from 'node:perf_hooks';
import { join } from 'node:path';

/** 把 cpuprofile 里的自采样数折算成耗时，返回排好序的前 N 条（含调用栈）。 */
export function topFrames(profile, limit = 15) {
  const byId = new Map();
  for (const node of profile?.nodes ?? []) byId.set(node.id, node);
  const self = new Map();
  for (let i = 0; i < (profile?.samples?.length ?? 0); i += 1) {
    const id = profile.samples[i];
    self.set(id, (self.get(id) ?? 0) + 1);
  }
  const interval = (profile?.endTime - profile?.startTime) / Math.max(1, profile?.samples?.length ?? 1) / 1000;
  const rows = [];
  for (const [id, count] of self) {
    const node = byId.get(id);
    if (!node) continue;
    const frame = node.callFrame ?? {};
    rows.push({
      self: count,
      ms: Math.round(count * interval),
      function: frame.functionName || '(anonymous)',
      url: frame.url || '',
      line: (frame.lineNumber ?? -1) + 1,
    });
  }
  rows.sort((a, b) => b.self - a.self);
  return rows.slice(0, limit);
}

function stampName() {
  return new Date().toISOString().replace(/[:.]/g, '-');
}

/**
 * @param {{ dir: string, log: (m: string) => void, thresholdMs?: number,
 *           resolution?: number, maxProfiles?: number, sampleIntervalUs?: number }} options
 * @returns {{ stop: () => Promise<void>, info: () => object }}
 */
export function startProfiler({
  dir,
  log,
  thresholdMs = 1500,
  resolution = 20,
  maxProfiles = 5,
  sampleIntervalUs = 200,
}) {
  const histogram = monitorEventLoopDelay({ resolution });
  histogram.enable();

  const session = new Session();
  let connected = false;
  let started = false;
  let stopped = false;
  let capturing = false;
  let profiles = 0;
  let lastTick = Date.now();
  let timer = null;

  const beginSampling = async () => {
    if (stopped || started) return;
    try {
      // connect 只能来一次：重复调用会抛 "The inspector session is already connected"，
      // 第一次卡死采完之后就再也采不到第二次了（这个坑实际踩过）。
      if (!connected) {
        session.connect();
        connected = true;
      }
      await session.post('Profiler.enable');
      await session.post('Profiler.setSamplingInterval', { interval: sampleIntervalUs });
      await session.post('Profiler.start');
      started = true;
    } catch (error) {
      log(`采样器启动失败：${error?.message ?? error}`);
      stopped = true;
    }
  };

  const capture = async (blockedMs) => {
    capturing = true;
    const name = `lag-${stampName()}.cpuprofile`;
    const file = join(dir, name);
    try {
      const { profile } = await session.post('Profiler.stop');
      await mkdir(dir, { recursive: true });
      await writeFile(file, JSON.stringify(profile));
      profiles += 1;
      const top = topFrames(profile, 3)
        .map((row) => `${row.function}@${row.line}:${row.ms}ms`)
        .join(' | ');
      log(`loop block ${Math.round(blockedMs)}ms profile=${name} top=${top}`);
      started = false;
      if (profiles < maxProfiles) await beginSampling();
      else log('采样器已达上限，停止采样（改 DSH_PET_PROFILE 配置后重启 DSH 才能继续）');
    } catch (error) {
      log(`采样落盘失败：${error?.message ?? error}`);
      started = false;
    } finally {
      capturing = false;
    }
  };

  const tick = () => {
    if (stopped) return;
    const now = Date.now();
    const gap = now - lastTick - resolution;
    lastTick = now;
    if (gap > thresholdMs && !capturing && started && profiles < maxProfiles) {
      // 事件循环刚恢复，立刻把卡住期间的 profile 收下来
      void capture(gap).finally(() => {
        if (profiles < maxProfiles && !stopped) schedule();
      });
      return;
    }
    schedule();
  };

  const schedule = () => {
    if (stopped) return;
    timer = setTimeout(tick, resolution);
    timer.unref?.();
  };

  void beginSampling().then(() => {
    lastTick = Date.now();
    schedule();
  });

  return {
    info() {
      return {
        profiles,
        maxLagMs: Math.round(histogram.max / 1e6),
        p99Ms: Math.round(histogram.percentile(99) / 1e6),
      };
    },
    async stop() {
      stopped = true;
      if (timer) clearTimeout(timer);
      histogram.disable();
      if (!started) return;
      try {
        await session.post('Profiler.stop');
      } catch {}
      try {
        session.disconnect();
      } catch {}
    },
  };
}