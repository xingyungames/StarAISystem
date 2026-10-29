/* ============================================================
   睡眠页：作息配置表单 / 手动控制 / 睡眠信号 / 水位线
   ============================================================ */

import {
  h, card, badge, fmtTime, toast, unimplementedNote, kvRow, emptyState
} from '../app.js';

/* 字段中文标签 */
const LABELS = {
  enabled: '启用睡眠巩固',
  ai_editable: 'AI 可编辑作息',
  windows: '睡眠时间窗',
  interval: '触发间隔',
  min_hours_between: '最短间隔（小时）',
  max_hours_between: '最长间隔（小时）',
  idle_required_seconds: '空闲前置（秒）',
  defer_if_busy: '忙碌时顺延',
  log_threshold_entries: '日志条数阈值',
  behavior_while_sleeping: '睡眠期间行为',
  chunk: '分片设置',
  max_entries: '单批最大条数',
  max_chars: '单批最大字符数',
  overlap_entries: '分片重叠条数',
  max_chunks_per_run: '单次最大分片数',
  start: '开始',
  end: '结束'
};

/* 睡眠期间行为的三档说明 */
const BEHAVIOR_OPTIONS = [
  { value: 'silent', label: 'silent - 静默', desc: '巩固期间完全不发言，消息照常写入日志但不回复。' },
  { value: 'fast_reply', label: 'fast_reply - 快速回复', desc: '只走最快的一档，不做深度思考，尽量缩短延迟。' },
  { value: 'queue', label: 'queue - 排队', desc: '消息先入队不回复，巩固结束后再统一处理。' }
];

const labelFor = (key) => LABELS[key] || key;
const hint = (text) => h('span', { class: 'field-hint' }, text);
const clone = (v) => JSON.parse(JSON.stringify(v === undefined ? null : v));

/** 按点号路径写值 */
function setByPath(obj, path, value) {
  const parts = String(path).split('.');
  let cur = obj;
  for (let i = 0; i < parts.length - 1; i += 1) {
    if (!cur[parts[i]] || typeof cur[parts[i]] !== 'object') cur[parts[i]] = {};
    cur = cur[parts[i]];
  }
  cur[parts[parts.length - 1]] = value;
}

/* ============================================================
   配置表单：按值类型生成控件
   ============================================================ */

/** 时间窗数组：开始-结束两个 time 输入，可增删 */
function windowField(key, value, cfg, path) {
  const rows = (Array.isArray(value) ? value : []).map((w) => ({
    start: (w && w.start) || '00:00',
    end: (w && w.end) || '00:00'
  }));
  const sync = () => setByPath(cfg, path, rows.map((r) => ({ start: r.start, end: r.end })));
  const rowsBox = h('div', { class: 'window-rows' });

  function paint() {
    const nodes = rows.map((row, idx) => {
      const startIn = h('input', { class: 'input', type: 'time' });
      startIn.value = row.start;
      startIn.addEventListener('input', () => { row.start = startIn.value; sync(); });
      const endIn = h('input', { class: 'input', type: 'time' });
      endIn.value = row.end;
      endIn.addEventListener('input', () => { row.end = endIn.value; sync(); });
      return h('div', { class: 'window-row' },
        startIn,
        h('span', { class: 'window-sep' }, '至'),
        endIn,
        h('button', {
          class: 'btn btn-danger btn-sm', type: 'button',
          onclick: () => { rows.splice(idx, 1); sync(); paint(); }
        }, '删除')
      );
    });
    nodes.push(h('div', { class: 'window-row' },
      h('button', {
        class: 'btn btn-ghost btn-sm', type: 'button',
        onclick: () => { rows.push({ start: '02:00', end: '05:00' }); sync(); paint(); }
      }, '增加时间窗')
    ));
    rowsBox.replaceChildren(...nodes);
  }

  sync();
  paint();
  return h('div', { class: 'fieldset' },
    h('legend', {}, labelFor(key)),
    rowsBox,
    hint('每行一个时间窗，24 小时制；跨零点的时间窗请拆成两行')
  );
}

