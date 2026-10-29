/* ============================================================
   思想层页：在线状态（P1）+ 配额与准入记账（P2）
   两层都还不调模型：P1 判「现在能不能打扰这个人」，P2 判「这条想法准不准」
   ============================================================ */

import { h, card, badge, fmtTime, toast, kvRow } from '../app.js';

const STATE_LABEL = { online: '在线', offline: '离线', unknown: '未知' };
const STATE_KIND = { online: 'ok', offline: 'danger', unknown: 'warn' };
const SOURCE_LABEL = {
  manual: '手动设定',
  self_report: '用户自报',
  activity: '最近活动',
  schedule: '静默时段',
  window: '允许时段',
};
const GATE_LABEL = {
  disabled: '总开关关闭',
  intent: '意图不在白名单',
  quiet: '静默时段',
  presence: '目标不允许被打扰',
  cooldown: '同一对象冷却中',
  hourly: '每小时上限',
  daily: '每日上限',
};

/* 意图子 AI 给出的开口动机 */
const INTENT_LABEL = {
  answer: '回答',
  ask: '追问',
  care: '接住',
  share: '分享',
  correct: '纠正',
  none: '无',
  skipped: '未启用',
  error: '判定失败',
};
const INTENTS = [
  ['speech', '在群里说话'],
  ['private', '私信某人'],
  ['poke', '戳一戳'],
  ['file', '发文件'],
];

