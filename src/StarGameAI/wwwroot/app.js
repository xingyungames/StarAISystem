/* ============================================================
   StarGameAI 管理后台 - 应用外壳
   职责：API 封装（含 401/404/501 处理）、认证与登录框、
         hash 路由调度、通用工具函数与基础 UI 组件。
   ============================================================ */

const TOKEN_KEY = 'admin_token';

/* 页面路由表：key 即 hash 路由，file 为按需加载的视图模块 */
const ROUTES = [
  { key: 'dashboard', label: '仪表盘', file: './views/dashboard.js' },
  { key: 'persona', label: '人设', file: './views/persona.js' },
  { key: 'memory', label: '记忆', file: './views/memory.js' },
  { key: 'deep', label: '深层记忆', file: './views/deep.js' },
  { key: 'gate', label: '主动说话', file: './views/gate.js' },
  { key: 'trace', label: '神经活动', file: './views/trace.js' },
  { key: 'delta', label: '变化度', file: './views/delta.js' },
  { key: 'idea', label: '思想层', file: './views/idea.js' },
  { key: 'dialogue', label: '对话分块', file: './views/dialogue.js' },
  { key: 'style', label: '风格样本', file: './views/style.js' },
  { key: 'tools', label: '工具', file: './views/tools.js' },
  { key: 'sandbox', label: '沙箱', file: './views/sandbox.js' },
  { key: 'sleep', label: '睡眠', file: './views/sleep.js' },
  { key: 'logs', label: '日志', file: './views/logs.js' },
  { key: 'server', label: '服务器', file: './views/server.js' }
];

/* 挂载序号：用于丢弃已被新导航取代的异步挂载结果 */
let mountSeq = 0;
/* 当前视图的清理函数（定时器、监听器等），切页时调用 */
let currentCleanup = null;

/* ============================================================
   一、通用工具函数
   ============================================================ */

/** 时间戳（秒）转本地时间字符串；无效值返回占位符 */
export function fmtTime(ts) {
  const n = Number(ts);
  if (ts === null || ts === undefined || ts === '' || !Number.isFinite(n) || n <= 0) return '-';
  return new Date(n * 1000).toLocaleString('zh-CN');
}

/** 秒数转可读时长 */
export function fmtDuration(sec) {
  const total = Math.max(0, Math.floor(Number(sec) || 0));
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const p = (n) => String(n).padStart(2, '0');
  return d > 0 ? `${d} 天 ${p(h)}:${p(m)}:${p(s)}` : `${p(h)}:${p(m)}:${p(s)}`;
}

/** 本地日期 yyyy-MM-dd（用于日志页默认日期） */
export function todayStr() {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** 生成查询串，跳过空值 */
export function qs(params) {
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(params || {})) {
    if (v === null || v === undefined || v === '') continue;
    usp.set(k, String(v));
  }
  const s = usp.toString();
  return s ? '?' + s : '';
}

/** 轻量 DOM 构造器 */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v === null || v === undefined || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = String(v);
      else if (k === 'style') el.setAttribute('style', v);
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (v === true) el.setAttribute(k, '');
      else el.setAttribute(k, String(v));
    }
  }
  appendAll(el, children);
  return el;
}

/** 递归展开数组并安全追加子节点（字符串按文本节点插入） */
export function appendAll(el, children) {
  for (const c of (children || []).flat(Infinity)) {
    if (c === null || c === undefined || c === false || c === '') continue;
    el.append(c.nodeType ? c : document.createTextNode(String(c)));
  }
  return el;
}

/** 清空节点 */
export function clearNode(node) {
  if (node) node.replaceChildren();
  return node;
}

/** 徽标 */
export function badge(text, kind = 'muted') {
  return h('span', { class: 'badge badge-' + kind }, text);
}

/** 空态 */
export function emptyState(text) {
  return h('div', { class: 'empty' }, text || '暂无数据');
}

/** 「功能尚未实现」提示（404 / 501） */
export function unimplementedNote() {
  return h('div', { class: 'note note-warn' }, '该功能尚未实现（P0 骨架）');
}

