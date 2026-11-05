/* ============================================================
   工具页：列表 / 展开详情（契约 + 源码）/ 启用禁用 / 试跑
   ============================================================ */

import {
  h, card, badge, toast, fmtTime, unimplementedNote, emptyState, openModal
} from '../app.js';

const STATUS_META = {
  active: { text: 'active 已启用', kind: 'ok' },
  disabled: { text: 'disabled 已禁用', kind: 'danger' },
  draft: { text: 'draft 草稿', kind: 'muted' },
  pending: { text: 'pending 待批准', kind: 'warn' }
};

function statusBadge(status) {
  const meta = STATUS_META[status] || { text: status || '-', kind: 'muted' };
  return badge(meta.text, meta.kind);
}

/** 带行号的源码块 */
function codeBlock(source) {
  const box = h('div', { class: 'code' });
  const lines = String(source == null ? '' : source).split('\n');
  if (!lines.length || (lines.length === 1 && lines[0] === '')) {
    box.append(h('div', { class: 'empty' }, '（空文件）'));
    return box;
  }
  lines.forEach((line, i) => {
    box.append(h('div', { class: 'code-line' },
      h('span', { class: 'ln' }, String(i + 1)),
      h('span', { class: 'lc' }, line === '' ? ' ' : line)
    ));
  });
  return box;
}

