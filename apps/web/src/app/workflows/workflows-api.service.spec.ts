import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowDetail, WorkflowsApiService, emptyWorkflowDefinition, toSummary } from './workflows-api.service';

const detail: WorkflowDetail = {
  id: 'w1',
  name: 'Заявки',
  active: false,
  version: 1,
  versionId: 'v1',
  definition: emptyWorkflowDefinition('Заявки'),
  updatedAt: '2026-10-23T10:00:00Z',
};

describe('WorkflowsApiService', () => {
  let api: WorkflowsApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(WorkflowsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list → GET /api/v1/workflows з пагінацією, без порожніх фільтрів', () => {
    let total = -1;
    api.list().subscribe((p) => (total = p.total));
    const req = http.expectOne((r) => r.url === '/api/v1/workflows');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('200');
    expect(req.request.params.has('search')).toBe(false);
    expect(req.request.params.has('active')).toBe(false);
    req.flush({ items: [toSummary(detail)], page: 1, pageSize: 200, total: 1 });
    expect(total).toBe(1);
  });

  it('list передає search і active', () => {
    api.list({ search: '  заяв ', active: false }).subscribe();
    const req = http.expectOne((r) => r.url === '/api/v1/workflows');
    expect(req.request.params.get('search')).toBe('заяв');
    expect(req.request.params.get('active')).toBe('false');
    req.flush({ items: [], page: 1, pageSize: 200, total: 0 });
  });

  it('create → POST /api/v1/workflows з порожнім визначенням formatVersion 1', () => {
    let id = '';
    api.create('Заявки').subscribe((d) => (id = d.id));
    const req = http.expectOne({ method: 'POST', url: '/api/v1/workflows' });
    expect(req.request.body).toEqual({
      formatVersion: 1,
      name: 'Заявки',
      settings: { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: true },
      nodes: [],
      connections: [],
      pinData: {},
    });
    req.flush(detail, { status: 201, statusText: 'Created' });
    expect(id).toBe('w1');
  });

  it('get → GET /api/v1/workflows/{id}', () => {
    let version = 0;
    api.get('w 1').subscribe((d) => (version = d.version));
    http.expectOne({ method: 'GET', url: '/api/v1/workflows/w%201' }).flush(detail);
    expect(version).toBe(detail.version);
  });

  it('rename → PATCH /api/v1/workflows/{id} { name }', () => {
    api.rename('w1', 'Нова').subscribe();
    const req = http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w1' });
    expect(req.request.body).toEqual({ name: 'Нова' });
    req.flush({ ...detail, name: 'Нова' });
  });

  it('activate / deactivate → POST …/activate і …/deactivate', () => {
    api.activate('w1').subscribe();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/activate' }).flush({ ...detail, active: true });
    api.deactivate('w1').subscribe();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/deactivate' }).flush(detail);
  });

  it('delete → DELETE /api/v1/workflows/{id}, id кодується', () => {
    let done = false;
    api.delete('a/b').subscribe({ complete: () => (done = true) });
    http.expectOne({ method: 'DELETE', url: '/api/v1/workflows/a%2Fb' }).flush(null, { status: 204, statusText: 'No Content' });
    expect(done).toBe(true);
  });

  it('toSummary бере поля рядка з деталей', () => {
    expect(toSummary(detail)).toEqual({
      id: 'w1',
      name: 'Заявки',
      active: false,
      version: 1,
      updatedAt: '2026-10-23T10:00:00Z',
      groupId: null,
      publishedVersion: null,
    });
    expect(toSummary({ ...detail, groupId: 'g1' }).groupId).toBe('g1');
  });

  it('групи (F3-64): CRUD, порядок, перенесення і створення в групі', () => {
    api.listGroups().subscribe();
    http.expectOne({ method: 'GET', url: '/api/v1/workflow-groups' }).flush([]);
    api.createGroup('Продажі').subscribe();
    expect(http.expectOne({ method: 'POST', url: '/api/v1/workflow-groups' }).request.body).toEqual({ name: 'Продажі' });
    api.renameGroup('g 1', 'Опт').subscribe();
    expect(http.expectOne({ method: 'PATCH', url: '/api/v1/workflow-groups/g%201' }).request.body).toEqual({ name: 'Опт' });
    api.deleteGroup('g1').subscribe();
    http.expectOne({ method: 'DELETE', url: '/api/v1/workflow-groups/g1' }).flush(null);
    api.reorderGroups(['g2', 'g1']).subscribe();
    expect(http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/order' }).request.body).toEqual({ ids: ['g2', 'g1'] });
    api.move('w1', null).subscribe();
    expect(http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w1' }).request.body).toEqual({ groupId: null });
    api.create('Новий', 'g1').subscribe();
    expect(http.expectOne((r) => r.method === 'POST' && r.url === '/api/v1/workflows').request.params.get('groupId')).toBe('g1');
  });
});
