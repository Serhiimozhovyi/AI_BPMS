import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { ConnectorDetail, ConnectorSummary } from '../../connectors/connectors-api.service';
import { ConnectorsPage } from './connectors.page';

const ok: ConnectorSummary = { id: 'c1', name: '1С', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: new Date().toISOString() };
const broken: ConnectorSummary = { id: 'c2', name: 'Файли', transport: 'stdio', status: 'error', toolCount: 0, toolsCachedAt: null };

const detail: ConnectorDetail = {
  summary: ok,
  config: { url: 'https://mcp.example/mcp' },
  credentialId: null,
  tools: [{ name: 'create_order', inputSchema: { type: 'object' } }],
};

describe('ConnectorsPage', () => {
  async function setup(items: ConnectorSummary[] = [ok, broken]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(ConnectorsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items, page: 1, pageSize: 200, total: items.length });
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('показує список серверів зі статусом українською', async () => {
    const { host } = await setup();
    expect(host.querySelector('h1')?.textContent).toContain('Конектори');
    const rows = host.querySelectorAll('[data-connector]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('1С');
    expect(rows[0].querySelector('[data-testid="status"]')?.textContent).toContain('Підключено');
    expect(rows[1].querySelector('[data-testid="status"]')?.textContent).toContain('Помилка');
  });

  it('порожній список — підказка', async () => {
    const { host } = await setup([]);
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
  });

  it('порожній список: «Відкрити каталог» перемикає на каталог', async () => {
    const { fixture, host } = await setup([]);
    (host.querySelector('[data-testid="empty-action"]') as HTMLButtonElement).click();
    await render(fixture);
    expect(host.querySelector('app-connector-catalog')).not.toBeNull();
  });

  it('помилка завантаження показується', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(ConnectorsPage);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne((r) => r.url === '/api/v1/connectors')
      .flush(null, { status: 403, statusText: 'Forbidden' });
    await render(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="page-error"]')?.textContent).toContain('прав');
  });

  it('вибір сервера відкриває деталі з tools', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-connector="c1"] button')!.click();
    http.expectOne({ method: 'GET', url: '/api/v1/connectors/c1' }).flush(detail);
    await render(fixture);
    const article = host.querySelector('[data-testid="connector-detail"]')!;
    expect(article.textContent).toContain('https://mcp.example/mcp');
    expect(article.querySelector('[data-tool="create_order"]')).not.toBeNull();
  });

  it("«Перевірити з'єднання» оновлює деталі і рядок списку", async () => {
    const { fixture, http, host } = await setup();
    fixture.componentInstance.select('c1');
    http.expectOne('/api/v1/connectors/c1').flush(detail);
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="check-connection"]')!.click();
    const refreshed: ConnectorDetail = {
      ...detail,
      summary: { ...ok, toolCount: 2 },
      tools: [...detail.tools, { name: 'get_stock', inputSchema: { type: 'object' } }],
    };
    http.expectOne({ method: 'POST', url: '/api/v1/connectors/c1/refresh-tools' }).flush(refreshed);
    await render(fixture);

    expect(host.querySelector('[data-tool="get_stock"]')).not.toBeNull();
    expect(host.querySelector('[data-connector="c1"]')?.textContent).toContain('2');
  });

  it('недоступний сервер при оновленні — помилка і перечитаний стан', async () => {
    const { fixture, http, host } = await setup();
    fixture.componentInstance.select('c1');
    http.expectOne('/api/v1/connectors/c1').flush(detail);
    await render(fixture);

    fixture.componentInstance.refresh('c1');
    http
      .expectOne('/api/v1/connectors/c1/refresh-tools')
      .flush({ detail: 'MCP-сервер недоступний: таймаут', code: 'CONNECTOR_UNAVAILABLE' }, { status: 502, statusText: 'Bad Gateway' });
    http
      .expectOne((r) => r.url === '/api/v1/connectors')
      .flush({ items: [{ ...ok, status: 'error' }, broken], page: 1, pageSize: 200, total: 2 });
    http.expectOne('/api/v1/connectors/c1').flush({ ...detail, summary: { ...ok, status: 'error' } });
    await render(fixture);

    expect(host.querySelector('[data-testid="connection-state"]')?.textContent).toContain('Не вдалося підключитись: MCP-сервер недоступний: таймаут');
    expect(host.querySelector('[data-connector="c1"] [data-testid="status"]')?.textContent).toContain('Помилка');
  });

  describe("стан з'єднання (F3-100)", () => {
    it("на картці немає «tools», є стан і «Перевірити з'єднання»; свіжий кеш — без запиту", async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush(detail);
      await render(fixture);

      const article = host.querySelector('[data-testid="connector-detail"]')!;
      expect(article.textContent?.toLowerCase()).not.toContain('tools');
      expect(article.textContent).toContain("Перевірити з'єднання");
      expect(article.querySelector('[data-testid="connection-state"]')?.textContent).toMatch(/Підключено · 1 дій · перевірено (щойно|\d+ хв тому)/);
      http.expectNone('/api/v1/connectors/c1/refresh-tools');
    });

    it('кеш старший за 10 хвилин — один запит при відкритті картки', async () => {
      const { fixture, http, host } = await setup();
      const stale = { ...ok, toolsCachedAt: new Date(Date.now() - 11 * 60_000).toISOString() };
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush({ ...detail, summary: stale });
      http.expectOne({ method: 'POST', url: '/api/v1/connectors/c1/refresh-tools' }).flush({ ...detail, summary: ok });
      await render(fixture);

      expect(host.querySelector('[data-testid="connection-state"]')?.textContent).toContain('перевірено');
      http.expectNone('/api/v1/connectors/c1/refresh-tools');
    });

    it('статус «помилка» — перевірка при відкритті й причина збою в стані', async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush({ ...detail, summary: { ...ok, status: 'error' } });
      http
        .expectOne('/api/v1/connectors/c1/refresh-tools')
        .flush({ detail: 'MCP-сервер недоступний: таймаут', code: 'CONNECTOR_UNAVAILABLE' }, { status: 502, statusText: 'Bad Gateway' });
      http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [{ ...ok, status: 'error' }], page: 1, pageSize: 200, total: 1 });
      http.expectOne('/api/v1/connectors/c1').flush({ ...detail, summary: { ...ok, status: 'error' } });
      await render(fixture);

      expect(host.querySelector('[data-testid="connection-state"]')?.textContent).toContain('Не вдалося підключитись: MCP-сервер недоступний: таймаут');
    });
  });

  describe('видалення (F3-98)', () => {
    const withCredential: ConnectorDetail = { ...detail, credentialId: 'cr1' };

    it('кнопка в рядку питає підтвердження; «Ні» нічого не видаляє', async () => {
      const { fixture, http, host } = await setup();
      host.querySelector<HTMLButtonElement>('[data-connector="c1"] [data-testid="row-delete"]')!.click();
      http.expectOne({ method: 'GET', url: '/api/v1/connectors/c1' }).flush(detail);
      await render(fixture);

      expect(host.querySelector('[data-testid="delete-confirm-box"]')?.textContent).toContain('«1С»');
      host.querySelector<HTMLButtonElement>('[data-testid="delete-cancel"]')!.click();
      await render(fixture);
      expect(host.querySelector('[data-testid="delete-confirm-box"]')).toBeNull();
      http.expectNone({ method: 'DELETE' });
    });

    it('підтвердження видаляє підключення, прибирає його зі списку й закриває деталі', async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush(detail);
      await render(fixture);

      host.querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.click();
      await render(fixture);
      expect(host.querySelector('[data-testid="delete-credential"]')).toBeNull();
      host.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click();
      http.expectOne({ method: 'DELETE', url: '/api/v1/connectors/c1' }).flush(null, { status: 204, statusText: 'No Content' });
      await render(fixture);

      expect(host.querySelector('[data-connector="c1"]')).toBeNull();
      expect(host.querySelector('[data-connector="c2"]')).not.toBeNull();
      expect(host.querySelector('[data-testid="connector-detail"]')).toBeNull();
    });

    it('з прапорцем видаляє й облікові дані; якщо їх хтось використовує — підключення все одно видалене', async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush(withCredential);
      await render(fixture);

      host.querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.click();
      await render(fixture);
      expect(host.querySelector('[data-testid="delete-credential"]')).not.toBeNull();
      host.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click();
      http.expectOne({ method: 'DELETE', url: '/api/v1/connectors/c1' }).flush(null, { status: 204, statusText: 'No Content' });
      http.expectOne({ method: 'DELETE', url: '/api/v1/credentials/cr1' }).flush(
        { code: 'CREDENTIAL_IN_USE', detail: 'Облікові дані «Бот» використовують: конектор «Інший».' },
        { status: 409, statusText: 'Conflict' },
      );
      await render(fixture);

      expect(host.querySelector('[data-connector="c1"]')).toBeNull();
    });

    it('без прапорця облікові дані не чіпаються', async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush(withCredential);
      await render(fixture);

      host.querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.click();
      fixture.componentInstance.deleteCredential.set(false);
      await render(fixture);
      host.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click();
      http.expectOne({ method: 'DELETE', url: '/api/v1/connectors/c1' }).flush(null, { status: 204, statusText: 'No Content' });
      await render(fixture);

      http.expectNone({ method: 'DELETE', url: '/api/v1/credentials/cr1' });
    });

    it('409: підключення використовують активні сценарії — текст Api й порада, список лишається', async () => {
      const { fixture, http, host } = await setup();
      fixture.componentInstance.select('c1');
      http.expectOne('/api/v1/connectors/c1').flush(detail);
      await render(fixture);

      host.querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.click();
      await render(fixture);
      host.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click();
      http.expectOne({ method: 'DELETE', url: '/api/v1/connectors/c1' }).flush(
        { code: 'CONNECTOR_IN_USE', detail: 'Конектор «1С» використовують активні сценарії: «Замовлення».' },
        { status: 409, statusText: 'Conflict' },
      );
      await render(fixture);

      const error = host.querySelector('[data-testid="detail-error"]')!.textContent!;
      expect(error).toContain('«Замовлення»');
      expect(error).toContain('Деактивуйте');
      expect(host.querySelector('[data-connector="c1"]')).not.toBeNull();
    });
  });

  it('«Додати MCP-сервер» відкриває форму; створений сервер зʼявляється у списку', async () => {
    const { fixture, host } = await setup([]);
    host.querySelector<HTMLButtonElement>('[data-testid="add-button"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="connector-form"]')).not.toBeNull();

    fixture.componentInstance.onCreated(detail);
    await render(fixture);
    expect(host.querySelector('[data-testid="connector-form"]')).toBeNull();
    expect(host.querySelector('[data-connector="c1"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="connector-detail"]')).not.toBeNull();
  });

  it('?catalog=<id> відкриває вкладку «Каталог» з підсвіченим записом; встановлений конектор повертає до списку', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ catalog: 'telegram' }) } } },
      ],
    });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@openbp.test', role: 'admin' } as never);
    const fixture = TestBed.createComponent(ConnectorsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [], page: 1, pageSize: 200, total: 0 });
    http.expectOne('/api/v1/credentials/types').flush([]);
    http.expectOne('/api/v1/connectors/catalog').flush([
      { manifest: { id: 'telegram', name: 'Telegram Bot API', description: '', transport: 'http', http: { url: 'http://telegram:8080/mcp' }, triggers: [] }, source: 'builtin', installed: [] },
    ]);
    await render(fixture);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-catalog="telegram"]')?.classList).toContain('border-bp-primary');
    expect(host.querySelector('[data-testid="add-button"]')).toBeNull();

    (host.querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/connectors/catalog/telegram/install').flush({ ...detail, summary: { ...ok, id: 'c9', name: 'Telegram Bot API' } });
    await render(fixture);

    expect(host.querySelector('[data-testid="catalog-list"]')).toBeNull();
    expect(host.querySelector('[data-connector="c9"]')?.textContent).toContain('Telegram Bot API');
    expect(host.querySelector('[data-testid="connector-detail"] h2')?.textContent).toContain('Telegram Bot API');
  });
});
