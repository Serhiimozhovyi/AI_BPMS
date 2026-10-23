import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { PASTE_OFFSET, connectNodes, copySelection, deleteSelection, pasteClipboard } from './canvas-edit';

function demo(): WorkflowDefinition {
  return {
    ...emptyWorkflowDefinition('Демо'),
    nodes: [
      { id: 'a', name: 'Тригер', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} },
      { id: 'b', name: 'Умова', type: 'core.if', typeVersion: 1, position: [300, 0], parameters: { x: { deep: 1 } }, uiColor: 'red' },
      { id: 'c', name: 'Поля', type: 'core.set', typeVersion: 1, position: [600, 0], parameters: {} },
    ],
    connections: [
      { from: { node: 'a', output: 0 }, to: { node: 'b', input: 0 }, label: 'перше' },
      { from: { node: 'b', output: 1 }, to: { node: 'c', input: 0 } },
    ],
    pinData: { a: [{ json: { x: 1 } }], c: [{ json: {} }] },
  };
}

let seq = 0;
const ids = () => `p${++seq}`;

describe('connectNodes', () => {
  it('додає ребро в кінець; решта визначення — ті самі об’єкти', () => {
    const def = demo();
    const next = connectNodes(def, { source: 'a', sourceOutput: 0, target: 'c', targetInput: 0 });

    expect(next.connections).toHaveLength(3);
    expect(next.connections[2]).toEqual({ from: { node: 'a', output: 0 }, to: { node: 'c', input: 0 } });
    expect(next.connections[0]).toBe(def.connections[0]);
    expect(next.nodes).toBe(def.nodes);
    expect(def.connections).toHaveLength(2);
  });

  it('дубль, петля, невідомий вузол чи некоректний порт — те саме визначення', () => {
    const def = demo();
    expect(connectNodes(def, { source: 'a', sourceOutput: 0, target: 'b', targetInput: 0 })).toBe(def);
    expect(connectNodes(def, { source: 'b', sourceOutput: 0, target: 'b', targetInput: 0 })).toBe(def);
    expect(connectNodes(def, { source: 'a', sourceOutput: 0, target: 'ghost', targetInput: 0 })).toBe(def);
    expect(connectNodes(def, { source: 'a', sourceOutput: -1, target: 'c', targetInput: 0 })).toBe(def);
  });
});

describe('deleteSelection', () => {
  it('вузол видаляється разом з ребрами і pinData; решта — ті самі об’єкти', () => {
    const def = demo();
    const next = deleteSelection(def, { nodes: ['b'], edges: [] });

    expect(next.nodes.map((n) => n.id)).toEqual(['a', 'c']);
    expect(next.nodes[0]).toBe(def.nodes[0]);
    expect(next.connections).toEqual([]);
    expect(next.pinData).toBe(def.pinData);

    const withoutA = deleteSelection(def, { nodes: ['a'], edges: [] });
    expect(withoutA.pinData).toEqual({ c: [{ json: {} }] });
    expect(withoutA.connections).toEqual([def.connections[1]]);
  });

  it('виділені ребра видаляються за індексом, вузли лишаються', () => {
    const def = demo();
    const next = deleteSelection(def, { nodes: [], edges: [0] });
    expect(next.nodes).toBe(def.nodes);
    expect(next.connections).toEqual([def.connections[1]]);
    expect(next.connections[0]).toBe(def.connections[1]);
  });

  it('нічого не виділено — те саме визначення', () => {
    const def = demo();
    expect(deleteSelection(def, { nodes: [], edges: [] })).toBe(def);
    expect(deleteSelection(def, { nodes: ['ghost'], edges: [99] })).toBe(def);
  });
});

describe('copySelection / pasteClipboard', () => {
  it('копіює вузли і лише внутрішні ребра; вставка — нові id, унікальні назви, зсув, перемаплені ребра', () => {
    const def = demo();
    const clip = copySelection(def, { nodes: ['b', 'c'], edges: [] })!;
    expect(clip.nodes.map((n) => n.id)).toEqual(['b', 'c']);
    expect(clip.connections).toEqual([{ from: { node: 'b', output: 1 }, to: { node: 'c', input: 0 } }]);

    seq = 0;
    const { definition, nodeIds } = pasteClipboard(def, clip, ids);
    expect(nodeIds).toEqual(['p1', 'p2']);
    expect(definition.nodes.slice(0, 3)).toEqual(def.nodes);
    const [b2, c2] = definition.nodes.slice(3);
    expect(b2).toEqual({ ...def.nodes[1], id: 'p1', name: 'Умова 2', position: [300 + PASTE_OFFSET, PASTE_OFFSET] });
    expect(c2).toMatchObject({ id: 'p2', name: 'Поля 2', position: [600 + PASTE_OFFSET, PASTE_OFFSET] });
    expect(definition.connections.slice(2)).toEqual([{ from: { node: 'p1', output: 1 }, to: { node: 'p2', input: 0 } }]);
    // pinData не копіюється.
    expect(definition.pinData).toBe(def.pinData);
  });

  it('буфер не залежить від сценарію: зміна вставленого вузла не змінює буфер і оригінал', () => {
    const def = demo();
    const clip = copySelection(def, { nodes: ['b'], edges: [] })!;
    const { definition } = pasteClipboard(def, clip, ids);
    (definition.nodes[3].parameters['x'] as { deep: number }).deep = 42;

    expect(clip.nodes[0].parameters).toEqual({ x: { deep: 1 } });
    expect(def.nodes[1].parameters).toEqual({ x: { deep: 1 } });
  });

  it('повторна вставка — ще далі й з наступною назвою; id не повторюються навіть при колізії генератора', () => {
    const def = demo();
    const clip = copySelection(def, { nodes: ['c'], edges: [] })!;
    const first = pasteClipboard(def, clip, () => 'dup', 1).definition;
    const fixed = ['dup', 'dup', 'new'];
    const second = pasteClipboard(first, clip, () => fixed.shift()!, 2).definition;

    expect(second.nodes.slice(3).map((n) => [n.id, n.name, n.position])).toEqual([
      ['dup', 'Поля 2', [640, 40]],
      ['new', 'Поля 3', [680, 80]],
    ]);
  });

  it('нічого не виділено — null', () => {
    expect(copySelection(demo(), { nodes: [], edges: [0] })).toBeNull();
  });
});
