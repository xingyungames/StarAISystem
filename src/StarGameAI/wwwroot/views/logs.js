/* ============================================================
   日志页：按日期 / scope / trace 浏览缓存日志，行展开看完整 JSON，
           并可按需加载发言门审计
   ============================================================ */

import {
  h, card, badge, fmtTime, qs, todayStr, unimplementedNote, emptyState
} from '../app.js';

const PAGE_SIZE = 100;

export function render(container, api) {
  let disposed = false;

  const state = {
    date: todayStr(),
    scope: '',
    trace: '',
    page: 1,
    total: 0,
    items: [],
    expanded: -1,
    audit: null
  };

  /* ---------- 顶部筛选 ---------- */
  const dateInput = h('input', { class: 'input', type: 'date', 'aria-label': '日期' });
  dateInput.value = state.date;
  const scopeInput = h('input', { class: 'input', type: 'text', placeholder: 'scope，如 group:100001', style: 'min-width:190px' });
  const traceInput = h('input', { class: 'input', type: 'text', placeholder: 'trace id（可空）', style: 'min-width:150px' });
  const queryBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => { state.page = 1; load(); } }, '查询');
  const resetBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => resetFilters() }, '重置');

  const pagerInfo = h('span', { class: 'pager-info' }, '');
  const firstBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(1) }, '首页');
  const prevBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(state.page - 1) }, '上一页');
  const nextBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goto(state.page + 1) }, '下一页');

  dateInput.addEventListener('change', () => { state.date = dateInput.value; state.page = 1; load(); });
  for (const input of [scopeInput, traceInput]) {
    input.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter') return;
      state.scope = scopeInput.value.trim();
      state.trace = traceInput.value.trim();
      state.page = 1;
      load();
    });
  }

  const errorSlot = h('div', {});
  const tableWrap = h('div', { class: 'table-wrap' });

  const toolbar = h('div', { class: 'toolbar' },
    h('span', { class: 'tiny muted' }, '日期'),
    dateInput,
    scopeInput,
    traceInput,
    queryBtn,
    resetBtn
  );

  const pagerBar = h('div', { class: 'toolbar' },
    h('div', { class: 'pager' }, firstBtn, prevBtn, nextBtn, pagerInfo),
    h('div', { class: 'spacer' }),
    badge('每页 ' + PAGE_SIZE + ' 条', 'muted')
  );

  const logCard = card('缓存日志', [
    toolbar,
    h('div', { class: 'card-body tight mt-12' }, tableWrap),
    h('div', { class: 'mt-12' }, pagerBar)
  ], h('span', { class: 'tiny muted' }, 'data/log/<日期>/<scope>.jsonl'));

  /* ---------- 发言门审计 ---------- */
  const auditWrap = h('div', {});
  const auditBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadAudit() }, '加载发言门审计');
  const auditCard = card('发言门审计', auditBtn);

  container.append(errorSlot, logCard, auditCard);

  /* ---------- 分页 ---------- */
  const totalPages = () => Math.max(1, Math.ceil((state.total || 0) / PAGE_SIZE));

  function goto(page) {
    const target = Math.min(Math.max(1, page), totalPages());
    if (target === state.page) return;
    state.page = target;
    load();
  }

  function paintPager() {
    const tp = totalPages();
    pagerInfo.textContent = '共 ' + (state.total || 0) + ' 条 / 第 ' + state.page + ' 页 / 共 ' + tp + ' 页';
    firstBtn.disabled = state.page <= 1;
    prevBtn.disabled = state.page <= 1;
    nextBtn.disabled = state.page >= tp;
  }

  /* ---------- 表格 ---------- */
  function detailRow(item) {
    return h('tr', {},
      h('td', { class: 'detail-cell', colspan: '6' },
        h('div', { class: 'row' },
          h('strong', { class: 'tiny' }, '完整 JSON'),
          h('div', { class: 'spacer', style: 'flex:1 1 auto' }),
          h('button', {
            class: 'btn btn-ghost btn-sm', type: 'button',
            onclick: () => { state.expanded = -1; paintRows(); }
          }, '收起')
        ),
        h('pre', { class: 'pre' }, JSON.stringify(item, null, 2))
      )
    );
  }

  function paintRows() {
    const items = state.items || [];
    if (!items.length) {
      tableWrap.replaceChildren(emptyState('该条件下没有日志'));
      return;
    }
    const tbody = h('tbody');
    items.forEach((item, idx) => {
      const isSelf = !!item.is_self;
      tbody.append(h('tr', {
        class: 'clickable' + (state.expanded === idx ? ' row-open' : ''),
        onclick: () => { state.expanded = state.expanded === idx ? -1 : idx; paintRows(); }
      },
        h('td', { class: 'tiny nowrap' }, fmtTime(item.ts)),
        h('td', { class: 'cell-mono tiny' }, item.scope || '-'),
        h('td', {}, (item.name || '-') + '（' + (item.user_id || '-') + '）'),
        h('td', {}, isSelf ? badge('AI', 'accent') : badge('用户', 'muted')),
        h('td', { class: 'tiny' }, item.trigger || '-'),
        h('td', { class: 'cell-text' }, item.text || '')
      ));
      if (state.expanded === idx) tbody.append(detailRow(item));
    });

    tableWrap.replaceChildren(
      h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '时间'), h('th', {}, 'scope'), h('th', {}, '发送者'),
          h('th', {}, '来源'), h('th', {}, 'trigger'), h('th', {}, '内容')
        )),
        tbody
      )
    );
  }

  /* ---------- 载入日志 ---------- */
  async function load() {
    state.expanded = -1;
    const query = qs({
      date: state.date, scope: state.scope, trace: state.trace,
      page: state.page, size: PAGE_SIZE
    });
    try {
      const res = await api('/api/log' + query);
      if (disposed) return;
      errorSlot.replaceChildren();
      state.total = Number(res && res.total) || 0;
      state.items = Array.isArray(res && res.items) ? res.items : [];
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
      errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '日志读取失败：' + err.message));
    }
  }

  function resetFilters() {
    state.date = todayStr();
    state.scope = '';
    state.trace = '';
    state.page = 1;
    dateInput.value = state.date;
    scopeInput.value = '';
    traceInput.value = '';
    load();
  }

  /* ---------- 发言门审计 ---------- */
  async function loadAudit() {
    auditWrap.replaceChildren(h('div', { class: 'loading' }, '正在加载审计数据'));
    try {
      const res = await api('/api/gate/audit' + qs({ date: state.date }));
      if (disposed) return;
      const items = Array.isArray(res && res.items) ? res.items : [];
      const rate = res && typeof res.skip_rate === 'number' ? res.skip_rate : null;
      const head = h('div', { class: 'row' },
        h('strong', { class: 'tiny' }, state.date + ' 的发言门判定'),
        rate === null ? badge('跳过率 -', 'muted') : badge('跳过率 ' + (rate * 100).toFixed(1) + '%', rate > 0.5 ? 'warn' : 'ok'),
        h('div', { class: 'spacer', style: 'flex:1 1 auto' }),
        h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => { auditWrap.replaceChildren(); } }, '收起')
      );
      if (!items.length) {
        auditWrap.replaceChildren(head, emptyState('没有审计记录'));
        return;
      }
      const tbody = h('tbody');
      for (const it of items) {
        tbody.append(h('tr', {},
          h('td', { class: 'tiny nowrap' }, fmtTime(it.ts)),
          h('td', { class: 'num' }, String(it.score ?? '-')),
          h('td', { class: 'cell-text' }, it.reason || '-'),
          h('td', { class: 'cell-text tiny' }, it.why_not || '-'),
          h('td', {}, it.used_llm ? badge('使用 LLM', 'accent') : badge('规则', 'muted'))
        ));
      }
      auditWrap.replaceChildren(head,
        h('div', { class: 'table-wrap mt-12' },
          h('table', { class: 'table' },
            h('thead', {}, h('tr', {},
              h('th', {}, '时间'), h('th', {}, '分数'), h('th', {}, '理由'),
              h('th', {}, '未命中原因'), h('th', {}, '判定方式')
            )),
            tbody
          )
        )
      );
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { auditWrap.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      auditWrap.replaceChildren(h('div', { class: 'note note-danger' }, '审计读取失败：' + err.message));
    }
  }

  /* ---------- 初始化 ---------- */
  scopeInput.value = state.scope;
  traceInput.value = state.trace;
  auditCard.body.append(auditWrap);
  paintPager();
  tableWrap.replaceChildren(h('div', { class: 'loading' }, '正在加载'));
  load();

  return function cleanup() { disposed = true; };
}
