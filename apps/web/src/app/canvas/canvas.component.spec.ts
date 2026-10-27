import { TestBed } from '@angular/core/testing';
import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { CanvasComponent } from './canvas.component';
import { SAMPLE_DESCRIPTORS, largeWorkflow } from './sample-workflows';
import { VflowCanvasComponent } from './vflow/vflow-canvas.component';

function demo(): WorkflowDefinition {
  return {
    ...emptyWorkflowDefinition('Демо'),
    nodes: [
      { id: 'n_webhook', name: 'Нова заявка', type: 'core.webhook', typeVersion: 1, position: [80, 160], parameters: {} },
      { id: 'n_if', name: 'Умова', type: 'core.if', typeVersion: 1, position: [380, 160], parameters: {} },
      {
        id: 'n_1c',
        name: '1С: створити замовлення',
        type: 'mcp.tool',
        typeVersion: 1,
        position: [680, 160],
        parameters: { serverId: 's', toolName: 'create_order' },
        continueOnFail: true,
      },
    ],
    connections: [
      { from: { node: 'n_webhook', output: 0 }, to: { node: 'n_if', input: 0 } },
      { from: { node: 'n_if', output: 1 }, to: { node: 'n_1c', input: 0 } },
      { from: { node: 'n_1c', output: 0 }, to: { node: 'ghost', input: 0 } },
    ],
  };
}

