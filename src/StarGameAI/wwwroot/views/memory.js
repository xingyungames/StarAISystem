/* ============================================================
   记忆页：筛选 / 分页 / 行内编辑 / 删除（pinned 保护、tombstone 弱化）
   ============================================================ */

import {
  h, card, badge, fmtTime, toast, qs, unimplementedNote, emptyState
} from '../app.js';

const KINDS = ['identity', 'fact', 'preference', 'event', 'quote', 'capability'];
const KIND_LABEL = {
  identity: '身份', fact: '事实', preference: '偏好',
  event: '事件', quote: '语录', capability: '能力'
};
const LEVELS = ['public', 'member', 'private'];
const LEVEL_LABEL = { public: '公开', member: '群内', private: '私密' };

const kindText = (k) => KIND_LABEL[k] || k || '-';
const levelText = (l) => LEVEL_LABEL[l] || l || '-';

export function render(container, api) {
  let disposed = false;

  const state = {
    scope: '', kind: '', q: '',
    page: 1, size: 50, total: 0,
    items: [],
    knownScopes: new Set(),
    editingId: null
  };

  /* ---------- 顶部筛选 ---------- */
  const scopeSelect = h('select', { class: 'select', 'aria-label': 'scope 筛选' });
  const kindSelect = h('select', { class: 'select', 'aria-label': 'kind 筛选' },
    h('option', { value: '' }, '全部类型'),
    ...KINDS.map((k) => h('option', { value: k }, kindText(k) + '（' + k + '）'))
  );
  const searchInput = h('input', {
    class: 'input', type: 'search', placeholder: '搜索内容（回车查询）', style: 'min-width:200px'
  });
  const queryBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => { state.page = 1; load(); } }, '查询');
  const resetBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => resetFilters() }, '重置');
  const sizeSelect = h('select', { class: 'select' },
    ...[20, 50, 100].map((n) => h('option', { value: String(n) }, '每页 ' + n))
  );
  const pagerInfo = h('span', { class: 'pager-info' }, '');
  const prevBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(state.page - 1) }, '上一页');
  const nextBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(state.page + 1) }, '下一页');
  const firstBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(1) }, '首页');

  scopeSelect.addEventListener('change', () => { state.scope = scopeSelect.value; state.page = 1; load(); });
  kindSelect.addEventListener('change', () => { state.kind = kindSelect.value; state.page = 1; load(); });
  sizeSelect.value = String(state.size);
  sizeSelect.addEventListener('change', () => { state.size = Number(sizeSelect.value) || 50; state.page = 1; load(); });
  searchInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { state.q = searchInput.value.trim(); state.page = 1; load(); }
  });

  /* ---------- 表格与状态位 ---------- */
  const errorSlot = h('div', {});
  const tableWrap = h('div', { class: 'table-wrap' });
  const tableBodySlot = h('div', { class: 'card-body tight' }, tableWrap);
  const pagerBar = h('div', { class: 'toolbar' },
    h('div', { class: 'pager' }, firstBtn, prevBtn, nextBtn, pagerInfo),
    h('div', { class: 'spacer' }),
    h('div', { class: 'pager' }, sizeSelect)
  );

  const filters = h('div', { class: 'toolbar' },
    h('span', { class: 'tiny muted' }, 'scope'),
    h('span', { class: 'select-wrap' }, scopeSelect),
    h('span', { class: 'tiny muted' }, '类型'),
    h('span', { class: 'select-wrap' }, kindSelect),
    searchInput,
    queryBtn,
    resetBtn
  );

  const mainCard = card('记忆条目', [filters, h('div', { class: 'mt-12' }, tableBodySlot), h('div', { class: 'mt-12' }, pagerBar)],
    h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '刷新'));

  container.append(errorSlot, mainCard);

  /* ---------- scope 下拉（从已加载数据收集去重） ---------- */
  function paintScopeOptions() {
    const current = state.scope;
    const opts = [h('option', { value: '' }, '全部 scope')];
    for (const s of Array.from(state.knownScopes).sort()) {
      opts.push(h('option', { value: s }, s));
    }
    scopeSelect.replaceChildren(...opts);
    scopeSelect.value = current;
    if (scopeSelect.value !== current) scopeSelect.value = '';
  }

  function collectScopes(items) {
    let changed = false;
    for (const it of items) {
      if (it && it.scope && !state.knownScopes.has(it.scope)) {
        state.knownScopes.add(it.scope);
        changed = true;
      }
    }
    if (changed) paintScopeOptions();
  }

  /* ---------- 分页 ---------- */
  function totalPages() {
    return Math.max(1, Math.ceil((state.total || 0) / (state.size || 50)));
  }
  function goto(page) {
    const max = totalPages();
    const target = Math.min(Math.max(1, page), max);
    if (target === state.page) return;
    state.page = target;
    load();
  }
  function paintPager() {
    const tp = totalPages();
    pagerInfo.textContent = '共 ' + (state.total || 0) + ' 条 / 第 ' + state.page + ' 页 / 共 ' + tp + ' 页';
    prevBtn.disabled = state.page <= 1;
    firstBtn.disabled = state.page <= 1;
    nextBtn.disabled = state.page >= tp;
  }

  /* ---------- 行渲染 ---------- */
  function statusCell(item) {
    if (item.tombstone) return badge('已删除', 'danger');
    if (item.pinned) return badge('已固定', 'warn');
    return badge('正常', 'ok');
  }

  function actionsCell(item) {
    if (state.editingId === item.id) {
      return h('td', { class: 'actions' },
        h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => saveEdit(item) }, '保存'),
        h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => { state.editingId = null; paintRows(); } }, '取消')
      );
    }
    const deleteBtn = h('button', {
      class: 'btn btn-danger btn-sm',
      type: 'button',
      title: item.pinned ? '已固定，不可删除' : '删除该条目',
      disabled: !!item.pinned,
      onclick: () => removeItem(item)
    }, '删除');
    return h('td', { class: 'actions' },
      h('button', {
        class: 'btn btn-ghost btn-sm', type: 'button',
        onclick: () => { state.editingId = item.id; paintRows(); }
      }, '编辑'),
      deleteBtn
    );
  }

  function editRow(item) {
    const textArea = h('textarea', { class: 'textarea', rows: '2', style: 'width:100%' });
    textArea.value = item.text || '';
    const weightInput = h('input', { class: 'input', type: 'number', min: '0', max: '100', step: '1', style: 'width:82px' });
    weightInput.value = String(item.weight ?? 0);
    const levelSel = h('select', { class: 'select' },
      ...LEVELS.map((l) => h('option', { value: l }, levelText(l) + '（' + l + '）'))
    );
    levelSel.value = LEVELS.includes(item.level) ? item.level : 'private';
    const pinBox = h('input', { type: 'checkbox' });
    pinBox.checked = !!item.pinned;
    item.__draft = { textArea, weightInput, levelSel, pinBox };

    return h('tr', { class: 'row-open' },
      h('td', { colspan: '2' }, textArea),
      h('td', {}, weightInput),
      h('td', {}, h('span', { class: 'select-wrap' }, levelSel)),
      h('td', {}, item.user_name || '-'),
      h('td', { class: 'tiny nowrap' }, fmtTime(item.ts)),
      h('td', {}, h('label', { class: 'check' }, pinBox, '固定')),
      actionsCell(item)
    );
  }

  function normalRow(item) {
    const rowClass = (state.editingId === item.id ? 'row-open ' : '') + (item.tombstone ? 'tombstone-row' : '');
    const tags = Array.isArray(item.tags) && item.tags.length
      ? h('div', { class: 'row mt-8' }, ...item.tags.map((t) => badge(t, 'accent')))
      : null;
    return h('tr', { class: rowClass || undefined },
      h('td', { class: 'cell-text' }, item.text || '', tags),
      h('td', {}, h('span', { class: 'nowrap' }, kindText(item.kind))),
      h('td', { class: 'num' }, String(item.weight ?? '-')),
      h('td', {}, levelText(item.level)),
      h('td', {}, item.user_name || '-'),
      h('td', { class: 'tiny nowrap' }, fmtTime(item.ts)),
      h('td', {}, statusCell(item)),
      actionsCell(item)
    );
  }

  function paintRows() {
    const items = state.items || [];
    if (!items.length) {
      tableWrap.replaceChildren(emptyState('没有符合条件的记忆条目'));
      return;
    }
    const tbody = h('tbody');
    for (const item of items) {
      tbody.append(state.editingId === item.id ? editRow(item) : normalRow(item));
    }
    tableWrap.replaceChildren(
      h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '内容'), h('th', {}, '类型'), h('th', {}, '权重'),
          h('th', {}, '可见性'), h('th', {}, '用户'), h('th', {}, '时间'),
          h('th', {}, '状态'), h('th', {}, '操作')
        )),
        tbody
      )
    );
  }

  /* ---------- 行内编辑保存 ---------- */
  async function saveEdit(item) {
    const d = item.__draft;
    if (!d) return;
    const body = {
      text: d.textArea.value,
      weight: Number(d.weightInput.value) || 0,
      level: d.levelSel.value,
      pinned: d.pinBox.checked
    };
    const url = '/api/memory/' + encodeURIComponent(item.scope) + '/' + encodeURIComponent(item.id);
    try {
      await api(url, { method: 'PATCH', body: JSON.stringify(body) });
      if (disposed) return;
      state.editingId = null;
      toast('已保存', 'ok');
      load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('保存失败：' + err.message, 'danger');
    }
  }

  /* ---------- 删除 ---------- */
  async function removeItem(item) {
    if (item.pinned) { toast('已固定，不可删除', 'warn'); return; }
    const url = '/api/memory/' + encodeURIComponent(item.scope) + '/' + encodeURIComponent(item.id);
    try {
      await api(url, { method: 'DELETE' });
      if (disposed) return;
      toast('已删除（tombstone）', 'ok');
      load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status === 409) toast('后端拒绝删除：' + err.message, 'warn');
      else if (err.status !== 401) toast('删除失败：' + err.message, 'danger');
    }
  }

  /* ---------- 载入 ---------- */
  async function load() {
    const query = qs({
      scope: state.scope, kind: state.kind, q: state.q,
      page: state.page, size: state.size
    });
    try {
      const res = await api('/api/memory' + query);
      if (disposed) return;
      errorSlot.replaceChildren();
      state.total = Number(res && res.total) || 0;
      state.items = Array.isArray(res && res.items) ? res.items : [];
      collectScopes(state.items);
      if (state.editingId && !state.items.some((it) => it.id === state.editingId)) state.editingId = null;
      paintRows();
      paintPager();
    } catch (err) {
      if (disposed) return;
      state.items = [];
      state.total = 0;
      paintRows();
      paintPager();
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '记忆读取失败：' + err.message));
    }
  }

  function resetFilters() {
    state.scope = '';
    state.kind = '';
    state.q = '';
    state.page = 1;
    scopeSelect.value = '';
    kindSelect.value = '';
    searchInput.value = '';
    state.editingId = null;
    load();
  }

  /* ---------- 初始化 ---------- */
  paintScopeOptions();
  paintPager();
  tableWrap.replaceChildren(h('div', { class: 'loading' }, '正在加载'));
  load();

  return function cleanup() { disposed = true; };
}
