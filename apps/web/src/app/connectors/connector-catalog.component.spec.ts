import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { ConnectorCatalogComponent } from './connector-catalog.component';
import { ConnectorCatalogEntry, ConnectorDetail, manifestTransports } from './connectors-api.service';

const telegram: ConnectorCatalogEntry = {
  manifest: {
    id: 'telegram',
    name: 'Telegram Bot API',
    description: 'Повідомлення бота.',
    icon: '✈️',
    transport: 'stdio',
    stdio: { command: 'dotnet', arguments: ['/opt/openbp/connectors/telegram/OpenBP.Connectors.Telegram.dll'] },
    http: { url: 'http://telegram:8080/mcp' },
    credential: { env: [{ name: 'TELEGRAM_BOT_TOKEN', title: 'Токен бота', required: true, secret: true }], headers: [] },
    triggers: [],
  },
  source: 'builtin',
  installed: [],
};

const plain: ConnectorCatalogEntry = {
  manifest: { id: 'fake', name: 'Fake', description: 'Без ключів.', transport: 'http', http: { url: 'http://fake/mcp' }, triggers: [] },
  source: 'external',
  installed: [],
};

function detail(id: string, name: string, transport: 'stdio' | 'http'): ConnectorDetail {
  return {
    summary: { id, name, transport, status: 'error', toolCount: 0, toolsCachedAt: null, projectId: null, catalogId: 'telegram' },
    config: { command: 'dotnet', arguments: ['x.dll'], catalog: 'telegram' },
    credentialId: null,
    tools: [],
  };
}

describe('ConnectorCatalogComponent', () => {
  function setup(role: 'admin' | 'viewer' = 'admin') {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@openbp.test', role } as never);
    const fixture = TestBed.createComponent(ConnectorCatalogComponent);
    fixture.componentRef.setInput('highlight', 'telegram');
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/credentials/types').flush([
      { type: 'mcp.stdio', displayName: 'stdio', description: '', fields: [{ name: 'env', title: 'Змінні', kind: 'secretMap', required: true }], schema: {} },
      { type: 'mcp.http', displayName: 'http', description: '', fields: [{ name: 'headers', title: 'Заголовки', kind: 'secretMap', required: true }], schema: {} },
    ]);
    http.expectOne('/api/v1/connectors/catalog').flush([telegram, plain]);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const installed: ConnectorDetail[] = [];
    fixture.componentInstance.installed.subscribe((d) => installed.push(d));
    return { fixture, http, host, installed };
  }

  const card = (host: HTMLElement, id: string) => host.querySelector(`[data-catalog="${id}"]`) as HTMLElement;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує записи з іконкою, джерелом і підсвіченим записом', () => {
    const { host } = setup();

    expect(Array.from(host.querySelectorAll('[data-catalog]')).map((e) => e.getAttribute('data-catalog'))).toEqual(['telegram', 'fake']);
    expect(card(host, 'telegram').textContent).toContain('Telegram Bot API');
    expect(card(host, 'telegram').classList).toContain('border-ac-kit');
    expect(card(host, 'fake').textContent).toContain('зовнішній');
    // У telegram два транспорти — є перемикач, у fake один.
    expect(card(host, 'telegram').querySelector('[data-testid="catalog-transport"]')).not.toBeNull();
    expect(card(host, 'fake').querySelector('[data-testid="catalog-transport"]')).toBeNull();
  });

  it('«Встановити»: POST install, форма облікових даних з ключами маніфесту, PUT з credentialId → installed', () => {
    const { fixture, http, host, installed } = setup();

    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    const install = http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/telegram/install' });
    expect(install.request.body).toEqual({ transport: 'stdio' });
    install.flush(detail('c1', 'Telegram Bot API', 'stdio'));
    fixture.detectChanges();

    const panel = host.querySelector('[data-testid="catalog-credential"]') as HTMLElement;
    expect(panel.querySelector('[data-key="TELEGRAM_BOT_TOKEN"]')?.textContent).toContain('Токен бота');
    expect((panel.querySelector('[data-testid="credential-name"]') as HTMLInputElement).value).toBe('Telegram Bot API');
    expect(card(host, 'telegram').querySelector('[data-testid="catalog-installed"]')?.textContent).toContain('Telegram Bot API');
    expect(installed).toEqual([]);

    fixture.componentInstance.attach(fixture.componentInstance.pending()!, {
      id: 'cred1',
      name: 'Telegram Bot API',
      type: 'mcp.stdio',
      fields: ['env.TELEGRAM_BOT_TOKEN'],
      createdBy: null,
      usedBy: { connectors: [], workflows: [] },
      error: null,
    });
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/connectors/c1' });
    expect(put.request.body).toEqual({
      name: 'Telegram Bot API',
      transport: 'stdio',
      config: { command: 'dotnet', arguments: ['x.dll'], catalog: 'telegram' },
      credentialId: 'cred1',
      projectId: null,
    });
    put.flush({ ...detail('c1', 'Telegram Bot API', 'stdio'), credentialId: 'cred1' });
    fixture.detectChanges();

    expect(installed.map((d) => d.credentialId)).toEqual(['cred1']);
    expect(host.querySelector('[data-testid="catalog-credential"]')).toBeNull();
  });

  it('обраний транспорт іде в install; запис без ключів — одразу installed; «Пізніше» пропускає облікові дані', () => {
    const { fixture, http, host, installed } = setup();

    fixture.componentInstance.chooseTransport(telegram, 'http');
    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    const install = http.expectOne('/api/v1/connectors/catalog/telegram/install');
    expect(install.request.body).toEqual({ transport: 'http' });
    // У маніфесті немає заголовків для http — облікові дані не потрібні.
    install.flush(detail('c2', 'Telegram Bot API', 'http'));
    expect(installed.map((d) => d.summary.id)).toEqual(['c2']);

    fixture.componentInstance.chooseTransport(telegram, 'stdio');
    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/connectors/catalog/telegram/install').flush(detail('c3', 'Telegram Bot API', 'stdio'));
    fixture.detectChanges();
    (host.querySelector('[data-testid="catalog-skip"]') as HTMLButtonElement).click();
    expect(installed.map((d) => d.summary.id)).toEqual(['c2', 'c3']);
  });

  it('помилка встановлення — повідомлення', () => {
    const { fixture, http, host } = setup();
    (card(host, 'fake').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http
      .expectOne('/api/v1/connectors/catalog/fake/install')
      .flush({ detail: 'Недостатньо прав.' }, { status: 403, statusText: 'Forbidden' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="catalog-error"]')?.textContent).toContain('Недостатньо прав');
  });

  it('viewer без проєктів: кнопки вимкнено', () => {
    const { host } = setup('viewer');
    expect((card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).disabled).toBe(true);
  });
});

describe('manifestTransports', () => {
  it('типовий першим, лише наявні', () => {
    expect(manifestTransports({ ...telegram.manifest, transport: 'http' })).toEqual(['http', 'stdio']);
    expect(manifestTransports(plain.manifest)).toEqual(['http']);
  });
});
