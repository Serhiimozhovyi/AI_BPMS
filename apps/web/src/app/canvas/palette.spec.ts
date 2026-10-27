import { emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { CONNECTORS_SECTION, FREQUENT_SECTION, MCP_TOOL, TRIGGER_PRESETS_SECTION, PaletteServer, addNode, buildPalette, createNode, filterPalette, itemHint, itemTitle, nextNodePosition } from './palette';
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
  it('групує core.* за групами у фіксованому порядку; конектори — по одному елементу за назвою, без узагальненого mcp.tool (F3-55)', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, servers);

    expect(sections.map((s) => s.id)).toEqual(['group:trigger', 'group:logic', 'group:transform', 'group:ai', CONNECTORS_SECTION]);
    expect(sections.flatMap((s) => s.items).filter((i) => i.type === MCP_TOOL && !i.serverId)).toEqual([]);
    expect(sections[0].items.map((i) => i.type)).toContain('core.webhook');
    const connectors = sections[4].items;
    // Конектор без tools не показується; дія за замовчуванням — перша в групах «Ресурс → Операція».
    expect(connectors.map((i) => [i.key, i.label, i.toolName, i.nodeName])).toEqual([
      ['connector:c-1c', '1С', 'create_order', 'create_order'],
      ['connector:c-tg', 'Telegram', 'send_message', 'Надіслати повідомлення'],
    ]);
    expect(connectors[0].actions?.map((a) => a.key)).toEqual(['c-1c/create_order', 'c-1c/find_customer']);
    expect(itemHint(connectors[0])).toBe('2 дій');
    expect(connectors[1].actions?.[0]).toMatchObject({ label: 'Telegram → Надіслати повідомлення', type: MCP_TOOL, serverId: 'c-tg', toolName: 'send_message', group: 'integration' });
  });

  it('пресети тригерів встановлених конекторів — картки в секції «Події конекторів»; вставка підставляє serverId і параметри (F6-21)', () => {
    const withPresets: PaletteServer[] = [
      {
        ...servers[0],
        triggers: [
          {
            id: 'new_update',
            title: 'Нове повідомлення Telegram',
            description: 'Вхідні повідомлення бота',
            node: { type: 'mcp.poll', parameters: { toolName: 'get_updates', arguments: {}, itemsPath: 'result', keyField: 'update_id', cron: '*/1 * * * *' } },
          },
          { id: 'unknown', title: 'Невідомий тип', node: { type: 'mcp.subscribe', parameters: { resourceUri: 'x://y', keyField: 'id' } } },
        ],
      },
      servers[1],
    ];

    const sections = buildPalette(SAMPLE_DESCRIPTORS, withPresets);

    // mcp.subscribe немає серед дескрипторів зразка — картка без дескриптора не показується.
    const presets = sections.find((s) => s.id === TRIGGER_PRESETS_SECTION)!;
    expect(presets.items.map((i) => [i.key, i.label, i.type, i.group, i.serverId])).toEqual([
      ['trigger:c-tg/new_update', 'Нове повідомлення Telegram', 'mcp.poll', 'trigger', 'c-tg'],
    ]);
    expect(buildPalette(SAMPLE_DESCRIPTORS, servers).some((s) => s.id === TRIGGER_PRESETS_SECTION)).toBe(false);

    const node = createNode(presets.items[0], emptyWorkflowDefinition('t'));
    expect(node.type).toBe('mcp.poll');
    expect(node.name).toBe('Нове повідомлення Telegram');
    expect(node.parameters).toEqual({ serverId: 'c-tg', toolName: 'get_updates', arguments: {}, itemsPath: 'result', keyField: 'update_id', cron: '*/1 * * * *' });
    // Пошук знаходить картку за назвою.
    expect(filterPalette(sections, 'telegram').flatMap((s) => s.items).some((i) => i.key === 'trigger:c-tg/new_update')).toBe(true);
  });

  it('«Часті дії» — до 5 дій з «Останніх» у їхньому порядку; невідомі ключі пропускаються', () => {
    const many: PaletteServer = { id: 'm', name: 'M', tools: Array.from({ length: 7 }, (_, i) => ({ name: `t${i}`, inputSchema: {} })) };
    const recent = ['core.set', 'c-tg/send_message', 'gone/x', ...many.tools.map((t) => `m/${t.name}`)];

    const frequent = buildPalette(SAMPLE_DESCRIPTORS, [...servers, many], recent).find((s) => s.id === FREQUENT_SECTION)!;

    expect(frequent.items.map((i) => i.key)).toEqual(['c-tg/send_message', 'm/t0', 'm/t1', 'm/t2', 'm/t3']);
    expect(buildPalette(SAMPLE_DESCRIPTORS, servers, ['core.set']).some((s) => s.id === FREQUENT_SECTION)).toBe(false);
  });

  it('для кожного типу лишає лише найновішу версію', () => {
    const descriptors = [...SAMPLE_DESCRIPTORS, { ...SAMPLE_DESCRIPTORS.find((d) => d.type === 'core.set')!, version: 3 }];

    const set = buildPalette(descriptors, []).flatMap((s) => s.items).filter((i) => i.type === 'core.set');

    expect(set).toHaveLength(1);
    expect(set[0].typeVersion).toBe(3);
  });

  it('пошук — кожне слово в назві, типі, tool-і чи описі без регістру; дії конекторів — на місці конектора', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, servers);
    const keys = (q: string) => filterPalette(sections, q).flatMap((s) => s.items.map((i) => i.key));

    expect(keys('УМОВ')).toEqual(['core.if']);
    expect(keys('core.merge')).toEqual(['core.merge']);
    // «надіслати повідомлення» → Telegram → send_message (за title), слова в будь-якому порядку.
    expect(keys('повідомлення надіслати')).toEqual(['c-tg/send_message']);
    expect(keys('в чат')).toEqual(['c-tg/send_message']);
    expect(keys('find_')).toEqual(['c-1c/find_customer']);
    // Збіг із назвою конектора — сам конектор; «telegram надіслати» — дія (назва конектора — у її підписі).
    expect(keys('1с')).toEqual(['connector:c-1c']);
    expect(keys('telegram надіслати')).toEqual(['c-tg/send_message']);
    expect(keys('конектори')).toEqual(['connector:c-1c', 'connector:c-tg']);
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

  it('новий вузол з конектора чи дії — mcp.tool з serverId, toolName, порожніми arguments і назвою дії', () => {
    const connectors = buildPalette(SAMPLE_DESCRIPTORS, servers).find((s) => s.id === CONNECTORS_SECTION)!.items;

    const node = createNode(connectors[1], emptyWorkflowDefinition('С'));
    expect(node).toMatchObject({
      type: MCP_TOOL,
      typeVersion: 1,
      name: 'Надіслати повідомлення',
      parameters: { serverId: 'c-tg', toolName: 'send_message', arguments: {} },
    });
    expect(node.id).toMatch(/^n_[0-9a-f]{12}$/);
    expect(createNode(connectors[0].actions![1], emptyWorkflowDefinition('С'))).toMatchObject({ name: 'find_customer', parameters: { toolName: 'find_customer' } });
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

  it('тригери йдуть за популярністю, а не за алфавітом (F3-77)', () => {
    const triggers = buildPalette(SAMPLE_DESCRIPTORS, []).find((x) => x.id === 'group:trigger')!.items.map((i) => i.type);
    const expected = ['core.manualTrigger', 'core.schedule', 'core.webhook', 'mcp.poll'].filter((t) => triggers.includes(t));
    expect(triggers.filter((t) => expected.includes(t))).toEqual(expected);
  });

  it('технічний тип не показується в підказці елемента, але лишається в title і пошуку (F3-77)', () => {
    const item = buildPalette(SAMPLE_DESCRIPTORS, []).flatMap((x) => x.items).find((i) => i.type === 'core.webhook')!;
    expect(itemHint(item)).toBe('');
    expect(itemTitle(item)).toContain('core.webhook');
    expect(filterPalette(buildPalette(SAMPLE_DESCRIPTORS, []), 'core.webhook').flatMap((x) => x.items.map((i) => i.key))).toContain('core.webhook');
  });
});
