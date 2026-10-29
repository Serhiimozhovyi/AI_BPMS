import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PageDashboard, PageDashboardBlock, ProjectPageView } from '../../projects/project-pages-api.service';
import { ExpandOpen } from './page-expand.component';
import { PageDashboardComponent } from './page-dashboard.component';

const base = '/api/v1/projects/p1/pages/today';

const view: ProjectPageView = {
  page: {
    id: 'pg',
    key: 'today',
    name: 'Сьогодні',
    kind: 'dashboard',
    definition: {
      table: 'slots',
      filters: [{ column: 'host', kind: 'select', label: 'Хто веде' }],
      blocks: [
        { kind: 'text', text: 'Вітаємо', width: 12 },
        { kind: 'tiles', label: 'Показники', page: 'metrics', width: 6 },
        { kind: 'list', label: 'Найближчі записи', page: 'bookings', limit: 5, width: 6 },
        { kind: 'calendar', label: 'Сьогодні', page: 'schedule', range: 'week' },
        { kind: 'form', label: 'Налаштування', page: 'settings-form' },
        {
          kind: 'actions',
          actions: [
            { label: 'Додати слот', page: 'slot-form', mode: 'drawer' },
            { label: 'Згенерувати', workflow: 'generate', confirm: 'Створити слоти?' },
          ],
        },
      ],
    },
    roles: [],
    sortOrder: 0,
    updatedAt: '2026-11-04T00:00:00Z',
  },
  tableId: 't1',
  columns: [],
  schema: null,
  canEdit: true,
  timezone: 'Europe/Kyiv',
  filterOptions: { host: [{ value: 'u1', label: 'Олена' }, { value: 'u2', label: 'Ірина' }] },
};

const listView: ProjectPageView = {
  page: {
    ...view.page,
    id: 'pg2',
    key: 'bookings',
    kind: 'list',
    definition: {
      table: 'bookings',
      columns: [{ column: 'name' }, { column: 'status', format: 'badge', labels: { active: 'Активний' }, colors: { active: 'green' } }],
      actions: [{ label: 'Змінити', page: 'booking-form', mode: 'drawer' }, { label: 'Скасувати', workflow: 'cancel', confirm: 'Скасувати?' }],
    },
  },
  tableId: 't2',
  columns: [{ column: 'name', label: 'Ім’я', type: 'string' }, { column: 'status', label: 'Стан', type: 'enum' }],
  schema: null,
  canEdit: true,
  timezone: 'Europe/Kyiv',
};

const calendarView: ProjectPageView = {
  page: {
    ...view.page,
    id: 'pg3',
    key: 'schedule',
    kind: 'calendar',
    definition: {
      table: 'slots',
      calendar: { start: 'starts_at', title: { column: 'note' }, color: { column: 'status', format: 'badge', colors: { open: 'green' } }, open: { page: 'slot-card', mode: 'drawer' } },
    },
  },
  tableId: 't1',
  columns: [{ column: 'note', label: 'Нотатка', type: 'string' }],
  schema: null,
  canEdit: true,
  timezone: 'Europe/Kyiv',
};

const formView: ProjectPageView = {
  page: { ...view.page, id: 'pg4', key: 'settings-form', kind: 'form', definition: { table: 'settings', onSubmit: { operation: 'upsert' } } },
  tableId: 't3',
  columns: [],
  schema: { type: 'object', properties: { greeting: { type: 'string', title: 'Привітання' } } },
  canEdit: true,
};

const blocks: PageDashboardBlock[] = [
  { index: 0, kind: 'text', width: 12, text: 'Вітаємо, Олено!' },
  { index: 1, kind: 'tiles', label: 'Показники', width: 6, source: 'metrics', metrics: { tiles: [{ label: 'Записів', value: 12 }], charts: [] } },
  {
    index: 2,
    kind: 'list',
    label: 'Найближчі записи',
    width: 6,
    source: 'bookings',
    view: listView,
    rows: {
      items: [{ key: 'b1', data: { name: 'Марія', status: 'active' }, display: {}, actions: [0, 1] }],
      page: 1,
      pageSize: 5,
      total: 7,
      filters: { host: { values: ['u1'] } },
    },
  },
  {
    index: 3,
    kind: 'calendar',
    label: 'Сьогодні',
    width: 12,
    source: 'schedule',
    view: calendarView,
    events: {
      from: '',
      to: '',
      truncated: false,
      events: [{ start: '2026-11-04T08:00:00Z', end: '2026-11-04T09:00:00Z', row: { key: 's1', data: { note: 'Консультація', status: 'open' }, display: {}, actions: [] } }],
    },
  },
  { index: 4, kind: 'form', label: 'Налаштування', width: 12, source: 'settings-form', view: formView },
  { index: 5, kind: 'actions', width: 12, actions: [0, 1] },
];

