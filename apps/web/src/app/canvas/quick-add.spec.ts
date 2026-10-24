import { WorkflowDefinition, WorkflowNode, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { parsePosition } from './canvas-model';
import { PaletteItem, buildPalette } from './palette';
import {
  FREE_STEP_Y,
  RECENT_KEY,
  RECENT_LIMIT,
  RECENT_SECTION_ID,
  addConnectedNode,
  applyQuickAdd,
  freePosition,
  insertOnEdge,
  quickAddSections,
  readRecent,
  rememberRecent,
  writeRecent,
} from './quick-add';
import { SAMPLE_DESCRIPTORS } from './sample-workflows';

const setItem: PaletteItem = { key: 'core.set', label: 'Задати поля', description: null, type: 'core.set', typeVersion: 1, group: 'transform' };
const ifItem: PaletteItem = { key: 'core.if', label: 'Умова', description: null, type: 'core.if', typeVersion: 1, group: 'logic' };
const triggerItem: PaletteItem = {
  key: 'core.manualTrigger',
  label: 'Ручний запуск',
  description: null,
  type: 'core.manualTrigger',
  typeVersion: 1,
  group: 'trigger',
};

function node(id: string, x: number, y: number, type = 'core.set'): WorkflowNode {
  return { id, name: id, type, typeVersion: 1, position: [x, y], parameters: {} };
}

function ids(): () => string {
  let i = 0;
  return () => `new_${++i}`;
}

/** Тригер → Set → Set у ряд з кроком 260, плюс невідоме поле на ребрі. */
function chain(): WorkflowDefinition {
  return {
    ...emptyWorkflowDefinition('t'),
    nodes: [node('t', 100, 200, 'core.manualTrigger'), node('a', 360, 200), node('b', 620, 200)],
    connections: [
      { from: { node: 't', output: 0 }, to: { node: 'a', input: 0 } },
      { from: { node: 'a', output: 0 }, to: { node: 'b', input: 0 }, note: 'keep' },
    ],
  };
}

function overlapping(def: WorkflowDefinition): boolean {
  const ps = def.nodes.map((n) => parsePosition(n.position));
  return ps.some((p, i) => ps.some((q, j) => i < j && Math.abs(p.x - q.x) < 220 && Math.abs(p.y - q.y) < 72));
}

describe('quick-add', () => {
  describe('addConnectedNode', () => {
    it('з порту: вузол праворуч від джерела, з’єднаний вихід → вхід 0', () => {
      const def = chain();
      const { definition, nodeId } = addConnectedNode(def, setItem, { node: 'b', output: 0 }, undefined, ids());

      const added = definition.nodes.find((n) => n.id === nodeId)!;
      expect(added.position).toEqual([880, 200]);
      expect(definition.connections.at(-1)).toEqual({ from: { node: 'b', output: 0 }, to: { node: nodeId, input: 0 } });
      // Решта визначення — ті самі об'єкти.
      expect(definition.nodes.slice(0, 3)).toEqual(def.nodes);
      expect(definition.nodes[0]).toBe(def.nodes[0]);
      expect(definition.connections[1]).toBe(def.connections[1]);
    });

    it('другий вихід (IF «false») ставить вузол нижче, зайняте місце пропускає', () => {
      const def: WorkflowDefinition = { ...emptyWorkflowDefinition('t'), nodes: [node('if', 100, 200, 'core.if'), node('x', 360, 320)], connections: [] };
      const { definition, nodeId } = addConnectedNode(def, setItem, { node: 'if', output: 1 }, undefined, ids());

      expect(definition.nodes.find((n) => n.id === nodeId)!.position).toEqual([360, 320 + FREE_STEP_Y]);
      expect(overlapping(definition)).toBe(false);
    });

    it('drag у порожнє: позиція відпускання, з’єднання з портом', () => {
      const { definition, nodeId } = addConnectedNode(chain(), ifItem, { node: 'a', output: 0 }, [400, 600], ids());

      expect(definition.nodes.find((n) => n.id === nodeId)!.position).toEqual([400, 600]);
      expect(definition.connections.at(-1)).toEqual({ from: { node: 'a', output: 0 }, to: { node: nodeId, input: 0 } });
    });

    it('тригер не з’єднується (у нього немає входів); без порту — вільне місце', () => {
      const def = chain();
      const fromPort = addConnectedNode(def, triggerItem, { node: 'b', output: 0 }, undefined, ids());
      expect(fromPort.definition.connections).toBe(def.connections);

      const empty = addConnectedNode(emptyWorkflowDefinition('t'), triggerItem, null, undefined, ids());
      expect(empty.definition.nodes[0].position).toEqual([100, 200]);
    });
  });

  describe('insertOnEdge', () => {
    it('A → B стає A → новий → B на тому самому місці в connections; невідомі поля ребра лишаються на вході', () => {
      const def = chain();
      const result = insertOnEdge(def, ifItem, 1, ids())!;

      expect(result.definition.connections).toEqual([
        def.connections[0],
        { from: { node: 'a', output: 0 }, to: { node: result.nodeId, input: 0 }, note: 'keep' },
        { from: { node: result.nodeId, output: 0 }, to: { node: 'b', input: 0 } },
      ]);
      expect(result.definition.connections[0]).toBe(def.connections[0]);
    });

    it('мало місця між A і B — B і все після нього зсуваються праворуч, нічого не накладається', () => {
      const def = chain();
      def.nodes.push(node('c', 880, 200));
      def.connections.push({ from: { node: 'b', output: 0 }, to: { node: 'c', input: 0 } });

      const result = insertOnEdge(def, setItem, 0, ids())!;
      const pos = (id: string) => result.definition.nodes.find((n) => n.id === id)!.position;

      expect(pos(result.nodeId)).toEqual([360, 200]);
      expect(pos('t')).toEqual([100, 200]);
      expect(pos('a')).toEqual([620, 200]);
      expect(pos('b')).toEqual([880, 200]);
      expect(pos('c')).toEqual([1140, 200]);
      expect(overlapping(result.definition)).toBe(false);
      // Вузол, що не зсувався, — той самий об'єкт.
      expect(result.definition.nodes[0]).toBe(def.nodes[0]);
    });

    it('достатньо місця — нічого не зсувається', () => {
      const def: WorkflowDefinition = {
        ...emptyWorkflowDefinition('t'),
        nodes: [node('a', 0, 0), node('b', 800, 0)],
        connections: [{ from: { node: 'a', output: 0 }, to: { node: 'b', input: 0 } }],
      };
      const result = insertOnEdge(def, setItem, 0, ids())!;

      expect(result.definition.nodes.slice(0, 2)).toEqual(def.nodes);
      expect(result.definition.nodes[2].position).toEqual([260, 0]);
    });

    it('тригер, неіснуюче ребро чи вузол — null', () => {
      const def = chain();
      expect(insertOnEdge(def, triggerItem, 0, ids())).toBeNull();
      expect(insertOnEdge(def, setItem, 5, ids())).toBeNull();
      const broken = { ...def, connections: [{ from: { node: 'zzz', output: 0 }, to: { node: 'a', input: 0 } }] };
      expect(insertOnEdge(broken, setItem, 0, ids())).toBeNull();
    });
  });

  it('freePosition зсуває вниз, поки місце зайняте', () => {
    const def: WorkflowDefinition = { ...emptyWorkflowDefinition('t'), nodes: [node('a', 100, 200), node('b', 150, 320)], connections: [] };
    expect(freePosition(def, [120, 210])).toEqual([120, 210 + 2 * FREE_STEP_Y]);
    expect(freePosition(def, [600, 200])).toEqual([600, 200]);
  });

  describe('quickAddSections', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, [
      { id: 'c1', name: '1С', tools: [{ name: 'create_order', inputSchema: {} }] },
    ]);

    it('режим trigger — лише тригери; step — усе, крім тригерів', () => {
      const triggers = quickAddSections(sections, 'trigger');
      expect(triggers.flatMap((s) => s.items).every((i) => i.group === 'trigger')).toBe(true);
      expect(triggers.length).toBeGreaterThan(0);

      const steps = quickAddSections(sections, 'step');
      expect(steps.flatMap((s) => s.items).some((i) => i.group === 'trigger')).toBe(false);
      expect(steps.map((s) => s.id)).toContain('server:c1');
    });

    it('«Останні» першою секцією, лише дозволені й наявні, у порядку списку', () => {
      const result = quickAddSections(sections, 'step', ['c1/create_order', 'core.manualTrigger', 'gone', 'core.set']);
      expect(result[0].id).toBe(RECENT_SECTION_ID);
      expect(result[0].items.map((i) => i.key)).toEqual(['c1/create_order', 'core.set']);

      expect(quickAddSections(sections, 'step', ['gone'])[0].id).not.toBe(RECENT_SECTION_ID);
    });
  });

  describe('останні', () => {
    beforeEach(() => localStorage.removeItem(RECENT_KEY));

    it('rememberRecent: новий першим, без повторів, не більше ліміту', () => {
      let recent: string[] = [];
      for (let i = 0; i < RECENT_LIMIT + 2; i++) recent = rememberRecent(recent, `k${i}`);
      expect(recent).toHaveLength(RECENT_LIMIT);
      expect(recent[0]).toBe(`k${RECENT_LIMIT + 1}`);

      expect(rememberRecent(['a', 'b', 'c'], 'b')).toEqual(['b', 'a', 'c']);
    });

    it('зберігається в localStorage; зіпсоване значення — порожній список', () => {
      writeRecent(['a', 'b']);
      expect(readRecent()).toEqual(['a', 'b']);
      localStorage.setItem(RECENT_KEY, '{oops');
      expect(readRecent()).toEqual([]);
      localStorage.setItem(RECENT_KEY, '{"a":1}');
      expect(readRecent()).toEqual([]);
    });
  });

  describe('applyQuickAdd', () => {
    it('порт, ребро, без з’єднання — відповідні дії; тригер на ребро — null', () => {
      const def = chain();
      const fromPort = applyQuickAdd(def, setItem, { kind: 'port', from: { node: 'b', output: 0 } }, ids())!;
      expect(fromPort.definition.connections.at(-1)).toEqual({ from: { node: 'b', output: 0 }, to: { node: fromPort.nodeId, input: 0 } });

      const onEdge = applyQuickAdd(def, setItem, { kind: 'edge', edgeIndex: 0 }, ids())!;
      expect(onEdge.definition.connections).toHaveLength(3);

      const free = applyQuickAdd(def, setItem, { kind: 'free' }, ids())!;
      expect(free.definition.connections).toBe(def.connections);
      expect(free.definition.nodes).toHaveLength(4);

      expect(applyQuickAdd(def, triggerItem, { kind: 'edge', edgeIndex: 0 }, ids())).toBeNull();
    });

    it('порт з точкою відпускання: вузол там, де відпустили', () => {
      const def = chain();
      const { definition, nodeId } = applyQuickAdd(def, setItem, { kind: 'port', from: { node: 'b', output: 0 }, at: [900, 600] }, ids())!;
      expect(parsePosition(definition.nodes.find((n) => n.id === nodeId)!.position)).toMatchObject({ x: 900, y: 600 });
    });
  });
});
