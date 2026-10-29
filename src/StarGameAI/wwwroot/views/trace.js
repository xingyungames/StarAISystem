/* ============================================================
   神经活动页：上半是神经元拓扑（今日汇总），下半是消息瀑布（单条解剖）
   数据源：/api/neurons/stats 与 /api/trace/recent
   ============================================================ */

import { h, card, badge, fmtTime, toast, emptyState, todayStr } from '../app.js';

const NEURONS = [
  { key: 'gate', label: '发言门', cls: 'n-gate' },
  { key: 'recall', label: '记忆元', cls: 'n-recall' },
  /* 思想层的 span 名是 idea，不是 think。写成 think 会让瀑布图里的思想决策显示成 "-"，
     而且落不到 n-think 的颜色上、退化成发言门那根蓝条——明明是完全不同的一层。 */
  { key: 'idea', label: '思想决策', cls: 'n-idea' },
  { key: 'speak', label: '发言', cls: 'n-speak' },
  { key: 'tool', label: '工具', cls: 'n-tool' },
  { key: 'send', label: '发送', cls: 'n-send' },
];

const NEURON_LABEL = NEURONS.reduce((acc, n) => { acc[n.key] = n.label; return acc; }, {});
/* 兼容历史 span 里的旧名字 */
NEURON_LABEL.think = NEURON_LABEL.idea;

const STAGE_LABEL = {
  disabled: '主动发言已关闭',
  addressed: '被叫到/私聊',
  cooldown: '冷却中',
  budget: '额度已满',
  noise: '灌水',
  direct_hint: '直呼词',
  sample: '抽样未命中',
  llm_quota: '判定用尽',
  no_key: '无模型',
  llm: '模型判定',
  llm_error: '判定失败',
  sleeping: '睡眠中',
  recall: '记忆注入',
  builtin: '内置工具',
  empty: '生成为空',
  ok: '已发送',
  fail: '发送失败',

  /* 思想层的落定结果——这些以前全显示成英文原样，第一眼看不懂 */
  weight_only: '只加了权重',
  'nothing': '什么都没做',
  no_intent: '没有开口动机',
  no_idea: '没冒出念头',
  sent: '主动说了一句',
  blocked: '被准入闸拦下',
  error: '出错',
};

const label = (k) => STAGE_LABEL[k] || k || '-';

