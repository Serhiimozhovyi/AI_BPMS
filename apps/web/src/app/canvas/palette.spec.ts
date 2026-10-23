import { emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { MCP_TOOL, PaletteServer, addNode, buildPalette, createNode, filterPalette, nextNodePosition } from './palette';
import { SAMPLE_DESCRIPTORS } from './sample-workflows';

const servers: PaletteServer[] = [
  {
    id: 'c-tg',
    name: 'Telegram',
    tools: [{ name: 'send_message', title: 'Надіслати повідомлення', description: 'Повідомлення в чат', inputSchema: { type: 'object' } }],
  },
  {
    id: 'c-1c',
    name: '1С',
    tools: [
      { name: 'create_order', description: 'Створює замовлення', inputSchema: { type: 'object' } },
      { name: 'find_customer', inputSchema: { type: 'object' } },
    ],
  },
  { id: 'c-empty', name: 'Порожній', tools: [] },
];

describe('palette', () => {
  it('групує core.* за групами у фіксованому порядку, сервери — за назвою, без узагальненого mcp.tool', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, servers);

    expect(sections.map((s) => s.id)).toEqual([
      'group:trigger',
      'group:logic',
      'group:transform',
      'group:ai',
      'server:c-1c',
      'server:c-tg',
    ]);
    expect(sections.flatMap((s) => s.items).filter((i) => i.type === MCP_TOOL && !i.serverId)).toEqual([]);
    expect(sections[0].items.map((i) => i.type)).toContain('core.webhook');
    expect(sections[4].items.map((i) => i.label)).toEqual(['create_order', 'find_customer']);
    expect(sections[5].items[0]).toMatchObject({
      label: 'Надіслати повідомлення',
      type: MCP_TOOL,
      serverId: 'c-tg',
      toolName: 'send_message',
      group: 'integration',
    });
  });

  it('для кожного типу лишає лише найновішу версію', () => {
    const descriptors = [...SAMPLE_DESCRIPTORS, { ...SAMPLE_DESCRIPTORS.find((d) => d.type === 'core.set')!, version: 3 }];

    const set = buildPalette(descriptors, []).flatMap((s) => s.items).filter((i) => i.type === 'core.set');

    expect(set).toHaveLength(1);
    expect(set[0].typeVersion).toBe(3);
  });

  it('пошук — за назвою, типом, tool-ом і описом без урахування регістру; збіг із сервером лишає всі його tools', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, servers);

    expect(filterPalette(sections, 'УМОВ').flatMap((s) => s.items.map((i) => i.type))).toEqual(['core.if']);
    expect(filterPalette(sections, 'core.merge').flatMap((s) => s.items.map((i) => i.type))).toEqual(['core.merge']);
    expect(filterPalette(sections, 'find_').flatMap((s) => s.items.map((i) => i.toolName))).toEqual(['find_customer']);
    expect(filterPalette(sections, 'в чат').flatMap((s) => s.items.map((i) => i.toolName))).toEqual(['send_message']);
    expect(filterPalette(sections, '1с').map((s) => s.id)).toEqual(['server:c-1c']);
    expect(filterPalette(sections, '1с')[0].items).toHaveLength(2);
    expect(filterPalette(sections, 'немає такого')).toEqual([]);
    expect(filterPalette(sections, '  ')).toEqual(sections);
  });

  it('новий вузол core.* — з унікальними id і назвою та порожніми параметрами', () => {
    const item = buildPalette(SAMPLE_DESCRIPTORS, []).flatMap((s) => s.items).find((i) => i.type === 'core.if')!;
    let def = emptyWorkflowDefinition('С');
    const ids = ['n_a', 'n_a', 'n_b'];

    def = { ...def, nodes: [createNode(item, def, [0, 0], () => 'n_a')] };
    const second = createNode(item, def, [0, 0], () => ids.shift()!);

    expect(def.nodes[0]).toEqual({ id: 'n_a', name: 'Умова', type: 'core.if', typeVersion: 1, position: [0, 0], parameters: {} });
    expect(second.id).toBe('n_b');
    expect(second.name).toBe('Умова 2');
  });

  it('новий вузол з tool-а — mcp.tool з serverId, toolName і порожніми arguments', () => {
    const item = buildPalette(SAMPLE_DESCRIPTORS, servers).find((s) => s.id === 'server:c-1c')!.items[0];

    const node = createNode(item, emptyWorkflowDefinition('С'));

    expect(node).toMatchObject({
      type: MCP_TOOL,
      typeVersion: 1,
      name: 'create_order',
      parameters: { serverId: 'c-1c', toolName: 'create_order', arguments: {} },
    });
    expect(node.id).toMatch(/^n_[0-9a-f]{12}$/);
  });

  it('новий вузол стає праворуч від крайнього; addNode не змінює решту визначення', () => {
    const item = buildPalette(SAMPLE_DESCRIPTORS, []).flatMap((s) => s.items)[0];
    const empty = { ...emptyWorkflowDefinition('С'), extra: { keep: true } };

    expect(nextNodePosition(empty)).toEqual([100, 200]);
    const one = addNode(empty, item);
    expect(one.nodes[0].position).toEqual([100, 200]);
    expect(one['extra']).toBe(empty['extra']);
    expect(empty.nodes).toEqual([]);

    const two = addNode({ ...one, nodes: [...one.nodes, { ...one.nodes[0], id: 'x', name: 'x', position: [500, 40] }] }, item);
    expect(two.nodes[2].position).toEqual([760, 40]);
  });
});
