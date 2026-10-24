import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { toCanvasModel } from '../canvas/canvas-model';
import { SAMPLE_DESCRIPTORS } from '../canvas/sample-workflows';
import { WorkflowConnection, WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessConnector, BusinessView, FOLDED_TYPES, LANE_OPENBP, buildBusinessView } from './business-model';
import { STEP_HEIGHT, STEP_WIDTH, layoutBusinessView } from './business-layout';
import dailyReport from '../../../../../examples/daily-report.json';
import helloManual from '../../../../../examples/hello-manual.json';
import orderTotal from '../../../../../examples/order-total.json';
import psyAlerts from '../../../../../examples/psy-support-alerts.json';
import psyBot from '../../../../../examples/psy-support-bot.json';
import psyScheduler from '../../../../../examples/psy-support-scheduler.json';
import telegramOllama from '../../../../../examples/telegram-1c-ollama.json';
import telegram1c from '../../../../../examples/telegram-1c.json';

const EXTRA: NodeTypeInfo[] = [
  { type: 'core.loop', version: 1, displayName: 'Цикл', group: 'logic', inputs: ['main'], outputs: ['item'], parametersSchema: {}, isTrigger: false },
  { type: 'core.wait', version: 1, displayName: 'Пауза', group: 'logic', inputs: ['main'], outputs: ['main'], parametersSchema: {}, isTrigger: false },
  { type: 'core.errorTrigger', version: 1, displayName: 'Збій', group: 'trigger', inputs: [], outputs: ['main'], parametersSchema: {}, isTrigger: true },
  {
    type: 'core.switch',
    version: 2,
    displayName: 'Перемикач',
    group: 'logic',
    inputs: ['main'],
    outputs: ['fallback', 'rule0', 'rule1', 'rule2'],
    parametersSchema: {},
    isTrigger: false,
  },
];
const DESCRIPTORS = [...SAMPLE_DESCRIPTORS, ...EXTRA];

const EXAMPLES: Record<string, unknown> = {
  'daily-report': dailyReport,
  'hello-manual': helloManual,
  'order-total': orderTotal,
  'psy-support-alerts': psyAlerts,
  'psy-support-bot': psyBot,
  'psy-support-scheduler': psyScheduler,
  'telegram-1c-ollama': telegramOllama,
  'telegram-1c': telegram1c,
};

function view(definition: WorkflowDefinition, connectors: BusinessConnector[] = []): BusinessView {
  return buildBusinessView(definition, toCanvasModel(definition, DESCRIPTORS), connectors);
}

function node(id: string, type: string, extra: Partial<WorkflowNode> = {}): WorkflowNode {
  return { id, name: id, type, typeVersion: 1, position: [0, 0], parameters: {}, ...extra };
}

function edge(from: string, to: string, output = 0, extra: Partial<WorkflowConnection> = {}): WorkflowConnection {
  return { from: { node: from, output }, to: { node: to, input: 0 }, ...extra };
}

function workflow(nodes: WorkflowNode[], connections: WorkflowConnection[], extra: Record<string, unknown> = {}): WorkflowDefinition {
  return { ...emptyWorkflowDefinition('t'), nodes, connections, ...extra };
}

/** Видимі кроки, досяжні з `from` у графі визначення через згорнуті вузли. */
function expectedTargets(definition: WorkflowDefinition, visible: Set<string>, from: string): Set<string> {
  const result = new Set<string>();
  const stack = definition.connections.filter((c) => c.from.node === from).map((c) => c.to.node);
  const seen = new Set<string>();
  while (stack.length > 0) {
    const id = stack.pop()!;
    if (seen.has(id)) continue;
    seen.add(id);
    if (visible.has(id)) result.add(id);
    else stack.push(...definition.connections.filter((c) => c.from.node === id).map((c) => c.to.node));
  }
  return result;
}

