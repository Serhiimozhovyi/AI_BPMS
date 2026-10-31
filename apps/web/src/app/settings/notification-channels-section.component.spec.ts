import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NotificationChannel, channelTypeLabel } from './notification-channels-api.service';
import { NotificationChannelsSection, channelInput } from './notification-channels-section.component';
import { chooseOption } from '../shared/select-search/select-testing';

const telegram: NotificationChannel = {
  id: 'c1',
  name: 'Черговий',
  type: 'telegram',
  config: { connectorId: 'tg', chatId: '-100500' },
  events: ['execution.failed', 'worker.stale'],
  cooldownSec: 900,
  enabled: true,
  updatedAt: '2026-10-24T10:00:00Z',
};

describe('channelInput', () => {
  it('бере лише поля вибраного типу', () => {
    const base = {
      name: ' Пошта ',
      connectorId: 'ms',
      from: 'ops@astarta.test',
      to: 'a@astarta.test, b@astarta.test;c@astarta.test',
      chatId: 'x',
      url: 'https://hooks.test',
      credentialId: '',
      events: ['backup.failed'],
      cooldownMin: 30,
      enabled: true,
    };
    expect(channelInput({ ...base, type: 'email' })).toEqual({
      name: 'Пошта',
      type: 'email',
      config: { connectorId: 'ms', from: 'ops@astarta.test', to: ['a@astarta.test', 'b@astarta.test', 'c@astarta.test'] },
      events: ['backup.failed'],
      cooldownSec: 1800,
      enabled: true,
    });
    expect(channelInput({ ...base, type: 'webhook' }).config).toEqual({ url: 'https://hooks.test' });
    expect(channelInput({ ...base, type: 'webhook', credentialId: 'cred' }).config).toEqual({ url: 'https://hooks.test', credentialId: 'cred' });
    expect(channelInput({ ...base, type: 'telegram' }).config).toEqual({ connectorId: 'ms', chatId: 'x' });
  });
});

describe('NotificationChannelsSection', () => {
  async function setup(items: NotificationChannel[] = [telegram]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(NotificationChannelsSection);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: '/api/v1/notification-channels' }).flush(items);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function flushOptions(http: HttpTestingController) {
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({ items: [{ id: 'tg', name: 'Telegram-бот' }], page: 1, pageSize: 200, total: 1 });
    http.expectOne('/api/v1/credentials').flush([{ id: 'cred', name: 'Токен вебхука' }]);
  }

  function input(host: HTMLElement, testid: string, value: string) {
    const el = host.querySelector<HTMLInputElement>(`[data-testid="${testid}"]`)!;
    if (el.tagName === 'MAT-SELECT') return chooseOption(el, value);
    el.value = value;
    el.dispatchEvent(new Event('input'));
  }

  /** Відкриває `mat-select` і обирає варіант за підписом (варіанти — в overlay поза панеллю). */
  async function choose(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }, host: HTMLElement, testid: string, label: string) {
    host.querySelector<HTMLElement>(`[data-testid="${testid}"] .mat-mdc-select-trigger`)!.click();
    await render(fixture);
    [...document.querySelectorAll<HTMLElement>('mat-option')].find((o) => o.textContent?.trim() === label)!.click();
    await render(fixture);
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує канали з подіями', async () => {
    const { host } = await setup();
    const row = host.querySelector('[data-channel="c1"]')!;
    expect(row.textContent).toContain('Черговий');
    expect(row.textContent).toContain('Telegram');
    expect(row.textContent).toContain('Воркер не відповідає');
  });

  it('порожньо — підказка', async () => {
    const { host } = await setup([]);
    expect(host.querySelector('[data-testid="channels-empty"]')).not.toBeNull();
  });

  it('створює вебхук-канал із заголовками з облікових даних', async () => {
    const { fixture, http, host } = await setup([]);
    host.querySelector<HTMLButtonElement>('[data-testid="channel-add"]')!.click();
    flushOptions(http);
    await render(fixture);
    input(host, 'channel-name', 'SIEM');
    await choose(fixture, host, 'channel-type', channelTypeLabel('webhook'));
    await render(fixture);
    expect(host.querySelector('[data-testid="channel-connector"]')).toBeNull();
    input(host, 'channel-url', 'https://siem.test/hook');
    await choose(fixture, host, 'channel-credential', 'Токен вебхука');
    host.querySelector<HTMLInputElement>('[data-event="worker.stale"]')!.click();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="channel-save"]')!.click();

    const req = http.expectOne({ method: 'POST', url: '/api/v1/notification-channels' });
    expect(req.request.body).toEqual({
      name: 'SIEM',
      type: 'webhook',
      config: { url: 'https://siem.test/hook', credentialId: 'cred' },
      events: ['execution.failed', 'workflow.disabled', 'connector.unavailable', 'backup.failed'],
      cooldownSec: 900,
      enabled: true,
    });
    req.flush({ ...telegram, id: 'c2' });
    http.expectOne({ method: 'GET', url: '/api/v1/notification-channels' }).flush([]);
  });

  it('змінює канал: форма заповнена з наявного', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-channel="c1"] [data-action="edit"]')!.click();
    flushOptions(http);
    await render(fixture);
    expect(host.querySelector<HTMLInputElement>('[data-testid="channel-chat"]')!.value).toBe('-100500');
    input(host, 'channel-cooldown', '60');
    host.querySelector<HTMLButtonElement>('[data-testid="channel-save"]')!.click();
    const req = http.expectOne({ method: 'PUT', url: '/api/v1/notification-channels/c1' });
    expect(req.request.body.cooldownSec).toBe(3600);
    expect(req.request.body.config).toEqual({ connectorId: 'tg', chatId: '-100500' });
    req.flush(telegram);
    http.expectOne({ method: 'GET', url: '/api/v1/notification-channels' }).flush([telegram]);
  });

  it('«Перевірити»: успіх і помилка доставки', async () => {
    const { fixture, http, host } = await setup();
    const button = () => host.querySelector<HTMLButtonElement>('[data-channel="c1"] [data-action="test"]')!;
    button().click();
    http.expectOne({ method: 'POST', url: '/api/v1/notification-channels/c1/test' }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    expect(host.querySelector('[data-testid="channels-notice"]')?.textContent).toContain('Черговий');

    button().click();
    http
      .expectOne({ method: 'POST', url: '/api/v1/notification-channels/c1/test' })
      .flush({ detail: 'Канал «Черговий» не прийняв повідомлення' }, { status: 502, statusText: 'Bad Gateway' });
    await render(fixture);
    expect(host.querySelector('[data-testid="channels-error"]')?.textContent).toContain('не прийняв');
  });

  it('видаляє канал і перечитує список', async () => {
    const { http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-channel="c1"] [data-action="delete"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/notification-channels/c1' }).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne({ method: 'GET', url: '/api/v1/notification-channels' }).flush([]);
  });
});
