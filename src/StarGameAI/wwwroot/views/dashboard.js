/* ============================================================
   仪表盘：指标卡 + 模型 / 水位线 / 最近一次巩固，10 秒轮询
   ============================================================ */

import {
  h, card, badge, fmtTime, fmtDuration, kvRow, unimplementedNote, setTopbarNote
} from '../app.js';

const POLL_MS = 10000;

/** 数值千分位 */
function fmtNum(n) {
  const v = Number(n);
  if (!Number.isFinite(v)) return '0';
  return v.toLocaleString('zh-CN');
}

/** 指标卡 */
function metricCard(label, valueNode, footText, tone, small) {
  return h('div', { class: 'metric' + (tone ? ' tone-' + tone : '') },
    h('div', { class: 'metric-label' }, label),
    h('div', { class: 'metric-value' + (small ? ' small' : '') }, valueNode),
    footText ? h('div', { class: 'metric-foot' }, footText) : null
  );
}

export function render(container, api) {
  let timer = null;
  let disposed = false;
  let failure = null;

  /* ---------- 骨架 ---------- */
  const refreshInfo = h('span', { class: 'tiny muted' }, '最近刷新：-');

  const metricsEl = h('div', { class: 'metrics' });
  const modelBody = h('div', { class: 'kv' });
  const watermarkBody = h('div', { class: 'kv' });
  const sleepBody = h('div', {});
  const errorSlot = h('div', {});

  container.append(
    h('div', { class: 'toolbar' },
      h('button', { class: 'btn btn-primary btn-sm', type: 'button', onclick: () => refresh() }, '刷新数据'),
      refreshInfo,
      h('div', { class: 'spacer' }),
      badge('每 10 秒自动刷新', 'accent')
    ),
    errorSlot,
    metricsEl,
    h('div', { class: 'grid-3' },
      card('模型', modelBody),
      card('水位线', watermarkBody),
      card('最近一次巩固', sleepBody)
    )
  );

  /* ---------- 渲染 ---------- */
  function paint(status) {
    const s = status || {};
    const today = s.today || {};
    const model = s.model || {};
    const wm = s.watermark || {};
    const sleep = s.last_sleep || null;
    const online = !!s.online;
    const napcat = !!s.napcat_online;

    /* 指标卡 */
    metricsEl.replaceChildren(
      metricCard('NapCat 在线状态',
        h('span', { class: 'row' },
          h('span', { class: 'dot ' + (napcat ? 'ok' : 'danger') }),
          h('span', {}, napcat ? '在线' : '离线')
        ),
        'self_id ' + (s.self_id || '-'),
        napcat ? 'ok' : 'danger'
      ),
      metricCard('今日消息数', fmtNum(today.messages), '入站消息条数'),
      metricCard('今日回复数', fmtNum(today.replies), '出站发言条数'),
      metricCard('今日静默数', fmtNum(today.gate_skips), '发言门跳过次数', 'warn'),
      metricCard('运行时长', fmtDuration(s.uptime_s), '服务' + (online ? '在线' : '离线'), online ? 'ok' : 'danger', true)
    );

    /* 模型 */
    modelBody.replaceChildren(
      kvRow('模型名', model.name || '-'),
      kvRow('base_url', h('span', { class: 'mono tiny' }, model.base_url || '-')),
      kvRow('密钥就位', model.key_present ? badge('已就位', 'ok') : badge('缺失', 'danger'))
    );

    /* 水位线 */
    watermarkBody.replaceChildren(
      kvRow('最后消费日志时间', fmtTime(wm.last_consumed_log_ts)),
      kvRow('原始时间戳', h('span', { class: 'mono tiny' }, String(wm.last_consumed_log_ts || 0))),
      kvRow('说明', h('span', { class: 'tiny muted' }, '睡眠巩固只消费水位线之后的新日志'))
    );

    /* 最近一次巩固 */
    if (sleep && (sleep.at || sleep.rev)) {
      sleepBody.replaceChildren(
        h('div', { class: 'kv' },
          kvRow('发生时间', fmtTime(sleep.at)),
          kvRow('人设版本', 'rev ' + (sleep.rev ?? '-')),
          kvRow('结果', sleep.ok ? badge('成功', 'ok') : badge('失败', 'danger'))
        )
      );
    } else {
      sleepBody.replaceChildren(h('div', { class: 'empty' }, '暂无巩固记录'));
    }
  }

  /* ---------- 取数 ---------- */
  async function refresh() {
    try {
      const data = await api('/api/status');
      if (disposed) return;
      failure = null;
      errorSlot.replaceChildren();
      paint(data);
      refreshInfo.textContent = '最近刷新：' + new Date().toLocaleString('zh-CN');
    } catch (err) {
      if (disposed) return;
      stopPoll();
      if (err.unimplemented) {
        errorSlot.replaceChildren(unimplementedNote());
        return;
      }
      if (err.status === 401) return; /* 已切回登录框 */
      failure = err.message;
      errorSlot.replaceChildren(
        h('div', { class: 'note note-danger' }, '状态获取失败：' + failure)
      );
      refreshInfo.textContent = '最近刷新：失败';
    }
  }

  function startPoll() {
    stopPoll();
    timer = setInterval(refresh, POLL_MS);
  }
  function stopPoll() {
    if (timer) { clearInterval(timer); timer = null; }
  }

  setTopbarNote('数据源 /api/status');
  refresh().finally(() => { if (!disposed) startPoll(); });

  return function cleanup() {
    disposed = true;
    stopPoll();
  };
}
