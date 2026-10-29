/* ============================================================
   对话分块页

   两维正交：Entity（谁，全局）/ Block（上下文边界，按 scope）。

   这一页是分块器的**验收工具**：一眼看出它把对话切成了哪些块、
   哪些消息判不了。判不了的比例是这套方案成立与否的第一判据——
   超过 40% 就别急着切 decide，先改信号权重。
   ============================================================ */

import { h, card, badge, fmtTime, toast, kvRow } from '../app.js';

const STATE_LABEL = {
  one_to_one: '一对一',
  open_topic: '对大家',
  cold: '冷了',
  closed: '已归档',
};

const STATE_KIND = { one_to_one: 'accent', open_topic: 'ok', cold: 'muted', closed: 'muted' };

const MODE_LABEL = {
  off: '关闭',
  observe: '只观测（不改变行为）',
  decide: '正式决策',
};

export function render(container, api) {
  let disposed = false;
  let data = null;

  const host = h('div', {});
  container.append(host);

  async function load() {
    try {
      data = await api('/api/dialogue');
      if (disposed) return;
      paint();
    } catch (err) {
      if (disposed || err.status === 401) return;
      host.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  function paint() {
    const cfg = data.config || {};
    const st = data.stats || {};
    const scopes = data.scopes || [];

    /* ---------- 指标 ---------- */
    const rate = st.unresolved_rate || 0;
    const rateKind = rate > 0.4 ? 'danger' : rate > 0.25 ? 'warn' : 'ok';

    const metrics = [
      kvRow('当前模式', badge(MODE_LABEL[cfg.mode] || cfg.mode || '-', cfg.mode === 'observe' ? 'warn' : 'accent')),
      kvRow('今天判定', String(st.total || 0) + ' 条'),
      kvRow('判不了比例', h('span', { class: 'row' },
        badge(Math.round(rate * 100) + '%', rateKind),
        h('span', { class: 'tiny muted' },
          '　' + (st.unresolved || 0) + ' / ' + (st.total || 0) +
          '　（>40% 说明分块器不可用，别切 decide）'))),
      kvRow('规则命中率', Math.round((st.rule_rate || 0) * 100) + '%　（太低说明一直在烧模型）'),
      kvRow('结果分布', '延续 ' + (st.continue_ || 0) + '　新块 ' + (st.new_block || 0) +
        '　判不了 ' + (st.unresolved || 0)),
      kvRow('判定来源', '规则 ' + (st.by_rule || 0) + '　模型 ' + (st.by_model || 0)),
    ];

    const metricCard = card('分块指标', [
      h('div', { class: 'note' },
        'observe 模式：只记录归属判定和块结构，**回复仍然走原来的路径**。',
        ' 分块器是这套方案里唯一真正"新"的算法，它错了后面全错、而且极难定位，所以先跑几天看数据。'),
      h('div', { class: 'kv' }, ...metrics),
    ]);

    /* ---------- 块甘特图 ---------- */
    const gantt = scopes.length === 0
      ? h('div', { class: 'empty' }, '还没有块。等有消息进来、分块器跑过之后这里就会出现。')
      : h('div', {}, ...scopes.map(renderScope));

    const ganttCard = card('块时间轴', [gantt],
      h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '刷新'));

    /* ---------- 未归属池 ---------- */
    const unresolvedAll = scopes.flatMap((s) => (s.unresolved || []).map((u) => ({ scope: s, u })));
    const unresolvedBody = unresolvedAll.length === 0
      ? h('div', { class: 'empty' }, '没有判不了的消息。要么分得很干净，要么还没跑够。')
      : h('div', {}, ...unresolvedAll.slice(0, 40).map(({ scope, u }) => renderUnresolved(scope, u)));

    const unresolvedCard = card('判不了的消息（可人工归位）', [unresolvedBody],
      h('span', { class: 'tiny muted' }, '共 ' + unresolvedAll.length + ' 条'));

    /* ---------- 配置 ---------- */
    const f = {
      mode: selectField('运行模式', [
        ['off', '关闭'], ['observe', '只观测'], ['decide', '正式决策'],
      ], cfg.mode),
      useModel: checkField('模糊时问模型', '关掉就是纯规则，省一次调用但判不了的比例会升', cfg.use_model),
      minConf: numField('置信度门槛', '低于它 → 判不了（0~1）', cfg.min_confidence, '0.05'),
      gap: numField('新块时间间隔（秒）', '距该块上次活动超过这么久就不倾向延续', cfg.gap_seconds_new_block),
      maxHot: numField('最多热块数', '超出的转冷', cfg.max_hot_blocks),
      hotIdle: numField('转冷空闲（秒）', '多久没新消息算冷', cfg.hot_idle_seconds),
      keepH: numField('未归属保留（小时）', '留多久等人工回填', cfg.unresolved_keep_hours),
      wReply: numField('权重：reply 同一块', '', cfg.w_reply_same_block, '0.05'),
      wSame: numField('权重：同一个人接着说', '现场最强的信号，成本为零', cfg.w_same_sender, '0.05'),
      wAt: numField('权重：@ 同一个人', '', cfg.w_at_same_entity, '0.05'),
      wPron: numField('权重：指代词', '这个/那个/刚才', cfg.w_mention_pronoun, '0.05'),
      wOverlap: numField('权重：实体重叠', '消息里提到块里的人', cfg.w_entity_overlap, '0.05'),
      wTime: numField('权重：时间接近', '', cfg.w_time_proximity, '0.05'),
    };

    const saveBtn = h('button', {
      class: 'btn btn-primary', type: 'button',
      onclick: async () => {
        saveBtn.disabled = true;
        try {
          await api('/api/dialogue', {
            method: 'PUT',
            body: JSON.stringify({
              mode: f.mode.get(),
              use_model: f.useModel.get(),
              min_confidence: f.minConf.get(),
              gap_seconds_new_block: f.gap.get(),
              max_hot_blocks: f.maxHot.get(),
              hot_idle_seconds: f.hotIdle.get(),
              unresolved_keep_hours: f.keepH.get(),
              w_reply_same_block: f.wReply.get(),
              w_same_sender: f.wSame.get(),
              w_at_same_entity: f.wAt.get(),
              w_mention_pronoun: f.wPron.get(),
              w_entity_overlap: f.wOverlap.get(),
              w_time_proximity: f.wTime.get(),
            }),
          });
          if (disposed) return;
          toast('已保存，立即生效', 'ok');
          load();
        } catch (err) {
          if (disposed || err.status === 401) return;
          toast('保存失败：' + err.message, 'danger');
        } finally { saveBtn.disabled = false; }
      },
    }, '保存并立即生效');

    const cfgCard = card('路由参数', [
      h('div', { class: 'form-grid' },
        f.mode.el, f.useModel.el, f.minConf.el, f.gap.el,
        f.maxHot.el, f.hotIdle.el, f.keepH.el
      ),
      h('div', { class: 'field-label', style: 'margin-top:14px' }, '信号权重'),
      h('div', { class: 'form-grid' },
        f.wReply.el, f.wSame.el, f.wAt.el, f.wPron.el, f.wOverlap.el, f.wTime.el
      ),
      h('div', { class: 'toolbar-row', style: 'margin-top:12px' }, saveBtn),
    ]);

    // 配置项放前面：调参是这一页最常做的事，别让人先滚过一屏数据
    host.replaceChildren(cfgCard, metricCard, ganttCard, unresolvedCard);
  }

  /* ---------------- 一个 scope 的块时间轴 ---------------- */

  function renderScope(s) {
    const blocks = s.blocks || [];
    if (blocks.length === 0) return null;

    const now = Math.floor(Date.now() / 1000);
    const start = Math.min(...blocks.map((b) => b.created_at || now));
    const end = Math.max(now, ...blocks.map((b) => b.last_active_at || now));
    const span = Math.max(60, end - start);

    const rows = blocks.map((b) => {
      const from = ((b.created_at || start) - start) / span;
      const width = Math.max(0.015, ((b.last_active_at || end) - (b.created_at || start)) / span);

      return h('div', { class: 'blk-row' },
        h('div', { class: 'blk-meta' },
          badge(STATE_LABEL[b.state] || b.state, STATE_KIND[b.state] || 'muted'),
          h('span', { class: 'tiny' }, (b.topic || '（没留话题）').slice(0, 18)),
          h('span', { class: 'tiny muted' }, ' ' + (b.message_count || 0) + '条'),
          h('span', { class: 'tiny mono muted' }, ' ' + (b.participants || []).length + '人')
        ),
        h('div', { class: 'blk-track' },
          h('div', {
            class: 'blk-bar blk-' + (b.state || 'open_topic'),
            style: 'left:' + Math.round(from * 100) + '%;width:' + Math.round(width * 100) + '%',
            title: (b.topic || '') + '　' + fmtTime(b.created_at) + ' → ' + fmtTime(b.last_active_at),
          }))
      );
    });

    return h('div', { class: 'blk-scope' },
      h('div', { class: 'blk-scope-head' },
        h('span', { class: 'tiny' }, s.scope_label || s.scope),
        h('span', { class: 'tiny muted' }, '　' + blocks.length + ' 块　' +
          fmtTime(start) + ' → 现在')),
      ...rows
    );
  }

  /* ---------------- 未归属消息 ---------------- */

  function renderUnresolved(scope, u) {
    const options = (scope.blocks || []).slice(0, 12).map((b) =>
      h('option', { value: b.id }, (STATE_LABEL[b.state] || b.state) + '｜' + (b.topic || b.id)));
    options.push(h('option', { value: '' }, '＋ 新建一块来装它'));

    const sel = h('select', { class: 'input', style: 'max-width:260px' }, ...options);
    const btn = h('button', {
      class: 'btn btn-primary btn-sm', type: 'button',
      onclick: async () => {
        btn.disabled = true;
        try {
          await api('/api/dialogue/attach', {
            method: 'POST',
            body: JSON.stringify({ scope: scope.scope, message_id: u.message_id, block_id: sel.value }),
          });
          if (disposed) return;
          toast('已归位', 'ok');
          load();
        } catch (err) {
          if (disposed || err.status === 401) return;
          toast('归位失败：' + err.message, 'danger');
        } finally { btn.disabled = false; }
      },
    }, '归位');

    return h('div', { class: 'weight-row' },
      h('div', { class: 'weight-head' },
        badge('判不了', 'warn'),
        h('span', { class: 'tiny mono muted' }, u.message_id),
        h('span', { class: 'tiny muted' }, '置信 ' + (u.confidence || 0)),
        h('span', { class: 'tiny muted' }, fmtTime(u.ts)),
        h('span', { class: 'tiny muted' }, u.why || '')
      ),
      h('div', { class: 'toolbar-row', style: 'margin-top:6px' }, sel, btn)
    );
  }

  /* ---------------- 表单小工具 ---------------- */

  function numField(label, hint, value, step) {
    const input = h('input', {
      class: 'input', type: 'number', step: step || '1',
      value: value === undefined || value === null ? '' : String(value),
    });
    return {
      el: h('div', { class: 'field' },
        h('label', { class: 'field-label' }, label), input,
        hint ? h('div', { class: 'field-hint' }, hint) : null),
      get: () => Number(input.value),
    };
  }

  function checkField(label, hint, checked) {
    const input = h('input', { type: 'checkbox' });
    input.checked = !!checked;
    return {
      el: h('div', { class: 'field' },
        h('label', { class: 'check' }, input, h('span', {}, label)),
        hint ? h('div', { class: 'field-hint' }, hint) : null),
      get: () => input.checked,
    };
  }

  function selectField(label, pairs, value) {
    const sel = h('select', { class: 'input' },
      ...pairs.map(([v, t]) => h('option', { value: v }, t)));
    sel.value = value || pairs[0][0];
    return {
      el: h('div', { class: 'field' }, h('label', { class: 'field-label' }, label), sel),
      get: () => sel.value,
    };
  }

  load();

  return () => { disposed = true; };
}
