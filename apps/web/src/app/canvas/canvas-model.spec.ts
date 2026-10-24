import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { DescriptorIndex, ERROR_OUTPUT, fromCanvasModel, moveNodes, toCanvasModel } from './canvas-model';
import { SAMPLE_DESCRIPTORS, largeWorkflow } from './sample-workflows';

/** Сценарій із невідомими полями на кожному рівні: верхній, settings, вузол, parameters, ребро, from/to, pinData. */
function workflowWithExtras(): WorkflowDefinition {
  return {
    formatVersion: 1,
    name: 'Заявка з Telegram → 1С',
    settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true, futureSetting: 'x' },
    nodes: [
      {
        id: 'n_webhook',
        name: 'Нова заявка',
        type: 'core.webhook',
        typeVersion: 1,
        position: [100, 200],
        parameters: { method: 'POST', path: 'telegram-in', extra: { nested: [1, 2] } },
        uiColor: '#ff0000',
      },
      {
        id: 'n_if',
        name: 'Є замовлення?',
        type: 'core.if',
        typeVersion: 1,
        position: [360, 200],
        parameters: {},
        continueOnFail: true,
        notes: 'перевірка',
      },
      {
        id: 'n_1c',
        name: '1С: створити замовлення',
        type: 'mcp.tool',
        typeVersion: 1,
        position: [620, 120],
        parameters: { serverId: 'srv_1c', toolName: 'create_order', arguments: { customer: '{{ $json.customer }}' } },
        timeoutSec: 30,
        retry: { maxTries: 3, waitMs: 2000, backoff: 'exp' },
        disabled: true,
      },
      { id: 'n_future', name: 'Майбутній вузол', type: 'acme.future', typeVersion: 7, position: [880, 200], parameters: { a: 1 } },
    ],
    connections: [
      { from: { node: 'n_webhook', output: 0 }, to: { node: 'n_if', input: 0 }, label: 'вхід' },
      { from: { node: 'n_if', output: 0, tag: 't' }, to: { node: 'n_1c', input: 0, tag: 'u' } },
      { from: { node: 'n_if', output: 2 }, to: { node: 'n_future', input: 1 } },
      { from: { node: 'n_1c', output: 0 }, to: { node: 'n_future', input: 0 } },
    ],
    pinData: { n_webhook: [{ json: { body: { text: 'hi' } } }] },
    meta: { createdBy: 'agent', tags: ['a'] },
  };
}