/** 键值行 */
export function kvRow(key, value) {
  return h('div', { class: 'kv-row' },
    h('span', { class: 'kv-key' }, key),
    h('span', { class: 'kv-val' }, value === null || value === undefined || value === '' ? '-' : value)
  );
}

/** 卡片：返回 section 元素，body 挂在其 body 属性上便于追加 */
export function card(title, bodyNodes, extraNodes) {
  const body = h('div', { class: 'card-body' }, bodyNodes);
  const el = h('section', { class: 'card' },
    h('div', { class: 'card-head' },
      h('h2', { class: 'card-title' }, title),
      extraNodes ? h('div', { class: 'card-extra' }, extraNodes) : null
    ),
    body
  );
  el.body = body;
  return el;
}

/** 轻提示 */
export function toast(message, kind = 'info') {
  const host = document.getElementById('toastHost');
  if (!host) return;
  const node = h('div', { class: 'toast toast-' + kind }, String(message));
  host.append(node);
  setTimeout(() => {
    node.classList.add('toast-out');
    setTimeout(() => node.remove(), 260);
  }, 3200);
}

/** 顶栏右侧说明文字 */
export function setTopbarNote(text) {
  const el = document.getElementById('topbarNote');
  if (el) el.textContent = text || '';
}

/** 模态框：返回 { el, body, foot, close } */
export function openModal(opts = {}) {
  const body = h('div', { class: 'modal-body' }, opts.body);
  const foot = h('div', { class: 'modal-foot' }, opts.footer);
  const mask = h('div', { class: 'modal-mask' });
  const close = () => {
    mask.remove();
    document.removeEventListener('keydown', onKey);
  };
  const onKey = (e) => { if (e.key === 'Escape') close(); };
  const box = h('div', { class: 'modal' },
    h('div', { class: 'modal-head' },
      h('strong', {}, opts.title || '提示'),
      h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: close }, '关闭')
    ),
    body,
    foot
  );
  mask.addEventListener('click', (e) => { if (e.target === mask) close(); });
  document.addEventListener('keydown', onKey);
  mask.append(box);
  document.body.append(mask);
  return { el: box, body, foot, close };
}

/* ============================================================
   二、token 管理
   ============================================================ */

export function getToken() {
  try { return localStorage.getItem(TOKEN_KEY) || ''; } catch (e) { return ''; }
}
export function setToken(v) {
  try { localStorage.setItem(TOKEN_KEY, v); } catch (e) { /* 隐私模式下忽略 */ }
}
export function clearToken() {
  try { localStorage.removeItem(TOKEN_KEY); } catch (e) { /* 忽略 */ }
}

/* ============================================================
   三、API 封装
   ============================================================ */

export class ApiError extends Error {
  constructor(message, status, body) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    /* 404 / 501：后端尚未实现该功能 */
    this.unimplemented = status === 404 || status === 501;
  }
}

/**
 * 统一请求封装
 * - 自动带 Authorization: Bearer <token>
 * - 401：清除 token 并回到登录框
 * - 404 / 501：抛出标了 unimplemented 的错误，由视图展示「尚未实现」
 * - 非 JSON 响应：抛出明确错误而不是崩溃
 */
export async function api(path, options = {}) {
  const token = getToken();
  const headers = Object.assign({ 'Accept': 'application/json' }, options.headers || {});
  const init = Object.assign({}, options, { headers });

  if (options.body !== undefined && options.body !== null) {
    headers['Content-Type'] = 'application/json';
  }
  if (token) headers['Authorization'] = 'Bearer ' + token;

  let res;
  try {
    res = await fetch(path, init);
  } catch (err) {
    throw new ApiError('网络请求失败：' + (err && err.message ? err.message : '未知错误'), 0, null);
  }

  if (res.status === 401) {
    handleUnauthorized();
    throw new ApiError('未授权，请重新输入 token', 401, null);
  }
  if (res.status === 404 || res.status === 501) {
    throw new ApiError('该功能尚未实现（P0 骨架）', res.status, null);
  }

  const raw = await res.text().catch(() => '');
  let data = null;
  if (raw) {
    try {
      data = JSON.parse(raw);
    } catch (err) {
      if (res.ok) throw new ApiError('响应不是合法 JSON（HTTP ' + res.status + '）', res.status, raw);
      data = null;
    }
  }

  if (!res.ok) {
    const msg = (data && (data.error || data.message || data.reason || data.detail))
      || ('请求失败（HTTP ' + res.status + '）');
    throw new ApiError(String(msg), res.status, data);
  }
  return data;
}

