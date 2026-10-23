import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConnectorDetail, ConnectorSummary } from '../../connectors/connectors-api.service';
import { ConnectorsPage } from './connectors.page';

const ok: ConnectorSummary = { id: 'c1', name: '1С', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: '2026-10-23T10:00:00Z' };
const broken: ConnectorSummary = { id: 'c2', name: 'Файли', transport: 'stdio', status: 'error', toolCount: 0, toolsCachedAt: null };

const detail: ConnectorDetail = {
  summary: ok,
  config: { url: 'https://mcp.example/mcp' },
  credentialId: null,
  tools: [{ name: 'create_order', inputSchema: { type: 'object' } }],
};

describe('ConnectorsPage', () => {
  async function setup(items: ConnectorSummary[] = [ok, broken]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
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

  it('помилка завантаження показується', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
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

  it('«Оновити tools» оновлює деталі і рядок списку', async () => {
    const { fixture, http, host } = await setup();
    fixture.componentInstance.select('c1');
    http.expectOne('/api/v1/connectors/c1').flush(detail);
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="refresh-button"]')!.click();
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

    expect(host.querySelector('[data-testid="detail-error"]')?.textContent).toContain('таймаут');
    expect(host.querySelector('[data-connector="c1"] [data-testid="status"]')?.textContent).toContain('Помилка');
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
});
