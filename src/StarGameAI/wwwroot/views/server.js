/* ============================================================
   服务器页：NapCat 状态 / 发送测试 / 配置只读视图 / 边界说明
   ============================================================ */

import {
  h, card, badge, fmtDuration, fmtTime, toast, unimplementedNote, kvRow
} from '../app.js';

export function render(container, api) {
  let disposed = false;

  /* ---------- NapCat ---------- */
  const napcatBody = h('div', {});

  /* ---------- 发送测试 ---------- */
  const scopeInput = h('input', {
    class: 'input', type: 'text', placeholder: 'group:100001', style: 'min-width:190px'
  });
  const textInput = h('input', {
    class: 'input', type: 'text', placeholder: '要发送的文本', style: 'flex:1 1 260px;min-width:200px'
  });
  const sendBtn = h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => sendTest() }, '发送');
  const sendResult = h('div', {});

  const sendCard = card('发送测试',
    h('div', { class: 'col' },
      h('div', { class: 'row' },
        h('span', { class: 'tiny muted' }, 'scope'),
        scopeInput,
        textInput,
        sendBtn
      ),
      sendResult,
      h('div', { class: 'tiny muted' }, 'scope 形如 group:<群号> 或 private:<QQ号>；发送成功后返回 message_id。')
    )
  );

  /* ---------- 只读配置 ---------- */
  const configBody = h('pre', { class: 'pre' }, '（正在加载）');
  const configCard = card('配置只读视图', configBody,
    h('span', { class: 'tiny muted' }, '来源于 GET /api/status'));

  /* ---------- 边界说明 ---------- */
  const infoCard = card('运行边界',
    h('div', { class: 'col' },
      h('div', { class: 'kv' },
        kvRow('监听地址', '仅 127.0.0.1（非本机访问直接 403）'),
        kvRow('发送限速', '每群每秒 1 条；每用户每分钟 6 条；每用户每天 300 条'),
        kvRow('回复长度', '单条最多 1500 字，最多 3 段'),
        kvRow('管理接口', '/api/* 需要 Authorization: Bearer <admin token>')
      ),
      h('div', { class: 'note' }, '本服务只监听 127.0.0.1，后端按群限速为每秒 1 条；本页所有操作均通过本机回环接口完成。')
    )
  );

  const errorSlot = h('div', {});

  /* ---------- 白名单 ---------- */
  const groupsArea = h('textarea', { class: 'textarea', rows: '5', placeholder: '每行一个群号' });
  const privateArea = h('textarea', { class: 'textarea', rows: '5', placeholder: '每行一个 QQ 号' });
  const unknownSelect = h('select', { class: 'select' },
    h('option', { value: 'silent' }, '静默（不回）'),
    h('option', { value: 'hint' }, '回一句提示')
  );
  const whitelistResult = h('div', {});
  const saveWhitelistBtn = h('button', {
    class: 'btn btn-primary btn-sm', type: 'button', onclick: () => saveWhitelist(),
  }, '保存白名单');

  const whitelistCard = card('白名单', [
    h('div', { class: 'note' },
      '两个名单都留空 = 不设限，任何群、任何人的私聊都会接。',
      ' 改动立即生效，不用重启。只接受纯数字，其他字符会被丢弃。'),
    h('div', { class: 'grid-2' },
      h('div', { class: 'field' },
        h('label', { class: 'field-label' }, '群号（每行一个）'), groupsArea),
      h('div', { class: 'field' },
        h('label', { class: 'field-label' }, 'QQ 号（私聊白名单，每行一个）'), privateArea)
    ),
    h('div', { class: 'field' },
      h('label', { class: 'field-label' }, '不在名单里的群/人'),
      unknownSelect,
      h('div', { class: 'field-hint' }, '目前无论选哪个都只记日志不回复，名单外一律不处理')
    ),
    h('div', { class: 'toolbar-row' }, saveWhitelistBtn),
    whitelistResult
  ], h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadWhitelist() }, '重新读取'));

  container.append(
    errorSlot,
    card('NapCat', napcatBody, h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadStatus() }, '刷新')),
    whitelistCard,
    sendCard,
    h('div', { class: 'grid-2' }, configCard, infoCard)
  );

  /* ---------- 白名单读写 ---------- */
  function fillWhitelist(w) {
    groupsArea.value = ((w && w.groups) || []).join('\n');
    privateArea.value = ((w && (w.private_ || w.private)) || []).join('\n');
    unknownSelect.value = (w && w.unknown_action) || 'silent';
  }

  function linesOf(area) {
    return area.value.split('\n').map((s) => s.trim()).filter(Boolean);
  }

  async function loadWhitelist() {
    try {
      const w = await api('/api/whitelist');
      if (disposed) return;
      fillWhitelist(w);
      whitelistResult.replaceChildren();
    } catch (err) {
      if (disposed || err.status === 401) return;
      whitelistResult.replaceChildren(
        h('div', { class: 'note note-danger' }, '白名单读取失败：' + err.message));
    }
  }

  async function saveWhitelist() {
    const groups = linesOf(groupsArea);
    const privateList = linesOf(privateArea);

    const bad = groups.concat(privateList).filter((v) => !/^\d+$/.test(v));
    if (bad.length > 0) {
      toast('这些不是纯数字：' + bad.slice(0, 3).join('、'), 'warn');
      return;
    }

    saveWhitelistBtn.disabled = true;
    try {
      const res = await api('/api/whitelist', {
        method: 'PUT',
        body: JSON.stringify({ groups, private_: privateList, unknown_action: unknownSelect.value }),
      });
      if (disposed) return;
      fillWhitelist(res);
      toast('白名单已保存，立即生效', 'ok');
      whitelistResult.replaceChildren(
        h('div', { class: 'note note-ok' },
          '已保存：群 ' + groups.length + ' 个，私聊 ' + privateList.length + ' 个'));
    } catch (err) {
      if (disposed || err.status === 401) return;
      whitelistResult.replaceChildren(
        h('div', { class: 'note note-danger' }, '保存失败：' + err.message));
    } finally {
      saveWhitelistBtn.disabled = false;
    }
  }

  /* ---------- 渲染 NapCat 区块 ---------- */
  function paintStatus(s) {
    const status = s || {};
    const napcat = !!status.napcat_online;
    const np = status.napcat || {};

    napcatBody.replaceChildren(
      h('div', { class: 'kv' },
        kvRow('在线状态', h('span', { class: 'row' },
          h('span', { class: 'dot ' + (napcat ? 'ok' : 'danger') }),
          h('span', {}, napcat ? '在线' : '离线'),
          napcat ? badge('可发送', 'ok') : badge('不可发送', 'danger')
        )),
        kvRow('探测延迟', np.latency_ms ? np.latency_ms + ' ms' : '-'),
        kvRow('上次探测', np.checked_at ? fmtTime(np.checked_at) : '-'),
        kvRow('探测错误', np.last_error || '无'),
        kvRow('self_id', h('span', { class: 'mono' }, status.self_id || '-')),
        kvRow('服务状态', status.online ? badge('在线', 'ok') : badge('离线', 'danger')),
        kvRow('运行时长', fmtDuration(status.uptime_s)),
        kvRow('今日统计', '消息 ' + ((status.today || {}).messages ?? 0) +
          ' 条 / 回复 ' + ((status.today || {}).replies ?? 0) +
          ' 条 / 静默 ' + ((status.today || {}).gate_skips ?? 0) + ' 次')
      )
    );
    configBody.textContent = JSON.stringify({
      model: status.model || null,
      napcat: status.napcat || null,
      watermark: status.watermark || null
    }, null, 2);
  }

  async function loadStatus() {
    try {
      const s = await api('/api/status');
      if (disposed) return;
      errorSlot.replaceChildren();
      paintStatus(s);
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) {
        errorSlot.replaceChildren(unimplementedNote());
        configBody.textContent = '该功能尚未实现（P0 骨架）';
        return;
      }
      if (err.status === 401) return;
      errorSlot.replaceChildren(h('div', { class: 'note note-danger' }, '状态读取失败：' + err.message));
      configBody.textContent = '状态读取失败';
    }
  }

  /* ---------- 发送测试 ---------- */
  async function sendTest() {
    const scope = scopeInput.value.trim();
    const text = textInput.value.trim();
    if (!scope || !text) {
      toast('scope 与文本都不能为空', 'warn');
      return;
    }
    sendBtn.disabled = true;
    sendBtn.textContent = '发送中';
    try {
      const res = await api('/api/napcat/send-test', {
        method: 'POST', body: JSON.stringify({ scope, text })
      });
      if (disposed) return;
      sendResult.replaceChildren(
        h('div', { class: 'note note-ok' },
          '发送结果：' + (res && res.ok ? '成功' : '失败') +
          '，message_id ' + (res && res.message_id ? res.message_id : '-'))
      );
    } catch (err) {
      if (disposed) return;
      if (err.unimplemented) { sendResult.replaceChildren(unimplementedNote()); return; }
      if (err.status === 401) return;
      sendResult.replaceChildren(h('div', { class: 'note note-danger' }, '发送失败：' + err.message));
    } finally {
      sendBtn.disabled = false;
      sendBtn.textContent = '发送';
    }
  }

  loadStatus();
  loadWhitelist();

  return function cleanup() { disposed = true; };
}
