import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Overview, formatDuration, formatRate } from '../../overview/overview-api.service';
import { OverviewPage } from './overview.page';

const day: Overview = {
  range: '24h',
  from: '2026-10-23T10:00:00Z',
  total: 40,
  success: 36,
  failed: 3,
  cancelled: 1,
  successRate: 0.9,
  p50Ms: 500,
  p95Ms: 5000,
  topFailing: [
    { workflowId: 'w1', name: 'Звіт продажів', failed: 2, total: 10 },
    { workflowId: 'w2', name: null, failed: 1, total: 30 },
  ],
  queued: 4,
  running: 2,
  waiting: 1,
  openViolations: 3,
};

describe('formatDuration і formatRate', () => {
  it('показують верхню межу кошика і відсотки', () => {
    expect(formatDuration(null)).toBe('—');
    expect(formatDuration(250)).toBe('до 250 мс');
    expect(formatDuration(2500)).toBe('до 2,5 с');
    expect(formatDuration(300_000)).toBe('до 5 хв');
    expect(formatRate(null)).toBe('—');
    expect(formatRate(0.9)).toBe('90 %');
  });
});

describe('OverviewPage', () => {
  async function setup(data: Overview = day) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(OverviewPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const req = http.expectOne((r) => r.url === '/api/v1/overview');
    expect(req.request.params.get('range')).toBe('24h');
    req.flush(data);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує підписки на події з власником і станом (F6-20)', async () => {
    const { host } = await setup({
      ...day,
      subscriptions: [
        { workflowId: 'w1', workflowName: 'Бот', nodeId: 'n1', nodeName: 'Нове повідомлення', serverId: 's1', serverName: 'Telegram',
          resourceUri: 'telegram://updates', workerId: 'worker-a', leasedUntil: '2026-10-26T10:00:30Z', state: 'held' },
        { workflowId: 'w2', workflowName: 'Звіт', nodeId: 'n2', nodeName: null, serverId: 's2', serverName: null,
          resourceUri: 'kit://changes/sales', workerId: null, leasedUntil: null, state: 'free' },
      ],
    });
    const held = host.querySelector('[data-subscription="w1/n1"]')!;
    expect(held.textContent).toContain('Telegram');
    expect(held.textContent).toContain('telegram://updates');
    expect(held.textContent).toContain('worker-a');
    expect(held.textContent).toContain('Відкрита');
    const free = host.querySelector('[data-subscription="w2/n2"]')!;
    expect(free.getAttribute('data-state')).toBe('free');
    expect(free.textContent).toContain('Немає власника');
    expect(free.textContent).toContain('s2');
  });

  it('без підписок показує порожній стан (F6-20)', async () => {
    const { host } = await setup();
    expect(host.querySelector('[data-testid="subscriptions-empty"]')).not.toBeNull();
  });

  it('показує лічильники, «зараз» і топ сценаріїв за помилками', async () => {
    const { host } = await setup();
    expect(host.querySelector('h1')?.textContent).toContain('Огляд');
    expect(host.querySelector('[data-testid="stat-total"]')?.textContent).toContain('40');
    expect(host.querySelector('[data-testid="stat-total"]')?.textContent).toContain('з помилкою: 3');
    expect(host.querySelector('[data-testid="stat-rate"]')?.textContent).toContain('90 %');
    expect(host.querySelector('[data-testid="stat-p95"]')?.textContent).toContain('до 5 с');
    expect(host.querySelector('[data-testid="now-running"]')?.textContent).toContain('2');
    expect(host.querySelector('[data-testid="now-waiting"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-testid="now-queued"]')?.textContent).toContain('4');
    const violations = host.querySelector<HTMLAnchorElement>('[data-testid="now-violations"]')!;
    expect(violations.textContent).toContain('3');
    expect(violations.getAttribute('href')).toBe('/executions?hasOpenViolations=true');
    const rows = host.querySelectorAll('[data-workflow]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Звіт продажів');
    expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/workflows/w1');
    expect(rows[1].textContent).toContain('w2');
  });

  it('без помилок — підказка замість таблиці', async () => {
    const { host } = await setup({ ...day, topFailing: [], total: 0, successRate: null, p50Ms: null, p95Ms: null });
    expect(host.querySelector('[data-testid="top-empty"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="stat-rate"]')?.textContent).toContain('—');
  });

  it('перемикач періоду перечитує дані', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-range="7d"]')!.click();
    const req = http.expectOne((r) => r.url === '/api/v1/overview');
    expect(req.request.params.get('range')).toBe('7d');
    req.flush({ ...day, range: '7d', total: 300 });
    await render(fixture);
    expect(host.querySelector('[data-range="7d"]')?.getAttribute('aria-pressed')).toBe('true');
    expect(host.querySelector('[data-testid="stat-total"]')?.textContent).toContain('300');
  });

  it('помилка Api — повідомлення', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(OverviewPage);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController)
      .expectOne((r) => r.url === '/api/v1/overview')
      .flush({ detail: 'Немає зв’язку з БД' }, { status: 503, statusText: 'Unavailable' });
    await render(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="page-error"]')?.textContent).toContain('Немає зв’язку з БД');
  });
});