/** 单个字段控件 */
function buildField(key, value, cfg, path) {
  /* 睡眠期间行为：固定三档下拉 */
  if (key === 'behavior_while_sleeping') {
    const desc = h('span', { class: 'field-hint' }, '');
    const sel = h('select', { class: 'select' },
      ...BEHAVIOR_OPTIONS.map((o) => h('option', { value: o.value }, o.label))
    );
    const known = BEHAVIOR_OPTIONS.some((o) => o.value === value);
    if (!known && value !== undefined) sel.append(h('option', { value: String(value) }, String(value)));
    sel.value = String(value == null ? 'silent' : value);
    const paintDesc = () => {
      const hit = BEHAVIOR_OPTIONS.find((o) => o.value === sel.value);
      desc.textContent = hit ? hit.desc : '';
    };
    sel.addEventListener('change', () => { setByPath(cfg, path, sel.value); paintDesc(); });
    paintDesc();
    return h('label', { class: 'field' },
      h('span', { class: 'field-label' }, labelFor(key)),
      h('span', { class: 'select-wrap' }, sel),
      desc
    );
  }

  /* 数组：元素是对象则当时间窗，否则按逗号分隔的文本处理 */
  if (Array.isArray(value)) {
    const isWindows = value.length > 0 && typeof value[0] === 'object' && value[0] !== null;
    if (isWindows) return windowField(key, value, cfg, path);
    const input = h('input', { class: 'input', type: 'text' });
    input.value = value.join(', ');
    input.addEventListener('input', () => {
      const arr = input.value.split(',').map((s) => s.trim()).filter((s) => s !== '');
      setByPath(cfg, path, arr);
    });
    return h('label', { class: 'field' },
      h('span', { class: 'field-label' }, labelFor(key)),
      input,
      hint('数组，用英文逗号分隔')
    );
  }

  /* 布尔 */
  if (typeof value === 'boolean') {
    const box = h('input', { type: 'checkbox' });
    box.checked = value;
    const text = h('span', {}, box.checked ? '已开启' : '已关闭');
    box.addEventListener('change', () => {
      setByPath(cfg, path, box.checked);
      text.textContent = box.checked ? '已开启' : '已关闭';
    });
    return h('div', { class: 'field' },
      h('span', { class: 'field-label' }, labelFor(key)),
      h('label', { class: 'check' }, box, text)
    );
  }

  /* 数字 */
  if (typeof value === 'number') {
    const input = h('input', { class: 'input', type: 'number', step: 'any' });
    input.value = String(value);
    input.addEventListener('input', () => {
      const n = Number(input.value);
      setByPath(cfg, path, Number.isFinite(n) ? n : 0);
    });
    return h('label', { class: 'field' },
      h('span', { class: 'field-label' }, labelFor(key)),
      input
    );
  }

  /* 字符串（含 null） */
  const input = h('input', { class: 'input', type: 'text' });
  input.value = value === null || value === undefined ? '' : String(value);
  input.addEventListener('input', () => setByPath(cfg, path, input.value));
  return h('label', { class: 'field' },
    h('span', { class: 'field-label' }, labelFor(key)),
    input
  );
}

/** 递归展开配置对象的每一层 */
function buildLevel(obj, cfg, path, grid) {
  for (const [key, value] of Object.entries(obj || {})) {
    const p = path ? path + '.' + key : key;
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      const inner = h('div', { class: 'form-grid' });
      grid.append(h('fieldset', { class: 'fieldset' },
        h('legend', {}, labelFor(key)),
        inner
      ));
      buildLevel(value, cfg, p, inner);
      continue;
    }
    grid.append(buildField(key, value, cfg, p));
  }
}

/* ============================================================
   视图
   ============================================================ */

