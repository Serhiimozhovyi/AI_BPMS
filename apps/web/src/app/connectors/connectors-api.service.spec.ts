import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConnectorDetail, ConnectorsApiService, apiErrorMessage } from './connectors-api.service';

const detail: ConnectorDetail = {
  summary: { id: 'c1', name: '1С', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: '2026-10-23T10:00:00Z' },
  config: { url: 'https://mcp.example/mcp' },
  credentialId: null,
  tools: [{ name: 'create_order', inputSchema: { type: 'object' } }],
};

describe('ConnectorsApiService', () => {
  let api: ConnectorsApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ConnectorsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list → GET /api/v1/connectors з пагінацією', () => {
    let total = -1;
    api.list().subscribe((p) => (total = p.total));
    const req = http.expectOne((r) => r.url === '/api/v1/connectors');
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('200');
    req.flush({ items: [detail.summary], page: 1, pageSize: 200, total: 1 });
    expect(total).toBe(1);
  });

  it('get → GET /api/v1/connectors/{id}', () => {
    let name = '';
    api.get('c1').subscribe((d) => (name = d.summary.name));
    http.expectOne({ method: 'GET', url: '/api/v1/connectors/c1' }).flush(detail);
    expect(name).toBe('1С');
  });

  it('create → POST /api/v1/connectors з тілом ConnectorInput', () => {
    api.create({ name: 'fs', transport: 'stdio', config: { command: 'npx' } }).subscribe();
    const req = http.expectOne({ method: 'POST', url: '/api/v1/connectors' });
    expect(req.request.body).toEqual({ name: 'fs', transport: 'stdio', config: { command: 'npx' } });
    req.flush(detail);
  });

  it('refreshTools → POST /api/v1/connectors/{id}/refresh-tools', () => {
    api.refreshTools('c1').subscribe();
    http.expectOne({ method: 'POST', url: '/api/v1/connectors/c1/refresh-tools' }).flush(detail);
  });

  it('callTool → POST …/tools/{tool}/call { arguments }, імʼя tool кодується', () => {
    let isError: boolean | undefined;
    api.callTool('c1', 'a/b', { x: 1 }).subscribe((r) => (isError = r.isError));
    const req = http.expectOne({ method: 'POST', url: '/api/v1/connectors/c1/tools/a%2Fb/call' });
    expect(req.request.body).toEqual({ arguments: { x: 1 } });
    req.flush({ isError: false, structuredContent: null, content: [], elapsed: '00:00:00.1' });
    expect(isError).toBe(false);
  });
});

describe('apiErrorMessage', () => {
  it('бере detail з ProblemDetails', () => {
    const e = new HttpErrorResponse({ status: 502, error: { detail: 'Сервер не відповідає', code: 'CONNECTOR_UNAVAILABLE' } });
    expect(apiErrorMessage(e)).toBe('Сервер не відповідає');
  });

  it('без тіла — повідомлення за статусом', () => {
    expect(apiErrorMessage(new HttpErrorResponse({ status: 403 }))).toContain('прав');
    expect(apiErrorMessage(new HttpErrorResponse({ status: 0 }))).toContain('недоступний');
    expect(apiErrorMessage(new HttpErrorResponse({ status: 500 }))).toContain('500');
  });

  it('невідома помилка', () => {
    expect(apiErrorMessage(new Error('x'))).toBe('Невідома помилка.');
  });
});
