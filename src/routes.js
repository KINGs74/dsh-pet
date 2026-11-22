// dsh-pet · 本地 HTTP 接口
//
// 浮层进程与宿主之间只有这一条通道：DSH 的 webServer 已经在 127.0.0.1 上监听，
// 我们挂一条 /x-dsh-pet/* 前缀路由。DSH 不在此路径上做鉴权，所以自己校验
// X-Pet-Token（每次启动随机生成，只写进本机 runtime 文件并作为参数传给浮层）。
//
// 状态用行协议下发（key=URL 编码值，一行一个），浮层用 split('\n') 解析，
// 比 JSON 更省事也更好排查；其余接口收 form-encoded 或 JSON。
const PREFIX = '/x-dsh-pet';
const MAX_BODY = 64 * 1024;

const TEXT = 'text/plain; charset=utf-8';
const JSON_TYPE = 'application/json; charset=utf-8';

function send(res, status, body, contentType = TEXT) {
  res.writeHead(status, {
    'content-type': contentType,
    'cache-control': 'no-store',
    'content-length': Buffer.byteLength(body),
  });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let size = 0;
    const chunks = [];
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        reject(new Error('请求体过大'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

function parseBody(text, contentType = '') {
  const raw = String(text ?? '').trim();
  if (!raw) return {};
  if (contentType.includes('application/json') || raw.startsWith('{')) {
    try {
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
      return {};
    }
  }
  const out = {};
  for (const [key, value] of new URLSearchParams(raw)) out[key] = value;
  return out;
}

/** 行协议序列化：空值省略，值统一 URL 编码 */
export function serializeState(snapshot) {
  const lines = [];
  const put = (key, value) => {
    if (value === undefined || value === null || value === '') return;
    lines.push(`${key}=${encodeURIComponent(String(value))}`);
  };

  put('status', snapshot.status);
  put('title', snapshot.title);
  put('line1', snapshot.line1);
  put('line2', snapshot.line2);
  put('elapsed', snapshot.elapsed);
  put('waiting', snapshot.waiting);
  if (snapshot.alert) {
    put('alert.id', snapshot.alert.id);
    put('alert.kind', snapshot.alert.kind);
    put('alert.text', snapshot.alert.text);
    put('alert.age', snapshot.alertAge);
  }
  put('pet.id', snapshot.pet?.id);
  put('pet.name', snapshot.pet?.name);
  put('pet.path', snapshot.pet?.sheet);
  put('frame.w', snapshot.pet?.grid?.frameW);
  put('frame.h', snapshot.pet?.grid?.frameH);
  put('frame.cols', snapshot.pet?.grid?.cols);
  put('frame.rows', snapshot.pet?.grid?.rows);
  put('scale', snapshot.scale);
  put('bubble', snapshot.bubble ? '1' : '0');
  put('sound', snapshot.sound ? '1' : '0');
  put('bubbleMs', snapshot.bubbleMs);
  put('alertMs', snapshot.alertMs);
  put('lang', snapshot.lang);
  put('toast', snapshot.toastText);
  put('overlay', snapshot.overlay);
  // 浮层据此判断「DSH 还在不在」：主进程活着就继续显示，死了就自己退出
  put('dsh.pid', snapshot.dshPid);
  put('dsh.exe', snapshot.dshExe);
  if (snapshot.pos) {
    put('pos.x', snapshot.pos.x);
    put('pos.y', snapshot.pos.y);
  }
  (snapshot.pets ?? []).forEach((pet, index) => {
    put(`petlist.${index}.id`, pet.id);
    put(`petlist.${index}.name`, pet.name);
  });
  return lines.join('\n');
}

function line(key, value) {
  return `${key}=${encodeURIComponent(value.toWellFormed())}`;
}

function serializeTargets({ preferredId, targets }) {
  const lines = [];
  if (preferredId) lines.push(line('preferredId', preferredId));
  targets.forEach(({ id, title }, index) => {
    lines.push(line(`target.${index}.id`, id), line(`target.${index}.title`, title));
  });
  return lines.join('\n');
}

export class PetHttpApi {
  /**
   * @param {object} options
   * @param {string} options.token  X-Pet-Token 校验值
   * @param {object} options.service 由 index.js 提供的门面：state()/ack()/configure()/install()/overlayEvent()
   * @param {(message: string) => void} [options.log]
   */
  constructor({ token, service, prefix = PREFIX, log = () => {} }) {
    this.token = token;
    this.service = service;
    this.log = log;
    // 实际挂载的前缀（重载撞车时可能是 /x-dsh-pet-2）
    this.prefix = prefix;
    this.handler = this.handler.bind(this);
  }

  async handler(req, res) {
    const url = new URL(req.url ?? '/', 'http://127.0.0.1');
    const path = url.pathname.replace(/\/+$/, '') || '/';

    const prefix = this.prefix ?? PREFIX;
    if (path !== prefix && !path.startsWith(`${prefix}/`)) {
      send(res, 404, 'not found');
      return;
    }

    const token = req.headers['x-pet-token'];
    if (token !== this.token) {
      send(res, 403, 'bad token');
      this.log(`拒绝未授权请求 ${req.method} ${path}`);
      return;
    }

    const endpoint = path.slice(prefix.length + 1) || '';
    try {
      if (req.method === 'GET' && endpoint === 'state') {
        send(res, 200, serializeState(this.service.state()));
        return;
      }
      if (req.method === 'GET' && endpoint === 'pets') {
        send(res, 200, JSON.stringify(this.service.state().pets ?? []), JSON_TYPE);
        return;
      }
      if (req.method === 'POST' && endpoint === 'ack') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        this.service.ack(String(body.alertId ?? ''));
        send(res, 200, 'ok');
        return;
      }
      if (req.method === 'POST' && endpoint === 'config') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        const result = await this.service.configure(body);
        send(res, 200, result);
        return;
      }
      if (req.method === 'POST' && endpoint === 'install') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        const result = await this.service.install(String(body.source ?? ''));
        send(res, 200, result);
        return;
      }
      if (req.method === 'POST' && endpoint === 'overlay') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        this.service.overlayEvent(body);
        send(res, 200, 'ok');
        return;
      }
      if (req.method === 'GET' && endpoint === 'compose-targets') {
        try {
          const targets = await this.service.compose.targets();
          send(res, 200, serializeTargets(targets));
        } catch {
          this.log('浮层会话列表暂不可用');
          send(res, 503, 'session list unavailable');
        }
        return;
      }
      if (req.method === 'POST' && endpoint === 'say') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        const { status, message } = await this.service.compose.submit(body);
        send(res, 200, [line('status', status), line('message', message)].join('\n'));
        return;
      }
      if (req.method === 'POST' && endpoint === 'queue') {
        send(res, 200, String(await this.service.clearQueue()));
        return;
      }

      if (req.method === 'POST' && endpoint === 'profile') {
        const body = await readBody(req);
        send(res, 200, String(await this.service.profile(body)));
        return;
      }

      if (req.method === 'POST' && endpoint === 'test') {
        const body = parseBody(await readBody(req), req.headers['content-type'] ?? '');
        this.service.test(body.kind);
        send(res, 200, '已触发结束提醒演练');
        return;
      }
      if (req.method === 'GET' && endpoint === '') {
        send(res, 200, `dsh-pet host alive\n${serializeState(this.service.state())}\n`);
        return;
      }
      send(res, 404, 'unknown endpoint');
    } catch (error) {
      this.log(`接口 ${endpoint} 失败：${error?.message ?? error}`);
      send(res, 500, `error: ${error?.message ?? error}`);
    }
  }
}

export { PREFIX };