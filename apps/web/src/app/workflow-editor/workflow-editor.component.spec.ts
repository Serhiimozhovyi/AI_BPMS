import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CanvasComponent } from '../canvas/canvas.component';
import { SAMPLE_DESCRIPTORS } from '../canvas/sample-workflows';
import { ConnectorDetail, ConnectorSummary } from '../connectors/connectors-api.service';
import { AUTOSAVE_DEBOUNCE_MS } from '../canvas/autosave';
import { WorkflowDefinition, WorkflowDetail, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { EVENT_SOURCE_FACTORY, ExecutionStep } from '../executions/executions-api.service';
import { FakeEventSource } from '../executions/fake-event-source';
import { WorkflowEditorComponent } from './workflow-editor.component';

const workflow: WorkflowDetail = {
  id: 'w1',
  name: 'Заявки',
  active: false,
  version: 3,
  versionId: 'v3',
  updatedAt: '2026-10-23T10:00:00Z',
  definition: {
    ...emptyWorkflowDefinition('Заявки'),
    nodes: [
      { id: 'n_hook', name: 'Нова заявка', type: 'core.webhook', typeVersion: 1, position: [100, 200], parameters: {} },
      { id: 'n_set', name: 'Поля', type: 'core.set', typeVersion: 1, position: [360, 200], parameters: {} },
    ],
    connections: [{ from: { node: 'n_hook', output: 0 }, to: { node: 'n_set', input: 0 } }],
  },
};

const withTools: ConnectorSummary = { id: 'c1', name: '1С', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: null };
const noTools: ConnectorSummary = { id: 'c2', name: 'Порожній', transport: 'http', status: 'new', toolCount: 0, toolsCachedAt: null };
const detail1c: ConnectorDetail = {
  summary: withTools,
  config: {},
  credentialId: null,
  tools: [{ name: 'create_order', inputSchema: { type: 'object' } }],
};

describe('WorkflowEditorComponent', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: EVENT_SOURCE_FACTORY, useValue: (url: string) => new FakeEventSource(url) },
      ],
    });
    const fixture = TestBed.createComponent(WorkflowEditorComponent);
    fixture.componentRef.setInput('workflowId', 'w1');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    // Облікові дані для вибору у вузлах (F4-03) — тут не перевіряються.
    http.expectOne((r) => r.url === '/api/v1/credentials/options').flush([]);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: ReturnType<typeof setup>['fixture']) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function flushConnectors(http: HttpTestingController) {
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [withTools, noTools], page: 1, pageSize: 200, total: 2 });
    http.expectOne('/api/v1/connectors/c1').flush(detail1c);
  }

  /** Визначення, передане в канвас (з віртуалізацією jsdom без розмірів не рендерить картки). */
  function canvasNodes(fixture: ReturnType<typeof setup>['fixture']) {
    const canvas = fixture.debugElement.query((d) => d.componentInstance instanceof CanvasComponent);
    return (canvas.componentInstance as CanvasComponent).definition().nodes;
  }

  // Палітра за замовчуванням згорнута, коли у сценарії є вузли (F3-89): більшість тестів користується палітрою — вона розгорнута вибором.
  beforeEach(() => localStorage.setItem('openbp.editor.palette', '1'));

  afterEach(() => {
    localStorage.removeItem('openbp.editor.palette');
    const http = TestBed.inject(HttpTestingController);
    // Запит останнього виконання при відкритті (F3-76) більшість тестів не цікавить — окремий блок нижче.
    http.match((r) => r.method === 'GET' && r.url === '/api/v1/executions');
    http.verify();
  });

  it('«не заповнено: N» (F3-57): обов’язкові аргументи tool’а; клік відкриває вузол і ставить фокус на перше поле', async () => {
    const { fixture, http, host } = setup();
    const mcpNode = { id: 'n_mcp', name: '1С', type: 'mcp.tool', typeVersion: 1, position: [360, 200] as [number, number], parameters: { serverId: 'c1', toolName: 'create_order', arguments: {} } };
    http.expectOne('/api/v1/workflows/w1').flush({ ...workflow, definition: { ...workflow.definition, nodes: [workflow.definition.nodes[0], mcpNode], connections: [] } });
    const mcpSchema = { type: 'object', properties: { serverId: { type: 'string' }, toolName: { type: 'string' }, arguments: { type: 'object' } } };
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS.map((d) => (d.type === 'mcp.tool' ? { ...d, parametersSchema: mcpSchema } : d)));
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [withTools], page: 1, pageSize: 200, total: 1 });
    http.expectOne('/api/v1/connectors/c1').flush({
      ...detail1c,
      tools: [{ name: 'create_order', inputSchema: { type: 'object', required: ['customer'], properties: { customer: { type: 'string', title: 'Клієнт' } } } }],
    });
    await render(fixture);

    expect(Object.fromEntries(canvasComponent(fixture).missingFields())).toEqual({ n_mcp: ['arguments.customer'] });
    canvasComponent(fixture).missingClick.emit({ nodeId: 'n_mcp', field: 'arguments.customer' });
    await render(fixture);
    expect(canvasComponent(fixture).selection().nodes).toEqual(['n_mcp']);
    expect(host.querySelector('[data-testid="panel-tab-params"]')?.getAttribute('aria-selected')).toBe('true');
    await vi.waitFor(() => expect(document.activeElement?.closest('[data-field="customer"]')).not.toBeNull());
  });

  it('палітра отримує пресети тригерів із каталогу для конекторів, встановлених з нього (F6-21)', async () => {
    const { fixture, http } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    const installed = { ...withTools, name: 'Telegram', catalogId: 'telegram' };
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [installed], page: 1, pageSize: 200, total: 1 });
    http.expectOne('/api/v1/connectors/c1').flush({ ...detail1c, summary: installed });
    http.expectOne('/api/v1/connectors/catalog').flush([
      {
        manifest: {
          id: 'telegram',
          triggers: [{ id: 'new_update', title: 'Нове повідомлення Telegram', node: { type: 'mcp.poll', parameters: { toolName: 'get_updates', keyField: 'update_id', cron: '*/1 * * * *' } } }],
        },
        source: 'builtin',
        installed: [installed],
      },
    ]);
    await render(fixture);

    const palette = (fixture.componentInstance as unknown as { palette(): { id: string; items: { key: string }[] }[] }).palette();
    expect(palette.find((s) => s.id === 'trigger-presets')?.items.map((i) => i.key)).toEqual(['trigger:c1/new_update']);
  });

  async function loaded(detail: WorkflowDetail = workflow) {
    const ctx = setup();
    ctx.http.expectOne('/api/v1/workflows/w1').flush(detail);
    ctx.http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(ctx.http);
    await render(ctx.fixture);
    return ctx;
  }

  function canvasComponent(fixture: ReturnType<typeof setup>['fixture']): CanvasComponent {
    return fixture.debugElement.query((d) => d.componentInstance instanceof CanvasComponent).componentInstance as CanvasComponent;
  }

  function click(host: HTMLElement, selector: string): void {
    (host.querySelector(selector) as HTMLButtonElement).click();
  }

  /** Пункт меню «⋯» шапки панелі вузла (F3-88): відкриває меню, якщо воно закрите; пункту немає — `null`. */
  async function menuItem(fixture: ReturnType<typeof setup>['fixture'], host: HTMLElement, testId: string): Promise<HTMLButtonElement | null> {
    if (!host.querySelector('[data-testid="node-menu"]')) {
      (host.querySelector('[data-testid="node-more"]') as HTMLButtonElement).click();
      await render(fixture);
    }
    return host.querySelector<HTMLButtonElement>(`[data-testid="${testId}"]`);
  }

  async function menuClick(fixture: ReturnType<typeof setup>['fixture'], host: HTMLElement, testId: string): Promise<void> {
    (await menuItem(fixture, host, testId))!.click();
  }

  function button(host: HTMLElement, testId: string): HTMLButtonElement {
    return host.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement;
  }

  function key(init: KeyboardEventInit): void {
    document.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }));
  }

  function status(host: HTMLElement): string | null {
    return host.querySelector('[data-testid="dirty"]')?.getAttribute('data-status') ?? null;
  }

  function saved(version: number, definition: WorkflowDefinition): WorkflowDetail {
    return { ...workflow, version, versionId: `v${version}`, definition };
  }

  it('сценарій у групі: назва групи поруч із назвою, посилання на список з фільтром (F3-64)', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush({ ...workflow, groupId: 'g1' });
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(http);
    http.expectOne('/api/v1/workflow-groups').flush([{ id: 'g1', name: 'Продажі', sortOrder: 0, workflowCount: 1 }]);
    await render(fixture);
    const link = host.querySelector<HTMLAnchorElement>('[data-testid="workflow-group"]')!;
    expect(link.textContent?.trim()).toBe('Продажі');
    expect(link.getAttribute('href')).toBe('/workflows?group=g1');
  });

  it('завантажує сценарій, дескриптори й tools; показує вузли на канвасі і палітру з групою сервера', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(http);
    await render(fixture);

    expect(host.querySelector('[data-testid="workflow-name"]')?.textContent).toContain('Заявки');
    expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook', 'n_set']);
    expect(host.querySelector('[data-testid="counts"]')?.textContent).toContain('2');
    expect(host.querySelector('[data-section="connectors"] [data-item="connector:c1"]')?.textContent).toContain('1С');
    expect(host.querySelector('[data-item="connector:c2"]')).toBeNull();
    expect(host.querySelector('[data-testid="dirty"]')).toBeNull();
  });

  it('посилання «Журнал виконань» веде на журнал цього сценарію', async () => {
    const { fixture, host } = await loaded();
    click(host, '[data-testid="toolbar-more"]');
    await render(fixture);
    const link = host.querySelector<HTMLAnchorElement>('[data-testid="executions-link"]')!;
    expect(link.getAttribute('href')).toBe('/executions?workflowId=w1');
  });

  it('клік по конектору в палітрі додає вузол mcp.tool з дією за замовчуванням', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(http);
    await render(fixture);

    (host.querySelector('[data-item="connector:c1"]') as HTMLButtonElement).click();
    await render(fixture);

    const nodes = canvasNodes(fixture);
    expect(nodes).toHaveLength(3);
    expect(nodes[2]).toMatchObject({
      type: 'mcp.tool',
      name: 'create_order',
      position: [620, 200],
      parameters: { serverId: 'c1', toolName: 'create_order', arguments: {} },
    });
    expect(nodes[0]).toBe(workflow.definition.nodes[0]);
    expect(host.querySelector('[data-testid="counts"]')?.textContent).toContain('3');
    expect(host.querySelector('[data-testid="dirty"]')).not.toBeNull();
    fixture.destroy();
    // Незбережене при виході з редактора відправляється одразу.
    http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' }).flush(saved(4, workflow.definition));
  });

  it('автозбереження: після паузи PUT з поточним визначенням, потім «Збережено · версія N»', async () => {
    const { fixture, http, host } = await loaded();
    vi.useFakeTimers();
    try {
      click(host, '[data-item="core.set"]');
      fixture.detectChanges();
      click(host, '[data-item="core.if"]');
      fixture.detectChanges();
      vi.advanceTimersByTime(AUTOSAVE_DEBOUNCE_MS - 1);
      http.expectNone({ method: 'PUT' });
      vi.advanceTimersByTime(1);
    } finally {
      vi.useRealTimers();
    }

    const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
    const body = put.request.body as WorkflowDefinition;
    expect(body.nodes.map((n) => n.type)).toEqual(['core.webhook', 'core.set', 'core.set', 'core.if']);
    expect(body.nodes[0]).toBe(workflow.definition.nodes[0]);
    await render(fixture);
    expect(status(host)).toBe('saving');

    put.flush(saved(4, body));
    await render(fixture);
    expect(host.querySelector('[data-testid="dirty"]')).toBeNull();
    expect(host.querySelector('[data-testid="saved"]')?.getAttribute('title')).toContain('4');
    expect(canvasNodes(fixture)).toBe(body.nodes);
  });

  it('undo/redo кнопками і клавішами; кожен крок теж автозберігається', async () => {
    const { fixture, http, host } = await loaded();
    expect(button(host, 'undo').disabled).toBe(true);

    click(host, '[data-item="core.set"]');
    await render(fixture);
    click(host, '[data-item="core.if"]');
    await render(fixture);
    expect(canvasNodes(fixture)).toHaveLength(4);
    expect(button(host, 'redo').disabled).toBe(true);

    button(host, 'undo').click();
    await render(fixture);
    expect(canvasNodes(fixture).map((n) => n.type)).toEqual(['core.webhook', 'core.set', 'core.set']);
    key({ key: 'z', ctrlKey: true });
    await render(fixture);
    expect(canvasNodes(fixture)).toBe(workflow.definition.nodes);
    expect(button(host, 'undo').disabled).toBe(true);

    key({ key: 'z', ctrlKey: true, shiftKey: true });
    await render(fixture);
    expect(canvasNodes(fixture)).toHaveLength(3);
    button(host, 'redo').click();
    await render(fixture);
    expect(canvasNodes(fixture)).toHaveLength(4);
    key({ key: 'z', metaKey: true });
    key({ key: 'y', ctrlKey: true });
    await render(fixture);
    expect(canvasNodes(fixture)).toHaveLength(4);

    button(host, 'undo').click();
    await render(fixture);
    fixture.destroy();
    // Зберігається стан після останнього кроку (undo), а не проміжні.
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
    expect((put.request.body as WorkflowDefinition).nodes).toHaveLength(3);
    put.flush(saved(4, put.request.body as WorkflowDefinition));
  });

  it('F3-58: D вимикає, Ctrl+D дублює, Ctrl+A виділяє все, стрілки переходять, F2 перейменовує, ПКМ → Дублювати', async () => {
    const { fixture, http, host } = await loaded();
    const canvas = () => canvasComponent(fixture);
    const nodes = () => canvasNodes(fixture);
    canvas().selectionChange.emit({ nodes: ['n_set'], edges: [] });
    await render(fixture);

    key({ key: 'd' });
    await render(fixture);
    expect(nodes().find((n) => n.id === 'n_set')?.disabled).toBe(true);
    canvas().nodeAction.emit({ nodeId: 'n_set', action: 'toggle' });
    await render(fixture);
    expect(nodes().find((n) => n.id === 'n_set')?.disabled).toBeUndefined();

    key({ key: 'd', ctrlKey: true });
    await render(fixture);
    expect(nodes().map((n) => n.name)).toEqual(['Нова заявка', 'Поля', 'Поля 2']);
    expect(canvas().selection().nodes).toEqual([nodes()[2].id]);

    key({ key: 'a', ctrlKey: true });
    await render(fixture);
    expect(canvas().selection()).toEqual({ nodes: nodes().map((n) => n.id), edges: [0] });

    canvas().selectionChange.emit({ nodes: ['n_hook'], edges: [] });
    await render(fixture);
    key({ key: 'ArrowRight' });
    await render(fixture);
    expect(canvas().selection().nodes).toEqual(['n_set']);

    // F2: inline-поле в шапці панелі (F3-88); зайнята назва — повідомлення, нова — перейменування.
    const rename = async (name: string) => {
      key({ key: 'F2' });
      await render(fixture);
      const input = host.querySelector<HTMLInputElement>('[data-testid="node-panel-name-input"]')!;
      expect(input).not.toBeNull();
      input.value = name;
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      await render(fixture);
    };
    await rename('Нова заявка');
    expect(host.querySelector('[data-testid="node-panel-name-input"]')).toBeNull();
    expect(host.querySelector('[data-testid="editor-notice"]')?.textContent).toContain('вже є');
    await rename('Поля заявки');
    expect(nodes().find((n) => n.id === 'n_set')?.name).toBe('Поля заявки');

    // ПКМ на вузлі → меню; «Дублювати» додає копію.
    canvas().contextMenu.emit({ target: { kind: 'node', nodeId: 'n_hook' }, anchor: { x: 40, y: 40 } });
    await render(fixture);
    expect(host.querySelector('[data-testid="context-menu"]')?.getAttribute('data-kind')).toBe('node');
    click(host, '[data-testid="menu-duplicate"]');
    await render(fixture);
    expect(host.querySelector('[data-testid="context-menu"]')).toBeNull();
    expect(nodes().map((n) => n.name)).toContain('Нова заявка 2');

    // ПКМ на канвасі → «Виділити все».
    canvas().contextMenu.emit({ target: { kind: 'canvas' }, anchor: { x: 40, y: 40 } });
    await render(fixture);
    click(host, '[data-testid="menu-select-all"]');
    await render(fixture);
    expect(canvas().selection().nodes).toHaveLength(4);

    fixture.destroy();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
    put.flush(saved(4, put.request.body as WorkflowDefinition));
  });

  it('F3-62: Shift+S і ПКМ → «Додати нотатку» додають нотатку з відкритим полем; Escape на порожній прибирає її; Delete видаляє виділену', async () => {
    const { fixture, http, host } = await loaded();
    const canvas = () => canvasComponent(fixture);
    const notes = () => canvas().definition().notes ?? [];

    // Shift+S: нова порожня нотатка в центрі канвасу, виділена, у режимі редагування.
    key({ key: 'S', shiftKey: true });
    await render(fixture);
    expect(notes()).toHaveLength(1);
    expect(notes()[0]).toMatchObject({ text: '', size: [240, 120], color: 'yellow' });
    expect(canvas().editingNote()).toBe(notes()[0].id);
    expect(canvas().selection().notes).toEqual([notes()[0].id]);

    // Збережений текст — крок історії; Ctrl+Z повертає порожню нотатку.
    canvas().noteEdit.emit({ id: notes()[0].id, text: 'Перевірити **ліміт** 1С' });
    await render(fixture);
    expect(canvas().editingNote()).toBeNull();
    expect(notes()[0].text).toBe('Перевірити **ліміт** 1С');
    key({ key: 'z', ctrlKey: true });
    await render(fixture);
    expect(notes()[0].text).toBe('');
    key({ key: 'y', ctrlKey: true });
    await render(fixture);

    // ПКМ на порожньому місці → «Додати нотатку» саме там; Escape на порожній нотатці прибирає її.
    canvas().contextMenu.emit({ target: { kind: 'canvas' }, anchor: { x: 40, y: 40 }, at: [500, 300] });
    await render(fixture);
    click(host, '[data-testid="menu-add-note"]');
    await render(fixture);
    expect(notes()).toHaveLength(2);
    expect(notes()[1].position).toEqual([500, 300]);
    expect(canvas().editingNote()).toBe(notes()[1].id);
    canvas().noteEdit.emit({ id: notes()[1].id, text: null });
    await render(fixture);
    expect(notes()).toHaveLength(1);
    expect(canvas().editingNote()).toBeNull();

    // ПКМ на нотатці → меню нотатки; «Редагувати нотатку» відкриває поле; Delete видаляє виділену нотатку, вузли лишаються.
    const id = notes()[0].id;
    canvas().contextMenu.emit({ target: { kind: 'note', noteId: id }, anchor: { x: 40, y: 40 } });
    await render(fixture);
    expect(host.querySelector('[data-testid="context-menu"]')?.getAttribute('data-kind')).toBe('note');
    click(host, '[data-testid="menu-edit-note"]');
    await render(fixture);
    expect(canvas().editingNote()).toBe(id);
    canvas().noteEdit.emit({ id, text: 'Перевірити **ліміт** 1С' });
    await render(fixture);
    expect(button(host, 'delete').disabled).toBe(false);
    key({ key: 'Delete' });
    await render(fixture);
    expect(canvas().definition().notes).toBeUndefined();
    expect(canvasNodes(fixture)).toHaveLength(2);
    expect(canvas().selection()).toEqual({ nodes: [], edges: [] });

    fixture.destroy();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
    put.flush(saved(4, put.request.body as WorkflowDefinition));
  });

  it('гарячі клавіші не перехоплюються в полях введення і коли дія недоступна', async () => {
    const { fixture, http, host } = await loaded();
    click(host, '[data-item="core.set"]');
    await render(fixture);

    const search = host.querySelector('[data-testid="palette-search"]') as HTMLInputElement;
    search.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }));
    await render(fixture);
    expect(canvasNodes(fixture)).toHaveLength(3);

    // Недоступна дія (копіювати без виділення, redo без історії) — стандартна поведінка браузера не блокується.
    const copy = new KeyboardEvent('keydown', { key: 'c', ctrlKey: true, bubbles: true, cancelable: true });
    document.dispatchEvent(copy);
    expect(copy.defaultPrevented).toBe(false);
    const redo = new KeyboardEvent('keydown', { key: 'y', ctrlKey: true, bubbles: true, cancelable: true });
    document.dispatchEvent(redo);
    expect(redo.defaultPrevented).toBe(false);

    fixture.destroy();
    http.expectOne({ method: 'PUT' }).flush(saved(4, workflow.definition));
  });

  it('виділення: Delete видаляє вузол з ребрами, Ctrl+C / Ctrl+V вставляє копію і виділяє її', async () => {
    const { fixture, http, host } = await loaded();
    const canvas = canvasComponent(fixture);
    expect(button(host, 'delete').disabled).toBe(true);
    expect(button(host, 'copy').disabled).toBe(true);

    canvas.selectionChange.emit({ nodes: ['n_set'], edges: [] });
    await render(fixture);
    expect(button(host, 'delete').disabled).toBe(false);
    key({ key: 'c', ctrlKey: true });
    key({ key: 'v', ctrlKey: true });
    await render(fixture);
    const nodes = canvasNodes(fixture);
    expect(nodes.map((n) => n.name)).toEqual(['Нова заявка', 'Поля', 'Поля 2']);
    expect(nodes[2].position).toEqual([400, 240]);
    expect(canvas.selection()).toEqual({ nodes: [nodes[2].id], edges: [] });

    canvas.selectionChange.emit({ nodes: ['n_set'], edges: [] });
    key({ key: 'Delete' });
    await render(fixture);
    expect(canvasNodes(fixture).map((n) => n.name)).toEqual(['Нова заявка', 'Поля 2']);
    expect(canvas.definition().connections).toEqual([]);
    expect(canvas.selection()).toEqual({ nodes: [], edges: [] });

    fixture.destroy();
    http.expectOne({ method: 'PUT' }).flush(saved(4, canvas.definition()));
  });

  it('панель вузла: один виділений вузол — форма; зміна у формі — крок історії й автозбереження; Delete у формі не видаляє вузол', async () => {
    const { fixture, http, host } = await loaded();
    const canvas = canvasComponent(fixture);
    expect(host.querySelector('app-node-panel')).toBeNull();

    canvas.selectionChange.emit({ nodes: ['n_set'], edges: [] });
    await render(fixture);
    expect(host.querySelector('[data-testid="node-panel-name"]')?.textContent).toContain('Поля');

    const notes = host.querySelector('[data-testid="field-notes"]') as HTMLTextAreaElement;
    notes.value = 'Нотатка';
    notes.dispatchEvent(new Event('input'));
    const del = new KeyboardEvent('keydown', { key: 'Backspace', bubbles: true, cancelable: true });
    notes.dispatchEvent(del);
    await render(fixture);
    expect(del.defaultPrevented).toBe(false);
    expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook', 'n_set']);
    expect(canvasNodes(fixture)[1].notes).toBe('Нотатка');
    expect(button(host, 'undo').disabled).toBe(false);

    fixture.componentInstance['autosave'].flush();
    const req = http.expectOne({ method: 'PUT' });
    expect(req.request.body.nodes[1]).toEqual({ ...workflow.definition.nodes[1], notes: 'Нотатка' });
    req.flush(saved(4, req.request.body));

    canvas.selectionChange.emit({ nodes: ['n_set', 'n_hook'], edges: [] });
    await render(fixture);
    expect(host.querySelector('app-node-panel')).toBeNull();
  });

  it('422: помилки валідації — на вузлах канвасу, загальні — у панелі; зміни лишаються незбереженими', async () => {
    const { fixture, http, host } = await loaded();
    click(host, '[data-item="core.set"]');
    await render(fixture);
    fixture.componentInstance['autosave'].flush();

    http.expectOne({ method: 'PUT' }).flush(
      {
        title: 'Validation',
        status: 422,
        detail: 'Сценарій не пройшов валідацію.',
        errors: [
          { code: 'SCHEMA_VIOLATION', message: 'Поле fields обов’язкове', nodeId: 'n_set', path: '/nodes/1/parameters' },
          { code: 'GRAPH_CYCLE', message: 'Граф містить цикл', path: '/connections' },
        ],
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await render(fixture);

    expect(canvasComponent(fixture).errors().map((e) => e.nodeId)).toEqual(['n_set', undefined]);
    const panel = host.querySelector('[data-testid="save-error"]')!;
    expect(panel.textContent).toContain('Сценарій не пройшов валідацію.');
    expect(panel.textContent).toContain('Граф містить цикл');
    expect(panel.textContent).not.toContain('fields');
    expect(status(host)).toBe('error');
    expect(canvasNodes(fixture)).toHaveLength(3);
  });

  it('помилка завантаження сценарію — повідомлення замість канвасу', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush({ title: 'Not found', detail: 'Сценарій не знайдено' }, { status: 404, statusText: 'Not Found' });
    // forkJoin скасовує паралельний запит дескрипторів після помилки.
    expect(http.match('/api/v1/nodes').every((r) => r.cancelled)).toBe(true);
    flushConnectors(http);
    await render(fixture);

    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('Сценарій не знайдено');
    expect(host.querySelector('app-canvas')).toBeNull();
  });

  it('недоступні конектори не ламають редактор: палітра лишається з core.*', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush(null, { status: 403, statusText: 'Forbidden' });
    await render(fixture);

    expect(host.querySelector('[data-testid="tools-error"]')).not.toBeNull();
    expect(host.querySelector('[data-item="core.if"]')).not.toBeNull();
    expect(canvasNodes(fixture)).toHaveLength(2);
  });

  it('focusNode (?node=, F3-81) виділяє вузол після завантаження; невідомий id ігнорується', async () => {
    for (const [focus, expected] of [['n_set', ['n_set']], ['n_nope', []]] as const) {
      TestBed.resetTestingModule();
      const { fixture, http } = setup();
      fixture.componentRef.setInput('focusNode', focus);
      http.expectOne('/api/v1/workflows/w1').flush(workflow);
      http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
      http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [], page: 1, pageSize: 200, total: 0 });
      await render(fixture);
      expect(canvasComponent(fixture).selection().nodes).toEqual(expected);
    }
  });

  it('без конекторів у палітрі нейтральна підказка, а не помилка (F3-77)', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [], page: 1, pageSize: 200, total: 0 });
    await render(fixture);

    expect(host.querySelector('[data-testid="tools-error"]')).toBeNull();
    expect(host.querySelector('[data-testid="no-connectors"]')?.textContent).toContain('Конекторів ще немає');
    expect(host.querySelector('[data-testid="no-connectors"] a')?.getAttribute('href')).toBe('/connectors');
  });

  it('збій конекторів показує причину і «Повторити» перезавантажує список (F3-77)', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ title: 'x', detail: 'База недоступна' }, { status: 500, statusText: 'Server Error' });
    await render(fixture);
    expect(host.querySelector('[data-testid="tools-error"]')?.textContent).toContain('База недоступна');

    (host.querySelector('[data-testid="tools-retry"]') as HTMLButtonElement).click();
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [], page: 1, pageSize: 200, total: 0 });
    await render(fixture);
    expect(host.querySelector('[data-testid="tools-error"]')).toBeNull();
  });

  describe('палітра: смуга й розгорнутий вигляд (F3-89)', () => {
    beforeEach(() => localStorage.removeItem('openbp.editor.palette'));

    it('у сценарії з вузлами палітра згорнута в смугу з «+ Додати крок»; «Розгорнути» показує палітру й запам’ятовує вибір', async () => {
      const { fixture, host } = await loaded();
      expect(host.querySelector('[data-testid="palette-rail"]')).not.toBeNull();
      expect(host.querySelector('app-palette')).toBeNull();

      click(host, '[data-testid="palette-expand"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="palette-rail"]')).toBeNull();
      expect(host.querySelector('app-palette')).not.toBeNull();
      expect(localStorage.getItem('openbp.editor.palette')).toBe('1');

      click(host, '[data-testid="palette-collapse"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="palette-rail"]')).not.toBeNull();
      expect(localStorage.getItem('openbp.editor.palette')).toBe('0');
    });

    it('порожній сценарій відкривається з розгорнутою палітрою, і вона не згортається, коли додано перший вузол', async () => {
      const { fixture, host } = await loaded({ ...workflow, definition: emptyWorkflowDefinition('Порожній') });
      expect(host.querySelector('app-palette')).not.toBeNull();
      (host.querySelector('[data-item="core.manualTrigger"]') as HTMLButtonElement).click();
      await render(fixture);
      expect(canvasNodes(fixture)).toHaveLength(1);
      expect(host.querySelector('app-palette')).not.toBeNull();
    });

    it('збережений вибір «згорнуто» діє і в порожньому сценарії', async () => {
      localStorage.setItem('openbp.editor.palette', '0');
      const { host } = await loaded({ ...workflow, definition: emptyWorkflowDefinition('Порожній') });
      expect(host.querySelector('[data-testid="palette-rail"]')).not.toBeNull();
    });

    it('«+ Додати крок» зі смуги відкриває спливаючу палітру, вибір додає вузол', async () => {
      const { fixture, host } = await loaded();
      click(host, '[data-testid="rail-add"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="quick-add"]')).not.toBeNull();
      const before = canvasNodes(fixture).length;
      (host.querySelector('[data-testid="quick-add"] [data-item]') as HTMLButtonElement).click();
      await render(fixture);
      expect(canvasNodes(fixture)).toHaveLength(before + 1);
    });
  });

  describe('керування канвасом (F3-90)', () => {
    const press = (init: KeyboardEventInit) => document.body.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, composed: true, ...init }));

    it('клавіші 1, 0, +, − масштабують канвас; у полі введення й з Ctrl — ні', async () => {
      const { fixture, host } = await loaded();
      const zoom = vi.spyOn(canvasComponent(fixture), 'zoom');
      press({ key: '1' });
      press({ key: '0' });
      press({ key: '+' });
      press({ key: '=' });
      press({ key: '-' });
      expect(zoom.mock.calls.map((c) => c[0])).toEqual(['fit', 'reset', 'in', 'in', 'out']);

      press({ key: '1', ctrlKey: true });
      const search = host.querySelector<HTMLInputElement>('[data-testid="palette-search"]')!;
      search.dispatchEvent(new KeyboardEvent('keydown', { key: '1', bubbles: true, composed: true }));
      expect(zoom).toHaveBeenCalledTimes(5);
    });

    it('«?» і пункт «Гарячі клавіші» в меню «⋯» відкривають довідку; у полі введення «?» — звичайний символ', async () => {
      const { fixture, host } = await loaded();
      const search = host.querySelector<HTMLInputElement>('[data-testid="palette-search"]')!;
      search.dispatchEvent(new KeyboardEvent('keydown', { key: '?', bubbles: true, composed: true }));
      await render(fixture);
      expect(document.querySelector('[data-testid="shortcuts"]')).toBeNull();

      press({ key: '?' });
      await render(fixture);
      expect(document.querySelector('[data-testid="shortcuts"]')).not.toBeNull();
      (document.querySelector('[data-testid="shortcuts-close"]') as HTMLButtonElement).click();
      await render(fixture);

      click(host, '[data-testid="toolbar-more"]');
      await render(fixture);
      click(host, '[data-testid="shortcuts"]');
      await render(fixture);
      expect(document.querySelector('[data-testid="shortcuts"] [data-group="canvas"]')).not.toBeNull();
    });
  });

  describe('шапка панелі вузла (F3-88)', () => {
    it('назва, людська назва типу в підказці, головна дія, меню «⋯» і «×»', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      const header = host.querySelector('[data-testid="node-panel-header"]')!;
      expect(header.querySelector('[data-testid="node-panel-name"]')?.textContent).toContain('Поля');
      expect(header.querySelector('[data-testid="node-panel-type"]')?.getAttribute('title')).toBe('core.set');
      expect(header.querySelector('[data-testid="run-node"]')?.textContent).toContain('Тестувати крок');
      expect(host.querySelector('[data-testid="node-menu"]')).toBeNull();
      for (const id of ['run-until', 'pin-manual', 'assistant-explain', 'assistant-expression']) expect(await menuItem(fixture, host, id)).not.toBeNull();
      expect(await menuItem(fixture, host, 'assistant-fix')).toBeNull();
    });

    it('клік по назві в шапці — inline-перейменування, Enter зберігає, Esc скасовує без закриття панелі', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      click(host, 'button[data-testid="node-panel-name"]');
      await render(fixture);
      const input = host.querySelector<HTMLInputElement>('[data-testid="node-panel-name-input"]')!;
      input.value = 'Інша назва';
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      await render(fixture);
      expect(host.querySelector('[data-testid="node-panel-name-input"]')).toBeNull();
      expect(host.querySelector('[data-testid="editor-panel"]')).not.toBeNull();
      expect(canvasComponent(fixture).definition().nodes.find((n) => n.id === 'n_set')?.name).toBe('Поля');

      click(host, 'button[data-testid="node-panel-name"]');
      await render(fixture);
      const again = host.querySelector<HTMLInputElement>('[data-testid="node-panel-name-input"]')!;
      again.value = 'Інша назва';
      again.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      await render(fixture);
      expect(canvasComponent(fixture).definition().nodes.find((n) => n.id === 'n_set')?.name).toBe('Інша назва');
    });

    it('«×» і Esc поза полем закривають панель і знімають виділення; Esc у полі лише знімає з нього фокус', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      click(host, '[data-testid="panel-close"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="editor-panel"]')).toBeNull();
      expect(canvasComponent(fixture).selection()).toEqual({ nodes: [], edges: [] });

      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      const field = host.querySelector<HTMLInputElement>('[data-testid="panel-params"] input:not([type="hidden"])')!;
      field.focus();
      field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
      await render(fixture);
      expect(document.activeElement).not.toBe(field);
      expect(host.querySelector('[data-testid="editor-panel"]')).not.toBeNull();

      key({ key: 'Escape' });
      await render(fixture);
      expect(host.querySelector('[data-testid="editor-panel"]')).toBeNull();
    });

    it('виділений вузол підсувається з-під панелі: канвас просять показати його', async () => {
      const { fixture } = await loaded();
      const reveal = vi.spyOn(canvasComponent(fixture), 'revealNode');
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      expect(reveal).toHaveBeenCalledWith('n_set');
    });
  });

  describe('тестовий запуск (F2-20)', () => {
    function step(nodeId: string, id: string, status: ExecutionStep['status'] = 'success'): ExecutionStep {
      return {
        id,
        nodeId,
        runIndex: 0,
        status,
        input: [[{ json: { from: 'input' } }]],
        output: [[{ json: { from: nodeId } }]],
        error: status === 'error' ? { message: 'Вузол упав' } : null,
        startedAt: '',
        finishedAt: '',
      };
    }

    function runStatuses(fixture: ReturnType<typeof setup>['fixture']) {
      return Object.fromEntries(canvasComponent(fixture).runStatuses());
    }

    beforeEach(() => (FakeEventSource.last = null));

    it('«Виконати» спершу зберігає зміни, потім запускає; SSE зафарбовує вузли, у панелі — input/output вузла', async () => {
      const { fixture, http, host } = await loaded();
      click(host, '[data-item="core.set"]');
      await render(fixture);

      click(host, '[data-testid="run"]');
      await render(fixture);
      // Відкладена зміна йде одразу, запуск — лише після збереження.
      const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
      http.expectNone({ method: 'POST' });
      put.flush(saved(4, put.request.body as WorkflowDefinition));
      await render(fixture);

      const start = http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' });
      start.flush({ id: 'e1', workflowId: 'w1', versionId: 'v4', mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null });
      await render(fixture);
      expect(button(host, 'run').disabled).toBe(true);
      expect(host.querySelector('[data-testid="run-status"]')?.getAttribute('data-state')).toBe('queued');

      const source = FakeEventSource.last!;
      expect(source.url).toBe('/api/v1/executions/e1/events');
      source.emit('step', step('n_hook', 's1'));
      source.emit('step', step('n_set', 's2', 'error'));
      await render(fixture);
      expect(runStatuses(fixture)).toEqual({ n_hook: 'success', n_set: 'error' });
      expect(host.querySelector('[data-testid="run-status"]')?.getAttribute('data-state')).toBe('running');

      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      const run = host.querySelector('[data-testid="node-run"]');
      expect(run?.getAttribute('data-status')).toBe('error');
      expect(run?.textContent).toContain('Вузол упав');
      // Вхід і вихід — схемою: поле і приклад значення.
      expect(host.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('"input"');
      expect(run?.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('"n_set"');
      // F3-57: перший рядок помилки — на картці; «Показати вхідні дані» на вузькій панелі відкриває вкладку входу.
      expect(Object.fromEntries(canvasComponent(fixture).runErrors())).toEqual({ n_set: 'Вузол упав' });
      click(host, '[data-testid="node-run-error-input"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="panel-tab-input"]')?.getAttribute('aria-selected')).toBe('true');

      source.emit('status', { executionId: 'e1', state: 'error', error: 'Вузол упав' });
      await render(fixture);
      expect(host.querySelector('[data-testid="run-status"]')?.textContent).toContain('Вузол упав');
      expect(button(host, 'run').disabled).toBe(false);
      expect(source.closed).toBe(true);
    });

    it('без змін запускає одразу; output запуску потрапляє в підказки виразів', async () => {
      const { fixture, http, host } = await loaded();
      click(host, '[data-testid="run"]');
      await render(fixture);
      http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).flush({ id: 'e1', workflowId: 'w1', versionId: 'v3', mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null });
      await render(fixture);
      FakeEventSource.last!.emit('step', step('n_hook', 's1'));
      FakeEventSource.last!.emit('status', { executionId: 'e1', state: 'success', error: null });
      await render(fixture);
      expect(host.querySelector('[data-testid="run-status"]')?.getAttribute('data-state')).toBe('success');

      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      const panel = fixture.debugElement.query((d) => d.name === 'app-node-panel').componentInstance as { expressionContext: () => { input?: unknown } };
      expect(panel.expressionContext().input).toEqual({ from: 'n_hook' });
    });

    it('сценарій не зберігся (422) — запуск не починається, показано причину', async () => {
      const { fixture, http, host } = await loaded();
      click(host, '[data-item="core.set"]');
      await render(fixture);
      click(host, '[data-testid="run"]');
      await render(fixture);
      http
        .expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' })
        .flush({ title: 'Invalid', errors: [{ code: 'GRAPH_CYCLE', message: 'Граф містить цикл' }] }, { status: 422, statusText: 'Unprocessable Entity' });
      await render(fixture);

      http.expectNone({ method: 'POST' });
      expect(host.querySelector('[data-testid="run-error"]')?.textContent).toContain('не збережено');
      expect(button(host, 'run').disabled).toBe(false);
    });

    it('помилка запуску з Api — повідомлення', async () => {
      const { fixture, http, host } = await loaded();
      click(host, '[data-testid="run"]');
      await render(fixture);
      http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).flush({ title: 'Forbidden', detail: 'Недостатньо прав' }, { status: 403, statusText: 'Forbidden' });
      await render(fixture);
      expect(host.querySelector('[data-testid="run-error"]')?.textContent).toContain('Недостатньо прав');
    });
  });

  describe('вузол: виконати один, закріпити output (F2-21)', () => {
    const queued = (id: string) => ({ id, workflowId: 'w1', versionId: 'v3', mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null });
    const step = (nodeId: string, id: string): ExecutionStep => ({
      id,
      nodeId,
      runIndex: 0,
      status: 'success',
      input: [],
      output: [[{ json: { from: nodeId, id } }]],
      error: null,
      startedAt: '',
      finishedAt: '',
    });

    beforeEach(() => (FakeEventSource.last = null));

    /** Повний запуск, у якому обидва вузли успішні. */
    async function ran() {
      const ctx = await loaded();
      click(ctx.host, '[data-testid="run"]');
      await render(ctx.fixture);
      ctx.http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).flush(queued('e1'));
      await render(ctx.fixture);
      FakeEventSource.last!.emit('step', step('n_hook', 's1'));
      FakeEventSource.last!.emit('step', step('n_set', 's2'));
      FakeEventSource.last!.emit('status', { executionId: 'e1', state: 'success', error: null });
      await render(ctx.fixture);
      return ctx;
    }

    it('«Виконати вузол»: лише вузол і попередники, виходи попередників з останнього запуску — у runData', async () => {
      const { fixture, http, host } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      click(host, '[data-testid="run-node"]');
      await render(fixture);

      const start = http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' });
      expect(start.request.body).toEqual({ destinationNodeId: 'n_set', runData: { n_hook: [[{ json: { from: 'n_hook', id: 's1' } }]] } });
      start.flush(queued('e2'));
      await render(fixture);
      // Кроки попереднього запуску лишаються, поки новий їх не замінить.
      expect(Object.fromEntries(canvasComponent(fixture).runStatuses())).toEqual({ n_hook: 'success', n_set: 'success' });
      FakeEventSource.last!.emit('step', step('n_set', 's4'));
      await render(fixture);
      expect(host.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('"s4"');
    });

    it('«Закріпити output» пише вихід 0 у pinData (бейдж на канвасі, зберігається); «Відкріпити» прибирає', async () => {
      const { fixture, http, host } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      expect(await menuItem(fixture, host, 'unpin-output')).toBeNull();
      await menuClick(fixture, host, 'pin-output');
      await render(fixture);
      expect(Object.keys(canvasComponent(fixture).definition().pinData)).toEqual(['n_set']);
      expect(await menuItem(fixture, host, 'pin-output')).toBeNull();

      // «Виконати» одразу зберігає відкладену зміну — у тілі вже є pinData.
      click(host, '[data-testid="run"]');
      await render(fixture);
      const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
      const body = put.request.body as WorkflowDefinition;
      expect(body.pinData).toEqual({ n_set: [{ json: { from: 'n_set', id: 's2' } }] });
      put.flush(saved(4, body));
      await render(fixture);
      http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).flush(queued('e2'));
      // Закріплений вузол не виконується: рушій віддає pinData як його крок.
      FakeEventSource.last!.emit('step', step('n_set', 's2'));
      FakeEventSource.last!.emit('status', { executionId: 'e2', state: 'success', error: null });
      await render(fixture);

      await menuClick(fixture, host, 'unpin-output');
      await render(fixture);
      expect(canvasComponent(fixture).definition().pinData).toEqual({});
      expect(await menuItem(fixture, host, 'pin-output')).not.toBeNull();
    });

    it('«До цього вузла» (F3-51): лише вузол-призначення, попередники виконуються заново', async () => {
      const { fixture, http, host } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      await menuClick(fixture, host, 'run-until');
      await render(fixture);
      expect(http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).request.body).toEqual({ destinationNodeId: 'n_set' });
    });

    it('«Ввести дані вручну» на невиконаному вузлі, редагування JSON: некоректне не застосовується, коректне — у pinData', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      expect(host.querySelector('[data-testid="node-panel-header"] [data-testid="node-actions"]')).not.toBeNull();
      expect(host.querySelector('[data-testid="pinned-editor"]')).toBeNull();
      await menuClick(fixture, host, 'pin-manual');
      await render(fixture);
      expect(canvasComponent(fixture).definition().pinData).toEqual({ n_set: [{ json: {} }] });

      const textarea = host.querySelector<HTMLTextAreaElement>('[data-testid="pinned-json"]')!;
      textarea.value = '[{ "a": 1 }]';
      textarea.dispatchEvent(new Event('input'));
      await render(fixture);
      click(host, '[data-testid="pinned-apply"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="pinned-error"]')?.textContent).toContain('Item 0');
      expect(canvasComponent(fixture).definition().pinData).toEqual({ n_set: [{ json: {} }] });

      textarea.value = '[{ "json": { "name": "Олена" } }, { "json": {} }]';
      textarea.dispatchEvent(new Event('input'));
      await render(fixture);
      click(host, '[data-testid="pinned-apply"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="pinned-error"]')).toBeNull();
      expect(canvasComponent(fixture).definition().pinData).toEqual({ n_set: [{ json: { name: 'Олена' } }, { json: {} }] });

      // Undo повертає попередні items і в редактор.
      click(host, '[data-testid="undo"]');
      await render(fixture);
      expect(host.querySelector<HTMLTextAreaElement>('[data-testid="pinned-json"]')!.value).toBe('[\n  {\n    "json": {}\n  }\n]');
    });

    it('клавіша P: успішний вузол — закріпити output, закріплений — відкріпити', async () => {
      const { fixture } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      key({ key: 'p' });
      await render(fixture);
      expect(canvasComponent(fixture).definition().pinData).toEqual({ n_set: [{ json: { from: 'n_set', id: 's2' } }] });
      key({ key: 'p' });
      await render(fixture);
      expect(canvasComponent(fixture).definition().pinData).toEqual({});
    });
  });

  describe('панель «Вхід | Параметри | Вихід» (F3-52)', () => {
    /** `core.set` з текстовим параметром `value` — ціль вставки виразів. */
    const descriptors = SAMPLE_DESCRIPTORS.map((d) =>
      d.type === 'core.set' ? { ...d, parametersSchema: { type: 'object', properties: { value: { type: 'string', title: 'Значення' } } } } : d,
    );
    const pinned: WorkflowDetail = {
      ...workflow,
      definition: { ...workflow.definition, pinData: { n_hook: [{ json: { body: { Клієнт: 'Петренко', id: 7 } } }, { json: { body: { id: 'x' } } }] } },
    };

    beforeEach(() => localStorage.clear());

    async function opened(detail = pinned) {
      const ctx = setup();
      ctx.http.expectOne('/api/v1/workflows/w1').flush(detail);
      ctx.http.expectOne('/api/v1/nodes').flush(descriptors);
      flushConnectors(ctx.http);
      await render(ctx.fixture);
      canvasComponent(ctx.fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(ctx.fixture);
      return ctx;
    }

    function field(host: HTMLElement, path: string): HTMLButtonElement {
      return host.querySelector(`[data-testid="schema-field"][data-path='${path}']`) as HTMLButtonElement;
    }

    it('вузька панель — вкладки; вхід — схема з pinData попередника з типами й прикладами, лічильник items', async () => {
      const { fixture, host } = await opened();
      expect(host.querySelector('[data-testid="panel-tab-params"]')?.getAttribute('aria-selected')).toBe('true');
      expect((host.querySelector('[data-testid="panel-input"]') as HTMLElement).hidden).toBe(true);

      click(host, '[data-testid="panel-tab-input"]');
      await render(fixture);
      expect((host.querySelector('[data-testid="panel-input"]') as HTMLElement).hidden).toBe(false);
      expect(host.querySelector('[data-testid="input-port"]')?.textContent).toContain('2 items');
      expect(field(host, '.body["Клієнт"]').textContent?.trim()).toBe('Клієнт');
      // Різні типи між items — обидва.
      expect(field(host, '.body.id').parentElement?.textContent).toContain('number | string');
      expect(host.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('"Петренко"');
    });

    it('клік по полю схеми вставляє вираз у позицію курсора останнього поля з фокусом і повертає на «Параметри»', async () => {
      const { fixture, host } = await opened();
      const input = host.querySelector('[data-testid="field-value"]') as HTMLInputElement;
      input.value = 'Клієнт: ';
      input.dispatchEvent(new Event('input'));
      input.dispatchEvent(new Event('focus'));
      input.setSelectionRange(8, 8);
      click(host, '[data-testid="panel-tab-input"]');
      await render(fixture);

      field(host, '.body["Клієнт"]').click();
      await render(fixture);
      expect(input.value).toBe('Клієнт: {{ $json.body["Клієнт"] }}');
      expect(host.querySelector('[data-testid="panel-tab-params"]')?.getAttribute('aria-selected')).toBe('true');
      const def = canvasComponent(fixture).definition();
      expect(def.nodes.find((n) => n.id === 'n_set')?.parameters).toEqual({ value: 'Клієнт: {{ $json.body["Клієнт"] }}' });
    });

    it('без поля з фокусом — підказка, нічого не змінюється; «Вузол: …» дає $node["Назва"]', async () => {
      const { fixture, host } = await opened();
      click(host, '[data-testid="panel-tab-input"]');
      await render(fixture);
      field(host, '.body.id').click();
      await render(fixture);
      expect(host.querySelector('[data-testid="map-hint"]')?.textContent).toContain('Спершу клацніть у поле параметра');

      const select = host.querySelector('[data-testid="input-source"]') as HTMLSelectElement;
      select.value = 'n_hook';
      select.dispatchEvent(new Event('change'));
      await render(fixture);
      expect(field(host, '.body.id').title).toBe('{{ $node["Нова заявка"].json.body.id }}');
    });

    it('немає даних — «Виконайте попередній вузол…» і «Виконати до цього вузла»; вид даних запам’ятовується', async () => {
      const { fixture, host, http } = await opened(workflow);
      click(host, '[data-testid="panel-tab-input"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="input-empty"]')?.textContent).toContain('Виконайте попередній вузол, щоб побачити дані');
      click(host, '[data-testid="input-run-until"]');
      await render(fixture);
      const start = http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' });
      expect(start.request.body).toEqual({ destinationNodeId: 'n_set' });
      start.flush({ id: 'e1', workflowId: 'w1', versionId: 'v3', mode: 'manual', state: 'queued', startedAt: null, finishedAt: null, error: null });
      await render(fixture);
      FakeEventSource.last!.emit('step', {
        id: 's1',
        nodeId: 'n_hook',
        runIndex: 0,
        status: 'success',
        input: [],
        output: [[{ json: { a: 1 } }, { json: { a: 2 } }, { json: { a: 3 } }]],
        error: null,
        startedAt: '',
        finishedAt: '',
      } satisfies ExecutionStep);
      await render(fixture);
      // Лічильник на ребрі з виходу n_hook.
      expect(canvasComponent(fixture).itemCounts().get('n_hook:0')).toBe(3);

      click(host, '[data-testid="input-view-json"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('"a": 3');
      expect(localStorage.getItem('openbp.nodeData.view.input')).toBe('json');
    });

    describe('ширина панелі', () => {
      const innerWidth = window.innerWidth;
      beforeEach(() => Object.defineProperty(window, 'innerWidth', { value: 1800, configurable: true }));
      afterEach(() => Object.defineProperty(window, 'innerWidth', { value: innerWidth, configurable: true }));

      it('стрілки на межі змінюють і запам’ятовують ширину', async () => {
        const { fixture, host } = await opened();
        const handle = host.querySelector('[data-testid="panel-resize"]') as HTMLElement;
        handle.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true }));
        await render(fixture);
        expect(localStorage.getItem('openbp.editor.panelWidth')).toBe('520');
        expect(handle.getAttribute('aria-valuenow')).toBe('520');
      });

      it('від 1100 px — три колонки без вкладок', async () => {
        localStorage.setItem('openbp.editor.panelWidth', '1200');
        const { host } = await opened();
        expect(host.querySelector('[data-testid="panel-tab-input"]')).toBeNull();
        expect((host.querySelector('[data-testid="panel-input"]') as HTMLElement).hidden).toBe(false);
        expect((host.querySelector('[data-testid="panel-params"]') as HTMLElement).hidden).toBe(false);
        expect((host.querySelector('[data-testid="node-run"]') as HTMLElement).hidden).toBe(false);
      });
    });
  });

  describe('вбудовування (F4-13)', () => {
    it('saved: після автозбереження — { workflowId, version }', async () => {
      const { fixture, http, host } = await loaded();
      const events: unknown[] = [];
      fixture.componentInstance.saved.subscribe((e) => events.push(e));
      click(host, '[data-item="core.set"]');
      await render(fixture);
      fixture.componentInstance['autosave'].flush();
      const req = http.expectOne({ method: 'PUT' });
      req.flush(saved(4, req.request.body));
      expect(events).toEqual([{ workflowId: 'w1', version: 4 }]);
    });

    it('failed: помилка завантаження і збереження — { code, message }', async () => {
      const { fixture, http } = setup();
      const events: unknown[] = [];
      fixture.componentInstance.failed.subscribe((e) => events.push(e));
      http.expectOne('/api/v1/workflows/w1').flush({ status: 404, code: 'NOT_FOUND', detail: 'Сценарій не знайдено.' }, { status: 404, statusText: 'Not Found' });
      // GET /nodes forkJoin міг уже скасувати.
      for (const r of http.match('/api/v1/nodes')) if (!r.cancelled) r.flush(SAMPLE_DESCRIPTORS);
      flushConnectors(http);
      await render(fixture);
      expect(events).toEqual([{ code: 'NOT_FOUND', message: 'Сценарій не знайдено.' }]);
    });

    it('readonly: без палітри, кнопок і гарячих клавіш; зміни з канвасу ігноруються', async () => {
      const { fixture, http, host } = await loaded();
      fixture.componentRef.setInput('readonly', true);
      await render(fixture);

      expect(host.querySelector('[data-item]')).toBeNull();
      expect(button(host, 'undo')).toBeNull();
      expect(button(host, 'run')).toBeNull();
      expect(host.querySelector('[data-testid="readonly"]')).not.toBeNull();
      expect(canvasComponent(fixture).readonly()).toBe(true);

      const canvas = canvasComponent(fixture);
      canvas.definitionChange.emit({ ...workflow.definition, nodes: [] });
      canvas.selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      key({ key: 'Delete' });
      await render(fixture);
      expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook', 'n_set']);
      expect(host.querySelector('[data-testid="node-actions"]')).toBeNull();
      expect(host.querySelector('[data-testid="node-common"]')?.hasAttribute('inert')).toBe(true);
      fixture.componentInstance['autosave'].flush();
      http.expectNone({ method: 'PUT' });
    });

    it('embedded: без посилань на сторінки SPA', async () => {
      const { fixture, host } = await loaded();
      fixture.componentRef.setInput('embedded', true);
      await render(fixture);
      click(host, '[data-testid="toolbar-more"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="executions-link"]')).toBeNull();
      expect(host.querySelector('a[href="/workflows"]')).toBeNull();
    });
  });
  describe('останній запуск (F3-76)', () => {
    const summary = { id: 'e1', workflowId: 'w1', versionId: 'v1', mode: 'manual', state: 'error', startedAt: '2026-10-27T18:10:40', finishedAt: '2026-10-27T18:10:42', error: 'boom' };
    const step = (nodeId: string, status: string, error?: string) => ({ id: `s-${nodeId}`, nodeId, runIndex: 0, status, input: [], output: [], error: error ? { message: error } : null, startedAt: '2026-10-27T18:10:40', finishedAt: '2026-10-27T18:10:41' });

    afterEach(() => localStorage.removeItem('openbp.editor.showLastRun'));

    async function loadedWithLastRun() {
      const ctx = setup();
      ctx.http.expectOne('/api/v1/workflows/w1').flush(workflow);
      ctx.http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
      flushConnectors(ctx.http);
      const list = ctx.http.expectOne((r) => r.method === 'GET' && r.url === '/api/v1/executions');
      expect(list.request.params.get('workflowId')).toBe('w1');
      expect(list.request.params.get('pageSize')).toBe('1');
      list.flush({ items: [summary], page: 1, pageSize: 1, total: 1 });
      ctx.http.expectOne('/api/v1/executions/e1').flush({ summary, steps: [step('n_hook', 'success'), step('n_set', 'error', 'Порахувати: ділення на нуль')] });
      await render(ctx.fixture);
      return ctx;
    }

    it('при відкритті статуси й помилка останнього запуску видно на вузлах, у шапці — підпис', async () => {
      const { fixture, host } = await loadedWithLastRun();
      expect(Object.fromEntries(canvasComponent(fixture).runStatuses())).toEqual({ n_hook: 'success', n_set: 'error' });
      expect(canvasComponent(fixture).runErrors().get('n_set')).toBe('Порахувати: ділення на нуль');
      expect(canvasComponent(fixture).runTimes().get('n_hook')).toBe('18:10:41');
      expect(canvasComponent(fixture).viewportKey()).toBe('w1');
      expect(host.querySelector('[data-testid="last-run"]')?.textContent).toContain('18:10, помилка');
    });

    it('після правки вузла значки зникають, підпис у шапці лишається', async () => {
      const { fixture, host } = await loadedWithLastRun();
      const def = canvasComponent(fixture).definition();
      canvasComponent(fixture).definitionChange.emit({ ...def, nodes: def.nodes.map((n) => (n.id === 'n_set' ? { ...n, name: 'Інша назва' } : n)) });
      await render(fixture);
      expect(canvasComponent(fixture).runStatuses().size).toBe(0);
      expect(canvasComponent(fixture).lastRunToggle()).toBeNull();
      expect(host.querySelector('[data-testid="last-run"]')).not.toBeNull();
    });

    it('перемикач «Останній запуск» ховає значки й запам’ятовує вибір', async () => {
      const { fixture } = await loadedWithLastRun();
      expect(canvasComponent(fixture).lastRunToggle()).toBe(true);
      canvasComponent(fixture).lastRunToggled.emit(false);
      await render(fixture);
      expect(canvasComponent(fixture).runStatuses().size).toBe(0);
      expect(canvasComponent(fixture).lastRunToggle()).toBe(false);
      expect(localStorage.getItem('openbp.editor.showLastRun')).toBe('0');
    });

    it('сценарій без запусків: ні значків, ні підпису, ні перемикача', async () => {
      const { fixture, http, host } = setup();
      http.expectOne('/api/v1/workflows/w1').flush(workflow);
      http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
      flushConnectors(http);
      http.expectOne((r) => r.method === 'GET' && r.url === '/api/v1/executions').flush({ items: [], page: 1, pageSize: 1, total: 0 });
      await render(fixture);
      expect(canvasComponent(fixture).runStatuses().size).toBe(0);
      expect(canvasComponent(fixture).lastRunToggle()).toBeNull();
      expect(host.querySelector('[data-testid="last-run"]')).toBeNull();
    });
  });

  describe('«Бізнес | Технічний» (F3-66)', () => {
    afterEach(() => {
      localStorage.removeItem('openbp.editor.view');
      history.replaceState(null, '', '/');
    });

    it('перемикач показує бізнес-схему замість канвасу і палітри та пише ?view=business', async () => {
      const { fixture, host } = await loaded();
      expect(button(host, 'view-technical').getAttribute('aria-checked')).toBe('true');
      click(host, '[data-testid="view-business"]');
      await render(fixture);
      expect(host.querySelector('app-business-view')).not.toBeNull();
      expect(host.querySelector('app-canvas')).toBeNull();
      expect(host.querySelector('app-palette')).toBeNull();
      expect(location.search).toBe('?view=business');
      expect(localStorage.getItem('openbp.editor.view')).toBe('business');
      // core.set згорнуто — на схемі лише тригер.
      const steps = [...host.querySelectorAll('[data-testid="business-step"]')].map((s) => s.getAttribute('data-step'));
      expect(steps).toEqual(['n_hook']);

      click(host, '[data-testid="view-technical"]');
      await render(fixture);
      expect(host.querySelector('app-canvas')).not.toBeNull();
      expect(location.search).toBe('');
    });

    it('?view=business відкриває бізнес-вигляд одразу', async () => {
      history.replaceState(null, '', '/?view=business');
      const { host } = await loaded();
      expect(host.querySelector('app-business-view')).not.toBeNull();
    });

    it('клік на крок — технічний вигляд з виділеним вузлом і панеллю', async () => {
      const { fixture, host } = await loaded();
      click(host, '[data-testid="view-business"]');
      await render(fixture);
      (host.querySelector('[data-step="n_hook"]') as SVGGElement).dispatchEvent(new MouseEvent('click', { bubbles: true }));
      // Редагована схема чекає, чи не буде подвійного кліку (F3-68).
      await vi.waitFor(() => expect(host.querySelector('app-canvas')).not.toBeNull());
      await render(fixture);
      expect(canvasComponent(fixture).selection().nodes).toEqual(['n_hook']);
      expect(host.querySelector('[data-testid="editor-panel"]')).not.toBeNull();
    });

    it('правка назви на схемі (F3-68) — звичайна зміна: undo, Ctrl+Z, автозбереження; вузли ті самі', async () => {
      const { fixture, http, host } = await loaded();
      const editor = () => fixture.componentInstance['definition']()!;
      click(host, '[data-testid="view-business"]');
      await render(fixture);
      host.querySelector('[data-step="n_hook"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      await render(fixture);
      const input = host.querySelector('[data-testid="business-inline"]') as HTMLInputElement;
      input.value = 'Надійшла заявка';
      input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
      await render(fixture);
      expect(editor().nodes[0].business).toEqual({ title: 'Надійшла заявка' });
      expect(editor().nodes[1]).toBe(workflow.definition.nodes[1]);
      expect(editor().connections).toBe(workflow.definition.connections);
      expect(host.querySelector('[data-step="n_hook"]')?.textContent).toContain('Надійшла заявка');

      click(host, '[data-testid="undo"]');
      await render(fixture);
      expect(editor().nodes[0].business).toBeUndefined();
      click(host, '[data-testid="redo"]');
      await render(fixture);
      expect(editor().nodes[0].business).toEqual({ title: 'Надійшла заявка' });
      key({ key: 'z', ctrlKey: true });
      await render(fixture);
      expect(editor().nodes[0].business).toBeUndefined();
      key({ key: 'z', ctrlKey: true, shiftKey: true });
      await render(fixture);

      fixture.componentInstance['autosave'].flush();
      const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
      expect((put.request.body as WorkflowDefinition).nodes[0].business).toEqual({ title: 'Надійшла заявка' });
      put.flush(saved(4, put.request.body as WorkflowDefinition));
    });

    it('розділ «Бізнес-вигляд» у панелі вузла (F3-68)', async () => {
      const { fixture, http, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_hook'], edges: [] });
      await render(fixture);
      const section = host.querySelector('[data-testid="business-node-fields"]') as HTMLElement;
      expect(section).not.toBeNull();
      const title = section.querySelector('[data-testid="business-node-title"]') as HTMLInputElement;
      expect(title.placeholder).toBe(workflow.definition.nodes[0].name);
      title.value = 'Надійшла заявка';
      title.dispatchEvent(new Event('change'));
      await render(fixture);
      const hidden = section.querySelector('[data-testid="business-node-hidden"]') as HTMLInputElement;
      hidden.checked = true;
      hidden.dispatchEvent(new Event('change'));
      await render(fixture);
      expect(fixture.componentInstance['definition']()!.nodes[0].business).toEqual({ title: 'Надійшла заявка', hidden: true });
      expect((host.querySelector('[data-testid="business-node-hidden"]') as HTMLInputElement).checked).toBe(true);

      fixture.componentInstance['autosave'].flush();
      const put = http.expectOne({ method: 'PUT', url: '/api/v1/workflows/w1' });
      put.flush(saved(4, put.request.body as WorkflowDefinition));
    });

    it('перегляд (readonly): схема без правок', async () => {
      const { fixture, host } = await loaded();
      fixture.componentRef.setInput('readonly', true);
      click(host, '[data-testid="view-business"]');
      await render(fixture);
      expect(host.querySelector('[data-testid="business-add-lane"]')).toBeNull();
      host.querySelector('[data-step="n_hook"]')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
      await render(fixture);
      expect(host.querySelector('[data-testid="business-inline"]')).toBeNull();
    });

    it('у бізнес-вигляді Delete не видаляє виділені вузли', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      click(host, '[data-testid="view-business"]');
      await render(fixture);
      key({ key: 'Delete' });
      await render(fixture);
      click(host, '[data-testid="view-technical"]');
      await render(fixture);
      expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook', 'n_set']);
    });
  });
  it('порожній сценарій (F3-59): картка «Додати тригер» відкриває палітру тригерів; «Почати з прикладу» підставляє шаблон', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush({ ...workflow, definition: emptyWorkflowDefinition('Заявки') });
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(http);
    await render(fixture);

    expect(host.querySelector('[data-testid="empty-start"]')).not.toBeNull();
    click(host, '[data-testid="start-trigger"]');
    await render(fixture);
    expect(host.querySelector('[data-testid="empty-start"]')).toBeNull();
    const items = Array.from(host.querySelectorAll('[data-testid="quick-add"] [data-item]')).map((i) => i.getAttribute('data-item'));
    expect(items).toContain('core.webhook');
    expect(items).not.toContain('core.set');
    host.querySelector('[data-testid="quick-add-backdrop"]')?.dispatchEvent(new PointerEvent('pointerdown'));
    await render(fixture);

    click(host, '[data-testid="start-template"]');
    const template = {
      ...emptyWorkflowDefinition('Привіт, OpenBP'),
      nodes: [{ id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0] as [number, number], parameters: {} }],
    };
    http.expectOne('/api/v1/workflows/templates').flush([{ id: 'hello-manual', name: 'Привіт, OpenBP', description: 'Найпростіший сценарій', definition: template }]);
    await render(fixture);
    expect(host.querySelector('[data-template="hello-manual"]')?.textContent).toContain('Найпростіший сценарій');
    click(host, '[data-template="hello-manual"]');
    await render(fixture);

    expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_start']);
    expect(host.querySelector('[data-testid="empty-start"]')).toBeNull();
    expect(status(host)).not.toBe('saved');
    key({ key: 'z', ctrlKey: true });
    await render(fixture);
    expect(canvasNodes(fixture)).toEqual([]);
  });
  it('помічник (F3-60): «Пояснити вузол» відкриває панель замість панелі вузла; відповідь — одна правка, Ctrl+Z її скасовує', async () => {
    const { fixture, http, host } = await loaded();
    canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
    await render(fixture);
    await menuClick(fixture, host, 'assistant-explain');
    await render(fixture);

    expect(host.querySelector('[data-testid="editor-panel"]')).toBeNull();
    expect(host.querySelector('[data-testid="assistant-panel"]')).not.toBeNull();
    // Панель показує пояснення процесу (F3-72).
    http
      .expectOne('/api/v1/workflows/w1/explanation')
      .flush({ status: 'ready', version: 1, currentVersion: 1, text: 'Коротко: приймає заявки.', error: null, updatedAt: null, stale: false });
    const request = http.expectOne('/api/v1/workflows/w1/assistant');
    expect(request.request.body.message).toContain('«Поля» (n_set)');
    expect(request.request.body.definition.nodes.map((n: { id: string }) => n.id)).toEqual(['n_hook', 'n_set']);
    const next = { ...workflow.definition, nodes: [workflow.definition.nodes[0]], connections: [] };
    request.flush(
      `event: definition\ndata: ${JSON.stringify({ type: 'definition', definition: next })}\n\n` +
        `event: message\ndata: ${JSON.stringify({ type: 'message', text: 'Прибрав крок' })}\n\nevent: done\ndata: {"type":"done"}\n\n`,
    );
    await render(fixture);
    expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook']);

    key({ key: 'z', ctrlKey: true });
    await render(fixture);
    expect(canvasNodes(fixture).map((n) => n.id)).toEqual(['n_hook', 'n_set']);

    click(host, '[data-testid="toolbar-more"]');
    await render(fixture);
    click(host, '[data-testid="assistant-toggle"]');
    canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
    await render(fixture);
    expect(host.querySelector('[data-testid="assistant-panel"]')).toBeNull();
    expect(host.querySelector('[data-testid="editor-panel"]')).not.toBeNull();
  });
});
