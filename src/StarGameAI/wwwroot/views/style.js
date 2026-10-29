/* ============================================================
   风格样本页：卡尔曼式不确定度驱动准入

   人负责喂种子 + 纠偏，AI 等数据收敛后自己接管。
   页面上要能一眼看出：现在处于哪个模式、离收敛还有多远、
   哪些样本是模型自己提的还没被批准。
   ============================================================ */

import { h, card, badge, fmtTime, toast, kvRow } from '../app.js';

const MODE_LABEL = {
  manual: '只信人工（样本还少 / 彼此不像）',
  assisted: '辅助（自评够高才收）',
  adaptive: '自适应（模型可以自己补样本）',
};

const MODE_KIND = { manual: 'warn', assisted: 'accent', adaptive: 'ok' };

export function render(container, api) {
  let disposed = false;
  let data = null;

  const host = h('div', {});
  container.append(host);

  async function load() {
    try {
      data = await api('/api/style');
      if (disposed) return;
      paint();
    } catch (err) {
      if (disposed || err.status === 401) return;
      host.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  function paint() {
    const st = data.state || {};
    const cfg = data.config || {};
    const items = data.items || [];

    /* ---------- 收敛状态 ---------- */
    const stateRows = [
      kvRow('当前模式', badge(MODE_LABEL[st.mode] || st.mode || '-', MODE_KIND[st.mode] || 'muted')),
      kvRow('不确定度', h('span', { class: 'row' },
        h('span', { class: 'weight-bar', style: 'flex:0 0 160px' },
          h('span', {
            class: 'weight-fill',
            style: 'width:' + Math.round((st.uncertainty || 0) * 100) + '%' +
              ((st.uncertainty || 0) > 0.65 ? ';background:var(--warn)' : ''),
          })),
        h('span', { class: 'tiny mono' }, String(st.uncertainty ?? 0)))),
      kvRow('覆盖度', (st.approved || 0) + ' / ' + (cfg.target_samples || '-') + ' 条（' +
        Math.round((st.coverage || 0) * 100) + '%）'),
      kvRow('一致性', String(st.consistency ?? 0) + '　（风格特征离散度的补数，越接近 1 说明越像同一个人）'),
      kvRow('样本', '已批准 ' + (st.approved || 0) + '　待定 ' + (st.pending || 0) + '　已否决 ' + (st.rejected || 0)),
    ];

    const stateCard = card('收敛状态', [
      h('div', { class: 'note' },
        '这不是卡尔曼滤波，但抄了同一个结构：**观测权重 ∝ 1/不确定度**。',
        ' 样本少、彼此不像 → 不确定度高 → 只信你喂的；样本多、越来越像 → 不确定度低 → 放开模型自评自动入库。'),
      h('div', { class: 'kv' }, ...stateRows),
      h('div', { class: 'field-hint', style: 'margin-top:8px' },
        '一致性用的是**结构特征**（有没有括号动作、长度、省略号、反问、第一人称），不是词汇重叠——' +
        '同一语气的两句话可能一个字都不重。'),
    ]);

    /* ---------- 人工喂 ---------- */
    const input = h('input', {
      class: 'input', placeholder: '（没接话，耳朵有点热）',
      style: 'flex:1 1 320px',
    });
    const scopeInput = h('input', {
      class: 'input', placeholder: 'private:10001（可空）', style: 'max-width:240px',
    });

    const addBtn = h('button', {
      class: 'btn btn-primary', type: 'button',
      onclick: async () => {
        const text = input.value.trim();
        if (!text) { toast('先写一句', 'warn'); return; }

        addBtn.disabled = true;
        try {
          await api('/api/style', { method: 'POST', body: JSON.stringify({ text, scope: scopeInput.value.trim() }) });
          if (disposed) return;
          input.value = '';
          toast('已加入样本库（权重 1.0）', 'ok');
          load();
        } catch (err) {
          if (disposed || err.status === 401) return;
          toast('添加失败：' + err.message, 'danger');
        } finally { addBtn.disabled = false; }
      },
    }, '加入样本');

    const feedCard = card('人工喂样本', [
      h('div', { class: 'note' },
        '写一句你觉得「就是她」的话——带动作、带停顿、半句话都行。这里加进去的是**满权重**，' +
        '模型自评的样本权重会被打折，所以你的判断永远压得住它。'),
      h('div', { class: 'toolbar-row' }, input, scopeInput, addBtn),
    ]);

    /* ---------- 样本列表 ---------- */
    const rows = items.length === 0
      ? h('div', { class: 'empty' }, '还没有样本。先喂几条你觉得像她的话，不确定度才会开始往下走。')
      : h('div', {}, ...items.map(renderItem));

    const listCard = card('样本库', [rows], h('span', { class: 'tiny muted' }, '共 ' + items.length + ' 条'));

    /* ---------- 配置 ---------- */
    const f = {
      enabled: checkField('启用风格样本注入', '关掉之后回复层不再看这份样本', cfg.enabled),
      target: numField('目标样本数', '样本数到齐才算"数据够了"', cfg.target_samples),
      manualAbove: numField('manual 阈值', '不确定度高于它 → 模型候选一律挂起等人工（0~1）', cfg.manual_above, '0.05'),
      adaptiveBelow: numField('adaptive 阈值', '不确定度低于它 → 模型自评够高就自动收（0~1）', cfg.adaptive_below, '0.05'),
      assistedMin: numField('assisted 自评门槛', '0~100', cfg.assisted_min_score),
      adaptiveMin: numField('adaptive 自评门槛', '0~100', cfg.adaptive_min_score),
      modelWeight: numField('模型样本权重上限', '人工恒为 1.0（0~1）', cfg.model_weight, '0.05'),
      injectN: numField('注入条数', '每次回复最多参照几条', cfg.inject_top_n),
    };

    const saveBtn = h('button', {
      class: 'btn btn-primary', type: 'button',
      onclick: async () => {
        saveBtn.disabled = true;
        try {
          await api('/api/style', {
            method: 'PUT',
            body: JSON.stringify({
              enabled: f.enabled.get(),
              target_samples: f.target.get(),
              manual_above: f.manualAbove.get(),
              adaptive_below: f.adaptiveBelow.get(),
              assisted_min_score: f.assistedMin.get(),
              adaptive_min_score: f.adaptiveMin.get(),
              model_weight: f.modelWeight.get(),
              inject_top_n: f.injectN.get(),
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

    const cfgCard = card('收敛参数', [
      h('div', { class: 'form-grid' },
        f.enabled.el, f.target.el, f.manualAbove.el, f.adaptiveBelow.el,
        f.assistedMin.el, f.adaptiveMin.el, f.modelWeight.el, f.injectN.el
      ),
      h('div', { class: 'toolbar-row', style: 'margin-top:12px' }, saveBtn),
    ]);

    // 配置项放前面；喂样本是这一页的主操作，紧跟其后
    host.replaceChildren(cfgCard, feedCard, stateCard, listCard);
  }

  function renderItem(it) {
    const verdictKind = it.verdict === 'approved' ? 'ok' : it.verdict === 'rejected' ? 'danger' : 'muted';
    const verdictLabel = it.verdict === 'approved' ? '已批准' : it.verdict === 'rejected' ? '已否决' : '待定';

    const act = async (verdict) => {
      try {
        await api('/api/style/' + encodeURIComponent(it.id) + '/verdict', {
          method: 'POST', body: JSON.stringify({ verdict }),
        });
        if (disposed) return;
        load();
      } catch (err) {
        if (disposed || err.status === 401) return;
        toast('操作失败：' + err.message, 'danger');
      }
    };

    const del = async () => {
      try {
        await api('/api/style/' + encodeURIComponent(it.id), { method: 'DELETE' });
        if (disposed) return;
        load();
      } catch (err) {
        if (disposed || err.status === 401) return;
        toast('删除失败：' + err.message, 'danger');
      }
    };

    return h('div', { class: 'weight-row' + (it.verdict === 'rejected' ? ' done' : '') },
      h('div', { class: 'weight-head' },
        badge(verdictLabel, verdictKind),
        badge(it.source === 'manual' ? '人工' : '模型自评', it.source === 'manual' ? 'accent' : 'muted'),
        h('span', { class: 'weight-bar' },
          h('span', { class: 'weight-fill', style: 'width:' + Math.round((it.weight || 0) * 100) + '%' })),
        h('span', { class: 'tiny mono muted' }, (it.weight || 0).toFixed(2)),
        it.source === 'model'
          ? h('span', { class: 'tiny muted' }, '自评 ' + (it.self_score || 0))
          : null,
        h('span', { class: 'tiny muted' }, it.scope_label || ''),
        h('span', { class: 'tiny muted' }, fmtTime(it.ts))
      ),
      h('div', { class: 'weight-body' }, it.text),
      h('div', { class: 'toolbar-row', style: 'margin-top:6px' },
        it.verdict !== 'approved' ? btn('批准', 'btn-ok', () => act('approved')) : null,
        it.verdict !== 'rejected' ? btn('否决', 'btn-danger', () => act('rejected')) : null,
        btn('删除', 'btn-ghost', del)
      )
    );
  }

  function btn(label, cls, onclick) {
    return h('button', {
      class: 'btn ' + cls + ' btn-sm', type: 'button', style: 'margin-right:6px',
      onclick: (e) => { e.stopPropagation(); onclick(); },
    }, label);
  }

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

  load();

  return () => { disposed = true; };
}
