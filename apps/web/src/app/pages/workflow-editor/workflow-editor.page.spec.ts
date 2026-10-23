import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { CanvasComponent } from '../../canvas/canvas.component';
import { SAMPLE_DESCRIPTORS } from '../../canvas/sample-workflows';
import { ConnectorDetail, ConnectorSummary } from '../../connectors/connectors-api.service';
import { WorkflowDetail, emptyWorkflowDefinition } from '../../workflows/workflows-api.service';
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
