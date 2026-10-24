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

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function loaded() {
    const ctx = setup();
    ctx.http.expectOne('/api/v1/workflows/w1').flush(workflow);
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
    expect(host.querySelector('[data-section="server:c1"]')?.textContent).toContain('create_order');
    expect(host.querySelector('[data-section="server:c2"]')).toBeNull();
    expect(host.querySelector('[data-testid="dirty"]')).toBeNull();
  });

  it('посилання «Журнал виконань» веде на журнал цього сценарію', async () => {
    const { host } = await loaded();
    const link = host.querySelector<HTMLAnchorElement>('[data-testid="executions-link"]')!;
    expect(link.getAttribute('href')).toBe('/executions?workflowId=w1');
  });

  it('клік по tool-у в палітрі додає вузол mcp.tool на канвас', async () => {
    const { fixture, http, host } = setup();
    http.expectOne('/api/v1/workflows/w1').flush(workflow);
    http.expectOne('/api/v1/nodes').flush(SAMPLE_DESCRIPTORS);
    flushConnectors(http);
    await render(fixture);

    (host.querySelector('[data-item="c1/create_order"]') as HTMLButtonElement).click();
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
    expect(host.querySelector('[data-testid="saved"]')?.textContent).toContain('4');
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
      expect(run?.querySelector('[data-testid="node-run-input"]')?.textContent).toContain('"from": "input"');
      expect(run?.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('"from": "n_set"');

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
      expect(host.querySelector('[data-testid="node-run-output"]')?.textContent).toContain('"id": "s4"');
    });

    it('«Закріпити output» пише вихід 0 у pinData (бейдж на канвасі, зберігається); «Відкріпити» прибирає', async () => {
      const { fixture, http, host } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      expect(button(host, 'unpin-output')).toBeNull();
      click(host, '[data-testid="pin-output"]');
      await render(fixture);
      expect(Object.keys(canvasComponent(fixture).definition().pinData)).toEqual(['n_set']);
      expect(button(host, 'pin-output')).toBeNull();

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

      click(host, '[data-testid="unpin-output"]');
      await render(fixture);
      expect(canvasComponent(fixture).definition().pinData).toEqual({});
      expect(button(host, 'pin-output')).not.toBeNull();
    });

    it('«До цього вузла» (F3-51): лише вузол-призначення, попередники виконуються заново', async () => {
      const { fixture, http, host } = await ran();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      click(host, '[data-testid="run-until"]');
      await render(fixture);
      expect(http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/executions' }).request.body).toEqual({ destinationNodeId: 'n_set' });
    });

    it('«Ввести дані вручну» на невиконаному вузлі, редагування JSON: некоректне не застосовується, коректне — у pinData', async () => {
      const { fixture, host } = await loaded();
      canvasComponent(fixture).selectionChange.emit({ nodes: ['n_set'], edges: [] });
      await render(fixture);
      expect(host.querySelector('[data-testid="node-panel"] [data-testid="node-actions"]')).not.toBeNull();
      expect(host.querySelector('[data-testid="pinned-editor"]')).toBeNull();
      click(host, '[data-testid="pin-manual"]');
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
      expect(host.querySelector('[data-testid="executions-link"]')).toBeNull();
      expect(host.querySelector('a[href="/workflows"]')).toBeNull();
    });
  });
});