describe('buildBusinessView на прикладах', () => {
  for (const [name, raw] of Object.entries(EXAMPLES)) {
    it(`${name}: детерміновано, без технічних вузлів, ребра ведуть туди ж, що й граф`, () => {
      const definition = raw as WorkflowDefinition;
      const first = view(definition);
      expect(view(definition)).toEqual(first);
      expect(layoutBusinessView(first)).toEqual(layoutBusinessView(view(definition)));

      const types = new Map(definition.nodes.map((n) => [n.id, n.type]));
      for (const step of first.steps) {
        expect(FOLDED_TYPES.has(types.get(step.nodeId!)!)).toBe(false);
        expect(first.lanes.some((l) => l.id === step.lane)).toBe(true);
      }

      const visible = new Set(first.steps.map((s) => s.id));
      for (const step of first.steps) {
        const actual = new Set(first.edges.filter((e) => e.from === step.id).map((e) => e.to));
        expect(actual).toEqual(expectedTargets(definition, visible, step.id));
      }
    });

    it(`${name}: кроки не накладаються`, () => {
      const layout = layoutBusinessView(view(raw as WorkflowDefinition));
      for (const a of layout.steps) {
        for (const b of layout.steps) {
          if (a === b) continue;
          const overlap = a.x < b.x + STEP_WIDTH && b.x < a.x + STEP_WIDTH && a.y < b.y + STEP_HEIGHT && b.y < a.y + STEP_HEIGHT;
          expect(overlap, `${a.id} / ${b.id}`).toBe(false);
        }
      }
    });
  }
});

