/* ============================================================
   变化度页：自我被改写了多少（代理指标，不是 loss）
   数据源：/api/delta 与 /api/behavior
   ============================================================ */

import { h, card, badge, fmtTime, emptyState } from '../app.js';

const KIND_LABEL = { persona: '人设', person: '人物画像', group: '事件画像' };

function metric(label, value, foot) {
  return h('div', { class: 'metric' },
    h('div', { class: 'metric-label' }, label),
    h('div', { class: 'metric-value small' }, value),
    foot ? h('div', { class: 'metric-foot' }, foot) : null
  );
}

function pct(v) {
  const n = Number(v);
  if (!Number.isFinite(n)) return '-';
  return (n * 100).toFixed(1) + '%';
}

function changeBadge(rate) {
  const n = Number(rate) || 0;
  const kind = n >= 0.25 ? 'ok' : n >= 0.08 ? 'warn' : 'muted';
  return badge(pct(n), kind);
}

export function render(container, api) {
  let disposed = false;
  const state = { days: 14 };

  const metricsEl = h('div', { class: 'metrics' });
  const noteEl = h('div', {});
  const barsHost = h('div', {});
  const behaviorHost = h('div', {});
  const recentHost = h('div', {});

  container.append(
    card('变化度', [noteEl, metricsEl, barsHost], [
      h('span', { class: 'tiny muted' }, '统计最近 14 天'),
    ]),
    card('行为指纹（每天的回复长什么样）', [behaviorHost]),
    card('最近改写明细', [recentHost])
  );

  async function load() {
    try {
      const [delta, behavior] = await Promise.all([
        api('/api/delta?days=' + state.days),
        api('/api/behavior?days=' + state.days),
      ]);
      if (disposed) return;
      paintDelta(delta);
      paintBehavior(behavior);
    } catch (err) {
      if (disposed) return;
      noteEl.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  function paintDelta(delta) {
    const s = delta.summary || {};

    noteEl.replaceChildren(
      h('div', { class: 'note' },
        '这里没有 loss，也不可能有：模型权重一个字节都没动过，',
        '所谓「学习」是睡眠时改写人设与画像文件。所以这里量的是',
        ' **变化率 = 1 - 改写前后的文本相似度**，即「这次自我被改掉了多少」。',
        ' 变化率长期接近 0 说明它没在学新东西；忽高忽低说明画像在震荡。')
    );

    metricsEl.replaceChildren(
      metric('改写次数', String(s.total || 0), '最近 ' + state.days + ' 天'),
      metric('平均变化率', pct(s.avg_change_rate), '越高说明改动越大'),
      metric('人工干预', String(s.persona || 0), '你手动改人设的次数'),
      metric('睡眠重写', String((s.person || 0) + (s.group || 0)), '人物画像 + 事件画像')
    );

    const daily = delta.daily || [];
    if (daily.length === 0) {
      barsHost.replaceChildren(emptyState(
        '还没有改写记录。跑一次睡眠巩固，或者到「人设」页保存一次，就会有数据。'));
      paintRecent(delta.recent || []);
      return;
    }

    const max = Math.max(...daily.map((d) => Number(d.avg_change_rate) || 0), 0.01);

    const bars = daily.map((d) => {
      const rate = Number(d.avg_change_rate) || 0;
      const height = Math.max(2, (rate / max) * 100);
      return h('div', { class: 'delta-bar', title: d.date + '　变化率 ' + pct(rate) + '　改写 ' + d.count + ' 次' },
        h('div', { class: 'delta-bar-fill', style: 'height:' + height + '%' }),
        h('div', { class: 'delta-bar-val tiny' }, pct(rate)),
        h('div', { class: 'delta-bar-label tiny muted' }, d.date.slice(5))
      );
    });

    barsHost.replaceChildren(
      h('div', { class: 'card-sub', style: 'margin-top:16px' }, '每天的平均变化率'),
      h('div', { class: 'delta-bars' }, ...bars)
    );

    paintRecent(delta.recent || []);
  }

  function paintRecent(recent) {
    if (recent.length === 0) {
      recentHost.replaceChildren(h('div', { class: 'empty' }, '暂无'));
      return;
    }

    const rows = recent.map((r) => h('tr', {},
      h('td', { class: 'tiny muted' }, fmtTime(r.ts)),
      h('td', {}, badge(KIND_LABEL[r.kind] || r.kind,
        r.kind === 'persona' ? 'accent' : r.kind === 'person' ? 'ok' : 'warn')),
      h('td', { class: 'mono tiny' }, r.target || '-'),
      h('td', {}, changeBadge(r.change_rate)),
      h('td', { class: 'tiny' }, '+' + String(r.added) + ' / -' + String(r.removed) + ' 行'),
      h('td', { class: 'tiny' }, String(r.absorbed || 0) + ' 条碎片'),
      h('td', { class: 'tiny muted' }, r.note || r.by || '')
    ));

    recentHost.replaceChildren(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, '时间'), h('th', {}, '类型'), h('th', {}, '对象'),
        h('th', {}, '变化率'), h('th', {}, '行增删'), h('th', {}, '吸收'), h('th', {}, '来源')
      )),
      h('tbody', {}, ...rows)
    )));
  }

  function paintBehavior(behavior) {
    const items = (behavior.items || []).slice().reverse();
    if (items.length === 0) {
      behaviorHost.replaceChildren(h('div', { class: 'empty' }, '暂无'));
      return;
    }

    const rows = items.map((d) => h('tr', {},
      h('td', { class: 'tiny' }, d.date),
      h('td', {}, String(d.messages)),
      h('td', {}, String(d.replies)),
      h('td', {}, d.replies ? String(d.avg_reply_chars) + ' 字' : '-'),
      h('td', {}, d.replies ? pct(d.bracket_rate) : '-'),
      h('td', {}, d.replies ? pct(d.question_rate) : '-')
    ));

    behaviorHost.replaceChildren(
      h('div', { class: 'note' },
        '括号率 = 回复里带「（动作）」的比例；问号率 = 回复里带问号的比例。',
        ' 这两个数最能反映「说话方式有没有真的变」——比人设文件变没变更硬。'),
      h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '日期'), h('th', {}, '总消息'), h('th', {}, '回复数'),
          h('th', {}, '平均字数'), h('th', {}, '括号率'), h('th', {}, '问号率')
        )),
        h('tbody', {}, ...rows)
      ))
    );
  }

  load();

  return () => { disposed = true; };
}