export const http = {
  get: (path) => api(path),
  put: (path, body) => api(path, { method: 'PUT', body: JSON.stringify(body === undefined ? {} : body) }),
  patch: (path, body) => api(path, { method: 'PATCH', body: JSON.stringify(body === undefined ? {} : body) }),
  post: (path, body) => api(path, { method: 'POST', body: JSON.stringify(body === undefined ? {} : body) }),
  del: (path) => api(path, { method: 'DELETE' })
};

/* ============================================================
   四、认证：登录框与 401 回退
   ============================================================ */

function handleUnauthorized() {
  clearToken();
  showLogin('登录已失效，请重新输入 token');
}

function showLogin(message) {
  mountSeq += 1; /* 作废进行中的挂载 */
  if (currentCleanup) {
    try { currentCleanup(); } catch (e) { /* 忽略 */ }
    currentCleanup = null;
  }
  document.body.classList.add('auth-locked');
  setTopbarNote('');
  document.getElementById('pageTitle').textContent = '登录';
  const content = document.getElementById('content');
  if (!content) return;
  clearNode(content);
  content.append(buildLoginBox(message || ''));
}

function buildLoginBox(message) {
  const input = h('input', {
    class: 'input',
    type: 'password',
    autocomplete: 'current-password',
    placeholder: '请输入 admin token',
    'aria-label': 'admin token'
  });
  const errBox = h('div', { class: 'login-err' }, message);
  const submitBtn = h('button', { class: 'btn btn-primary btn-block', type: 'submit' }, '确定');

  const form = h('form', {
    class: 'login-box',
    onsubmit: async (e) => {
      e.preventDefault();
      const token = input.value.trim();
      if (!token) {
        errBox.textContent = 'token 不能为空';
        input.focus();
        return;
      }
      setToken(token);
      submitBtn.disabled = true;
      submitBtn.textContent = '校验中';
      let ok = true;
      try {
        await api('/api/status');
      } catch (err) {
        submitBtn.disabled = false;
        submitBtn.textContent = '确定';
        if (err.status === 401) { ok = false; return; } /* 已切回登录框 */
        if (!err.unimplemented) toast('校验请求失败：' + err.message, 'warn');
      }
      if (ok && getToken()) enterApp();
    }
  },
    h('div', { class: 'login-head' },
      h('strong', {}, 'StarGameAI 管理后台'),
      h('span', {}, '请输入后端控制台输出的 admin token')
    ),
    h('label', { class: 'field' },
      h('span', { class: 'field-label' }, '访问令牌'),
      input
    ),
    errBox,
    submitBtn,
    h('div', { class: 'tiny muted' }, '令牌保存在浏览器 localStorage，所有接口请求以 Authorization: Bearer 形式携带。')
  );

  setTimeout(() => input.focus(), 0);
  return h('div', { class: 'login-wrap' }, form);
}

function enterApp() {
  document.body.classList.remove('auth-locked');
  mount(currentRoute());
}

/* ============================================================
   五、路由
   ============================================================ */