export function render(container, api) {
  let disposed = false;
  let data = null;
  let cfg = null;

  const statusLine = h('div', { class: 'status-line' });
  const errorSlot = h('div', {});
  const formGrid = h('div', { class: 'form-grid' });
  const formCard = card('作息配置', formGrid,
    h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => saveConfig() }, '保存配置'));
  const manualBody = h('div', { class: 'col' });
  const manualCard = card('手动控制', manualBody);
  const signalsWrap = h('div', { class: 'table-wrap' });
  const signalsCard = card('睡眠信号', h('div', { class: 'card-body tight' }, signalsWrap),
    h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '刷新'));

  container.append(errorSlot, statusLine, h('div', { class: 'grid-2' }, formCard, manualCard), signalsCard);

  /* ---------- 状态条 ---------- */
  function paintStatus() {
    const st = (data && data.state) || {};
    const wm = (data && data.watermark) || {};
    const sleeping = !!st.sleeping;
    statusLine.replaceChildren(
      h('span', { class: 'dot ' + (sleeping ? 'accent' : 'ok') }),
      h('strong', {}, sleeping ? '当前状态：睡眠中' : '当前状态：清醒'),
      h('span', { class: 'tiny muted' }, '上次巩固：' + fmtTime(st.last_run_ts)),
      h('span', { class: 'tiny muted' }, '水位线：' + fmtTime(wm.last_consumed_log_ts) +
        '（ts ' + (wm.last_consumed_log_ts || 0) + '）'),
      badge(sleeping ? '巩固进行中' : '等待触发', sleeping ? 'accent' : 'ok')
    );
  }

  /* ---------- 手动控制 ---------- */
  function paintManual() {
    const st = (data && data.state) || {};
    const sleeping = !!st.sleeping;
    const startBtn = h('button', {
      class: 'btn btn-primary', type: 'button',
      disabled: sleeping,
      onclick: () => manual('start')
    }, '立刻睡眠');
    const cancelBtn = h('button', {
      class: 'btn btn-warn', type: 'button',
      disabled: !sleeping,
      onclick: () => manual('cancel')
    }, '取消');
    manualBody.replaceChildren(
      h('div', { class: 'kv' },
        kvRow('是否睡眠中', sleeping ? badge('睡眠中', 'accent') : badge('清醒', 'ok')),
        kvRow('上次巩固时间', fmtTime(st.last_run_ts)),
        kvRow('水位线', fmtTime((data && data.watermark || {}).last_consumed_log_ts))
      ),
      h('div', { class: 'row mt-12' }, startBtn, cancelBtn),
      h('div', { class: 'tiny muted mt-8' }, '立刻睡眠会跳过间隔与空闲判定，直接开始一次巩固；取消会中断进行中的巩固。')
    );
  }

  /* ---------- 信号表 ---------- */
  function paintSignals() {
    const signals = (data && Array.isArray(data.signals)) ? data.signals : [];
    if (!signals.length) {
      signalsWrap.replaceChildren(emptyState('暂无睡眠信号'));
      return;
    }
    const tbody = h('tbody');
    for (const s of signals) {
      tbody.append(h('tr', {},
        h('td', { class: 'tiny nowrap' }, fmtTime(s.ts)),
        h('td', { class: 'cell-text' }, s.reason || '-'),
        h('td', {}, s.consumed ? badge('已消费', 'ok') : badge('未消费', 'muted'))
      ));
    }
    signalsWrap.replaceChildren(
      h('table', { class: 'table' },
        h('thead', {}, h('tr', {}, h('th', {}, '时间'), h('th', {}, '理由'), h('th', {}, '是否已消费'))),
        tbody
      )
    );
  }

  /* ---------- 表单 ---------- */
  function paintForm() {
    formGrid.replaceChildren();
    if (!cfg || typeof cfg !== 'object') {
      formGrid.append(h('div', { class: 'note note-warn' }, '当前 config 为空或格式异常'));
      return;
    }
    buildLevel(cfg, cfg, '', formGrid);
  }

  /* ---------- 动作 ---------- */
  async function saveConfig() {
    try {
      await api('/api/sleep/config', { method: 'PUT', body: JSON.stringify(cfg) });
      if (disposed) return;
      toast('作息配置已保存', 'ok');
      await load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('保存失败：' + err.message, 'danger');
    }
  }

  async function manual(action) {
    try {
      await api('/api/sleep/' + action, { method: 'POST', body: '{}' });
      if (disposed) return;
      toast(action === 'start' ? '已开始睡眠巩固' : '已取消睡眠巩固', 'ok');
      await load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('操作失败：' + err.message, 'danger');
    }
  }

  /* ---------- 载入 ---------- */
  async function load() {
    try {
      const res = await api('/api/sleep');
      if (disposed) return;
      data = res || {};
      cfg = clone(data.config || {});
      errorSlot.replaceChildren();
      paintStatus();
      paintManual();
      paintForm();
      paintSignals();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '睡眠状态读取失败：' + err.message));
    }
  }

  statusLine.append(h('div', { class: 'loading' }, '正在加载'));
  load();

  return function cleanup() { disposed = true; };
}
