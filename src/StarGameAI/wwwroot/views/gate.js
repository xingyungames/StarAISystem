/* ============================================================
   主动说话页：发言门配置 + 事件画像加成 + 各群覆盖 + 门控沙盒
   ============================================================ */

import { h, card, badge, fmtTime, toast, kvRow } from '../app.js';

/* ---------- 表单小工具 ---------- */

function numField(label, hint, value, step) {
  const input = h('input', {
    class: 'input', type: 'number', step: step || '1',
    value: value === undefined || value === null ? '' : String(value),
  });
  return {
    el: h('div', { class: 'field' },
      h('label', { class: 'field-label' }, label),
      input,
      hint ? h('div', { class: 'field-hint' }, hint) : null
    ),
    get: () => Number(input.value),
  };
}

function checkField(label, hint, checked) {
  const input = h('input', { type: 'checkbox' });
  input.checked = !!checked;
  return {
    el: h('div', { class: 'field' },
      h('label', { class: 'check' }, input, h('span', {}, label)),
      hint ? h('div', { class: 'field-hint' }, hint) : null
    ),
    get: () => input.checked,
  };
}

function listField(label, hint, arr) {
  const ta = h('textarea', { class: 'textarea', rows: '5' });
  ta.value = (arr || []).join('\n');
  return {
    el: h('div', { class: 'field' },
      h('label', { class: 'field-label' }, label),
      ta,
      h('div', { class: 'field-hint' }, hint || '每行一个')
    ),
    get: () => ta.value.split('\n').map((s) => s.trim()).filter(Boolean),
  };
}

const STAGE_LABEL = {
  disabled: '主动发言已关闭',
  addressed: '真 @ / 私聊',
  cooldown: '冷却中',
  budget: '今日额度已满',
  noise: '没有可判断的内容',
  llm_quota: '判定次数用尽',
  no_key: '未配置模型',
  llm: '模型判定：没在叫我',
  llm_addressed: '模型判定：在跟我说话',
  llm_error: '判定失败，按静默处理',
};