export function render(container, api) {
  let disposed = false;
  const state = { tools: [], expanded: null, detail: null, loadingDetail: false };

  const errorSlot = h('div', {});
  const tableWrap = h('div', { class: 'table-wrap' });
  const tableSlot = h('div', { class: 'card-body tight' }, tableWrap);
  const detailSlot = h('div', {});

  const listCard = card('工具列表', tableSlot,
    h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '刷新')
  );

  const callSlot = h('div', { class: 'card-body tight' });
  const callCard = card('调用记录', callSlot,
    h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadCalls() }, '刷新')
  );

  container.append(errorSlot, callCard, listCard, detailSlot);

  /* ---------- 调用记录 ---------- */
  // 内置工具每次调用都落一条，含参数和返回。
  // 没有这一屏，「她到底调没调工具、拿回了什么」只能翻聊天记录猜。
  function paintCalls(items) {
    if (!items || items.length === 0) {
      callSlot.replaceChildren(h('div', { class: 'empty' },
        '还没有工具调用记录。她在聊天里用到工具时，这里会出现每次调用的参数和返回。'));
      return;
    }

    callSlot.replaceChildren(...items.map((it) => {
      const ok = it.ok ? badge('成功', 'ok') : badge('失败', 'warn');
      const head = h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, fmtTime(it.ts)),
        h('span', { class: 'stage-val' }, h('code', {}, it.tool || '-')),
        h('span', { class: 'tiny muted' }, (it.ms || 0) + 'ms'),
        ok);

      const detail = h('div', { style: 'display:none;margin:4px 0 8px 12px' },
        h('div', { class: 'tiny muted' }, '场景：' + (it.scope || '-')),
        h('div', { class: 'tiny' }, '参数：' + (it.args || '-')),
        h('div', { class: 'tiny' }, '返回：' + (it.result || '-')));

      return h('div', { style: 'border-bottom:1px solid var(--line-soft)' },
        h('div', { style: 'cursor:pointer', onclick: () => {
          detail.style.display = detail.style.display === 'none' ? 'block' : 'none';
        } }, head),
        detail);
    }));
  }

  async function loadCalls() {
    try {
      const r = await api('/api/tool/trace');
      if (disposed) return;
      paintCalls(r.items || []);
    } catch (err) {
      if (disposed) return;
      callSlot.replaceChildren(h('div', { class: 'empty' }, '读不到调用记录：' + err.message));
    }
  }

  /* ---------- 列表 ---------- */
  function paintList() {
    const tools = state.tools || [];
    if (!tools.length) {
      tableWrap.replaceChildren(emptyState('暂无工具'));
      return;
    }
    const tbody = h('tbody');
    for (const t of tools) {
      const tool = t || {};
      tbody.append(h('tr', {
        class: 'clickable' + (state.expanded === tool.name ? ' row-open' : ''),
        onclick: (e) => {
          if (e.target.closest('button')) return; /* 操作按钮不触发展开 */
          toggleDetail(tool.name);
        }
      },
        h('td', { class: 'cell-mono' }, tool.name || '-'),
        h('td', {}, statusBadge(tool.status)),
        h('td', { class: 'num' }, String(tool.version ?? '-')),
        h('td', { class: 'cell-text' }, tool.description || '-'),
        h('td', { class: 'cell-text tiny' }, tool.last_error
          ? h('span', { class: 'muted' }, tool.last_error)
          : h('span', { class: 'tiny muted' }, '无')),
        h('td', { class: 'actions' },
          h('button', {
            class: 'btn btn-ok btn-sm', type: 'button',
            disabled: tool.status === 'active',
            onclick: () => setStatus(tool.name, 'enable')
          }, '启用'),
          h('button', {
            class: 'btn btn-danger btn-sm', type: 'button',
            disabled: tool.status !== 'active',
            onclick: () => setStatus(tool.name, 'disable')
          }, '禁用'),
          h('button', {
            class: 'btn btn-sm', type: 'button',
            onclick: () => openRunDialog(tool)
          }, '试跑')
        )
      ));
    }
    tableWrap.replaceChildren(
      h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '名称'), h('th', {}, '状态'), h('th', {}, '版本'),
          h('th', {}, '描述'), h('th', {}, '最近错误'), h('th', {}, '操作')
        )),
        tbody
      )
    );
  }

  /* ---------- 详情 ---------- */
  function toggleDetail(name) {
    if (state.expanded === name) {
      state.expanded = null;
      state.detail = null;
      detailSlot.replaceChildren();
      paintList();
      return;
    }
    state.expanded = name;
    state.detail = null;
    paintList();
    detailSlot.replaceChildren(h('div', { class: 'loading' }, '正在加载 ' + name + ' 的契约与源码'));
    loadDetail(name);
  }

  async function loadDetail(name) {
    state.loadingDetail = true;
    try {
      const res = await api('/api/tools/' + encodeURIComponent(name));
      if (disposed || state.expanded !== name) return;
      state.detail = res || {};
      paintDetail(name);
    } catch (err) {
      if (disposed || state.expanded !== name) return;
      if (err.unimplemented) { detailSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      detailSlot.replaceChildren(h('div', { class: 'note note-danger' }, '详情加载失败：' + err.message));
    } finally {
      state.loadingDetail = false;
    }
  }

  function paintDetail(name) {
    const d = state.detail || {};
    const metaNode = d.meta && Object.keys(d.meta).length
      ? h('pre', { class: 'pre', style: 'max-height:180px' }, JSON.stringify(d.meta, null, 2))
      : h('div', { class: 'tiny muted' }, '（meta 为空）');

    detailSlot.replaceChildren(
      h('div', { class: 'grid-2' },
        card('契约 tool.md', h('pre', { class: 'pre' }, d.contract || '（空）'),
          h('div', { class: 'row' },
            badge(name, 'accent'),
            h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadDetail(name) }, '刷新')
          )
        ),
        card('meta.json', metaNode)
      ),
      card('源码 main.py', codeBlock(d.code))
    );
  }

  /* ---------- 启用 / 禁用 ---------- */
  async function setStatus(name, action) {
    try {
      const res = await api('/api/tools/' + encodeURIComponent(name) + '/' + action, { method: 'POST', body: '{}' });
      if (disposed) return;
      toast(name + ' 已' + (action === 'enable' ? '启用' : '禁用') +
        (res && res.status ? '（' + res.status + '）' : ''), 'ok');
      await load();
      if (state.expanded === name) loadDetail(name);
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('操作失败：' + err.message, 'danger');
    }
  }

  /* ---------- 试跑 ---------- */
  function openRunDialog(tool) {
    const ta = h('textarea', {
      class: 'textarea', rows: '8', spellcheck: 'false',
      style: 'font-family:monospace;width:100%',
      placeholder: '{ }'
    });
    ta.value = '{}';
    const result = h('div', {});
    const runBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button' }, '执行');

    const modal = openModal({
      title: '试跑 ' + (tool.name || ''),
      body: [
        h('div', { class: 'field' },
          h('span', { class: 'field-label' }, '参数（JSON 对象，对应 args 字段）'),
          ta
        ),
        result
      ],
      footer: [runBtn]
    });

    runBtn.addEventListener('click', async () => {
      let args;
      try {
        args = JSON.parse(ta.value.trim() || '{}');
      } catch (e) {
        toast('参数不是合法 JSON', 'danger');
        return;
      }
      if (args === null || typeof args !== 'object' || Array.isArray(args)) {
        toast('参数必须是 JSON 对象', 'danger');
        return;
      }
      runBtn.disabled = true;
      result.replaceChildren(h('div', { class: 'loading' }, '执行中'));
      try {
        const res = await api('/api/tools/' + encodeURIComponent(tool.name) + '/run', {
          method: 'POST', body: JSON.stringify({ args })
        });
        const ok = !!(res && res.ok);
        const dataText = res && res.data !== undefined
          ? (typeof res.data === 'string' ? res.data : JSON.stringify(res.data, null, 2))
          : '';
        result.replaceChildren(
          h('div', { class: 'row' },
            ok ? badge('执行成功', 'ok') : badge('执行失败', 'danger'),
            h('span', { class: 'tiny muted' }, '耗时 ' + (res && res.ms !== undefined ? res.ms : '-') + ' ms')
          ),
          res && res.error ? h('div', { class: 'note note-danger' }, '错误：' + res.error) : null,
          dataText ? h('pre', { class: 'pre' }, dataText) : h('div', { class: 'tiny muted' }, '（无返回值）')
        );
      } catch (err) {
        if (err.unimplemented) { result.replaceChildren(unimplementedNote()); return; }
        if (err.status === 401) { modal.close(); return; }
        result.replaceChildren(h('div', { class: 'note note-danger' }, '请求失败：' + err.message));
      } finally {
        runBtn.disabled = false;
      }
    });
  }

  /* ---------- 载入 ---------- */
  async function load() {
    try {
      const res = await api('/api/tools');
      if (disposed) return;
      errorSlot.replaceChildren();
      state.tools = Array.isArray(res) ? res : (res && Array.isArray(res.items) ? res.items : []);
      paintList();
    } catch (err) {
      if (disposed) return;
      state.tools = [];
      paintList();
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '工具列表读取失败：' + err.message));
    }
  }

  tableWrap.replaceChildren(h('div', { class: 'loading' }, '正在加载'));
  load();
  loadCalls();

  return function cleanup() { disposed = true; };
}
