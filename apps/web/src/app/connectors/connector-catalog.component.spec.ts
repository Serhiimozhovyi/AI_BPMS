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
    credential: {
      type: 'telegram.bot',
      map: { token: 'TELEGRAM_BOT_TOKEN' },
      env: [{ name: 'TELEGRAM_BOT_TOKEN', title: 'Токен бота', required: true, secret: true }],
      headers: [],
    },
    triggers: [],
  },
  source: 'builtin',
  installed: [],
};

/** Кіт: облікові дані загального типу + налаштування з обов'язковою адресою. */
const kit: ConnectorCatalogEntry = {
  manifest: {
    id: 'crm-sample',
    name: 'Кіт CRM',
    description: 'Угоди й задачі.',
    transport: 'stdio',
    stdio: { command: 'dotnet', arguments: ['kit.dll'] },
    credential: { type: 'oauth2.clientCredentials', map: { clientSecret: 'KIT_CLIENT_SECRET' }, env: [{ name: 'KIT_CLIENT_ID', required: true, secret: false }] },
    settings: [
      { name: 'baseUrl', title: 'Адреса OData', description: 'Адреса сервісу.', placeholder: 'https://crm.example.ua/api/', required: true, env: 'Connector__BaseUrl' },
      { name: 'authorPersonId', title: 'Id автора нотаток', env: 'Connector__AuthorPersonId' },
    ],
    triggers: [],
  },
  source: 'builtin',
  installed: [],
};