describe('buildBusinessView', () => {
  it('ланцюжок set → code → set згортається в одне ребро', () => {
    const v = view(
      workflow(
        [node('t', 'core.manualTrigger'), node('s1', 'core.set'), node('c', 'core.code'), node('s2', 'core.set'), node('m', 'mcp.tool')],
        [edge('t', 's1'), edge('s1', 'c'), edge('c', 's2'), edge('s2', 'm')],
      ),
    );
    expect(v.steps.map((s) => s.id)).toEqual(['t', 'm']);
    expect(v.edges).toEqual([expect.objectContaining({ from: 't', to: 'm', kind: 'flow' })]);
    expect(v.edges[0].label).toBeUndefined();
  });

  it('розгалуження через згорнутий вузол дає по ребру в кожну гілку без дублікатів', () => {
    const v = view(
      workflow(
        [node('t', 'core.manualTrigger'), node('s', 'core.set'), node('a', 'mcp.tool'), node('b', 'ai.llm')],
        [edge('t', 's'), edge('t', 's'), edge('s', 'a'), edge('s', 'b')],
      ),
    );
    expect(v.edges.map((e) => `${e.from}->${e.to}`).sort()).toEqual(['t->a', 't->b']);
  });

  it('if з обома гілками — ромб з підписами «так»/«ні»', () => {
    const v = view(
      workflow(
        [node('t', 'core.webhook'), node('if', 'core.if'), node('y', 'mcp.tool'), node('n', 'mcp.tool')],
        [edge('t', 'if'), edge('if', 'y', 0), edge('if', 'n', 1)],
      ),
    );
    expect(v.steps.find((s) => s.id === 'if')?.kind).toBe('decision');
    expect(v.edges.filter((e) => e.from === 'if').map((e) => [e.to, e.label])).toEqual([
      ['y', 'так'],
      ['n', 'ні'],
    ]);
  });

  it('switch v2: підписи «інакше» і «варіант N»', () => {
    const v = view(
      workflow(
        [node('t', 'core.manualTrigger'), node('sw', 'core.switch', { typeVersion: 2, parameters: { rules: [{}, {}] } }), node('a', 'mcp.tool'), node('b', 'mcp.tool')],
        [edge('t', 'sw'), edge('sw', 'a', 0), edge('sw', 'b', 2)],
      ),
    );
    expect(v.edges.filter((e) => e.from === 'sw').map((e) => e.label)).toEqual(['інакше', 'варіант 2']);
  });

  it('гілка error з continueOnFail — альтернативне ребро «збій»', () => {
    const v = view(
      workflow([node('t', 'core.manualTrigger'), node('m', 'mcp.tool', { continueOnFail: true }), node('a', 'ai.llm'), node('b', 'mcp.tool')], [
        edge('t', 'm'),
        edge('m', 'a', 0),
        edge('m', 'b', 1),
      ]),
    );
    expect(v.edges.filter((e) => e.from === 'm').map((e) => [e.kind, e.label])).toEqual([
      ['flow', undefined],
      ['alt', 'збій'],
    ]);
  });

  it('доріжки за типом вузла і назвою конектора; вимкнений вузол прихований', () => {
    const v = view(
      workflow(
        [
          node('t', 'core.webhook'),
          node('k', 'mcp.tool', { parameters: { serverId: 'srv_kit' } }),
          node('x', 'mcp.tool', { parameters: { serverId: 'srv_gone' } }),
          node('ai', 'ai.llm'),
          node('w', 'core.wait', { parameters: { amount: 5, unit: 'minutes' } }),
          node('off', 'mcp.tool', { disabled: true }),
        ],
        [edge('t', 'k'), edge('k', 'x'), edge('x', 'ai'), edge('ai', 'w'), edge('w', 'off')],
      ),
      [{ id: 'srv_kit', name: 'Кіт CRM' }],
    );
    expect(v.lanes.map((l) => l.name)).toEqual(['Зовнішня система', 'Кіт CRM', 'Інша система', 'ШІ', 'OpenBP']);
    expect(v.steps.find((s) => s.id === 'w')).toEqual(expect.objectContaining({ kind: 'wait', badge: '5 хв', lane: LANE_OPENBP }));
    expect(v.steps.some((s) => s.id === 'off')).toBe(false);
  });

  it('бізнес-атрибути: назва, підрядок, власні доріжки, приховування, підпис ребра і зовнішні кроки', () => {
    const v = view(
      workflow(
        [
          node('t', 'core.schedule', { business: { title: 'Щоранку', lane: 'openbp2' } }),
          node('s', 'core.set', { business: { hidden: false, title: 'Підготувати' } }),
          node('m', 'mcp.tool', { business: { hidden: true } }),
          node('n', 'mcp.tool', { business: { title: 'Призначити', subtitle: 'kit-crm', lane: 'missing' } }),
        ],
        [edge('t', 's'), edge('s', 'm', 0, { business: { label: '2 дні' } }), edge('m', 'n')],
        {
          business: {
            lanes: [
              { id: 'kit', name: 'Кіт 2', description: 'люди у веб-формах' },
              { id: 'openbp2', name: 'Сценарій' },
            ],
            steps: [
              { id: 'h', title: 'Погоджувач тисне «Погодити»', subtitle: 'статус', lane: 'kit', links: [{ node: 'n', direction: 'in', label: 'читає' }] },
              { id: 'w', title: 'Задача в Кіті', lane: 'kit', links: [{ node: 't', direction: 'out' }] },
              { id: 'dangling', title: 'Без зв’язку', lane: 'kit', links: [{ node: 'm', direction: 'in' }] },
            ],
          },
        },
      ),
    );
    expect(v.lanes.map((l) => l.id)).toEqual(['kit', 'openbp2', 'openbp', 'other']);
    expect(v.lanes[0].description).toBe('люди у веб-формах');
    expect(v.steps.map((s) => [s.id, s.title, s.lane])).toEqual([
      ['t', 'Щоранку', 'openbp2'],
      ['s', 'Підготувати', 'openbp'],
      ['n', 'Призначити', 'other'],
      ['h', 'Погоджувач тисне «Погодити»', 'kit'],
      ['w', 'Задача в Кіті', 'kit'],
    ]);
    expect(v.edges.map((e) => [e.from, e.to, e.kind, e.label])).toEqual([
      ['t', 's', 'flow', undefined],
      ['s', 'n', 'flow', '2 дні'],
      ['h', 'n', 'read', 'читає'],
      ['t', 'w', 'write', undefined],
    ]);
  });
});
