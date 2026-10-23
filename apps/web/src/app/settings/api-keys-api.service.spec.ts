import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ApiKeyCreated, ApiKeyInfo, ApiKeysApiService, mcpClientConfig } from './api-keys-api.service';

describe('ApiKeysApiService', () => {
  let api: ApiKeysApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    api = TestBed.inject(ApiKeysApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list → GET /api/v1/api-keys', () => {
    let keys: ApiKeyInfo[] = [];
    api.list().subscribe((k) => (keys = k));
    http
      .expectOne({ method: 'GET', url: '/api/v1/api-keys' })
      .flush([{ id: 'k1', name: 'Claude', createdBy: 'u1', lastUsedAt: null }]);
    expect(keys.map((k) => k.name)).toEqual(['Claude']);
  });

  it('create → POST /api/v1/api-keys з назвою, відповідь містить ключ', () => {
    let created: ApiKeyCreated | null = null;
    api.create('Claude').subscribe((c) => (created = c));
    const req = http.expectOne({ method: 'POST', url: '/api/v1/api-keys' });
    expect(req.request.body).toEqual({ name: 'Claude' });
    req.flush({ id: 'k1', name: 'Claude', key: 'obp_secret' });
    expect(created!.key).toBe('obp_secret');
  });

  it('revoke → DELETE /api/v1/api-keys/{id}', () => {
    let done = false;
    api.revoke('k 1').subscribe(() => (done = true));
    http.expectOne({ method: 'DELETE', url: '/api/v1/api-keys/k%201' }).flush(null, { status: 204, statusText: 'No Content' });
    expect(done).toBe(true);
  });

  it('mcpClientConfig — JSON з URL /mcp і заголовком Authorization', () => {
    const config = JSON.parse(mcpClientConfig('https://openbp.example/mcp', 'obp_x'));
    expect(config).toEqual({
      mcpServers: { openbp: { type: 'http', url: 'https://openbp.example/mcp', headers: { Authorization: 'Bearer obp_x' } } },
    });
  });
});
