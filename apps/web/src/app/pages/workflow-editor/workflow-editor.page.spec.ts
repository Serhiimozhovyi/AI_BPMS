import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CanvasComponent } from '../../canvas/canvas.component';
import { SAMPLE_DESCRIPTORS } from '../../canvas/sample-workflows';
import { ConnectorDetail, ConnectorSummary } from '../../connectors/connectors-api.service';
import { AUTOSAVE_DEBOUNCE_MS } from '../../canvas/autosave';
import { WorkflowDefinition, WorkflowDetail, emptyWorkflowDefinition } from '../../workflows/workflows-api.service';
import { WorkflowEditorPage } from './workflow-editor.page';

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

describe('WorkflowEditorPage', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ id: 'w1' })) } },
      ],
    });
    const fixture = TestBed.createComponent(WorkflowEditorPage);
    fixture.detectChanges();
    return { fixture, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
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
});