describe('canvas-model', () => {
  it('workflow → модель → workflow повертає визначення без втрат, з усіма невідомими полями', () => {
    const def = workflowWithExtras();
    const snapshot = structuredClone(def);

    const back = fromCanvasModel(toCanvasModel(def, SAMPLE_DESCRIPTORS), def);

    expect(back).toStrictEqual(snapshot);
    expect(JSON.stringify(back)).toBe(JSON.stringify(snapshot)); // і порядок ключів
    expect(def).toStrictEqual(snapshot); // вхід не мутується
  });

  it('round-trip без дескрипторів і для сценарію з некоректними позиціями теж без втрат', () => {
    const def = workflowWithExtras();
    def.nodes[0].position = 'зламано' as unknown as [number, number];
    const snapshot = structuredClone(def);

    expect(fromCanvasModel(toCanvasModel(def, []), def)).toStrictEqual(snapshot);
  });

  it('вузли — за position, порти — за дескрипторами; continueOnFail додає вихід error', () => {
    const model = toCanvasModel(workflowWithExtras(), SAMPLE_DESCRIPTORS);
    const byId = new Map(model.nodes.map((n) => [n.id, n]));

    const webhook = byId.get('n_webhook')!;
    expect(webhook).toMatchObject({ x: 100, y: 200, group: 'trigger', inputs: [], outputs: ['main'], disabled: false });
    expect(byId.get('n_if')!.outputs).toEqual(['true', 'false', ERROR_OUTPUT]);
    expect(byId.get('n_1c')).toMatchObject({ group: 'integration', disabled: true, subtitle: 'mcp.tool · create_order' });
  });

  it('core.switch v2: fallback і по порту на правило; порт з ребром не ховається; v1 — усі 5 портів', () => {
    const rules = (n: number) => Array.from({ length: n }, () => ({ conditions: [] }));
    const switchV2 = {
      type: 'core.switch',
      version: 2,
      displayName: 'Перемикач',
      group: 'logic' as const,
      inputs: ['main'],
      outputs: ['fallback', ...Array.from({ length: 16 }, (_, i) => `rule${i}`)],
      parametersSchema: { type: 'object' },
      isTrigger: false,
    };
    const switchV1 = { ...switchV2, version: 1, outputs: ['rule0', 'rule1', 'rule2', 'rule3', 'fallback'] };
    const node = (id: string, typeVersion: number, parameters: Record<string, unknown>, extra: object = {}) => ({
      id,
      name: id,
      type: 'core.switch',
      typeVersion,
      position: [0, 0] as [number, number],
      parameters,
      ...extra,
    });
    const definition = {
      formatVersion: 1,
      name: 'switch',
      settings: {},
      pinData: {},
      nodes: [
        node('seven', 2, { rules: rules(7) }),
        node('none', 2, {}),
        node('edge', 2, { rules: rules(2) }),
        node('cof', 2, { rules: rules(2) }, { continueOnFail: true }),
        node('old', 1, { rules: rules(2) }),
      ],
      connections: [{ from: { node: 'edge', output: 9 }, to: { node: 'seven', input: 0 } }],
    } as unknown as WorkflowDefinition;

    const byId = new Map(toCanvasModel(definition, [switchV1, switchV2]).nodes.map((n) => [n.id, n.outputs]));

    expect(byId.get('seven')).toEqual(['fallback', 'rule0', 'rule1', 'rule2', 'rule3', 'rule4', 'rule5', 'rule6']);
    expect(byId.get('none')).toEqual(['fallback', 'rule0']);
    expect(byId.get('edge')).toEqual(switchV2.outputs.slice(0, 10));
    expect(byId.get('cof')).toEqual([...switchV2.outputs, 'error']);
    expect(byId.get('old')).toEqual(switchV1.outputs);
  });

  it('невідомий тип отримує порти за ребрами, щоб жодне ребро не зникло', () => {
    const model = toCanvasModel(workflowWithExtras(), SAMPLE_DESCRIPTORS);
    const future = model.nodes.find((n) => n.id === 'n_future')!;

    expect(future.group).toBe('unknown');
    expect(future.inputs).toEqual(['main', '1']);
    expect(future.outputs).toEqual([]);
    expect(model.edges.map((e) => [e.source, e.sourceOutput, e.target, e.targetInput])).toEqual([
      ['n_webhook', 0, 'n_if', 0],
      ['n_if', 0, 'n_1c', 0],
      ['n_if', 2, 'n_future', 1],
      ['n_1c', 0, 'n_future', 0],
    ]);
  });

  it('стара версія типу без точного дескриптора бере порти з найновішої', () => {
    const index = new DescriptorIndex([
      ...SAMPLE_DESCRIPTORS,
      { ...SAMPLE_DESCRIPTORS.find((d) => d.type === 'core.if')!, version: 2, outputs: ['yes', 'no'] },
    ]);

    expect(index.find('core.if', 1)!.outputs).toEqual(['true', 'false']);
    expect(index.find('core.if', 2)!.outputs).toEqual(['yes', 'no']);
    expect(index.find('core.if', 9)!.version).toBe(2);
    expect(index.latest('core.if')!.version).toBe(2);
  });

  it('moveNodes змінює лише position перетягнутих вузлів, решта — ті самі об’єкти', () => {
    const def = workflowWithExtras();

    const moved = moveNodes(def, [{ id: 'n_if', x: 400, y: 260 }], SAMPLE_DESCRIPTORS);

    expect(moved.nodes[1]).toStrictEqual({ ...def.nodes[1], position: [400, 260] });
    expect(moved.nodes[0]).toBe(def.nodes[0]);
    expect(moved.nodes[2]).toBe(def.nodes[2]);
    expect(moved.connections).toStrictEqual(def.connections);
    expect(moved.connections[1]).toBe(def.connections[1]);
    expect(moved['meta']).toBe(def['meta']);
    expect(moveNodes(def, [], SAMPLE_DESCRIPTORS)).toBe(def);
  });

  it('великий сценарій на 200 вузлів проходить round-trip без втрат', () => {
    const def = largeWorkflow(200);
    const model = toCanvasModel(def, SAMPLE_DESCRIPTORS);

    expect(model.nodes).toHaveLength(200);
    expect(model.edges).toHaveLength(def.connections.length);
    expect(fromCanvasModel(model, def)).toStrictEqual(def);
  });
});