/** Старий спосіб: лише ключі env (форма пар після встановлення). */
const legacy: ConnectorCatalogEntry = {
  manifest: {
    id: 'psy',
    name: 'Психологічна підтримка',
    description: 'Бот.',
    transport: 'stdio',
    stdio: { command: 'dotnet', arguments: ['psy.dll'] },
    credential: { env: [{ name: 'ConnectionStrings__PsySupport', title: 'Рядок підключення', required: true, secret: true }], headers: [] },
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
    config: { command: 'dotnet', arguments: ['x.dll'], catalog: id === 'p1' ? 'psy' : 'telegram' },
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
      {
        type: 'telegram.bot',
        displayName: 'Telegram: бот',
        description: 'Бот Telegram.',
        fields: [{ name: 'token', title: 'Токен бота', kind: 'secret', required: true, description: 'Видає @BotFather.', placeholder: '123456789:AAE…' }],
        schema: {},
      },
    ]);
    http.expectOne('/api/v1/connectors/catalog').flush([telegram, plain, kit, legacy]);
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

    expect(Array.from(host.querySelectorAll('[data-catalog]')).map((e) => e.getAttribute('data-catalog'))).toEqual(['telegram', 'fake', 'crm-sample', 'psy']);
    expect(card(host, 'telegram').textContent).toContain('Telegram Bot API');
    expect(card(host, 'telegram').classList).toContain('border-bp-primary');
    expect(card(host, 'fake').textContent).toContain('зовнішній');
    // У telegram два транспорти — є перемикач, у fake один.
    expect(card(host, 'telegram').querySelector('[data-testid="catalog-transport"]')).not.toBeNull();
    expect(card(host, 'fake').querySelector('[data-testid="catalog-transport"]')).toBeNull();
  });

  const type = (el: Element | null, value: string) => {
    const input = el as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  };

  it('«Встановити» для типізованих облікових даних: крок «Підключення» без імен змінних, нові облікові дані → один install', () => {
    const { fixture, http, host, installed } = setup();

    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    // Конектор ще не створено: спершу вибір облікових даних.
    http.expectOne('/api/v1/credentials/options?type=telegram.bot').flush([]);
    fixture.detectChanges();

    const panel = host.querySelector('[data-testid="catalog-connect"]') as HTMLElement;
    expect((panel.querySelector('[data-testid="connect-name"]') as HTMLInputElement).value).toBe('Telegram Bot API');
    expect(panel.querySelector('[data-testid="credential-form"]')).not.toBeNull();
    expect(panel.textContent).toContain('Токен бота');
    expect(panel.textContent).toContain('Видає @BotFather.');
    expect(panel.textContent).not.toContain('TELEGRAM_BOT_TOKEN');
    expect(panel.querySelector('[data-testid="field-token"]')?.getAttribute('placeholder')).toBe('123456789:AAE…');
    expect(panel.querySelector('[data-testid="field-token"]')?.getAttribute('type')).toBe('password');

    type(panel.querySelector('[data-testid="field-token"]'), '123:secret');
    fixture.detectChanges();
    (panel.querySelector('[data-testid="credential-save"]') as HTMLButtonElement).click();
    const created = http.expectOne({ method: 'POST', url: '/api/v1/credentials' });
    expect(created.request.body).toEqual({ name: 'Telegram Bot API', type: 'telegram.bot', data: { token: '123:secret' } });
    created.flush({ id: 'cred1', name: 'Telegram Bot API', type: 'telegram.bot', fields: ['token'], createdBy: null, usedBy: { connectors: [], workflows: [] }, error: null });

    const install = http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/telegram/install' });
    expect(install.request.body).toEqual({ name: 'Telegram Bot API', transport: 'stdio', credentialId: 'cred1' });
    install.flush({ ...detail('c1', 'Telegram Bot API', 'stdio'), credentialId: 'cred1' });
    fixture.detectChanges();

    expect(installed.map((d) => d.credentialId)).toEqual(['cred1']);
    expect(host.querySelector('[data-testid="catalog-connect"]')).toBeNull();
    expect(card(host, 'telegram').querySelector('[data-testid="catalog-installed"]')?.textContent).toContain('Telegram Bot API');
  });

  it('наявні облікові дані обираються одразу, налаштування з маніфесту: обов\'язкове блокує «Підключити», значення йдуть у settings', () => {
    const { fixture, http, host, installed } = setup();

    (card(host, 'crm-sample').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http
      .expectOne('/api/v1/credentials/options?type=oauth2.clientCredentials')
      .flush([{ id: 'svc1', name: 'Keycloak сервіс', type: 'oauth2.clientCredentials' }]);
    fixture.detectChanges();

    const panel = host.querySelector('[data-testid="catalog-connect"]') as HTMLElement;
    // Нових облікових даних форма не показується, обрані наявні.
    expect(panel.querySelector('[data-testid="credential-form"]')).toBeNull();
    expect(panel.textContent).toContain('Адреса OData');
    expect(panel.textContent).toContain('Адреса сервісу.');
    expect(panel.textContent).toContain('Id автора нотаток (необов\'язково)');
    expect(panel.textContent).not.toContain('Connector__BaseUrl');
    expect(panel.textContent).not.toContain('KIT_');
    const submit = panel.querySelector('[data-testid="connect-submit"]') as HTMLButtonElement;
    expect(submit.disabled).toBe(true);

    type(panel.querySelector('[data-testid="setting-baseUrl"]'), ' https://crm.example.ua/api/ ');
    fixture.detectChanges();
    expect(submit.disabled).toBe(false);
    submit.click();

    const install = http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/crm-sample/install' });
    expect(install.request.body).toEqual({
      name: 'Кіт CRM',
      transport: 'stdio',
      credentialId: 'svc1',
      settings: { baseUrl: 'https://crm.example.ua/api/' },
    });
    install.flush({ ...detail('k1', 'Кіт CRM', 'stdio'), credentialId: 'svc1' });
    expect(installed.map((d) => d.summary.id)).toEqual(['k1']);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="catalog-connect"]')).toBeNull();
  });

  it('помилка встановлення в кроці — текст у кроці, вибір не губиться; «Скасувати» закриває крок', () => {
    const { fixture, http, host } = setup();

    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/credentials/options?type=telegram.bot').flush([{ id: 'old', name: 'Мій бот', type: 'telegram.bot' }]);
    fixture.detectChanges();
    (host.querySelector('[data-testid="connect-submit"]') as HTMLButtonElement).click();
    http
      .expectOne('/api/v1/connectors/catalog/telegram/install')
      .flush({ detail: 'Конектор «Telegram Bot API» очікує облікові дані іншого типу.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(host.querySelector('[data-testid="connect-error"]')?.textContent).toContain('очікує облікові дані');
    expect(fixture.componentInstance.credentialChoice()).toBe('old');
    (host.querySelector('[data-testid="connect-cancel"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="catalog-connect"]')).toBeNull();
  });

  it('старі ключі env: install одразу, форма пар облікових даних, PUT з credentialId → installed', () => {
    const { fixture, http, host, installed } = setup();

    (card(host, 'psy').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    const install = http.expectOne({ method: 'POST', url: '/api/v1/connectors/catalog/psy/install' });
    expect(install.request.body).toEqual({ transport: 'stdio' });
    install.flush(detail('p1', 'Психологічна підтримка', 'stdio'));
    fixture.detectChanges();

    const panel = host.querySelector('[data-testid="catalog-credential"]') as HTMLElement;
    expect(panel.querySelector('[data-key="ConnectionStrings__PsySupport"]')?.textContent).toContain('Рядок підключення');
    expect(installed).toEqual([]);

    fixture.componentInstance.attach(fixture.componentInstance.pending()!, {
      id: 'cred1',
      name: 'Психологічна підтримка',
      type: 'mcp.stdio',
      fields: ['env.ConnectionStrings__PsySupport'],
      createdBy: null,
      usedBy: { connectors: [], workflows: [] },
      error: null,
    });
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/connectors/p1' });
    expect(put.request.body).toEqual({
      name: 'Психологічна підтримка',
      transport: 'stdio',
      config: { command: 'dotnet', arguments: ['x.dll'], catalog: 'psy' },
      credentialId: 'cred1',
      projectId: null,
    });
    put.flush({ ...detail('p1', 'Психологічна підтримка', 'stdio'), credentialId: 'cred1' });
    fixture.detectChanges();

    expect(installed.map((d) => d.credentialId)).toEqual(['cred1']);
    expect(host.querySelector('[data-testid="catalog-credential"]')).toBeNull();
  });

  it('обраний транспорт іде в крок; запис без облікових даних і налаштувань — одразу installed; «Пізніше» пропускає старі ключі', () => {
    const { fixture, http, host, installed } = setup();

    (card(host, 'fake').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    const direct = http.expectOne('/api/v1/connectors/catalog/fake/install');
    expect(direct.request.body).toEqual({ transport: 'http' });
    direct.flush(detail('c2', 'Fake', 'http'));
    expect(installed.map((d) => d.summary.id)).toEqual(['c2']);

    (card(host, 'psy').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/connectors/catalog/psy/install').flush(detail('c3', 'Психологічна підтримка', 'stdio'));
    fixture.detectChanges();
    (host.querySelector('[data-testid="catalog-skip"]') as HTMLButtonElement).click();
    expect(installed.map((d) => d.summary.id)).toEqual(['c2', 'c3']);

    // У telegram для http немає налаштувань, але є загальний тип: крок «Підключення» з транспортом http.
    fixture.componentInstance.chooseTransport(telegram, 'http');
    (card(host, 'telegram').querySelector('[data-testid="catalog-install"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/credentials/options?type=telegram.bot').flush([{ id: 'old', name: 'Мій бот', type: 'telegram.bot' }]);
    fixture.detectChanges();
    (host.querySelector('[data-testid="connect-submit"]') as HTMLButtonElement).click();
    expect(http.expectOne('/api/v1/connectors/catalog/telegram/install').request.body).toEqual({
      name: 'Telegram Bot API',
      transport: 'http',
      credentialId: 'old',
    });
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