describe('CanvasComponent', () => {
  async function create(definition: WorkflowDefinition, virtualization = false) {
    const fixture = TestBed.createComponent(CanvasComponent);
    fixture.componentRef.setInput('definition', definition);
    fixture.componentRef.setInput('descriptors', SAMPLE_DESCRIPTORS);
    fixture.componentRef.setInput('virtualization', virtualization);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  function vflow(fixture: Awaited<ReturnType<typeof create>>) {
    return fixture.debugElement.query((d) => d.componentInstance instanceof VflowCanvasComponent)
      .componentInstance as VflowCanvasComponent;
  }

  it('рендерить картку для кожного вузла з назвою, групою і типом', async () => {
    const fixture = await create(demo());

    const cards = (fixture.nativeElement as HTMLElement).querySelectorAll('[data-node-id]');
    expect(Array.from(cards).map((c) => c.getAttribute('data-node-id'))).toEqual(['n_webhook', 'n_if', 'n_1c']);
    expect(cards[0].textContent).toContain('Нова заявка');
    expect(cards[0].textContent).toContain('core.webhook');
    expect(cards[0].textContent).toContain('Тригери');
    expect(cards[2].textContent).toContain('mcp.tool · create_order');
  });

  it('F3-62: нотатки — картки з Markdown-текстом і кольором; зміна кольору чи розміру з канвасу — нове визначення', async () => {
    const definition: WorkflowDefinition = {
      ...demo(),
      notes: [
        { id: 'note_in', text: '## Вхід\n\nЗаявки з **Telegram** <b>не html</b>', position: [0, -160], size: [260, 120], color: 'green' },
        { id: 'note_empty', text: '', position: [500, 0] },
      ],
    };
    const fixture = await create(definition);
    const host = fixture.nativeElement as HTMLElement;
    const changes: WorkflowDefinition[] = [];
    fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));

    const notes = host.querySelectorAll<HTMLElement>('[data-note-id]');
    expect(Array.from(notes).map((n) => n.getAttribute('data-note-id'))).toEqual(['note_in', 'note_empty']);
    expect(notes[0].getAttribute('data-note-color')).toBe('green');
    expect(notes[1].getAttribute('data-note-color')).toBe('yellow');
    const body = notes[0].querySelector('[data-testid="note-body"]')!;
    expect(body.querySelector('h2')?.textContent).toBe('Вхід');
    expect(body.querySelector('strong')?.textContent).toBe('Telegram');
    expect(body.querySelector('b')).toBeNull();
    expect(body.textContent).toContain('<b>не html</b>');
    expect(notes[1].textContent).toContain('Двічі клацніть');
    // Вузли лишаються картками з портами, нотатки — без портів.
    expect(host.querySelectorAll('[data-node-id]').length).toBe(3);
    expect(notes[0].querySelectorAll('handle').length).toBe(0);

    const canvas = vflow(fixture);
    canvas.noteChange.emit({ id: 'note_in', color: 'red' });
    canvas.noteChange.emit({ id: 'note_empty', size: [300, 80] });
    canvas.noteChange.emit({ id: 'note_in', color: 'green' });
    expect(changes).toHaveLength(2);
    expect(changes[0].notes![0].color).toBe('red');
    expect(changes[0].nodes).toBe(definition.nodes);
    expect(changes[1].notes![1].size).toEqual([300, 80]);

    canvas.notesMoved.emit([{ id: 'note_empty', x: 520, y: 40 }]);
    expect(changes[2].notes![1].position).toEqual([520, 40]);
    expect(changes[2].notes![0]).toBe(definition.notes![0]);
  });

  it('порти — за дескрипторами: тригер без входу, IF з двома виходами, continueOnFail додає error', async () => {
    const fixture = await create(demo());
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelectorAll('[data-node-id="n_webhook"] handle').length).toBe(1);
    const ifCard = host.querySelector('[data-node-id="n_if"]')!;
    expect(ifCard.querySelectorAll('handle').length).toBe(3);
    expect(ifCard.textContent).toContain('true');
    expect(ifCard.textContent).toContain('false');
    const tool = host.querySelector('[data-node-id="n_1c"]')!;
    expect(tool.querySelectorAll('handle').length).toBe(3);
    expect(tool.textContent).toContain('error');
  });

  it('передає у vflow ребра за connections з портами за індексами; ребро на неіснуючий вузол не малюється', async () => {
    const fixture = await create(demo());

    const edges = (vflow(fixture) as unknown as { edges: () => { sourceHandle: string; targetHandle: string }[] }).edges();
    expect(edges.map((e) => [e.sourceHandle, e.targetHandle])).toEqual([
      ['out-0', 'in-0'],
      ['out-1', 'in-0'],
    ]);
  });

  it('перетягування вузла віддає визначення з новою position, решта без змін', async () => {
    const def = demo();
    const fixture = await create(def);
    let changed: WorkflowDefinition | null = null;
    fixture.componentInstance.definitionChange.subscribe((d) => (changed = d));

    const inner = vflow(fixture) as unknown as { nodes: () => { id: string; point: { set(p: { x: number; y: number }): void } }[]; dragEnded(): void };
    inner.nodes()[1].point.set({ x: 400, y: 300 });
    inner.dragEnded();

    expect(changed!.nodes[1].position).toEqual([400, 300]);
    expect(changed!.nodes[0]).toBe(def.nodes[0]);
    expect(changed!.connections[0]).toBe(def.connections[0]);
    expect(changed!.connections).toStrictEqual(def.connections);
  });

  it('200 вузлів зі спайку рендеряться повністю (без віртуалізації) і швидко', async () => {
    const def = largeWorkflow(200);
    const started = performance.now();

    const fixture = await create(def);

    const elapsed = performance.now() - started;
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-node-id]').length).toBe(200);
    const edges = (vflow(fixture) as unknown as { edges: () => unknown[] }).edges();
    expect(edges).toHaveLength(def.connections.length);
    // jsdom у CI повільніший за браузер; межа — від регресій на порядок (O(n²) і т. п.), не від мілісекунд.
    expect(elapsed).toBeLessThan(10_000);
  });

  it('200 вузлів з віртуалізацією за замовчуванням створюють модель без помилок', async () => {
    const fixture = await create(largeWorkflow(200), true);

    const nodes = (vflow(fixture) as unknown as { nodes: () => unknown[] }).nodes();
    expect(nodes).toHaveLength(200);
  });

  it('з’єднання портів у vflow додає ребро в кінець connections; петля — без змін', async () => {
    const def = demo();
    const fixture = await create(def);
    const changes: WorkflowDefinition[] = [];
    fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));
    const inner = vflow(fixture) as unknown as { connected(c: { source: string; target: string; sourceHandle?: string; targetHandle?: string }): void };

    inner.connected({ source: 'n_webhook', sourceHandle: 'out-0', target: 'n_1c', targetHandle: 'in-0' });
    inner.connected({ source: 'n_if', sourceHandle: 'out-0', target: 'n_if', targetHandle: 'in-0' });
    inner.connected({ source: 'n_webhook', sourceHandle: 'out-0', target: 'n_if', targetHandle: 'in-0' });

    expect(changes).toHaveLength(1);
    expect(changes[0].connections).toHaveLength(4);
    expect(changes[0].connections[3]).toEqual({ from: { node: 'n_webhook', output: 0 }, to: { node: 'n_1c', input: 0 } });
    expect(changes[0].connections.slice(0, 3)).toEqual(def.connections);
  });

  it('виділення з бібліотеки віддається назовні: вузли за id, ребра за індексом у connections', async () => {
    const fixture = await create(demo());
    const selections: unknown[] = [];
    fixture.componentInstance.selectionChange.subscribe((s) => selections.push(s));
    const inner = vflow(fixture) as unknown as {
      nodes: () => { id: string; selected: { set(v: boolean): void } }[];
      edges: () => { selected: { set(v: boolean): void } }[];
      selectionChanged(): void;
    };

    inner.nodes()[2].selected.set(true);
    inner.edges()[1].selected.set(true);
    inner.selectionChanged();

    expect(selections).toEqual([{ nodes: ['n_1c'], edges: [1] }]);
  });

  it('виділення ззовні (вставка) позначає вузли на канвасі', async () => {
    const fixture = await create(demo());
    fixture.componentRef.setInput('selection', { nodes: ['n_if'], edges: [] });
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-node-id="n_if"]')?.classList).toContain('selected');
    expect(host.querySelector('[data-node-id="n_webhook"]')?.classList).not.toContain('selected');
  });

  it('помилки валідації з nodeId підсвічують вузол і показують текст у підказці', async () => {
    const fixture = await create(demo());
    fixture.componentRef.setInput('errors', [
      { code: 'SCHEMA_VIOLATION', message: 'Поле condition обов’язкове', nodeId: 'n_if', path: '/nodes/1/parameters' },
      { code: 'MCP_TOOL_UNKNOWN', message: 'Tool не знайдено', nodeId: 'n_if' },
      { code: 'TRIGGER_MISSING', message: 'Немає тригера' },
    ]);
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const card = host.querySelector('[data-node-id="n_if"]')!;
    expect(card.getAttribute('data-invalid')).toBe('true');
    const badge = card.querySelector('[data-testid="node-errors"]')!;
    expect(badge.textContent).toContain('2');
    expect(badge.getAttribute('title')).toBe('Поле condition обов’язкове\nTool не знайдено');
    expect(host.querySelector('[data-node-id="n_webhook"]')?.getAttribute('data-invalid')).toBeNull();
    expect(host.querySelectorAll('[data-testid="node-errors"]')).toHaveLength(1);
  });

  it('статуси тестового запуску (F2-20) зафарбовують вузли: успіх — ✓, помилка — ✕, без кроку — без позначки', async () => {
    const fixture = await create(demo());
    fixture.componentRef.setInput('runStatuses', new Map([
      ['n_webhook', 'success'],
      ['n_if', 'error'],
    ]));
    fixture.detectChanges();
    await fixture.whenStable();

    const host = fixture.nativeElement as HTMLElement;
    const card = (id: string) => host.querySelector(`[data-node-id="${id}"]`)!;
    expect(card('n_webhook').getAttribute('data-run-status')).toBe('success');
    expect(card('n_webhook').classList).toContain('run-success');
    expect(card('n_webhook').querySelector('[data-testid="node-run-badge"]')?.textContent).toContain('✓');
    expect(card('n_if').getAttribute('data-run-status')).toBe('error');
    expect(card('n_if').querySelector('[data-testid="node-run-badge"]')?.textContent).toContain('✕');
    expect(card('n_1c').getAttribute('data-run-status')).toBeNull();
    expect(card('n_1c').querySelector('[data-testid="node-run-badge"]')).toBeNull();
  });

  it('помилка запуску (F3-57): перший рядок замість підзаголовка, повний текст — у підказці', async () => {
    const fixture = await create(demo());
    fixture.componentRef.setInput('runStatuses', new Map([['n_if', 'error']]));
    fixture.componentRef.setInput('runErrors', new Map([['n_if', 'Поле «x» порожнє. Вкажіть значення.\nдругий рядок']]));
    fixture.detectChanges();
    await fixture.whenStable();

    const card = (fixture.nativeElement as HTMLElement).querySelector('[data-node-id="n_if"]')!;
    const line = card.querySelector('[data-testid="node-run-error"]')!;
    expect(line.textContent?.trim()).toBe('Поле «x» порожнє. Вкажіть значення.');
    expect(line.getAttribute('title')).toContain('другий рядок');
    expect(card.querySelector('.node__type')).toBeNull();
  });

  it('«не заповнено: N» (F3-57): лічильник на картці, клік віддає перше поле', async () => {
    const fixture = await create(demo());
    fixture.componentRef.setInput('missingFields', new Map([['n_1c', ['toolName', 'arguments.text']]]));
    fixture.detectChanges();
    await fixture.whenStable();
    const requests: unknown[] = [];
    fixture.componentInstance.missingClick.subscribe((r) => requests.push(r));

    const host = fixture.nativeElement as HTMLElement;
    const badge = host.querySelector<HTMLButtonElement>('[data-node-id="n_1c"] [data-testid="node-missing"]')!;
    expect(badge.textContent?.trim()).toBe('не заповнено: 2');
    expect(badge.title).toContain('toolName, arguments.text');
    expect(host.querySelector('[data-node-id="n_if"] [data-testid="node-missing"]')).toBeNull();
    badge.click();
    expect(requests).toEqual([{ nodeId: 'n_1c', field: 'toolName' }]);
  });

  it('тулбар вузла, inline-перейменування і ПКМ (F3-58) віддаються назовні; у перегляді тулбару немає', async () => {
    const fixture = await create(demo());
    const c = fixture.componentInstance;
    const actions: unknown[] = [];
    const renames: unknown[] = [];
    const menus: unknown[] = [];
    c.nodeAction.subscribe((a) => actions.push(a));
    c.renamed.subscribe((r) => renames.push(r));
    c.contextMenu.subscribe((m) => menus.push(m));
    const host = fixture.nativeElement as HTMLElement;
    const card = () => host.querySelector<HTMLElement>('[data-node-id="n_if"]')!;

    card().querySelector<HTMLButtonElement>('[data-testid="node-tool-duplicate"]')!.click();
    card().querySelector<HTMLButtonElement>('[data-testid="node-tool-toggle"]')!.click();
    expect(actions).toEqual([
      { nodeId: 'n_if', action: 'duplicate' },
      { nodeId: 'n_if', action: 'toggle' },
    ]);

    fixture.componentRef.setInput('renaming', 'n_if');
    fixture.detectChanges();
    const input = card().querySelector<HTMLInputElement>('[data-testid="node-rename"]')!;
    expect(input.value).toBe('Умова');
    input.value = 'Перевірка';
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    input.dispatchEvent(new Event('blur'));
    expect(renames).toEqual([{ nodeId: 'n_if', name: 'Перевірка' }]);

    card().dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 5, clientY: 6 }));
    expect(menus).toEqual([{ target: { kind: 'node', nodeId: 'n_if' }, anchor: { x: 5, y: 6 } }]);

    fixture.componentRef.setInput('readonly', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.querySelector('[data-testid="node-toolbar"]')).toBeNull();
  });

  it('«Вирівняти» (F3-58): нові позиції зліва направо, ребра ті самі; кнопки fit/zoom на місці', async () => {
    const def = demo();
    def.nodes[2] = { ...def.nodes[2], position: [0, 600] };
    const fixture = await create(def);
    const changes: WorkflowDefinition[] = [];
    fixture.componentInstance.definitionChange.subscribe((d) => changes.push(d));
    const host = fixture.nativeElement as HTMLElement;
    for (const id of ['canvas-fit', 'canvas-zoom-in', 'canvas-zoom-out']) expect(host.querySelector(`[data-testid="${id}"]`)).not.toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="canvas-arrange"]')!.click();

    expect(changes).toHaveLength(1);
    const xs = changes[0].nodes.map((n) => n.position[0]);
    expect(xs[0]).toBeLessThan(xs[1]);
    expect(xs[1]).toBeLessThan(xs[2]);
    expect(changes[0].connections).toBe(def.connections);

    fixture.componentRef.setInput('readonly', true);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="canvas-arrange"]')).toBeNull();
  });
  it('підказка «Додайте наступний крок» (F3-59): біля порту тригера, доки немає з’єднань; у перегляді — немає', async () => {
    const lone: WorkflowDefinition = { ...emptyWorkflowDefinition('Новий'), nodes: [demo().nodes[0], demo().nodes[1]] };
    const fixture = await create(lone);
    const host = fixture.nativeElement as HTMLElement;
    const hint = () => host.querySelectorAll('[data-testid="first-step-hint"]');

    expect(hint().length).toBe(1);
    expect(hint()[0].closest('[data-node-id]')?.getAttribute('data-node-id')).toBe('n_webhook');
    expect(host.querySelector('[data-node-id="n_webhook"] [data-testid="port-add"]')).not.toBeNull();

    fixture.componentRef.setInput('definition', { ...lone, connections: [demo().connections[0]] });
    fixture.detectChanges();
    expect(hint().length).toBe(0);

    fixture.componentRef.setInput('definition', lone);
    fixture.componentRef.setInput('readonly', true);
    fixture.detectChanges();
    expect(hint().length).toBe(0);
  });

  it('подвійний клік по картці (зокрема по назві) віддає «open», а не «rename» (F3-78)', async () => {
    const fixture = await create(demo());
    const actions: unknown[] = [];
    fixture.componentInstance.nodeAction.subscribe((a) => actions.push(a));
    const card = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('[data-node-id="n_if"]')!;
    card.querySelector('.node__name')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
    expect(actions).toEqual([{ nodeId: 'n_if', action: 'open' }]);
  });
});