export function render(container, api) {
  let disposed = false;
  let cfg = null;

  const presenceHost = h('div', {});
  const cfgForm = h('div', {});
  const counterHost = h('div', {});
  const ledgerHost = h('div', {});
  const testResult = h('div', {});
  const statusHost = h('div', {});
  const roundsHost = h('div', {});
  const topoHost = h('div', {});
  const wfHost = h('div', {});
  const weightsHost = h('div', {});

  // 排版原则：常看的在前、配置项紧接着、会越来越长的折叠起来、很少用的放最后。
  //
  // 原来的顺序是「加权意向」和「最近几轮」夹在中间，这两个会越积越长，
  // 一涨起来下面的配置项和记账就得滚很久才找得到。
  container.append(
    card('运行状态', [statusHost], [
      h('button', {
        class: 'btn btn-primary btn-sm', type: 'button', style: 'margin-right:8px',
        onclick: (e) => runOnce(e.target),
      }, '立刻想一次'),
      h('span', { class: 'tiny muted' }, '每 3 秒刷新'),
    ]),
    card('思想拓扑', [topoHost],
      h('span', { class: 'tiny muted' }, '最近一轮：意图 → 两路发散 → 合成 → 收敛 → 决策')),
    card('耗时瀑布', [wfHost],
      h('span', { class: 'tiny muted' }, '每一段花了多少毫秒')),
    card('思想层配额', [cfgForm]),
    card('在线状态', [presenceHost],
      h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => load() }, '刷新')),
    card('加权意向', [weightsHost],
      h('button', { class: 'btn btn-ghost btn-sm', type: 'button', onclick: () => loadWeights() }, '刷新')),
    card('最近几轮（发散 » 收敛）', [roundsHost]),
    card('最近记账', [counterHost, ledgerHost]),
    card('投递一条想法（试闸门）', [buildTester(), testResult])
  );

  /* ---------------------------------------------------------------- 运行状态 */

  const OUTCOME_LABEL = {
    sent: '主动说话了',
    no_idea: '想了，但决定什么都不做',
    blocked: '被闸门拦下',
    error: '出错',
    skipped: '跳过',
  };
  const OUTCOME_KIND = { sent: 'ok', no_idea: 'muted', blocked: 'warn', error: 'danger', skipped: 'muted' };
  const PHASE_KIND = { '发散中': 'accent', '收敛中': 'warn', '发送中': 'ok' };

  function paintStatus(s) {
    const nowSec = Math.floor(Date.now() / 1000);
    const nextIn = Math.max(0, (s.next_tick_at || 0) - nowSec);
    const phaseSec = s.phase_started_at ? Math.max(0, nowSec - s.phase_started_at) : 0;

    const running = !!s.running;
    const phaseText = running ? (s.phase || '在想') : (s.enabled ? '待命（等下一次发散）' : '已关闭');

    const rows = [
      kvRow('状态', running
        ? h('span', { class: 'row' }, h('span', { class: 'dot ok' }), h('span', {}, phaseText),
            h('span', { class: 'tiny muted' }, '　已进行 ' + phaseSec + ' 秒'))
        : badge(phaseText, s.enabled ? 'muted' : 'danger')),
      kvRow('当前在想什么', running ? (s.phase_detail || '-') : (s.last_skip || '—')),
      kvRow('下次发散', s.enabled ? (nextIn > 0 ? nextIn + ' 秒后' : '即将开始') : '—'),
      kvRow('发散频率', '每 ' + (s.interval_minutes || '-') + ' 分钟'),
      kvRow('轮次预算', '本小时 ' + (s.rounds_last_hour || 0) + '/' + (s.max_rounds_per_hour || '-') +
        '　今天 ' + (s.rounds_today || 0) + '/' + (s.max_rounds_per_day || '-')),
      kvRow('只在有对话时想', '最近 ' + (s.active_within_minutes || '-') + ' 分钟内有活动'),
    ];

    statusHost.replaceChildren(
      h('div', { class: 'kv' }, ...rows),
      h('div', { class: 'tiny muted', style: 'margin-top:8px' },
        '发散过程不进入对话上下文；只有真的决定说话时，才会把说的话和理由记录下来。')
    );
  }

  /* ---------------- 思想拓扑：意图 → 两路发散 → 合成 → 收敛 → 决策 ---------------- */

  const KIND_LABEL = { group: '群', private: '私聊', poke: '戳一戳', file: '发文件' };

  function topoNode(kind, title, lines, extra) {
    return h('div', { class: 'topo-node topo-' + kind },
      h('div', { class: 'topo-title' }, title),
      ...(lines || []).map((t) => h('div', { class: 'topo-line' }, t)),
      extra ? h('div', { class: 'topo-extra' }, extra) : null
    );
  }

  function paintTopo(rounds, weights) {
    const r = (rounds || [])[0];

    if (!r) {
      topoHost.replaceChildren(h('div', { class: 'empty' },
        '还没有想过，所以没有拓扑。等一轮跑完再看。'));
      return;
    }

    const it = r.intent || {};
    const hasIntent = it.kind && it.kind !== 'none' && it.kind !== 'skipped';
    const du = r.div_user || [];
    const ds = r.div_self || [];
    const decisions = r.decisions || [];
    const ws = r.weights || [];

    const intentNode = topoNode('intent', '意图子 AI',
      [hasIntent ? (INTENT_LABEL[it.kind] || it.kind) + '：' + (it.about || '') : '没有开口动机'],
      hasIntent ? ('置信 ' + (it.confidence || 0)) : (it.not_yet || ''));

    const userNode = topoNode('user', '发散 · 上下文',
      du.length ? du.slice(0, 2) : ['（这一路没有念头）'],
      du.length + ' 条 · 用户需求');

    const selfNode = topoNode('self', '发散 · 记忆碎片',
      ds.length ? ds.slice(0, 2) : ['（这一路没有念头）'],
      ds.length + ' 条 · 自我意图');

    const mergeNode = topoNode('merge', '合成思想',
      [(r.thoughts || []).length + ' 条念头'],
      '第 ' + (r.version || 1) + ' 版');

    const convNode = topoNode('conv', '收敛',
      r.thought ? ['「' + r.thought + '」'] : ['（没有答复）'],
      r.topic ? ('主题：' + r.topic) : '');

    const decNode = topoNode('dec', '决策',
      decisions.length
        ? decisions.map((d) => (d.kind === 'speak' ? '说话：' : d.kind === 'boost' ? '加权：' : '不做：') + (d.text || d.reason || ''))
        : ['（这一轮不做什么）'],
      r.outcome === 'sent' ? '已发出' : (r.outcome || ''));

    const wNode = topoNode('weight', '加权意向',
      ws.length ? ws.slice(0, 3).map((w) => (w.title || IdeaWeightTitle(w)) + ' ' + (w.weight || 0).toFixed(2)) : ['（这一轮没有新增）'],
      ws.length ? ws.length + ' 条' : '');

    const col = (...nodes) => h('div', { class: 'topo-col' }, ...nodes);
    const arrow = () => h('div', { class: 'topo-arrow' }, '→');

    topoHost.replaceChildren(
      h('div', { class: 'topo' },
        col(intentNode), arrow(),
        col(userNode, selfNode), arrow(),
        col(mergeNode), arrow(),
        col(convNode, decNode), arrow(),
        col(wNode)
      ),
      h('div', { class: 'tiny muted', style: 'margin-top:10px' },
        '「上下文」那一路只想对方要什么，「记忆碎片」那一路只想我自己在意什么；两路分开跑，再合成一份思想，避免一边压倒另一边。')
    );
  }

  function IdeaWeightTitle(w) {
    if (w.kind === 'private') return '和' + (w.who || w.target || '某人') + '私聊';
    if (w.kind === 'poke') return '戳一下' + (w.who || '某人');
    return '在群里发言';
  }

  /* ---------------- 耗时瀑布 ---------------- */

  function paintWaterfall(rounds) {
    const r = (rounds || [])[0];

    if (!r) {
      wfHost.replaceChildren(h('div', { class: 'empty' }, '还没有一轮跑完。'));
      return;
    }

    const steps = [
      ['意图判定', r.ms_intent],
      ['发散·上下文', r.ms_div_user],
      ['发散·记忆碎片', r.ms_div_self],
      ['收敛', r.ms_converge],
      ['加权合并', r.ms_weight],
    ].filter(([, ms]) => typeof ms === 'number' && ms > 0);

    const total = steps.reduce((a, [, ms]) => a + ms, 0);

    if (total === 0) {
      wfHost.replaceChildren(h('div', { class: 'empty' },
        '这一轮没有分段耗时（老记录里没有这些字段），跑完新一轮就会出现。'));
      return;
    }

    const max = Math.max(...steps.map(([, ms]) => ms));

    wfHost.replaceChildren(
      h('div', { class: 'wf2' },
        ...steps.map(([label, ms]) => h('div', { class: 'wf2-row' },
          h('span', { class: 'wf2-label tiny muted' }, label),
          h('div', { class: 'wf2-track' },
            h('div', { class: 'wf2-bar', style: 'width:' + Math.max(2, Math.round((ms / max) * 100)) + '%' })),
          h('span', { class: 'wf2-ms tiny mono' }, ms + 'ms')
        ))
      ),
      h('div', { class: 'tiny muted', style: 'margin-top:8px' },
        '合计 ' + total + 'ms（不含发送），整轮 ' + (r.ms || 0) + 'ms')
    );
  }

  /* ---------------- 加权意向（title + body） ---------------- */

  let weightItems = [];
  let weightCfg = null;
  let weightsOpen = false;

  async function loadWeights() {
    try {
      const data = await api('/api/idea/weights');
      if (disposed) return;
      weightItems = data.items || [];
      weightCfg = data.config || null;
      weightStats = data;
      paintWeights();
    } catch (err) {
      if (disposed || err.status === 401) return;
      weightsHost.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  let weightStats = null;

  function paintWeights() {
    const cfg = weightCfg || {};
    const actAt = cfg.act_threshold || 0.95;
    const legacy = (weightStats && weightStats.legacy) || 0;

    if (!weightItems.length) {
      weightsHost.replaceChildren(h('div', { class: 'empty' },
        '还没有加权意向。收敛层选择「加权重」时会往这里写一条。'));
      return;
    }

    const mature = weightItems.filter((w) => w.mature);

    /* 会越积越长 —— 默认只露出权重最高的几条，其余折叠 */
    const shown = weightsOpen ? weightItems : weightItems.slice(0, 4);

    const rows = shown.map((w) => h('div', { class: 'weight-row' },
      h('div', { class: 'weight-head' },
        badge(w.title || IdeaWeightTitle(w), w.mature ? 'accent' : 'muted'),
        /* 自我意象 vs 对方需求：一个自行去做，一个等时机 */
        w.origin === 'self' ? badge('自我', 'ok') : badge('需求', 'muted'),
        w.mature ? badge('够重了', 'warn') : null,
        h('span', { class: 'weight-bar' },
          h('span', {
            class: 'weight-fill',
            style: 'width:' + Math.round((w.weight || 0) * 100) + '%' +
              (w.mature ? ';background:linear-gradient(90deg,var(--warn),rgba(200,145,44,.5))' : ''),
          })),
        h('span', { class: 'tiny mono muted' }, (w.weight || 0).toFixed(2)),
        w.reinforced > 1 ? h('span', { class: 'tiny muted' }, '×' + w.reinforced) : null,
        h('button', {
          class: 'btn btn-ghost btn-sm', type: 'button',
          title: '切到另一边：自我意象自行去做 / 对方需求等时机',
          onclick: async () => {
            try {
              const to = w.origin === 'self' ? 'context' : 'self';
              await api('/api/idea/weights/' + encodeURIComponent(w.id) + '/origin', {
                method: 'POST', body: JSON.stringify({ origin: to }),
              });
              if (disposed) return;
              toast(to === 'self' ? '改为自我意象（自行去做）' : '改为对方需求（等时机）', 'ok');
              loadWeights();
            } catch (err) { toast('改判失败：' + err.message, 'danger'); }
          },
        }, w.origin === 'self' ? '改判为需求' : '改判为自我'),
        h('button', {
          class: 'btn btn-ghost btn-sm', type: 'button',
          onclick: async () => {
            try {
              await api('/api/idea/weights/' + encodeURIComponent(w.id) + '/done', { method: 'POST' });
              if (disposed) return;
              toast('已标记做完，移出列表', 'ok');
              loadWeights();
            } catch (err) { toast('操作失败：' + err.message, 'danger'); }
          },
        }, '标记做完'),
        h('button', {
          class: 'btn btn-danger btn-sm', type: 'button',
          onclick: async () => {
            try {
              await api('/api/idea/weights/' + encodeURIComponent(w.id), { method: 'DELETE' });
              if (disposed) return;
              loadWeights();
            } catch (err) { toast('删除失败：' + err.message, 'danger'); }
          },
        }, '删除')
      ),
      /* **一件事一条** —— 不再是一句被缝起来又被截断的流水句 */
      h('div', {}, ...(w.items || []).map((it) => h('div', { class: 'weight-item' },
        h('span', { class: 'weight-item-dot' }, '·'),
        h('span', {}, it.text))))
    ));

    const foot = [];
    if (weightItems.length > 4) {
      foot.push(h('button', {
        class: 'btn btn-ghost btn-sm', type: 'button',
        onclick: () => { weightsOpen = !weightsOpen; paintWeights(); },
      }, weightsOpen ? '收起' : ('展开全部 ' + weightItems.length + ' 条')));
    }
    if (legacy > 0) {
      foot.push(h('button', {
        class: 'btn btn-ghost btn-sm', type: 'button',
        onclick: async () => {
          try {
            const r = await api('/api/idea/weights/cleanup-legacy', { method: 'POST' });
            if (disposed) return;
            toast('已合并/清理 ' + (r.removed || 0) + ' 条历史遗留', 'ok');
            loadWeights();
          } catch (err) { toast('清理失败：' + err.message, 'danger'); }
        },
      }, '清理 ' + legacy + ' 条历史遗留'));
    }

    weightsHost.replaceChildren(
      h('div', { class: 'tiny muted', style: 'margin-bottom:8px' },
        '同一落点（kind + target）的意向往一条上累加；到 ' + actThText(actAt) +
        ' 就「够重了」，不再加，转为下一轮思考的开口动机。没人管的按半衰期 ' +
        (cfg.half_life_days || 3) + ' 天慢慢变淡，掉到 ' + (cfg.forget_below || 0.08) + ' 以下就完全遗忘。'),
      mature.length > 0
        ? h('div', { class: 'note note-warn' },
            '有 ' + mature.length + ' 条已经够重但一直没做——它们会被当成开口动机，该找机会去做，别再继续惦记。')
        : null,
      ...rows,
      foot.length ? h('div', { class: 'toolbar-row', style: 'margin-top:6px' }, ...foot) : null
    );
  }

  function actThText(v) {
    return String(v).replace(/0+$/, '').replace(/\.$/, '');
  }

  function paintRounds(rounds) {
    const list = rounds || [];
    if (list.length === 0) {
      roundsHost.replaceChildren(h('div', { class: 'empty' },
        '还没有想过。要么是思想层没开，要么是最近没有对话活动（只在有人说话之后才值得想）。'));
      return;
    }

    roundsHost.replaceChildren(...list.map((r, i) => {
      r.__latest = i === 0;
      return renderRound(r);
    }));
  }

  /* ---------------- 一轮的四段：发散 / 收敛 / 决策 / 落定 ---------------- */

  function stageHead(no, title, extra) {
    return h('div', { class: 'stage-head' },
      h('span', { class: 'stage-no' }, String(no)),
      h('span', { class: 'stage-title' }, title),
      extra ? h('span', { class: 'stage-extra' }, extra) : null
    );
  }

  function kvLine(label, value, tone) {
    return h('div', { class: 'stage-line' },
      h('span', { class: 'stage-key' }, label),
      h('span', { class: 'stage-val' + (tone ? ' ' + tone : '') }, value)
    );
  }

  function bulletList(items, emptyText) {
    if (!items || items.length === 0) {
      return h('div', { class: 'stage-line' }, h('span', { class: 'stage-key' }, '—'),
        h('span', { class: 'stage-val muted tiny' }, emptyText || '（空）'));
    }
    return h('div', {}, ...items.map((t) => h('div', { class: 'stage-line' },
      h('span', { class: 'stage-key' }, '·'),
      h('span', { class: 'stage-val' }, t))));
  }

  function renderRound(r) {
    const inputs = r.inputs || {};
    const thoughts = r.thoughts || [];
    const decisions = r.decisions || [];

    /* —— 1 发散：分层的核心输入 —— */
    const inputNodes = [];
    const selfLines = (inputs.self || [])
      .map((s) => String(s).replace(/\s*\n\s*/g, ' ').trim())
      .filter(Boolean);
    if (selfLines.length) inputNodes.push(kvLine('自我', selfLines.join('　')));
    if (inputs.recent && inputs.recent.length) inputNodes.push(kvLine('最近信息', inputs.recent.join(' / ')));
    if (inputs.memories && inputs.memories.length) inputNodes.push(kvLine('记忆碎片', inputs.memories.join(' / ')));
    if (inputs.profile && inputs.profile.length) inputNodes.push(kvLine('画像/模式', inputs.profile.join(' / ')));
    if (inputs.weights && inputs.weights.length) inputNodes.push(kvLine('在意的事', inputs.weights.join(' / ')));
    // 接口用 SnakeCaseLower（ConfigureHttpJsonOptions），这里是 prev_thought / prev_thoughts；
    // 兼容 camelCase 写法，避免改了序列化策略之后前端静默显示空白。
    const prevThought = inputs.prev_thought || inputs.prevThought || '';
    const prevThoughts = inputs.prev_thoughts || inputs.prevThoughts || [];
    if (prevThought) inputNodes.push(kvLine('上次思绪', prevThought));
    if (prevThoughts.length) inputNodes.push(kvLine('上次念头', prevThoughts.join(' / ')));

    const stage1 = h('div', { class: 'stage' },
      stageHead(1, '发散', '第 ' + (r.version || 1) + ' 版'),
      inputNodes.length
        ? h('div', { class: 'stage-body' }, ...inputNodes)
        : h('div', { class: 'stage-body' },
            h('div', { class: 'stage-line' },
              h('span', { class: 'stage-key' }, '输入'),
              h('span', { class: 'stage-val muted tiny' }, '（这一轮没有分层快照，点下方「看母题原文」）'))),
      h('div', { class: 'stage-sub' }, '念头'),
      bulletList(thoughts, '（没有念头）')
    );

    /* —— 2 收敛：思绪 —— */
    const repeat = Number(r.topic_repeat) || 0;
    const stage2 = h('div', { class: 'stage' },
      stageHead(2, '收敛', repeat >= 3 ? '卡住了：连续 ' + repeat + ' 轮同题' : '压成一句'),
      h('div', { class: 'stage-body' },
        r.topic
          ? h('div', { class: 'stage-line' },
              h('span', { class: 'stage-key' }, '主题'),
              h('span', { class: 'stage-val' },
                r.topic,
                repeat >= 3
                  ? h('span', { class: 'tiny', style: 'color:var(--warn)' },
                      '　连续 ' + repeat + ' 轮没换过')
                  : (repeat === 2
                      ? h('span', { class: 'tiny muted' }, '　连着第 2 轮')
                      : null)))
          : null,
        r.thought
          ? h('div', { class: 'thought-quote' }, r.thought)
          : h('div', { class: 'stage-line' },
              h('span', { class: 'stage-val muted tiny' }, '（这一轮没有留下思绪）')))
    );

    /* —— 3 决策：列出一系列行为 —— */
    const decisionNodes = decisions.length
      ? decisions.map((d) => {
          if (d.kind === 'speak') {
            return h('div', { class: 'stage-line' },
              h('span', { class: 'stage-key' }, badge('说话', 'ok')),
              h('span', { class: 'stage-val' },
                '「' + (d.text || '') + '」' + (d.target ? ' → ' + d.target : ''),
                d.reason ? h('span', { class: 'tiny muted' }, '　因为：' + d.reason) : null));
          }
          if (d.kind === 'boost') {
            return h('div', { class: 'stage-line' },
              h('span', { class: 'stage-key' }, badge('提权重', 'accent')),
              h('span', { class: 'stage-val' }, d.text || ''));
          }
          return h('div', { class: 'stage-line' },
            h('span', { class: 'stage-key' }, badge('不做', 'muted')),
            h('span', { class: 'stage-val muted' }, d.reason || ''));
        })
      : [h('div', { class: 'stage-line' },
          h('span', { class: 'stage-val muted tiny' }, '（这一轮没有留下决策）'))];

    const stage3 = h('div', { class: 'stage' },
      stageHead(3, '决策', decisions.length + ' 条'),
      h('div', { class: 'stage-body' }, ...decisionNodes)
    );

    /* —— 4 落定：实际做了什么 —— */
    const settled = [];
    if (r.outcome === 'sent') {
      settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, badge('已发出', 'ok')),
        h('span', { class: 'stage-val' }, r.spoke || '')));
      if (r.why) settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, '为什么'),
        h('span', { class: 'stage-val muted' }, r.why)));
    } else if (r.outcome === 'blocked') {
      settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, badge('被拦下', 'warn')),
        h('span', { class: 'stage-val' }, GATE_LABEL[r.blocked_by] || r.blocked_by || '-')));
      if (r.detail) settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, '原因'),
        h('span', { class: 'stage-val muted' }, r.detail)));
    } else if (r.outcome === 'error') {
      settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, badge('出错', 'danger')),
        h('span', { class: 'stage-val' }, r.detail || '')));
    } else {
      settled.push(h('div', { class: 'stage-line' },
        h('span', { class: 'stage-key' }, badge('什么都没做', 'muted')),
        h('span', { class: 'stage-val muted' }, r.why || '')));
    }

    const stage4 = h('div', { class: 'stage' },
      stageHead(4, '落定', r.boost ? '权重 ' + (r.boost_amount || 0).toFixed(2) : ''),
      h('div', { class: 'stage-body' }, ...settled)
    );

    /* —— 母题原文（折叠） —— */
    const subject = (r.subject || '（没有留下母题）').trim();
    const subjectBox = h('div', { class: 'subject-box', style: 'display:none' }, subject);
    const subjBtn = h('button', {
      class: 'btn btn-ghost btn-sm', type: 'button',
      onclick: (e) => {
        e.stopPropagation();
        const open = subjectBox.style.display === 'none';
        subjectBox.style.display = open ? 'block' : 'none';
        subjBtn.textContent = open ? '收起母题' : '看母题原文';
      },
    }, '看母题原文');

    /* —— 意图子 AI 的判定（在发散之前就定了这一轮要不要想） —— */
    const it = r.intent || {};
    const intentKind = String(it.kind || '');
    const hasIntent = intentKind && intentKind !== 'none' && intentKind !== 'skipped';
    const intentStrip = intentKind
      ? h('div', { class: 'intent-strip' + (hasIntent ? ' has' : '') },
          hasIntent
            ? [
                badge('动机 · ' + (INTENT_LABEL[intentKind] || intentKind), 'accent'),
                h('span', { class: 'intent-about' }, it.about || ''),
                it.why ? h('span', { class: 'tiny muted' }, '　因为 ' + it.why) : null,
                it.confidence ? h('span', { class: 'intent-conf' }, '置信 ' + it.confidence) : null,
              ]
            : [
                badge('动机 · 无', 'muted'),
                h('span', { class: 'tiny muted' }, it.not_yet || '此刻没有具体的开口动机'),
              ])
      : null;

    /* —— 折叠：收起时只留一行「时间 · 对象 · tokens · 耗时」，点开才展开四段 —— */
    const body = h('div', { class: 'round-body' }, intentStrip, stage1, stage2, stage3, stage4,
      h('div', { class: 'round-foot' }, subjBtn, subjectBox));

    const caret = h('span', { class: 'round-caret' }, '▸');

    const head = h('div', {
      class: 'round-head round-clickable', role: 'button', tabindex: '0',
      onclick: () => toggle(),
      onkeydown: (e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } },
    },
      caret,
      h('span', { class: 'mono tiny muted' }, fmtTime(r.ts)),
      badge(r.target_name || r.target || '-', 'muted'),
      h('span', { class: 'tiny muted' },
        'tokens ' + (r.tokens || 0) + '　耗时 ' + (r.ms || 0) + 'ms')
    );

    function toggle(force) {
      const open = typeof force === 'boolean' ? force : body.style.display === 'none';
      body.style.display = open ? 'block' : 'none';
      caret.textContent = open ? '▾' : '▸';
      cardEl.classList.toggle('open', open);
    }

    const cardEl = h('div', { class: 'round-card' }, head, body);

    // 最新一轮默认展开，其余收起——不然几十轮铺满整页
    toggle(!!r.__latest);

    return cardEl;
  }

  async function runOnce(btnEl) {
    if (btnEl) { btnEl.disabled = true; btnEl.textContent = '在想…'; }
    try {
      const r = await api('/api/idea/run-once', { method: 'POST', body: JSON.stringify({}) });
      if (disposed) return;
      toast(r.ok ? (r.message || '已想一轮') : ('没想成：' + r.message), r.ok ? 'ok' : 'warn');
      loadStatus();
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('触发失败：' + err.message, 'danger');
    } finally {
      if (btnEl) { btnEl.disabled = false; btnEl.textContent = '立刻想一次'; }
    }
  }

  async function loadStatus() {
    try {
      const s = await api('/api/idea/status');
      if (disposed) return;
      paintStatus(s);

      // 只有内容变化时才重画列表，避免展开状态被冲掉
      const sig = JSON.stringify((s.rounds || []).map((r) => r.id));
      if (sig !== lastRoundsSig) {
        lastRoundsSig = sig;
        paintRounds(s.rounds);
        paintTopo(s.rounds, weightItems);
        paintWaterfall(s.rounds);
      }
    } catch (err) {
      if (disposed || err.status === 401) return;
    }
  }

  let lastRoundsSig = '';
  let statusTimer = null;

  /* ---------------------------------------------------------------- 加载 */

  async function load() {
    try {
      const [pres, idea] = await Promise.all([api('/api/presence'), api('/api/idea')]);
      if (disposed) return;
      cfg = idea.config;
      paintPresence(pres);
      paintConfig();
      paintCounters(idea.counters);
      paintLedger(idea.recent);
      await loadWeights();
    } catch (err) {
      if (disposed || err.status === 401) return;
      presenceHost.replaceChildren(h('div', { class: 'note note-danger' }, '加载失败：' + err.message));
    }
  }

  /* ---------------------------------------------------------------- 在线状态 */

  let presenceItems = [];
  let presencePage = 0;
  const PRESENCE_PER_PAGE = 5;

  function paintPresence(pres) {
    presenceItems = pres.items || [];
    const counters = pres.counters || {};

    const manualInput = h('input', { class: 'input', placeholder: 'QQ 号', style: 'max-width:150px' });
    const manualNote = h('input', { class: 'input', placeholder: '备注（如：说要睡觉）', style: 'flex:1 1 200px' });
    const manualTtl = h('input', {
      class: 'input', type: 'number', value: '8', min: '0', max: '720', style: 'max-width:90px',
    });

    const addBtn = h('button', {
      class: 'btn btn-primary btn-sm', type: 'button',
      onclick: () => setManual(manualInput.value.trim(), 'offline', manualNote.value.trim(),
        Number(manualTtl.value) || 0),
    }, '设为离线');

    // 一面 5 个：人多了翻页，不把整页撑长
    const totalPages = Math.max(1, Math.ceil(presenceItems.length / PRESENCE_PER_PAGE));
    if (presencePage >= totalPages) presencePage = totalPages - 1;
    if (presencePage < 0) presencePage = 0;

    const from = presencePage * PRESENCE_PER_PAGE;
    const pageItems = presenceItems.slice(from, from + PRESENCE_PER_PAGE);

    const rows = pageItems.map((it) => h('tr', {},
      h('td', { class: 'mono' }, it.user_id),
      h('td', {}, it.name || '-'),
      h('td', {}, badge(STATE_LABEL[it.state] || it.state, STATE_KIND[it.state] || 'muted')),
      h('td', {}, badge(SOURCE_LABEL[it.source] || it.source, it.source === 'manual' ? 'accent' : 'muted')),
      h('td', {}, it.can_disturb ? badge('可打扰', 'ok') : badge('不打扰', 'danger')),
      h('td', { class: 'tiny muted' }, it.note || '-'),
      h('td', { class: 'tiny muted' }, it.last_seen ? fmtTime(it.last_seen) : '-'),
      h('td', {},
        btn('在线', 'btn-ok', () => setManual(it.user_id, 'online', '', 0)),
        btn('离线', 'btn-danger', () => setManual(it.user_id, 'offline', '', 0)),
        it.manual
          ? btn('清除手动', 'btn-ghost', () => clearManual(it.user_id))
          : h('span', { class: 'tiny muted' }, '自动')
      )
    ));

    const pager = presenceItems.length > PRESENCE_PER_PAGE
      ? h('div', { class: 'pager' },
          h('button', {
            class: 'btn btn-ghost btn-sm', type: 'button', disabled: presencePage === 0,
            onclick: () => { presencePage = Math.max(0, presencePage - 1); paintPresence(pres); },
          }, '上一页'),
          h('span', { class: 'tiny muted' },
            (presencePage + 1) + ' / ' + totalPages + '　共 ' + presenceItems.length + ' 人'),
          h('button', {
            class: 'btn btn-ghost btn-sm', type: 'button', disabled: presencePage >= totalPages - 1,
            onclick: () => { presencePage = Math.min(totalPages - 1, presencePage + 1); paintPresence(pres); },
          }, '下一页'))
      : null;

    presenceHost.replaceChildren(
      h('div', { class: 'note' },
        '只列**可以私聊的人**：群里说过话但没加好友的号不会出现在这里——思想层最后的动作要落定成「私聊谁」，',
        ' 没私聊过的人根本发不出去，把他们算成在线对象只会让一整轮思考注定失败。'),
      h('div', { class: 'note' },
        '判定优先级：手动设定 > 用户自报（「我要睡了 / 在上课」）> 最近活动 > 静默时段 > 允许时段。',
        ' 判为离线时，AI 不会主动找这个人——包括私信、戳一戳、发文件。'),
      h('div', { class: 'kv' },
        kvRow('最近多少分钟说话算在线', (pres.config || {}).activity_online_minutes + ' 分钟'),
        kvRow('静默时段', ((pres.config || {}).quiet_hours || {}).start + ' - ' + ((pres.config || {}).quiet_hours || {}).end),
        kvRow('允许时段（判不出来时）', ((pres.config || {}).allow_window || {}).start + ' - ' + ((pres.config || {}).allow_window || {}).end),
        kvRow('今日想法', '共 ' + (counters.total || 0) + ' 条，放行 ' + (counters.allowed || 0) +
          ' 条，拦下 ' + (counters.blocked || 0) + ' 条')
      ),
      h('div', { class: 'toolbar-row', style: 'margin-top:10px' },
        h('span', { class: 'tiny muted' }, '手动设置某人：'),
        manualInput, manualNote,
        h('span', { class: 'tiny muted' }, '有效小时'), manualTtl, addBtn),
      presenceItems.length === 0
        ? h('div', { class: 'empty' }, '还没有可私聊的人。要先有私聊消息，这个人才会出现在这里。')
        : h('div', { class: 'table-wrap', style: 'margin-top:12px' }, h('table', { class: 'table' },
            h('thead', {}, h('tr', {},
              h('th', {}, 'QQ'), h('th', {}, '名字'), h('th', {}, '状态'),
              h('th', {}, '来源'), h('th', {}, '能否打扰'), h('th', {}, '备注'),
              h('th', {}, '最近活动'), h('th', {}, '手动设置')
            )),
            h('tbody', {}, ...rows)
          )),
      pager
    );
  }

  function btn(label, cls, onclick) {
    return h('button', {
      class: 'btn ' + cls + ' btn-sm', type: 'button', style: 'margin-right:6px',
      onclick: (e) => { e.stopPropagation(); onclick(); },
    }, label);
  }

  async function setManual(userId, state, note, ttl) {
    if (!userId) { toast('先填 QQ 号', 'warn'); return; }
    try {
      await api('/api/presence/manual', {
        method: 'POST',
        body: JSON.stringify({ user_id: userId, state, note, ttl_hours: ttl }),
      });
      if (disposed) return;
      toast('已设为' + (STATE_LABEL[state] || state), 'ok');
      load();
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('设置失败：' + err.message, 'danger');
    }
  }

  async function clearManual(userId) {
    try {
      await api('/api/presence/manual/' + encodeURIComponent(userId), { method: 'DELETE' });
      if (disposed) return;
      toast('已清除手动设定，回到自动判定', 'ok');
      load();
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('清除失败：' + err.message, 'danger');
    }
  }

  /* ---------------------------------------------------------------- 配置 */

  /** 勾选框字段（(label, hint, 初值) 顺序与 gate.js 保持一致） */
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

  function numField(label, key, step, hint) {
    const input = h('input', {
      class: 'input', type: 'number', step: step || '1', value: String(cfg[key] ?? ''),
    });
    return {
      el: h('div', { class: 'field' },
        h('label', { class: 'field-label' }, label), input,
        hint ? h('div', { class: 'field-hint' }, hint) : null),
      get: () => Number(input.value),
    };
  }

  /** 数字字段：直接给初值（用于配置里嵌套一层的字段，如 think.max_rounds_per_hour） */
  function numValue(label, initial, step, hint) {
    const input = h('input', {
      class: 'input', type: 'number', step: step || '1',
      value: initial === null || initial === undefined ? '' : String(initial),
    });
    return {
      el: h('div', { class: 'field' },
        h('label', { class: 'field-label' }, label), input,
        hint ? h('div', { class: 'field-hint' }, hint) : null),
      get: () => Number(input.value),
    };
  }

  function timeField(label, key, hint) {
    const a = h('input', { class: 'input', type: 'time', value: (cfg[key] || {}).start || '00:00' });
    const b = h('input', { class: 'input', type: 'time', value: (cfg[key] || {}).end || '00:00' });
    return {
      el: h('div', { class: 'field' },
        h('label', { class: 'field-label' }, label),
        h('div', { class: 'row' }, a, h('span', { class: 'tiny muted' }, '到'), b),
        hint ? h('div', { class: 'field-hint' }, hint) : null),
      get: () => ({ start: a.value, end: b.value }),
    };
  }

  function paintConfig() {
    const enabledBox = h('input', { type: 'checkbox' });
    enabledBox.checked = !!cfg.enabled;

    const f = {
      interval: numField('发散频率（分钟）', 'interval_minutes', '1'),
      cooldown: numField('同一对象冷却（小时）', 'per_target_cooldown_hours', '1', '多久内不再打扰同一个人'),
      hourly: numField('每小时上限', 'per_hour_limit'),
      daily: numField('每日上限', 'daily_limit'),
      onlineMin: numField('活动在线阈值（分钟）', 'activity_online_minutes', '1'),
      unknownH: numField('活动线索失效（小时）', 'activity_unknown_hours', '1'),
      sleepH: numField('自报睡觉 TTL（小时）', 'self_report_sleep_hours', '1'),
      busyH: numField('自报忙碌 TTL（小时）', 'self_report_busy_hours', '1'),
      quiet: timeField('静默时段（硬禁）', 'quiet_hours', '这段时间内任何自发行为都不允许'),
      allow: timeField('允许时段（判不出来时）', 'allow_window', '在线状态未知时，落在这段里才放行'),
    };

    // 发散预算：这三个是"这一层要花模型调用"的真实闸门，以前后台改不到，
    // 只能在 config/idea.json 手改，而且一点保存就被重置——所以摆到界面上来。
    const think = cfg.think || {};
    const t = {
      enabled: checkField('启用发散/收敛这一层', '关掉之后只看在线状态和准入记账，不再调模型', think.enabled),
      perHour: numValue('每小时最多想几次', think.max_rounds_per_hour, '1', 'think.max_rounds_per_hour；超了本轮直接跳过'),
      perDay: numValue('每天最多想几次', think.max_rounds_per_day, '1', 'think.max_rounds_per_day'),
      activeMin: numValue('多久内有对话才值得想（分钟）', think.active_within_minutes, '1', 'think.active_within_minutes；安静太久就不空转'),
      targets: numValue('一轮最多想几个对象', think.max_targets_per_round, '1', 'think.max_targets_per_round'),
      thoughts: numValue('一次最多冒几个念头', think.max_candidates, '1', 'think.max_candidates'),
    };

    // 加权意向的生命周期：够重就动手 / 没人管就慢慢忘 / 忘干净就移除
    const wt = cfg.weight || {};
    const wf = {
      act: numValue('够重的门槛', wt.act_threshold, '0.05', '权重到这条线就不再叠加，转为下一轮的开口动机'),
      halfLife: numValue('半衰期（天）', wt.half_life_days, '0.5', '没人管的意向按这条曲线慢慢变淡'),
      forget: numValue('遗忘门槛', wt.forget_below, '0.01', '掉到这个值以下整条移除（完全遗忘）'),
      buckets: numValue('最多保留几个落点', wt.max_buckets, '1'),
      items: numValue('一个落点最多几件事', wt.max_items, '1'),
    };

    const intentBoxes = INTENTS.map(([key, label]) => {
      const box = h('input', { type: 'checkbox' });
      box.checked = (cfg.allowed_intents || []).includes(key);
      return { key, el: h('label', { class: 'check' }, box, h('span', {}, label)), box };
    });

    const saveBtn = h('button', {
      class: 'btn btn-primary', type: 'button', onclick: () => save(saveBtn, enabledBox, f, intentBoxes, t, wf),
    }, '保存并立即生效');

    cfgForm.replaceChildren(
      h('div', { class: 'note' },
        '第一版建议只开「在群里说话」。私信、戳一戳、发文件这类直接找人的动作先关着——',
        ' 发错了最尴尬，等配额跑稳再打开。'),
      h('div', { class: 'form-grid' },
        h('div', { class: 'field' },
          h('label', { class: 'check' }, enabledBox, h('span', {}, '启用思想层'))),
        f.interval.el, f.cooldown.el, f.hourly.el, f.daily.el,
        f.onlineMin.el, f.unknownH.el, f.sleepH.el, f.busyH.el,
        f.quiet.el, f.allow.el
      ),
      h('div', { class: 'field' },
        h('label', { class: 'field-label' }, '发散预算（每小时/每天的次数闸门）'),
        h('div', { class: 'row' }, t.enabled.el)),
      h('div', { class: 'form-grid', style: 'margin-top:8px' },
        t.perHour.el, t.perDay.el, t.activeMin.el, t.targets.el, t.thoughts.el
      ),
      h('div', { class: 'field', style: 'margin-top:14px' },
        h('label', { class: 'field-label' }, '加权意向的生命周期'),
        h('div', { class: 'field-hint' },
          '权重只会加、没有出口的话会卡住：顶在满值不动、永远占着上下文，却从不变成动作。',
          ' 所以这里给三个出口——够重就去做、没人管就慢慢忘、忘干净就移除。')),
      h('div', { class: 'form-grid', style: 'margin-top:4px' },
        wf.act.el, wf.halfLife.el, wf.forget.el, wf.buckets.el, wf.items.el
      ),
      h('div', { class: 'field', style: 'margin-top:14px' },
        h('label', { class: 'field-label' }, '允许的意图'),
        h('div', { class: 'row' }, ...intentBoxes.map((x) => x.el))),
      h('div', { class: 'toolbar-row', style: 'margin-top:12px' }, saveBtn)
    );
  }

  async function save(btnEl, enabledBox, f, intentBoxes, t, wf) {
    const payload = {
      enabled: enabledBox.checked,
      interval_minutes: f.interval.get(),
      per_target_cooldown_hours: f.cooldown.get(),
      per_hour_limit: f.hourly.get(),
      daily_limit: f.daily.get(),
      activity_online_minutes: f.onlineMin.get(),
      activity_unknown_hours: f.unknownH.get(),
      self_report_sleep_hours: f.sleepH.get(),
      self_report_busy_hours: f.busyH.get(),
      quiet_hours: f.quiet.get(),
      allow_window: f.allow.get(),
      allowed_intents: intentBoxes.filter((x) => x.box.checked).map((x) => x.key),
      // 只发这几个子对象；服务端是深合并，没发的（chain / burst / intent）原样保留
      think: {
        enabled: t.enabled.get(),
        max_rounds_per_hour: t.perHour.get(),
        max_rounds_per_day: t.perDay.get(),
        active_within_minutes: t.activeMin.get(),
        max_targets_per_round: t.targets.get(),
        max_candidates: t.thoughts.get(),
      },
      weight: {
        act_threshold: wf.act.get(),
        half_life_days: wf.halfLife.get(),
        forget_below: wf.forget.get(),
        max_buckets: wf.buckets.get(),
        max_items: wf.items.get(),
      },
    };

    btnEl.disabled = true;
    try {
      await api('/api/idea', { method: 'PUT', body: JSON.stringify(payload) });
      if (disposed) return;
      toast('已保存，立即生效', 'ok');
      load();
    } catch (err) {
      if (disposed || err.status === 401) return;
      toast('保存失败：' + err.message, 'danger');
    } finally {
      btnEl.disabled = false;
    }
  }

  /* ---------------------------------------------------------------- 计数与账本 */

  function paintCounters(counters) {
    const c = counters || {};
    const blocked = Object.entries(c)
      .filter(([k]) => k.startsWith('blocked:'))
      .map(([k, v]) => kvRow((GATE_LABEL[k.slice(8)] || k.slice(8)), String(v)));

    counterHost.replaceChildren(
      h('div', { class: 'metrics' },
        metric('今日想法', String(c.total || 0), '含被拦下的'),
        metric('放行', String(c.allowed || 0), ''),
        metric('拦下', String(c.blocked || 0), '')
      ),
      blocked.length > 0
        ? h('div', { class: 'kv', style: 'margin-top:12px' },
            h('div', { class: 'card-sub' }, '被哪道闸拦下'), ...blocked)
        : h('div', { class: 'tiny muted', style: 'margin-top:12px' }, '今天还没有被拦下的记录')
    );
  }

  function metric(label, value, foot) {
    return h('div', { class: 'metric' },
      h('div', { class: 'metric-label' }, label),
      h('div', { class: 'metric-value small' }, value),
      foot ? h('div', { class: 'metric-foot' }, foot) : null);
  }

  function paintLedger(recent) {
    const items = recent || [];
    if (items.length === 0) {
      ledgerHost.replaceChildren(h('div', { class: 'empty' },
        '还没有记账。用下面「投递一条想法」试一下，或被 AI 真正触发之后就会出现。'));
      return;
    }

    const rows = items.map((r) => h('tr', {},
      h('td', { class: 'tiny muted' }, fmtTime(r.ts)),
      h('td', {}, r.intent),
      h('td', { class: 'mono tiny' }, r.target || '-'),
      h('td', { class: 'tiny' }, r.text || '-'),
      h('td', {}, r.allowed ? badge('放行', 'ok') : badge(GATE_LABEL[r.blocked_by] || r.blocked_by, 'danger')),
      h('td', { class: 'tiny muted' }, r.blocked_detail || r.presence || '-')
    ));

    ledgerHost.replaceChildren(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
      h('thead', {}, h('tr', {},
        h('th', {}, '时间'), h('th', {}, '意图'), h('th', {}, '目标'),
        h('th', {}, '内容'), h('th', {}, '结果'), h('th', {}, '说明'))),
      h('tbody', {}, ...rows)
    )));
  }

  /* ---------------------------------------------------------------- 投递测试 */

  function buildTester() {
    const intentSel = h('select', { class: 'select' },
      ...INTENTS.map(([k, l]) => h('option', { value: k }, l + '（' + k + '）')));
    const targetInput = h('input', { class: 'input', placeholder: 'private:10001 或 group:100001' });
    const textInput = h('input', { class: 'input', placeholder: '想说什么 / 做什么' });

    const runBtn = h('button', {
      class: 'btn btn-primary btn-sm', type: 'button',
      onclick: () => runTest(runBtn, intentSel.value, targetInput.value.trim(), textInput.value.trim()),
    }, '走一遍闸门');

    return h('div', {},
      h('div', { class: 'note' },
        '只做判定并记账，**不会真的发出去**。用来确认配额和在线状态是否符合预期。'),
      h('div', { class: 'form-grid' },
        h('div', { class: 'field' }, h('label', { class: 'field-label' }, '意图'), intentSel),
        h('div', { class: 'field' }, h('label', { class: 'field-label' }, '目标'), targetInput)
      ),
      h('div', { class: 'field' }, h('label', { class: 'field-label' }, '内容'), textInput),
      h('div', { class: 'toolbar-row' }, runBtn)
    );
  }

  async function runTest(btnEl, intent, target, text) {
    if (!target) { toast('先填目标', 'warn'); return; }

    btnEl.disabled = true;
    try {
      const r = await api('/api/idea/test', {
        method: 'POST',
        body: JSON.stringify({ intent, target, text, reason: '后台手动投递' }),
      });
      if (disposed) return;

      testResult.replaceChildren(
        h('div', { class: 'note ' + (r.allowed ? 'note-ok' : 'note-warn') },
          r.allowed ? '放行（这条想法可以变成动作）' : '拦下：' + (GATE_LABEL[r.blocked_by] || r.blocked_by)),
        h('div', { class: 'kv', style: 'margin-top:10px' },
          kvRow('目标', target + (r.target_name ? '（' + r.target_name + '）' : '')),
          kvRow('在线状态', r.presence || '-'),
          kvRow('拦下原因', r.blocked_detail || '无'),
          kvRow('今日计数', '共 ' + r.counters.total + '　放行 ' + r.counters.allowed + '　拦下 ' + r.counters.blocked)
        )
      );
      load();
    } catch (err) {
      if (disposed || err.status === 401) return;
      testResult.replaceChildren(h('div', { class: 'note note-danger' }, '判定失败：' + err.message));
    } finally {
      btnEl.disabled = false;
    }
  }

  load();
  loadStatus();
  statusTimer = setInterval(loadStatus, 3000);

  return () => {
    disposed = true;
    if (statusTimer) clearInterval(statusTimer);
  };
}
