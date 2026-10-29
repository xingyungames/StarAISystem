/* ============================================================
   深层记忆页：人物画像（面向人、跨会话）与事件画像（面向群、会过期）
   数据来自 /api/deep 系列，只读展示（画像由睡眠自动重写）
   ============================================================ */

import { h, card, badge, fmtTime, emptyState } from '../app.js';

const KIND_LABEL = { meme: '梗 / 复读', targeted: '对某人的固定话', topic: '持续话题' };

function metric(label, valueNode, footText) {
  return h('div', { class: 'metric' },
    h('div', { class: 'metric-label' }, label),
    h('div', { class: 'metric-value small' }, valueNode),
    footText ? h('div', { class: 'metric-foot' }, footText) : null
  );
}

function codeBlock(text, emptyText) {
  if (!text || !text.trim()) {
    return h('div', { class: 'empty' }, emptyText || '（空）');
  }
  return h('pre', { class: 'pre' }, text);
}

function weightBadge(w) {
  const n = Number(w) || 0;
  const kind = n >= 0.5 ? 'ok' : n >= 0.2 ? 'warn' : 'muted';
  return badge(n.toFixed(2), kind);
}

export function render(container, api) {
  let disposed = false;

  let data = null;
  let tab = 'persons';
  let selected = null;

  const metricsEl = h('div', { class: 'metrics' });
  const tabBar = h('div', { class: 'toolbar-row' });
  const detailHost = h('div', {});

  container.append(
    card('两层记忆', [metricsEl]),
    card('深层画像', [tabBar, detailHost])
  );

  async function load() {
    try {
      data = await api('/api/deep');
      if (disposed) return;
      paint();
    } catch (err) {
      if (disposed) return;
      container.replaceChildren(
        h('div', { class: 'note note-danger' }, '加载失败：' + (err.message || '未知错误'))
      );
    }
  }

  function paint() {
    const policy = data.policy || {};
    const shallow = data.shallow || {};
    const persons = data.persons || [];
    const groups = data.groups || [];

    const shallowTotal = Object.values(shallow).reduce((a, b) => a + Number(b || 0), 0);

    metricsEl.replaceChildren(
      metric('浅层碎片', String(shallowTotal) + ' 条', '会被吸收、会过期'),
      metric('人物画像', String(persons.length) + ' 份', '面向人，跨会话'),
      metric('事件画像', String(groups.length) + ' 份', '面向群，会过期'),
      metric('浅层保留', String(policy.shallow_retention_days ?? '-') + ' 天', '吸收后进归档')
    );

    tabBar.replaceChildren(
      tabBtn('persons', '人物画像（' + persons.length + '）'),
      tabBtn('groups', '事件画像（' + groups.length + '）'),
      tabBtn('policy', '保留策略')
    );

    paintDetail();
  }

  function tabBtn(key, label) {
    return h('button', {
      type: 'button',
      class: 'btn btn-sm ' + (tab === key ? 'btn-primary' : 'btn-ghost'),
      onclick: () => { tab = key; selected = null; paint(); },
    }, label);
  }

  function paintDetail() {
    if (tab === 'policy') {
      detailHost.replaceChildren(policyTable(data.policy || {}));
      return;
    }

    const list = tab === 'persons' ? (data.persons || []) : (data.groups || []);
    if (list.length === 0) {
      detailHost.replaceChildren(emptyState(
        '还没有深层画像。需要先跑一次睡眠巩固——' +
        (tab === 'persons' ? '人物画像' : '事件画像') + '会在巩固时把浅层碎片归纳出来。'
      ));
      return;
    }

    const rows = list.map((it) => {
      const id = tab === 'persons' ? it.user_id : it.group_id;
      const isSel = selected === id;
      return h('tr', {
        class: isSel ? 'row-selected' : '',
        style: 'cursor:pointer',
        onclick: () => { selected = isSel ? null : id; paintDetail(); },
      },
        h('td', { class: 'mono' }, id),
        h('td', {}, String(it.lines ?? 0) + ' 行'),
        h('td', {}, tab === 'persons'
          ? String(it.chars ?? 0) + ' 字'
          : badge(String(it.patterns ?? 0) + ' 条', (it.patterns ?? 0) > 0 ? 'ok' : 'muted')),
        h('td', { class: 'tiny muted' }, tab === 'persons' ? '-' : fmtTime(it.updated_at || 0))
      );
    });

    const nodes = [h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, tab === 'persons' ? 'QQ' : '群号'),
        h('th', {}, '正文行数'),
        h('th', {}, tab === 'persons' ? '字数' : '活跃 pattern'),
        h('th', {}, '更新时间')
      )),
      h('tbody', {}, ...rows)
    ))];

    detailHost.replaceChildren(...nodes);

    if (selected) {
      paintSelected();
    }
  }

  function paintSelected() {
    const box = h('div', { style: 'margin-top:14px' });
    detailHost.append(box);
    box.replaceChildren(h('div', { class: 'tiny muted' }, '读取中…'));

    const path = tab === 'persons'
      ? '/api/deep/person/' + encodeURIComponent(selected)
      : '/api/deep/group/' + encodeURIComponent(selected);

    api(path).then((res) => {
      if (disposed) return;

      const parts = [
        h('div', { class: 'card-sub' }, tab === 'persons' ? '人物画像正文' : '事件画像正文'),
        codeBlock(res.md, '（画像还是空的）')
      ];

      if (tab === 'groups') {
        const pats = res.patterns || [];
        parts.push(h('div', { class: 'card-sub', style: 'margin-top:14px' },
          '重复模式（权重按当前时间实时衰减）'));

        if (pats.length === 0) {
          parts.push(h('div', { class: 'note' },
            '暂无。需要群里反复出现同一句话才会形成 pattern（默认至少出现 2 次）。'));
        } else {
          parts.push(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
            h('thead', {}, h('tr', {},
              h('th', {}, '类型'), h('th', {}, '内容'), h('th', {}, '目标'),
              h('th', {}, '出现'), h('th', {}, '权重'), h('th', {}, '最后出现')
            )),
            h('tbody', {}, ...pats.map((p) => h('tr', {},
              h('td', {}, badge(KIND_LABEL[p.kind] || p.kind, p.kind === 'meme' ? 'ok' : 'warn')),
              h('td', {}, p.text || ''),
              h('td', { class: 'mono tiny' }, p.target || '-'),
              h('td', {}, String(p.count ?? 0)),
              h('td', {}, weightBadge(p.weight)),
              h('td', { class: 'tiny muted' }, fmtTime(p.last_seen || 0))
            )))
          )));
        }
      }

      box.replaceChildren(...parts);
    }).catch((err) => {
      if (disposed) return;
      box.replaceChildren(h('div', { class: 'note note-danger' }, '读取失败：' + err.message));
    });
  }

  function policyTable(p) {
    const rows = [
      ['shallow_retention_days', p.shallow_retention_days, '浅层碎片被吸收后还能留多久用于溯源'],
      ['person_half_life_days', p.person_half_life_days, '人物画像时间半衰期'],
      ['group_half_life_hours', p.group_half_life_hours, '事件 pattern 时间半衰期'],
      ['person_profile_max_lines', p.person_profile_max_lines, '超过就逼模型压缩'],
      ['group_profile_max_lines', p.group_profile_max_lines, ''],
      ['group_pattern_min_count', p.group_pattern_min_count, '只出现一次不算模式'],
      ['group_pattern_min_weight', p.group_pattern_min_weight, '低于此权重清掉'],
      ['group_pattern_expire_days', p.group_pattern_expire_days, '多久没出现就彻底忘掉'],
      ['max_objects_per_sleep', p.max_objects_per_sleep, '单次巩固最多更新几个对象'],
      ['min_turns_to_update_person', p.min_turns_to_update_person, '发言少于这个数不更新画像'],
    ];

    return h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {}, h('th', {}, '配置项'), h('th', {}, '值'), h('th', {}, '说明'))),
      h('tbody', {}, ...rows.map(([k, v, note]) => h('tr', {},
        h('td', { class: 'mono tiny' }, k),
        h('td', {}, v === undefined || v === null ? '-' : String(v)),
        h('td', { class: 'tiny muted' }, note || '')
      )))
    ));
  }

  load();

  return () => { disposed = true; };
}
