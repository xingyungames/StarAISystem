/* ============================================================
   人设页：分段列表 + 整份 md 编辑 + 版本历史 / diff / 回滚
   ============================================================ */

import {
  h, card, badge, fmtTime, toast, unimplementedNote
} from '../app.js';

const th = (t) => h('th', {}, t);

/** 把纯文本 diff 逐行渲染到 pre，+ 绿色 / - 红色 */
function renderDiff(text) {
  const pre = h('pre', { class: 'diff' });
  const lines = String(text == null ? '' : text).split('\n');
  if (!lines.length || (lines.length === 1 && lines[0] === '')) {
    pre.append(h('div', { class: 'diff-line diff-ctx' }, '（没有差异内容）'));
    return pre;
  }
  for (const line of lines) {
    let cls = 'diff-ctx';
    if (/^\+/.test(line)) cls = 'diff-add';
    else if (/^-/.test(line)) cls = 'diff-del';
    pre.append(h('div', { class: 'diff-line ' + cls }, line === '' ? ' ' : line));
  }
  return pre;
}

export function render(container, api) {
  let disposed = false;
  let data = null;
  let originalMd = '';

  /* ---------- 左侧：分段 ---------- */
  const segListEl = h('div', { class: 'seg-list' });
  const segCard = card('分段列表', segListEl,
    h('span', { class: 'tiny muted' }, '按 <!--seg:key--> 切分'));

  /* ---------- 右侧：编辑器 ---------- */
  const ta = h('textarea', {
    class: 'editor',
    spellcheck: 'false',
    placeholder: '人设正文（markdown）'
  });
  const revInfo = h('span', { class: 'tiny muted' }, '当前版本 rev -');
  const budgetInfo = h('span', { class: 'tiny muted' }, '今日修订预算 -');
  const dirtyBadge = badge('无改动', 'muted');
  const saveBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => save() }, '保存');
  const reloadBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '重新加载');

  const editorCard = card('编辑整份人设（markdown）',
    h('div', { class: 'col' },
      h('div', { class: 'row' }, revInfo, budgetInfo, dirtyBadge),
      ta,
      h('div', { class: 'tiny muted' }, '提示：<!--seg:xxx--> 与 <!--/seg:xxx--> 必须成对出现；不可编辑的分段保存后仍会被后端校验。')
    ),
    h('div', { class: 'card-extra' }, reloadBtn, saveBtn)
  );

  /* ---------- 版本历史 ---------- */
  const historyBody = h('div', {});
  const diffSlot = h('div', { class: 'mt-12' });
  const historyCard = card('版本历史', [historyBody, diffSlot]);

  const errorSlot = h('div', {});

  container.append(
    errorSlot,
    h('div', { class: 'split-persona' }, segCard, editorCard),
    historyCard
  );

  /* ---------- 渲染：分段列表 ---------- */
  function paintSegments() {
    const segments = Array.isArray(data && data.segments) ? data.segments : [];
    if (!segments.length) {
      segListEl.replaceChildren(h('div', { class: 'empty' }, '未解析到分段'));
      return;
    }
    segListEl.replaceChildren(...segments.map((s) => {
      const editable = s.editable !== false;
      return h('button', {
        class: 'seg-item',
        type: 'button',
        title: '点击定位到该分段',
        onclick: () => focusSegment(s.key)
      },
        h('div', { class: 'seg-top' },
          h('span', { class: 'seg-key' }, s.key || '-'),
          h('span', { class: 'seg-title' }, s.title || '')
        ),
        h('div', { class: 'seg-meta' },
          editable ? badge('可编辑', 'accent') : badge('AI 不可写', 'warn'),
          s.hash ? h('span', { class: 'seg-hash' }, String(s.hash).slice(0, 8)) : null
        )
      );
    }));
  }

  /* ---------- 渲染：版本与预算 ---------- */
  function paintMeta() {
    const meta = (data && data.meta) || {};
    const budget = meta.budget || {};
    revInfo.textContent = '当前版本 rev ' + (meta.rev ?? '-') +
      (meta.updated_at ? '（' + fmtTime(meta.updated_at) + ' 由 ' + (meta.by || '未知') + ' 修改）' : '');
    budgetInfo.textContent = '今日修订预算 ' +
      (budget.used_today ?? '-') + '/' + (budget.limit_per_day ?? '-') +
      (budget.reset_on ? '（' + budget.reset_on + ' 重置）' : '');
  }

  /* ---------- 渲染：历史 ---------- */
  function paintHistory(list) {
    if (!Array.isArray(list) || !list.length) {
      historyBody.replaceChildren(h('div', { class: 'empty' }, '暂无版本历史'));
      return;
    }
    const tbody = h('tbody');
    for (const r of list) {
      tbody.append(h('tr', {},
        h('td', { class: 'num' }, 'rev ' + (r.rev ?? '-')),
        h('td', {}, fmtTime(r.updated_at)),
        h('td', { class: 'mono tiny' }, r.by || '-'),
        h('td', { class: 'actions' },
          h('button', {
            class: 'btn btn-ghost btn-sm', type: 'button',
            onclick: () => loadDiff(r.rev)
          }, '查看 diff'),
          h('button', {
            class: 'btn btn-warn btn-sm', type: 'button',
            onclick: () => rollback(r.rev)
          }, '回滚')
        )
      ));
    }
    historyBody.replaceChildren(
      h('div', { class: 'table-wrap' },
        h('table', { class: 'table' },
          h('thead', {}, h('tr', {}, th('版本'), th('时间'), th('来源'), th('操作'))),
          tbody
        )
      )
    );
  }

  /* ---------- 分段定位 ---------- */
  function focusSegment(key) {
    const md = ta.value;
    const open = '<!--seg:' + key + '-->';
    const close = '<!--/seg:' + key + '-->';
    const start = md.indexOf(open);
    if (start < 0) {
      toast('正文中未找到分段标记 ' + open, 'warn');
      return;
    }
    const endIdx = md.indexOf(close, start);
    const end = endIdx < 0 ? md.length : endIdx + close.length;
    ta.focus();
    ta.setSelectionRange(start, end);
    ta.scrollTop = Math.max(0, (start / Math.max(1, md.length)) * ta.scrollHeight - 40);
  }

  /* ---------- 脏检查 ---------- */
  function updateDirty() {
    const dirty = ta.value !== originalMd;
    dirtyBadge.textContent = dirty ? '有未保存修改' : '无改动';
    dirtyBadge.className = 'badge badge-' + (dirty ? 'warn' : 'muted');
    saveBtn.disabled = !dirty;
  }
  ta.addEventListener('input', updateDirty);

  /* ---------- 保存 ---------- */
  async function save() {
    const md = ta.value;
    /* 前端拦截：分段标记必须成对 */
    const openCount = (md.match(/<!--\s*seg:/g) || []).length;
    const closeCount = (md.match(/<!--\s*\/seg:/g) || []).length;
    if (openCount !== closeCount) {
      toast('分段标记不完整（开始 ' + openCount + ' 个，结束 ' + closeCount + ' 个）', 'danger');
      return;
    }
    saveBtn.disabled = true;
    try {
      const res = await api('/api/persona', { method: 'PUT', body: JSON.stringify({ md }) });
      if (disposed) return;
      originalMd = md;
      updateDirty();
      toast('保存成功，当前 rev ' + (res && res.rev !== undefined ? res.rev : '-'), 'ok');
      await load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('保存失败：' + err.message, 'danger');
      updateDirty();
    }
  }

  /* ---------- diff ---------- */
  async function loadDiff(rev) {
    try {
      const res = await api('/api/persona/diff?rev=' + encodeURIComponent(rev));
      if (disposed) return;
      diffSlot.replaceChildren(
        h('div', { class: 'row mt-8' },
          h('strong', { class: 'tiny' }, 'rev ' + (res && res.rev !== undefined ? res.rev : rev) + ' 的差异'),
          h('div', { class: 'spacer', style: 'flex:1 1 auto' }),
          h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => diffSlot.replaceChildren() }, '收起')
        ),
        renderDiff(res && res.diff)
      );
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { diffSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('获取 diff 失败：' + err.message, 'danger');
    }
  }

  /* ---------- 回滚 ---------- */
  async function rollback(rev) {
    if (!window.confirm('确认回滚到 rev ' + rev + ' 吗？该操作会生成一个新版本。')) return;
    try {
      const res = await api('/api/persona/rollback/' + encodeURIComponent(rev), { method: 'POST', body: '{}' });
      if (disposed) return;
      toast('已回滚，当前 rev ' + (res && res.rev !== undefined ? res.rev : '-'), 'ok');
      await load();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) toast('回滚失败：' + err.message, 'danger');
    }
  }

  /* ---------- 载入 ---------- */
  async function load() {
    try {
      data = await api('/api/persona');
      if (disposed) return;
      errorSlot.replaceChildren();
      originalMd = (data && data.md) || '';
      ta.value = originalMd;
      paintSegments();
      paintMeta();
      updateDirty();
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { errorSlot.replaceChildren(unimplementedNote()); return; }
      if (err.status !== 401) {
        errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '人设读取失败：' + err.message));
      }
      return;
    }
    try {
      const list = await api('/api/persona/history');
      if (disposed) return;
      paintHistory(list);
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) historyBody.replaceChildren(unimplementedNote());
      else if (err.status !== 401) historyBody.replaceChildren(h('div', { class: 'note note-danger' }, '版本历史读取失败：' + err.message));
    }
  }

  updateDirty();
  load();

  return function cleanup() { disposed = true; };
}