describe('PageDashboardComponent (F8-18)', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<PageDashboardComponent>;
  let host: HTMLElement;
  const opened: ExpandOpen[] = [];

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function setup(data: PageDashboard = { blocks }) {
    fixture = TestBed.createComponent(PageDashboardComponent);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.componentRef.setInput('view', view);
    fixture.componentInstance.open.subscribe((o) => opened.push(o));
    host = fixture.nativeElement as HTMLElement;
    await render();
    const req = http.expectOne((r) => r.url === `${base}/dashboard`);
    req.flush(data);
    await render();
    return req.request;
  }

  async function nextRequest(data: PageDashboard = { blocks }) {
    await render();
    const req = http.expectOne((r) => r.url === `${base}/dashboard`);
    req.flush(data);
    await render();
    return req.request;
  }

  const q = (selector: string) => host.querySelector<HTMLElement>(selector);
  const all = (selector: string) => Array.from(host.querySelectorAll<HTMLElement>(selector));

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-04T10:00:00Z') });
    opened.length = 0;
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    http.verify();
  });

  it('показує блоки в сітці з потрібною шириною', async () => {
    await setup();
    expect(all('[data-block]').map((b) => b.getAttribute('data-kind'))).toEqual(['text', 'tiles', 'list', 'calendar', 'form', 'actions']);
    expect(q('[data-block="1"]')!.className).toContain('md:col-span-6');
    expect(q('[data-block="0"]')!.className).toContain('md:col-span-12');
    expect(q('[data-block="0"] [data-testid="block-text"]')?.textContent).toBe('Вітаємо, Олено!');
    expect(q('[data-block="1"] [data-tile]')?.textContent).toContain('12');
  });

  it('короткий список: мітки, кількість «Усі», дії рядка', async () => {
    await setup();
    const row = q('[data-block="2"] [data-row="b1"]')!;
    expect(row.textContent).toContain('Марія');
    expect(row.querySelector('[data-field="status"]')?.textContent?.trim()).toBe('Активний');
    expect(row.querySelector('[data-field="status"] span')?.className).toContain('bg-ac-green');
    expect(q('[data-block="2"] [data-testid="block-all"]')?.textContent).toContain('Усі');
    expect(q('[data-block="2"] [data-testid="block-all"]')?.textContent).toContain('(7)');
    expect(q('[data-block="2"] [data-testid="block-all"]')?.getAttribute('href')).toBe('/projects/p1/app/bookings');

    row.querySelector<HTMLElement>('[data-action="0"]')!.click();
    expect(opened).toEqual([{ page: 'booking-form', rowKey: 'b1', mode: 'drawer' }]);
  });

  it('дія рядка-сценарій питає підтвердження, запускає сторінку блоку й перечитує дані', async () => {
    await setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    q('[data-block="2"] [data-action="1"]')!.click();
    const run = http.expectOne(`/api/v1/projects/p1/pages/bookings/rows/b1/actions/1`);
    run.flush({ state: 'success', message: 'Запис скасовано' });
    await nextRequest();
    expect(q('[data-testid="dash-notice"]')?.textContent).toBe('Запис скасовано');
  });

  it('відмову підтвердження дія не виконується', async () => {
    await setup();
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    q('[data-block="2"] [data-action="1"]')!.click();
    http.expectNone(`/api/v1/projects/p1/pages/bookings/rows/b1/actions/1`);
  });

  it('події дня: час у поясі застосунку, клік відкриває картку', async () => {
    await setup();
    const event = q('[data-block="3"] [data-event="s1"]')!;
    expect(event.textContent).toContain('10:00–11:00');
    expect(event.textContent).toContain('Консультація');
    expect(q('[data-block="3"] [data-testid="block-all"]')?.getAttribute('href')).toBe('/projects/p1/app/schedule');
    event.click();
    expect(opened).toEqual([{ page: 'slot-card', rowKey: 's1', mode: 'drawer' }]);
  });

  it('форма блоку зберігається через сторінку блоку', async () => {
    await setup();
    const input = q('[data-block="4"] input')! as HTMLInputElement;
    input.value = 'Привіт';
    input.dispatchEvent(new Event('input'));
    q('[data-block="4"] [data-testid="app-submit"]')!.click();
    await render();
    const submit = http.expectOne('/api/v1/projects/p1/pages/settings-form/submit');
    expect(submit.request.body.values).toEqual({ greeting: 'Привіт' });
    submit.flush({ row: null, then: null });
    await nextRequest();
  });

  it('кнопки: перехід у панель і сценарій від імені користувача', async () => {
    await setup();
    q('[data-block-action="0"]')!.click();
    expect(opened).toEqual([{ page: 'slot-form', mode: 'drawer' }]);

    vi.spyOn(window, 'confirm').mockReturnValue(true);
    q('[data-block-action="1"]')!.click();
    const run = http.expectOne(`${base}/blocks/5/actions/1`);
    expect(run.request.method).toBe('POST');
    run.flush({ state: 'rejected', message: 'Слоти вже є' });
    await nextRequest();
    const notice = q('[data-testid="dash-notice"]')!;
    expect(notice.textContent).toBe('Слоти вже є');
    expect(notice.getAttribute('data-state')).toBe('rejected');
  });

  it('спільні фільтри: типове значення з блоків видно, зміна перечитує всі блоки', async () => {
    await setup();
    expect(q('[data-filter="host"]')).not.toBeNull();
    expect((q('[data-filter="host"]') as HTMLSelectElement).value).toBe('u1');
    const select = q('[data-filter="host"]') as HTMLSelectElement;
    select.value = 'u2';
    select.dispatchEvent(new Event('change'));
    const request = await nextRequest();
    expect(request.params.getAll('filters[host]')).toEqual(['u2']);
  });

  it('порожня сторінка й помилка Api', async () => {
    await setup({ blocks: [] });
    expect(q('[data-testid="dash-empty"]')).not.toBeNull();
    fixture.componentRef.setInput('version', 1);
    await render();
    http.expectOne((r) => r.url === `${base}/dashboard`).flush({ title: 'Помилка', detail: 'Збій' }, { status: 500, statusText: 'Server Error' });
    await render();
    expect(q('[data-testid="dash-error"]')).not.toBeNull();
  });
});