export function render(container, api) {
  let disposed = false;
  let data = null;
  let fields = null;

  const bodyHost = h('div', {});
  container.append(bodyHost);

  async function load() {
    try {
      data = await api('/api/gate');
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
    const cfg = data.config || {};
    const eb = cfg.event_boost || {};
    const today = data.today || {};

    /* ---------- 保存 ---------- */
    fields = {
      enabled: checkField('启用主动发言', '关闭后所有消息都按普通回复处理，不再判断该不该开口', cfg.enabled),
      ambient: numField('主动插话门槛', '没人叫我的时候，模型打的分要达到多少才插话（0-100）。门槛会写进提示词，模型按这个刻度打分', cfg.ambient_threshold),
      cooldown: numField('冷却秒数', '同一个群两次主动发言的最小间隔', cfg.cooldown_seconds),
      budget: numField('每群每日上限', '主动插话每天最多几次，防止刷屏被风控', cfg.daily_budget),
      llmQuota: numField('每日判定上限', '每群每天最多花多少次判定请求。现在每条消息都会判定，这个值要够大', cfg.max_llm_calls_per_day),
      botNames: listField('我的名字', '别人这样叫我时会被认出来。判定模型靠它判断「是不是在跟我说话」', cfg.bot_names),

      boostEnabled: checkField('注入群里的活跃梗', '把群里正在复读的话交给判定模型当上下文', eb.enabled),
      boostScale: numField('加成系数（已停用）', '保留字段，命中后不再折算成分数', eb.scale, '1'),
      boostMax: numField('加成上限（已停用）', '保留字段，命中后不再折算成分数', eb.max, '1'),
      boostTopN: numField('注入条数', '把最近几个活跃梗写进判定用的提示词', eb.inject_top_n),
    };

    const saveBtn = h('button', {
      class: 'btn btn-primary', type: 'button', onclick: () => save(saveBtn),
    }, '保存并立即生效');

    const formCard = card('主动发言', [
      h('div', { class: 'note' },
        '判定由模型逐条做，不再靠词表猜：真 @ 直接回；其余每条都带着上下文问模型「这句是不是在跟我说话、我插进去有没有价值」。'),
      h('div', { class: 'field' },
        h('label', { class: 'field-label' }, '判定模型看到的「我是谁」'),
        h('div', { class: 'note', style: 'font-family:var(--mono);font-size:11.5px' },
          (data && data.self) || '（服务端没有返回，可能是旧版本）')),
      h('div', { class: 'form-grid' },
        fields.enabled.el, fields.ambient.el,
        fields.cooldown.el, fields.budget.el, fields.llmQuota.el
      ),
      h('div', { class: 'grid-2', style: 'margin-top:12px' }, fields.botNames.el),
      h('div', { class: 'toolbar-row', style: 'margin-top:12px' }, saveBtn)
    ]);

    const boostCard = card('群里的活跃梗', [
      h('div', { class: 'note' },
        '命中的复读话术只作为上下文写进判定提示词，交给模型判断该不该顺着接一句，' +
        '不再折算成分数——加分会让词表重新变成隐式阈值。'),
      h('div', { class: 'form-grid' },
        fields.boostEnabled.el, fields.boostTopN.el
      )
    ]);

    bodyHost.replaceChildren(formCard, boostCard, groupsCard(), simulateCard());

    function save(btn) {
      const payload = {
        enabled: fields.enabled.get(),
        ambient_threshold: fields.ambient.get(),
        cooldown_seconds: fields.cooldown.get(),
        daily_budget: fields.budget.get(),
        max_llm_calls_per_day: fields.llmQuota.get(),
        bot_names: fields.botNames.get(),
        event_boost: {
          enabled: fields.boostEnabled.get(),
          scale: fields.boostScale.get(),
          max: fields.boostMax.get(),
          inject_top_n: fields.boostTopN.get(),
        },
        groups: cfg.groups || {},
      };

      btn.disabled = true;
      api('/api/gate', { method: 'PUT', body: JSON.stringify(payload) })
        .then(() => { toast('已保存，立即生效', 'ok'); return load(); })
        .catch((err) => toast('保存失败：' + err.message, 'danger'))
        .finally(() => { btn.disabled = false; });
    }
  }

  /* ---------- 各群 ---------- */
  function groupsCard() {
    const groups = data.groups || [];
    const today = data.today || {};

    const head = h('div', { class: 'kv' },
      kvRow('今日回复数', String(today.replies ?? 0)),
      kvRow('今日静默数', String(today.gate_skips ?? 0)),
      kvRow('今日意愿判定请求', String(today.gate_llm_calls ?? 0))
    );

    if (groups.length === 0) {
      return card('各群情况', [head, h('div', { class: 'note' }, '白名单里还没有群。')]);
    }

    const rows = groups.map((g) => {
      const ef = g.effective || {};
      const st = g.today || {};
      return h('tr', {},
        h('td', { class: 'mono' }, g.group_id),
        h('td', {}, ef.enabled ? badge('启用', 'ok') : badge('关闭', 'muted')),
        h('td', {}, String(ef.ambient_threshold ?? '-')),
        h('td', {}, String(ef.daily_budget ?? '-')),
        h('td', {}, String(ef.cooldown_seconds ?? '-') + ' 秒'),
        h('td', {}, String(st.active ?? 0) + ' / ' + String(st.passive ?? 0)),
        h('td', { class: 'tiny muted' }, st.lastSpoke ? fmtTime(st.lastSpoke) : '-')
      );
    });

    return card('各群情况', [head,
      h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
        h('thead', {}, h('tr', {},
          h('th', {}, '群号'), h('th', {}, '状态'), h('th', {}, '插话阈值'),
          h('th', {}, '每日上限'), h('th', {}, '冷却'),
          h('th', {}, '主动 / 被动'), h('th', {}, '上次发言')
        )),
        h('tbody', {}, ...rows)
      )),
      h('div', { class: 'tiny muted', style: 'margin-top:8px' },
        '逐群覆盖暂时只能改 gate.json 的 groups 字段（后台表单下一步支持）。')
    ]);
  }

  /* ---------- 沙盒 ---------- */
  function simulateCard() {
    const firstGroup = (data.groups && data.groups[0] && data.groups[0].group_id) || '100001';

    const scopeInput = h('input', { class: 'input', value: 'group:' + firstGroup });
    const speakerInput = h('input', { class: 'input', value: '测试用户' });
    const textArea = h('textarea', { class: 'textarea', rows: '3', placeholder: '粘一句群里的消息，看门控会怎么判' });
    const addressedBox = h('input', { type: 'checkbox' });
    const resultHost = h('div', {});

    const runBtn = h('button', {
      class: 'btn btn-primary btn-sm', type: 'button',
      onclick: () => run(),
    }, '模拟一次');

    function run() {
      const text = textArea.value.trim();
      if (!text) { toast('先写一句话', 'warn'); return; }

      runBtn.disabled = true;
      resultHost.replaceChildren(h('div', { class: 'tiny muted' }, '判定中…'));

      api('/api/gate/simulate', {
        method: 'POST',
        body: JSON.stringify({
          scope: scopeInput.value.trim(),
          text,
          speaker: speakerInput.value.trim(),
          addressed: addressedBox.checked,
        }),
      }).then((r) => {
        if (disposed) return;
        const matched = (r.matched_patterns || []).join('、') || '-';
        resultHost.replaceChildren(
          h('div', { class: 'note ' + (r.speak ? 'note-ok' : 'note-warn') },
            (r.speak ? '会开口' : '不会开口') + '　（判定层：' + (STAGE_LABEL[r.stage] || r.stage || '-') + '）'),
          h('div', { class: 'kv', style: 'margin-top:10px' },
            kvRow('最终分', String(r.score ?? 0) + '　阈值 ' + String(r.threshold ?? '-')),
            kvRow('在跟我说话', r.addressed ? '是（按被动回复）' : '否'),
            kvRow('命中的群体模式', matched),
            kvRow('理由', r.reason || '-'),
            kvRow('未开口原因', r.why_not || '-'),
            kvRow('需要深度思考', r.need_think ? '是' : '否'),
            kvRow('走了模型判定', r.used_llm ? '是' : '否')
          )
        );
      }).catch((err) => {
        if (disposed) return;
        resultHost.replaceChildren(h('div', { class: 'note note-danger' }, '模拟失败：' + err.message));
      }).finally(() => { runBtn.disabled = false; });
    }

    return card('门控沙盒', [
      h('div', { class: 'note' }, '这里只做判定，不会真的发消息，也不会占用冷却和额度。调阈值时用它试。'),
      h('div', { class: 'form-grid' },
        h('div', { class: 'field' }, h('label', { class: 'field-label' }, '会话'), scopeInput),
        h('div', { class: 'field' }, h('label', { class: 'field-label' }, '说话人'), speakerInput)
      ),
      h('div', { class: 'field' },
        h('label', { class: 'field-label' }, '消息内容'), textArea,
        h('label', { class: 'check', style: 'margin-top:8px' }, addressedBox, h('span', {}, '当作被 @ 了'))
      ),
      h('div', { class: 'toolbar-row' }, runBtn),
      resultHost
    ]);
  }

  load();

  return () => { disposed = true; };
}
