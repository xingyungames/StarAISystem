/* ============================================================
   沙箱页：浏览 / 预览 / 编辑 / 删除 AI 的文件工作区
   与内置工具 sb_* 共用同一个沙箱根，路径越界会被后端拒绝
   ============================================================ */

import { h, card, badge, fmtTime, toast, emptyState, qs } from '../app.js';

function humanSize(n) {
  const v = Number(n) || 0;
  if (v < 1024) return v + ' B';
  if (v < 1024 * 1024) return (v / 1024).toFixed(1) + ' KB';
  return (v / 1024 / 1024).toFixed(1) + ' MB';
}

export function render(container, api) {
  let disposed = false;

  const state = { path: '', parent: null, items: [], root: '' };

  const pathLabel = h('span', { class: 'mono' }, '/');
  const listHost = h('div', {});
  const editorHost = h('div', {});

  const upBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => goUp() }, '上一级');
  const refreshBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load(state.path) }, '刷新');
  const rootBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load('') }, '回到根目录');
  const mkdirBtn = h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => makeDir() }, '新建目录');
  const newFileBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => openEditor('', '') }, '新建文件');

  container.append(
    card('沙箱文件', [
      h('div', { class: 'note' },
        '这里就是 AI 的文件工作区。内置工具 sb_list / sb_read / sb_write / sb_delete / sb_move ',
        '只能在这个目录里活动，`../` 和绝对路径会被拒绝。'),
      h('div', { class: 'toolbar-row' }, rootBtn, upBtn, refreshBtn, mkdirBtn, newFileBtn,
        h('span', { class: 'tiny muted' }, '当前位置：'), pathLabel),
      listHost,
      editorHost
    ], h('span', { class: 'tiny muted' }, '根目录见下方'))
  );

  async function load(path) {
    try {
      const res = await api('/api/sandbox' + qs({ path: path || '' }));
      if (disposed) return;
      state.path = res.path || '';
      state.parent = res.parent;
      state.items = res.items || [];
      state.root = res.root || '';
      paintList();
    } catch (err) {
      if (disposed || err.status === 401) return;
      listHost.replaceChildren(h('div', { class: 'note note-danger' }, '读取失败：' + err.message));
    }
  }

  function goUp() {
    if (state.parent === null || state.parent === undefined) { toast('已经在根目录了', 'warn'); return; }
    load(state.parent);
  }

  function paintList() {
    pathLabel.textContent = '/' + state.path;

    if (state.items.length === 0) {
      listHost.replaceChildren(emptyState('这个目录是空的。用「新建文件」放点东西进来，或者让 AI 自己写。'));
      return;
    }

    const rows = state.items.map((it) => h('tr', {},
      h('td', {},
        it.is_dir
          ? h('span', { class: 'mono', style: 'cursor:pointer;color:var(--accent)' , onclick: () => load(it.path) }, '[目录] ' + it.name)
          : h('span', { class: 'mono', style: 'cursor:pointer', onclick: () => openFile(it.path) }, it.name)
      ),
      h('td', { class: 'tiny muted' }, it.is_dir ? '-' : humanSize(it.size)),
      h('td', { class: 'tiny muted' }, fmtTime(it.modified_at || 0)),
      h('td', {},
        it.is_dir ? null : h('button', {
          class: 'btn btn-ghost btn-sm', type: 'button', onclick: (e) => { e.stopPropagation(); openFile(it.path); },
        }, '编辑'),
        h('button', {
          class: 'btn btn-danger btn-sm', type: 'button', style: 'margin-left:6px',
          onclick: (e) => { e.stopPropagation(); remove(it.path, it.is_dir); },
        }, '删除')
      )
    ));

    listHost.replaceChildren(
      h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '名称'), h('th', {}, '大小'), h('th', {}, '修改时间'), h('th', {}, '操作')
        )),
        h('tbody', {}, ...rows)
      )),
      h('div', { class: 'tiny muted', style: 'margin-top:8px' }, '根目录：' + (state.root || '-'))
    );
  }

  async function openFile(path) {
    try {
      const res = await api('/api/sandbox/file' + qs({ path }));
      if (disposed) return;
      openEditor(path, res.text || '', res.truncated, res.size);
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('读取失败：' + err.message, 'danger');
    }
  }

  function openEditor(path, text, truncated, size) {
    const pathInput = h('input', { class: 'input', value: path, placeholder: '相对沙箱的路径，例如 notes/todo.md' });
    const area = h('textarea', { class: 'textarea', rows: '16', style: 'font-family:monospace' });
    area.value = text || '';

    const saveBtn = h('button', {
      class: 'btn btn-primary btn-sm', type: 'button',
      onclick: async () => {
        const p = pathInput.value.trim();
        if (!p) { toast('路径不能为空', 'warn'); return; }
        saveBtn.disabled = true;
        try {
          const res = await api('/api/sandbox/file', {
            method: 'PUT',
            body: JSON.stringify({ path: p, text: area.value }),
          });
          if (disposed) return;
          toast('已保存（' + humanSize(res.size) + '）', 'ok');
          load(state.path);
        } catch (err) {
          if (disposed || err.status === 401) return;
          toast('保存失败：' + err.message, 'danger');
        } finally {
          saveBtn.disabled = false;
        }
      },
    }, '保存');

    const closeBtn = h('button', {
      class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => editorHost.replaceChildren(),
    }, '关闭');

    editorHost.replaceChildren(
      card(path ? '编辑：' + path : '新建文件', [
        truncated ? h('div', { class: 'note note-warn' },
          '文件较大（' + humanSize(size) + '），只显示了前 256KB。直接保存会把截断后的内容写回去，注意。') : null,
        h('div', { class: 'field' },
          h('label', { class: 'field-label' }, '路径'), pathInput),
        h('div', { class: 'field' },
          h('label', { class: 'field-label' }, '内容'), area),
        h('div', { class: 'toolbar-row' }, saveBtn, closeBtn)
      ])
    );
  }

  async function remove(path, isDir) {
    if (!window.confirm('确认删除 ' + path + ' ?')) return;
    try {
      await api('/api/sandbox' + qs({ path }), { method: 'DELETE' });
      if (disposed) return;
      toast('已删除', 'ok');
      load(state.path);
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('删除失败：' + err.message, 'danger');
    }
  }

  async function makeDir() {
    const name = window.prompt('新目录名（可带路径，如 notes/daily）');
    if (!name) return;
    const full = state.path ? state.path + '/' + name : name;
    try {
      await api('/api/sandbox/mkdir', { method: 'POST', body: JSON.stringify({ path: full }) });
      if (disposed) return;
      toast('已创建', 'ok');
      load(state.path);
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('创建失败：' + err.message, 'danger');
    }
  }

  load('');

  return () => { disposed = true; };
}