function currentRoute() {
  const raw = String(location.hash || '').replace(/^#\/?/, '').split('?')[0].trim();
  return ROUTES.find((r) => r.key === raw) || ROUTES[0];
}

function buildNav() {
  const nav = document.getElementById('nav');
  if (!nav) return;
  clearNode(nav);
  for (const r of ROUTES) {
    nav.append(h('button', {
      type: 'button',
      class: 'nav-item',
      'data-key': r.key,
      title: r.label,
      onclick: () => { location.hash = '#/' + r.key; }
    },
      h('span', { class: 'nav-ico', 'aria-hidden': 'true' }),
      h('span', { class: 'nav-label' }, r.label)
    ));
  }
}

function setActiveNav(key) {
  const items = document.querySelectorAll('.nav-item');
  items.forEach((el) => el.classList.toggle('active', el.dataset.key === key));
}

async function mount(route) {
  const seq = ++mountSeq;
  if (currentCleanup) {
    try { currentCleanup(); } catch (e) { /* 忽略清理异常 */ }
    currentCleanup = null;
  }
  setActiveNav(route.key);
  document.getElementById('pageTitle').textContent = route.label;
  document.title = route.label + ' - StarGameAI 管理后台';
  setTopbarNote('');

  const content = document.getElementById('content');
  if (!content) return;
  clearNode(content);

  if (!getToken()) {
    showLogin('输入 token 后即可管理本机服务');
    return;
  }

  content.append(h('div', { class: 'loading' }, '正在加载 ' + route.label));

  const host = h('div', { class: 'view' });
  try {
    const mod = await import(route.file);
    if (seq !== mountSeq) return;
    clearNode(content);
    content.append(host);
    if (typeof mod.render !== 'function') throw new Error('视图模块缺少 render 函数：' + route.file);
    const cleanup = await mod.render(host, api);
    if (seq !== mountSeq) {
      if (typeof cleanup === 'function') { try { cleanup(); } catch (e) { /* 忽略 */ } }
      return;
    }
    currentCleanup = typeof cleanup === 'function' ? cleanup : null;
  } catch (err) {
    if (seq !== mountSeq) return;
    clearNode(content);
    content.append(host);
    renderViewError(host, err);
  }
}

function renderViewError(host, err) {
  clearNode(host);
  if (err && err.unimplemented) { host.append(unimplementedNote()); return; }
  if (err && err.status === 401) return; /* 已切回登录框 */
  const msg = err && err.message ? err.message : String(err);
  host.append(h('div', { class: 'note note-danger' }, '页面加载失败：' + msg));
}

/* ============================================================
   五之二、首次运行引导

   从仓库 clone 下来应该是一个**空白 AI**：没有配置、没有记忆、没有人格。
   手写 config/*.json 是纯粹的摩擦，所以第一次打开后台直接出向导。
   两个本地 token 后端已经自动生成好了，这里只是把它们展示出来。
   ============================================================ */

async function peekSetup() {
  try {
    const res = await fetch('/api/setup/state', { headers: { Accept: 'application/json' } });
    if (!res.ok) return null;
    return await res.json();
  } catch (err) {
    return null;
  }
}

function showSetup(state) {
  mountSeq += 1;
  if (currentCleanup) {
    try { currentCleanup(); } catch (e) { /* 忽略 */ }
    currentCleanup = null;
  }
  document.body.classList.add('auth-locked');
  setTopbarNote('');
  document.getElementById('pageTitle').textContent = '首次运行配置';

  const content = document.getElementById('content');
  if (!content) return;
  clearNode(content);
  content.append(buildSetupBox(state));
}

function buildSetupBox(state) {
  const s = state || {};

  const llmKey = h('input', {
    class: 'input', type: 'password', placeholder: 'sk-...', autocomplete: 'off',
  });
  const llmBase = h('input', {
    class: 'input', placeholder: 'https://api.deepseek.com', value: s.llm_base_url || '',
  });
  const napcatBase = h('input', {
    class: 'input', placeholder: 'http://127.0.0.1:3000', value: s.napcat_base || 'http://127.0.0.1:3000',
  });
  const napcatToken = h('input', {
    class: 'input', type: 'password', placeholder: 'NapCat 的 access token（没设就留空）', autocomplete: 'off',
  });
  const selfId = h('input', {
    class: 'input', placeholder: '机器人的 QQ 号', value: s.self_id || '',
  });
  const groups = h('textarea', {
    class: 'textarea', rows: '2', placeholder: '群号，一行一个或逗号分隔',
  });
  groups.value = (s.groups || []).join('\n');
  const privates = h('textarea', {
    class: 'textarea', rows: '2', placeholder: '允许私聊的 QQ，一行一个',
  });
  privates.value = (s.private_list || []).join('\n');
  const persona = h('textarea', {
    class: 'textarea', rows: '4',
    placeholder: '我是谁？留空就从零开始——人格会在之后的「睡眠-巩固」里慢慢长出来。',
  });

  const llmResult = h('div', { class: 'tiny muted' }, '');
  const napcatResult = h('div', { class: 'tiny muted' }, '');
  const errBox = h('div', { class: 'login-err' }, '');

  const testLlm = h('button', {
    class: 'btn btn-ghost btn-sm', type: 'button',
    onclick: async () => {
      testLlm.disabled = true;
      llmResult.textContent = '测试中…';
      /* 先把 key 临时存进去，否则后端拿不到 */
      try {
        await fetch('/api/setup/apply', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ llm_api_key: llmKey.value.trim(), llm_base_url: llmBase.value.trim() }),
        });
        const res = await fetch('/api/setup/test-llm', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
        const d = await res.json();
        llmResult.textContent = d.ok ? ('通了：' + (d.reply || '可以')) : ('失败：' + (d.error || '未知'));
        llmResult.className = 'tiny ' + (d.ok ? 'ok-text' : 'danger-text');
      } catch (err) {
        llmResult.textContent = '测试失败：' + err.message;
        llmResult.className = 'tiny danger-text';
      } finally { testLlm.disabled = false; }
    },
  }, '测试');

  const testNapcat = h('button', {
    class: 'btn btn-ghost btn-sm', type: 'button',
    onclick: async () => {
      testNapcat.disabled = true;
      napcatResult.textContent = '测试中…';
      try {
        await fetch('/api/setup/apply', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            napcat_base: napcatBase.value.trim(),
            napcat_token: napcatToken.value.trim(),
          }),
        });
        const res = await fetch('/api/setup/test-napcat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
        const d = await res.json();
        napcatResult.textContent = d.ok ? '通了' : ('失败：' + (d.error || '未知'));
        napcatResult.className = 'tiny ' + (d.ok ? 'ok-text' : 'danger-text');
      } catch (err) {
        napcatResult.textContent = '测试失败：' + err.message;
        napcatResult.className = 'tiny danger-text';
      } finally { testNapcat.disabled = false; }
    },
  }, '测试');

  const saveBtn = h('button', { class: 'btn btn-primary btn-block', type: 'submit' }, '保存并进入后台');

  const form = h('form', {
    class: 'setup-box',
    onsubmit: async (e) => {
      e.preventDefault();
      errBox.textContent = '';

      if (!llmKey.value.trim() && !s.has_llm_key) { errBox.textContent = '请填 DeepSeek API Key'; return; }
      if (!selfId.value.trim()) { errBox.textContent = '请填机器人的 QQ 号'; return; }

      saveBtn.disabled = true;
      saveBtn.textContent = '保存中…';

      try {
        const res = await fetch('/api/setup/apply', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            llm_api_key: llmKey.value.trim(),
            llm_base_url: llmBase.value.trim(),
            napcat_base: napcatBase.value.trim(),
            napcat_token: napcatToken.value.trim(),
            self_id: selfId.value.trim(),
            groups: splitList(groups.value),
            private_list: splitList(privates.value),
            persona: persona.value.trim(),
          }),
        });
        const d = await res.json();

        if (!res.ok || !d.ok) {
          errBox.textContent = '保存失败：' + (d.error || ('HTTP ' + res.status));
          saveBtn.disabled = false;
          saveBtn.textContent = '保存并进入后台';
          return;
        }

        /* 后端已经生成好 admin token，直接登录进去 */
        setToken(d.admin_token);
        doneBox.classList.remove('hidden');
        form.classList.add('hidden');
      } catch (err) {
        errBox.textContent = '保存失败：' + err.message;
        saveBtn.disabled = false;
        saveBtn.textContent = '保存并进入后台';
      }
    },
  },
    h('div', { class: 'login-head' },
      h('strong', {}, '首次运行配置'),
      h('span', {}, '填完这一步就能跑起来。配置写在 config/ 下，随时可以在后台改。')
    ),

    h('div', { class: 'setup-sec' },
      h('div', { class: 'setup-sec-title' }, '① 模型'),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, 'DeepSeek API Key'), llmKey),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, '接口地址（留空用默认）'), llmBase),
      h('div', { class: 'row' }, testLlm, llmResult)),

    h('div', { class: 'setup-sec' },
      h('div', { class: 'setup-sec-title' }, '② QQ 网关（NapCat）'),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, 'OneBot HTTP 地址'), napcatBase),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, 'NapCat access token'), napcatToken),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, '机器人 QQ 号'), selfId),
      h('div', { class: 'row' }, testNapcat, napcatResult)),

    h('div', { class: 'setup-sec' },
      h('div', { class: 'setup-sec-title' }, '③ 在哪里能说话'),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, '白名单群号'), groups),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, '白名单私聊 QQ'), privates),
      h('div', { class: 'field-hint' },
        '留空 = 不设限（任何群/私聊都会响应）。建议先只填一两个，跑稳了再放开。')),

    h('div', { class: 'setup-sec' },
      h('div', { class: 'setup-sec-title' }, '④ 初始自我（可跳过）'),
      h('label', { class: 'field' }, h('span', { class: 'field-label' }, '我是谁'), persona),
      h('div', { class: 'field-hint' },
        '这一格是一个立场问题：这套系统的前提是「人格必须通过睡眠巩固长出来」。',
        ' 留空就是真的从零开始——先没有自我，靠之后的对话和巩固一点点长。',
        ' 填了等于给一个起点，之后仍会被它自己改写。')),

    errBox,
    saveBtn
  );

  const doneBox = h('div', { class: 'hidden setup-done' },
    h('div', { class: 'note note-ok' }, '配置已写入 config/。'),
    h('div', { class: 'note note-warn' },
      '**需要重启服务**：人设和白名单在启动时加载，重启后新配置才完整生效。'),
    h('div', { class: 'kv' },
      kvRow('后台口令', s.admin_token || ''),
      kvRow('上报签名密钥', s.report_token || '')),
    h('div', { class: 'field-hint' },
      '这两个 token 已经自动生成并存在 config/secrets.json 里。后台口令浏览器已记住，' +
      '换设备时从这里复制；上报签名密钥要填进 NapCat 的 HTTP 上报配置里。'),
    h('div', { class: 'toolbar-row', style: 'margin-top:12px' },
      h('button', {
        class: 'btn btn-primary', type: 'button', onclick: () => enterApp(),
      }, '进入后台（重启后）'))
  );

  return h('div', {}, form, doneBox);
}

function splitList(text) {
  return String(text || '')
    .split(/[\n,，\s]+/)
    .map((x) => x.trim())
    .filter(Boolean);
}

/* ============================================================
   六、启动
   ============================================================ */

function boot() {
  buildNav();

  const logoutBtn = document.getElementById('logoutBtn');
  if (logoutBtn) {
    logoutBtn.addEventListener('click', () => {
      clearToken();
      showLogin('已退出登录，请重新输入 token');
    });
  }

  window.addEventListener('hashchange', () => mount(currentRoute()));

  /* 先看是不是刚 clone 下来的空白 AI —— 是的话直接出向导 */
  peekSetup().then((state) => {
    if (state && state.needs_setup) {
      showSetup(state);
      return;
    }
    if (!getToken()) {
      showLogin('输入 token 后即可管理本机服务');
      return;
    }
    mount(currentRoute());
  }).catch(() => {
    if (!getToken()) {
      showLogin('输入 token 后即可管理本机服务');
      return;
    }
    mount(currentRoute());
  });
}

boot();
