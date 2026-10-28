import { emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { ConnectorCatalogEntry } from '../connectors/connectors-api.service';
import { CONNECTORS_SECTION, CUSTOM_CONNECTORS_SECTION, FREQUENT_SECTION, MCP_TOOL, PaletteServer, addNode, buildPalette, connectorPageSections, createNode, filterPalette, itemHint, itemTitle, nextNodePosition } from './palette';
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
  it('групує core.* за групами у фіксованому порядку; власні конектори — за назвою, дії й події — на сторінці конектора, без узагальненого mcp.tool', () => {
    const sections = buildPalette(SAMPLE_DESCRIPTORS, servers);

    expect(sections.map((s) => s.id)).toEqual(['group:trigger', 'group:logic', 'group:transform', 'group:ai', CONNECTORS_SECTION]);
    expect(sections.flatMap((s) => s.items).filter((i) => i.type === MCP_TOOL && !i.serverId && !i.connector)).toEqual([]);
    expect(sections[0].items.map((i) => i.type)).toContain('core.webhook');
    const connectors = sections[4].items;
    // Конектор без tools не показується; клік відкриває сторінку, а не додає вузол.
    expect(connectors.map((i) => [i.key, i.label, i.toolName])).toEqual([
      ['connector:c-1c', '1С', undefined],
      ['connector:c-tg', 'Telegram', undefined],
    ]);
    expect(connectors[0].actions?.map((a) => a.key)).toEqual(['c-1c/create_order', 'c-1c/find_customer']);
    expect(itemHint(connectors[0])).toBe('підключено · 2');
    expect(connectors[1].actions?.[0]).toMatchObject({ label: 'Telegram → Надіслати повідомлення', type: MCP_TOOL, serverId: 'c-tg', toolName: 'send_message', group: 'integration', connectorId: 'c-tg' });
  });

  describe('каталог (F3-96)', () => {
    const catalog: ConnectorCatalogEntry[] = [
      {
        manifest: {
          id: 'telegram',
          name: 'Telegram',
          description: 'Повідомлення бота.',
          icon: '✈️',
          transport: 'stdio',
          actions: [
            { name: 'send_message', title: 'Надіслати повідомлення' },
            { name: 'send_photo', title: 'Надіслати фото' },
          ],
          triggers: [
            { id: 'new_update', title: 'Нове повідомлення Telegram', description: 'Вхідні повідомлення бота', node: { type: 'mcp.poll', parameters: { toolName: 'get_updates', arguments: {}, itemsPath: 'result', keyField: 'update_id' } } },
            { id: 'unknown', title: 'Невідомий тип', node: { type: 'mcp.subscribe', parameters: { resourceUri: 'x://y' } } },
          ],
        },
        source: 'builtin',
        installed: [],
      },
      { manifest: { id: 'crm', name: 'CRM', description: 'Угоди.', transport: 'stdio', triggers: [] }, source: 'builtin', installed: [] },
    ];
    const installed: PaletteServer[] = [
      { id: 'bot1', name: 'Бот продажів', catalogId: 'telegram', tools: [{ name: 'send_message', title: 'Надіслати повідомлення', inputSchema: {} }, { name: 'get_updates', inputSchema: {} }] },
      { id: 'own', name: 'Свій сервер', catalogId: null, tools: [{ name: 'ping', inputSchema: {} }] },
    ];

    it('записи каталогу (підключені й ні), потім «Власні конектори»', () => {
      const sections = buildPalette(SAMPLE_DESCRIPTORS, installed, [], catalog);
      expect(sections.map((s) => s.id)).toEqual(['group:trigger', 'group:logic', 'group:transform', 'group:ai', CONNECTORS_SECTION, CUSTOM_CONNECTORS_SECTION]);
      const entries = sections.find((s) => s.id === CONNECTORS_SECTION)!.items;
      expect(entries.map((i) => [i.label, i.connector?.connected, itemHint(i)])).toEqual([
        ['CRM', false, 'не підключено'],
        ['Telegram', true, 'підключено · 3'],
      ]);
      expect(entries[1].connector).toMatchObject({ icon: '✈️', description: 'Повідомлення бота.' });
      expect(sections.find((s) => s.id === CUSTOM_CONNECTORS_SECTION)!.items.map((i) => i.label)).toEqual(['Свій сервер']);
    });

    it('сторінка конектора: події (пресети; невідомий тип пропущено) і дії з кешу tools підключення', () => {
      const telegram = buildPalette(SAMPLE_DESCRIPTORS, installed, [], catalog).flatMap((s) => s.items).find((i) => i.key === 'connector:telegram')!;
      const page = connectorPageSections(telegram);
      expect(page[0]).toMatchObject({ label: 'Події' });
      expect(page[0].items.map((i) => i.key)).toEqual(['trigger:telegram/new_update']);
      // Дії згруповані, як у полі «Дія» (F3-55): кілька груп — кожна зі своєю назвою.
      expect(page.slice(1).flatMap((s) => s.items.map((i) => i.key)).sort()).toEqual(['telegram/get_updates', 'telegram/send_message']);
      // Єдине підключення підставляється одразу.
      expect(page[0].items[0]).toMatchObject({ label: 'Telegram → Нове повідомлення Telegram', nodeName: 'Нове повідомлення Telegram', group: 'trigger', type: 'mcp.poll', serverId: 'bot1', connectorId: 'telegram' });
      const node = createNode(page[0].items[0], emptyWorkflowDefinition('t'));
      expect(node.parameters).toEqual({ serverId: 'bot1', toolName: 'get_updates', arguments: {}, itemsPath: 'result', keyField: 'update_id' });
    });

    it('непідключений конектор: дії з маніфесту, підключення (serverId) обирає редактор', () => {
      const telegram = buildPalette(SAMPLE_DESCRIPTORS, [], [], catalog).flatMap((s) => s.items).find((i) => i.key === 'connector:telegram')!;
      expect(telegram.connector?.connected).toBe(false);
      const actions = connectorPageSections(telegram).filter((s) => s.id !== 'events').flatMap((s) => s.items);
      expect(actions.map((a) => [a.key, a.nodeName, a.serverId, a.connectorId])).toEqual([
        ['telegram/send_message', 'Надіслати повідомлення', undefined, 'telegram'],
        ['telegram/send_photo', 'Надіслати фото', undefined, 'telegram'],
      ]);
    });

    it('кілька підключень одного запису: serverId не підставляється, конектор — підключено', () => {
      const two: PaletteServer[] = [installed[0], { ...installed[0], id: 'bot2', name: 'Бот підтримки' }];
      const telegram = buildPalette(SAMPLE_DESCRIPTORS, two, [], catalog).flatMap((s) => s.items).find((i) => i.key === 'connector:telegram')!;
      expect(telegram.connector?.connected).toBe(true);
      expect(telegram.actions?.every((a) => a.serverId === undefined)).toBe(true);
    });

    it('пошук у корені знаходить дії й події, зокрема непідключеного конектора', () => {
      const sections = buildPalette(SAMPLE_DESCRIPTORS, [], [], catalog);
      const found = (q: string) => filterPalette(sections, q).flatMap((s) => s.items.map((i) => [i.key, i.label]));
      expect(found('надіслати фото')).toEqual([['telegram/send_photo', 'Telegram → Надіслати фото']]);
      expect(found('нове повідомлення')).toEqual([['trigger:telegram/new_update', 'Telegram → Нове повідомлення Telegram']]);
      // Збіг із назвою — сам конектор.
      expect(found('crm')).toEqual([['connector:crm', 'CRM']]);
    });

    it('пошук на сторінці конектора — лише серед його дій', () => {
      const telegram = buildPalette(SAMPLE_DESCRIPTORS, [], [], catalog).flatMap((s) => s.items).find((i) => i.key === 'connector:telegram')!;
      expect(filterPalette(connectorPageSections(telegram), 'фото').flatMap((s) => s.items.map((i) => i.key))).toEqual(['telegram/send_photo']);
    });

    it('«Часті дії» знаходять дії конектора з каталогу за ключем запису', () => {
      const frequent = buildPalette(SAMPLE_DESCRIPTORS, installed, ['telegram/send_message'], catalog).find((s) => s.id === FREQUENT_SECTION)!;
      expect(frequent.items.map((i) => i.key)).toEqual(['telegram/send_message']);
    });
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

    const node = createNode(connectors[1].actions![0], emptyWorkflowDefinition('С'));
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