export function render(container, api) {
  let disposed = false;

  const state = { date: todayStr(), items: [], stats: null, selected: null };

  const dateInput = h('input', { class: 'input', type: 'date', value: state.date, style: 'max-width:160px' });
  const refreshBtn = h('button', {
    class: 'btn btn-primary btn-sm', type: 'button',
    onclick: () => { state.date = dateInput.value || todayStr(); load(); },
  }, '刷新');

  const topoHost = h('div', {});
  const listHost = h('div', {});
  const detailHost = h('div', {});

  container.append(
    card('神经元拓扑（当天汇总）', [topoHost]),
    card('消息瀑布', [
      h('div', { class: 'toolbar-row' }, dateInput, refreshBtn,
        h('span', { class: 'tiny muted' }, '点任意一行可在上方点亮它经过的神经元')),
      listHost,
      detailHost
    ])
  );

  async function load() {
    try {
      const [stats, recent] = await Promise.all([
        api('/api/neurons/stats?date=' + encodeURIComponent(state.date)),
        api('/api/trace/recent?limit=60&date=' + encodeURIComponent(state.date)),
      ]);
      if (disposed) return;
      state.stats = stats;
      state.items = recent.items || [];
      state.selected = null;
      paintTopology();
      paintList();
      paintDetail();
    } catch (err) {
      if (disposed) return;
      listHost.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  /* ---------- 拓扑 ---------- */
  function paintTopology() {
    const stats = state.stats || { neurons: [], gate_stages: [], traces: 0 };
    const byKey = {};
    (stats.neurons || []).forEach((n) => { byKey[n.neuron] = n; });

    const selected = selectedNeurons();

    const nodes = NEURONS.map((n, i) => {
      const st = byKey[n.key] || { calls: 0, avg_ms: 0, max_ms: 0, fail: 0 };
      const on = selected.size === 0 || selected.has(n.key);
      const dim = selected.size > 0 && !selected.has(n.key);

      const node = h('div', {
        class: 'neuron ' + n.cls + (on && selected.size > 0 ? ' neuron-on' : '') + (dim ? ' neuron-dim' : ''),
      },
        h('div', { class: 'neuron-name' }, n.label),
        h('div', { class: 'neuron-calls' }, String(st.calls) + ' 次'),
        h('div', { class: 'neuron-ms' }, st.calls ? st.avg_ms + ' ms 均' : '—'),
        (st.fail || 0) > 0 ? h('div', { class: 'neuron-fail' }, '失败 ' + st.fail) : null
      );

      return i === 0 ? [node] : [h('div', { class: 'flow-arrow' }), node];
    }).flat();

    /* 静默支路 */
    const silentCount = (stats.gate_stages || [])
      .filter((s) => ['noise', 'sample', 'cooldown', 'budget', 'llm_quota', 'no_key', 'sleeping'].includes(s.stage))
      .reduce((a, b) => a + b.count, 0);

    const silentNode = h('div', { class: 'neuron n-silent' + (selected.size > 0 && !selected.has('silent') ? ' neuron-dim' : '') },
      h('div', { class: 'neuron-name' }, '静默'),
      h('div', { class: 'neuron-calls' }, String(silentCount) + ' 次'),
      h('div', { class: 'neuron-ms' }, '没进模型')
    );

    const stageRows = (stats.gate_stages || []).map((s) => h('tr', {},
      h('td', {}, label(s.stage)),
      h('td', {}, String(s.count))
    ));

    topoHost.replaceChildren(
      h('div', { class: 'flow-row' }, ...nodes),
      h('div', { class: 'flow-branch' },
        h('span', { class: 'flow-branch-line' }, '发言门判不说话时走这条'),
        silentNode
      ),
      h('div', { class: 'card-sub', style: 'margin-top:16px' }, '发言门各判定层分布'),
      h('div', { class: 'table-wrap' },
        h('table', { class: 'table' },
          h('thead', {}, h('tr', {}, h('th', {}, '判定层'), h('th', {}, '次数'))),
          h('tbody', {}, stageRows.length ? stageRows : h('tr', {}, h('td', { colspan: '2', class: 'muted' }, '今天还没有记录')))
        )
      ),
      h('div', { class: 'tiny muted', style: 'margin-top:8px' },
        '共 ' + String(stats.traces || 0) + ' 条消息有埋点记录')
    );
  }

  function selectedNeurons() {
    const set = new Set();
    if (!state.selected) return set;
    const item = state.items.find((x) => x.trace_id === state.selected);
    if (!item) return set;

    (item.neurons || []).forEach((n) => set.add(n));
    if (!item.spoke) set.add('silent');
    return set;
  }

  /* ---------- 瀑布列表 ---------- */
  function paintList() {
    if (state.items.length === 0) {
      listHost.replaceChildren(emptyState(
        '这一天还没有埋点记录。埋点是这一版新加的，重启服务后收到新消息才会有数据。'));
      return;
    }

    const maxTotal = Math.max(...state.items.map((x) => x.total_ms || 1), 1);

    const rows = state.items.map((item) => {
      const isSel = state.selected === item.trace_id;

      const segs = (item.spans || []).map((s) => {
        const width = Math.max(0.8, ((s.ms || 0) / maxTotal) * 100);
        const cls = 'wf-seg ' + (NEURONS.find((n) => n.key === s.neuron)?.cls || 'n-gate')
          + (s.ok ? '' : ' wf-fail');
        return h('span', {
          class: cls,
          style: 'width:' + width + '%',
          title: (NEURON_LABEL[s.neuron] || s.neuron) + ' ' + (s.ms || 0) + 'ms　' + (s.brief || ''),
        });
      });

      return h('div', {
        class: 'wf-row' + (isSel ? ' wf-row-sel' : ''),
        onclick: () => {
          state.selected = isSel ? null : item.trace_id;
          paintTopology();
          paintList();
          paintDetail();
        },
      },
        h('div', { class: 'wf-meta' },
          h('span', { class: 'wf-time tiny muted' }, fmtTime(item.ts)),
          h('span', { class: 'wf-who' }, item.speaker || '-'),
          h('span', { class: 'wf-text' }, item.text || '-')
        ),
        h('div', { class: 'wf-bar' }, ...segs),
        h('div', { class: 'wf-total tiny' },
          item.spoke ? badge(String(item.total_ms) + 'ms', 'ok') : badge(label(lastStage(item)), 'muted')
        )
      );
    });

    listHost.replaceChildren(h('div', { class: 'wf-list' }, ...rows));
  }

  function lastStage(item) {
    const spans = item.spans || [];
    for (let i = spans.length - 1; i >= 0; i--) {
      if (spans[i].stage) return spans[i].stage;
    }
    return '-';
  }

  /* ---------- 详情 ---------- */
  function paintDetail() {
    if (!state.selected) { detailHost.replaceChildren(); return; }

    const item = state.items.find((x) => x.trace_id === state.selected);
    if (!item) { detailHost.replaceChildren(); return; }

    const rows = (item.spans || []).map((s) => h('tr', {},
      h('td', {}, String(s.seq)),
      h('td', {}, h('span', { class: 'wf-dot ' + (NEURONS.find((n) => n.key === s.neuron)?.cls || 'n-gate') }),
        NEURON_LABEL[s.neuron] || s.neuron),
      h('td', {}, String(s.ms) + ' ms'),
      h('td', {}, s.ok ? badge('成功', 'ok') : badge('失败', 'danger')),
      h('td', { class: 'tiny muted' }, label(s.stage)),
      h('td', { class: 'tiny' }, String(s.brief || '-')),
      h('td', { class: 'tiny muted' }, s.tokens ? String(s.tokens) : '-')
    ));

    detailHost.replaceChildren(
      h('div', { class: 'card-sub', style: 'margin-top:14px' },
        '这条消息的神经元调用　trace=' + item.trace_id),
      h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '序'), h('th', {}, '神经元'), h('th', {}, '耗时'),
          h('th', {}, '结果'), h('th', {}, '判定层'), h('th', {}, '摘要'), h('th', {}, 'tokens')
        )),
        h('tbody', {}, ...rows)
      ))
    );
  }

  load();

  return () => { disposed = true; };
}
