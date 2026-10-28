import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CredentialSummary, CredentialTypeInfo } from '../../credentials/credentials-api.service';
import { CredentialsPage } from './credentials.page';

const types: CredentialTypeInfo[] = [
  {
    type: 'anthropic',
    displayName: 'LLM: Anthropic',
    description: 'ai.llm',
    fields: [{ name: 'apiKey', title: 'API-ключ', kind: 'secret', required: true }],
    schema: {},
  },
];
const used: CredentialSummary = {
  id: 'c1',
  name: 'Anthropic прод',
  type: 'anthropic',
  fields: ['apiKey'],
  createdBy: 'u1',
  usedBy: { connectors: [], workflows: [{ id: 'w1', name: 'Звіт' }] },
  error: null,
};

describe('CredentialsPage', () => {
  async function setup(list: CredentialSummary[] = [used]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(CredentialsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/credentials/types').flush(types);
    http.expectOne({ method: 'GET', url: '/api/v1/credentials' }).flush(list);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function type(host: HTMLElement, selector: string, value: string) {
    const input = host.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('таблиця: тип, заповнені поля, використання', async () => {
    const { host } = await setup();
    const row = host.querySelector('[data-credential="c1"]')!;
    expect(row.textContent).toContain('LLM: Anthropic');
    // Заповнені поля — підписами типу, а не ключами даних.
    expect(row.textContent).toContain('API-ключ');
    expect(row.textContent).not.toContain('apiKey');
    expect(row.textContent).toContain('сценарій «Звіт»');
  });

  it('таблиця: кількість підключень, що використовують облікові дані', async () => {
    const { host } = await setup([
      { ...used, usedBy: { connectors: [{ id: 'k1', name: 'Бот 1' }, { id: 'k2', name: 'Бот 2' }], workflows: [] } },
    ]);
    expect(host.querySelector('[data-credential="c1"] [data-testid="connection-count"]')?.textContent?.trim()).toBe('2');
  });

  it('створення: єдиний тип обрано, ключ іде в POST, у таблиці значення немає', async () => {
    const { fixture, http, host } = await setup([]);
    host.querySelector<HTMLButtonElement>('[data-testid="add-button"]')!.click();
    await render(fixture);
    type(host, '[data-testid="credential-name"]', 'Новий');
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="form-error"]')?.textContent).toContain('API-ключ');
    http.expectNone('/api/v1/credentials');

    type(host, '[data-testid="field-apiKey"]', 'sk-secret');
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!.click();
    const req = http.expectOne({ method: 'POST', url: '/api/v1/credentials' });
    expect(req.request.body).toEqual({ name: 'Новий', type: 'anthropic', data: { apiKey: 'sk-secret' } });
    req.flush({ ...used, id: 'c2', name: 'Новий', usedBy: { connectors: [], workflows: [] } });
    await render(fixture);
    expect(host.querySelector('[data-credential="c2"]')).not.toBeNull();
    expect(host.textContent).not.toContain('sk-secret');
  });

  it('редагування: лише перейменування без даних', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-testid="edit"]')!.click();
    await render(fixture);
    expect(host.querySelector<HTMLInputElement>('[data-testid="field-apiKey"]')!.placeholder).toContain('заповнено');
    type(host, '[data-testid="credential-name"]', 'Anthropic тест');
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!.click();
    const req = http.expectOne({ method: 'PATCH', url: '/api/v1/credentials/c1' });
    expect(req.request.body).toEqual({ name: 'Anthropic тест' });
    req.flush({ ...used, name: 'Anthropic тест' });
    await render(fixture);
    expect(host.querySelector('[data-credential="c1"]')!.textContent).toContain('Anthropic тест');
  });

  it('видалення використовуваного: 409 — зрозуміле повідомлення, рядок лишається', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-testid="delete"]')!.click();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="confirm-delete"]')!.click();
    http
      .expectOne({ method: 'DELETE', url: '/api/v1/credentials/c1' })
      .flush(
        { code: 'CREDENTIAL_IN_USE', detail: 'Облікові дані «Anthropic прод» використовують: сценарій «Звіт».' },
        { status: 409, statusText: 'Conflict' },
      );
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('сценарій «Звіт»');
    expect(host.querySelector('[data-credential="c1"]')).not.toBeNull();
  });

  it('видалення невикористовуваного прибирає рядок', async () => {
    const { fixture, http, host } = await setup([{ ...used, usedBy: { connectors: [], workflows: [] } }]);
    host.querySelector<HTMLButtonElement>('[data-testid="delete"]')!.click();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="confirm-delete"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/credentials/c1' }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    expect(host.querySelector('[data-credential="c1"]')).toBeNull();
  });

  it('oauth2: стан і «Підключити» переходить на адресу провайдера', async () => {
    const oauth: CredentialSummary = {
      ...used,
      id: 'o1',
      name: 'Microsoft',
      type: 'oauth2',
      fields: ['clientId', 'accessToken'],
      usedBy: { connectors: [], workflows: [] },
      oauth: { status: 'connected', expiresAt: null, error: null },
    };
    const { fixture, http, host } = await setup([oauth]);
    expect(host.querySelector('[data-testid="oauth-status"]')?.getAttribute('data-status')).toBe('connected');
    expect(host.textContent).not.toContain('accessToken');
    const assign = vi.fn();
    Object.defineProperty(fixture.componentInstance as object, 'document', { value: { location: { assign } } });
    host.querySelector<HTMLButtonElement>('[data-testid="oauth-connect"]')!.click();
    http.expectOne({ method: 'POST', url: '/api/v1/credentials/o1/oauth2/start' }).flush({ authorizationUrl: 'https://login.example/authorize?x=1' });
    expect(assign).toHaveBeenCalledWith('https://login.example/authorize?x=1');
  });
});
