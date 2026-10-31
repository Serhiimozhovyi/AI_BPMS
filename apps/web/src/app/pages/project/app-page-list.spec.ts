import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProjectPageView } from '../../projects/project-pages-api.service';
import { AppPagePage } from './app-page.page';
import { chooseOption, selectValue } from '../../shared/select-search/select-testing';

const base = '/api/v1/projects/p1/pages';

/** Список подій: панель фільтрів, мітки, обчислювана колонка «3 / 8», підлеглий список, форма в панелі. */
const eventsView: ProjectPageView = {
  page: {
    id: 'pg1',
    key: 'events',
    name: 'Події',
    kind: 'list',
    definition: {
      table: 'events',
      columns: [
        { column: 'title', label: 'Назва' },
        { column: 'starts_at', format: 'datetime' },
        { column: 'status', format: 'badge', labels: { planned: 'Заплановано', cancelled: 'Скасовано' }, colors: { planned: 'green', cancelled: 'gray' } },
        { column: 'registered' },
      ],
      computed: [{ key: 'registered', label: 'Місця', table: 'regs', by: 'event_id', of: 'capacity' }],
      filters: [
        { column: 'host', kind: 'select', label: 'Хто веде', default: '{{ $user.id }}' },
        { column: 'status', kind: 'chips', label: 'Стан' },
        { column: 'starts_at', kind: 'period', label: 'Коли' },
      ],
      actions: [{ label: 'Змінити', page: 'event-form', mode: 'drawer' }],
      create: { page: 'event-form', mode: 'drawer' },
      expand: [
        {
          table: 'regs',
          by: 'event_id',
          label: 'Учасники',
          columns: [{ column: 'name', label: 'Ім’я' }],
          actions: [{ label: 'Скасувати', workflow: 'cancel-reg', confirm: 'Скасувати?' }, { label: 'Відкрити', page: 'reg', mode: 'drawer' }],
          create: { page: 'reg-form', mode: 'drawer' },
          empty: 'Ще ніхто не зареєструвався',
        },
      ],
      empty: 'Подій немає.',
    },
    roles: [],
    sortOrder: 0,
    updatedAt: '2026-10-29T00:00:00Z',
  },
  tableId: 't1',
  columns: [
    { column: 'title', label: 'Назва', type: 'string' },
    { column: 'starts_at', label: 'Початок', type: 'datetime', format: 'datetime' },
    { column: 'status', label: 'Стан', type: 'enum' },
  ],
  schema: null,
  canEdit: true,
  timezone: 'Europe/Kyiv',
  filterOptions: {
    host: [{ value: 'u1', label: 'Олена' }, { value: 'u2', label: 'Ірина' }],
    status: [{ value: 'planned', label: 'Заплановано' }, { value: 'cancelled', label: 'Скасовано' }],
  },
};

const formView: ProjectPageView = {
  page: { ...eventsView.page, id: 'pg2', key: 'event-form', name: 'Подія', kind: 'form', definition: { table: 'events', onSubmit: { operation: 'insert' } } },
  tableId: 't1',
  columns: [],
  schema: { type: 'object', required: ['title'], properties: { title: { type: 'string', title: 'Назва' }, event_id: { type: 'string', title: 'Подія' } } },
  canEdit: true,
};

const rows = (filters?: Record<string, unknown>) => ({
  items: [
    {
      key: 'e1',
      data: { title: 'Консультація', starts_at: '2026-11-02T08:00:00.0000000Z', status: 'planned', registered: 3, capacity: 8 },
      display: {},
      actions: [0],
    },
  ],
  page: 1,
  pageSize: 50,
  total: 1,
  filters,
});

describe('AppPagePage: список з панеллю, мітками й підлеглими списками (F8-16)', () => {
  async function setup(params: Record<string, string> = { key: 'events' }, query: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap(params)),
            queryParamMap: of(convertToParamMap(query)),
            parent: { parent: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } },
          },
        },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(AppPagePage);
    fixture.detectChanges();
    return { fixture, navigate, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function openList() {
    const ctx = await setup();
    ctx.http.expectOne(`${base}/events`).flush(eventsView);
    const first = ctx.http.expectOne((r) => r.url === `${base}/events/rows`);
    // Перший запит без фільтрів — діють значення за замовчуванням; відповідь повертає їх для панелі.
    expect(first.request.params.keys().filter((k) => k.startsWith('filters'))).toEqual([]);
    first.flush(rows({ host: { values: ['u1'] } }));
    await render(ctx.fixture);
    return ctx;
  }

  it('панель показує значення за замовчуванням з відповіді, зміна фільтра надсилає всі фільтри й знімає default', async () => {
    const { fixture, http, host } = await openList();

    expect(selectValue(host.querySelector('mat-select[data-filter="host"]')!)).toBe('u1');
    expect(host.querySelectorAll('[data-chip]')).toHaveLength(2);

    host.querySelector<HTMLButtonElement>('[data-chip="planned"]')!.click();
    const chips = http.expectOne((r) => r.url === `${base}/events/rows`);
    expect(chips.request.params.get('filters[host]')).toBe('u1');
    expect(chips.request.params.getAll('filters[status]')).toEqual(['planned']);
    expect(chips.request.params.get('filters[starts_at]')).toBe('');
    chips.flush(rows());
    await render(fixture);
    expect(host.querySelector('[data-chip="planned"]')!.getAttribute('aria-pressed')).toBe('true');

    host.querySelector<HTMLButtonElement>('[data-chip="cancelled"]')!.click();
    const both = http.expectOne((r) => r.url === `${base}/events/rows`);
    expect(both.request.params.getAll('filters[status]')).toEqual(['planned', 'cancelled']);
    both.flush(rows());
    await render(fixture);

    chooseOption(host.querySelector('mat-select[data-filter="host"]')!, '');
    const cleared = http.expectOne((r) => r.url === `${base}/events/rows`);
    expect(cleared.request.params.get('filters[host]')).toBe('');
    cleared.flush(rows());
    await render(fixture);

    chooseOption(host.querySelector('mat-select[data-filter="starts_at"]')!, 'week');
    const week = http.expectOne((r) => r.url === `${base}/events/rows`);
    expect(week.request.params.get('filters[starts_at]')).toBe('week');
    week.flush(rows());
    await render(fixture);

    chooseOption(host.querySelector('mat-select[data-filter="starts_at"]')!, 'custom');
    http.expectOne((r) => r.url === `${base}/events/rows`).flush(rows());
    await render(fixture);
    const from = host.querySelector<HTMLInputElement>('[data-filter-from="starts_at"]')!;
    from.value = '2026-11-01';
    from.dispatchEvent(new Event('change'));
    const range = http.expectOne((r) => r.url === `${base}/events/rows`);
    expect(range.request.params.get('filters[starts_at][from]')).toBe('2026-11-01');
    expect(range.request.params.has('filters[starts_at]')).toBe(false);
    range.flush(rows());
  });

  it('мітка стану з кольором, дата в поясі застосунку, обчислювана колонка «3 / 8», заголовок без сортування', async () => {
    const { host } = await openList();
    const row = host.querySelector('[data-row="e1"]')!;
    const badge = row.querySelector('[data-badge]')!;
    expect(badge.textContent?.trim()).toBe('Заплановано');
    expect(badge.getAttribute('data-badge')).toBe('green');
    expect(badge.getAttribute('data-tone')).toBe('green');
    expect(row.textContent).toContain('10:00');
    expect(row.textContent).toContain('3 / 8');
    expect(host.querySelector('[data-sort="registered"]')).toBeNull();
    expect(host.querySelector('[data-sort="title"]')).not.toBeNull();
  });

  it('порожній список показує свій текст', async () => {
    const { http, host, fixture } = await setup();
    http.expectOne(`${base}/events`).flush(eventsView);
    http.expectOne((r) => r.url === `${base}/events/rows`).flush({ items: [], page: 1, pageSize: 50, total: 0 });
    await render(fixture);
    expect(host.querySelector('[data-testid="app-rows-empty"]')?.textContent).toContain('Подій немає.');
  });

  it('layout: cards — заголовок і підзаголовок картки, решта колонок, мітка й дія', async () => {
    const cards: ProjectPageView = {
      ...eventsView,
      page: {
        ...eventsView.page,
        definition: { ...eventsView.page.definition, layout: 'cards', card: { title: 'title', subtitle: 'status' }, filters: undefined },
      },
    };
    const { http, host, fixture } = await setup();
    http.expectOne(`${base}/events`).flush(cards);
    http.expectOne((r) => r.url === `${base}/events/rows`).flush(rows());
    await render(fixture);

    expect(host.querySelector('[data-testid="app-rows"]')).toBeNull();
    const card = host.querySelector('[data-testid="app-cards"] [data-row="e1"]')!;
    expect(card.querySelector('[data-testid="card-title"]')?.textContent).toContain('Консультація');
    expect(card.querySelector('[data-testid="card-subtitle"]')?.textContent).toContain('Заплановано');
    expect(card.querySelector('[data-field="registered"]')?.textContent).toContain('3 / 8');
    expect(card.querySelector('[data-field="title"]')).toBeNull();
    card.querySelector<HTMLButtonElement>('[data-action="0"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="app-drawer"]')).not.toBeNull();
    http.expectOne(`${base}/event-form`).flush(formView);
    await render(fixture);
    http.expectOne(`${base}/event-form/rows/e1`).flush({ row: { key: 'e1', data: { title: 'Консультація' }, display: {}, actions: [] }, related: [] });
  });

  it('підлеглий список: розгортання, мітка порожнього стану, дії, «Додати» з ключем батьківського рядка', async () => {
    const { fixture, http, host } = await openList();
    host.querySelector<HTMLButtonElement>('[data-expand-toggle="e1"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-expand-toggle="e1"]')!.getAttribute('aria-expanded')).toBe('true');
    http.expectOne(`${base}/events/rows/e1/expand/0`).flush({
      label: 'Учасники',
      columns: [{ column: 'name', label: 'Ім’я', type: 'string' }],
      rows: [{ key: 'r1', data: { name: 'Олена' }, display: {}, actions: [0, 1] }],
    });
    await render(fixture);

    expect(host.querySelector('[data-sub-row="r1"]')?.textContent).toContain('Олена');
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    host.querySelector<HTMLButtonElement>('[data-sub-row="r1"] [data-sub-action="0"]')!.click();
    http.expectOne({ method: 'POST', url: `${base}/events/rows/e1/expand/0/r1/actions/0` }).flush({ state: 'success', message: 'Реєстрацію скасовано.' });
    // Після дії підлеглий і основний списки перечитуються.
    http.expectOne(`${base}/events/rows/e1/expand/0`).flush({ label: 'Учасники', columns: [], rows: [] });
    http.expectOne((r) => r.url === `${base}/events/rows`).flush(rows());
    await render(fixture);
    expect(host.querySelector('[data-testid="action-result"]')?.textContent).toContain('Реєстрацію скасовано.');
    expect(host.querySelector('[data-testid="expand-empty"]')?.textContent).toContain('Ще ніхто не зареєструвався');

    host.querySelector<HTMLButtonElement>('[data-expand-create="0"]')!.click();
    await render(fixture);
    http.expectOne(`${base}/reg-form`).flush(formView);
    await render(fixture);
    // У формі створення підлеглого запису ключ батьківського рядка вже підставлено.
    expect(host.querySelector<HTMLInputElement>('[data-testid="form-field-event_id"]')?.value).toBe('e1');

    host.querySelector<HTMLButtonElement>('[data-expand-toggle="e1"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-expand-row="e1"]')).toBeNull();
  });

  it('форма в боковій панелі: «Створити» не веде на іншу сторінку, збереження закриває панель і перечитує список', async () => {
    const { fixture, http, host, navigate } = await openList();
    host.querySelector<HTMLButtonElement>('[data-testid="app-create"]')!.click();
    await render(fixture);
    http.expectOne(`${base}/event-form`).flush(formView);
    await render(fixture);

    expect(navigate).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="app-drawer"]')?.getAttribute('aria-modal')).toBe('true');
    expect(host.querySelector('[data-testid="drawer-title"]')?.textContent).toContain('Подія');
    const title = host.querySelector<HTMLInputElement>('[data-testid="form-field-title"]')!;
    title.value = 'Лекція';
    title.dispatchEvent(new Event('input'));
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.click();
    http
      .expectOne({ method: 'POST', url: `${base}/event-form/submit` })
      .flush({ row: { key: 'e2', data: {}, display: {}, actions: [] }, then: null });
    http.expectOne((r) => r.url === `${base}/events/rows`).flush(rows());
    await render(fixture);
    expect(host.querySelector('[data-testid="app-drawer"]')).toBeNull();
  });

  it('Esc і хрестик закривають панель без збереження; сторінка не форма відкривається переходом', async () => {
    const { fixture, http, host, navigate } = await openList();
    host.querySelector<HTMLButtonElement>('[data-testid="app-create"]')!.click();
    await render(fixture);
    http.expectOne(`${base}/event-form`).flush(formView);
    await render(fixture);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await render(fixture);
    expect(host.querySelector('[data-testid="app-drawer"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="app-create"]')!.click();
    await render(fixture);
    http.expectOne(`${base}/event-form`).flush(formView);
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="drawer-close"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="app-drawer"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="app-create"]')!.click();
    await render(fixture);
    http.expectOne(`${base}/event-form`).flush({ ...formView, page: { ...formView.page, kind: 'detail' } });
    await render(fixture);
    expect(host.querySelector('[data-testid="app-drawer"]')).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'event-form']);
  });

  it('форма сторінки бере початкові значення з адреси (?поле=значення)', async () => {
    const { fixture, http, host } = await setup({ key: 'reg-form' }, { event_id: 'e1', other: 'x' });
    http.expectOne(`${base}/reg-form`).flush({ ...formView, page: { ...formView.page, key: 'reg-form' } });
    await render(fixture);
    expect(host.querySelector<HTMLInputElement>('[data-testid="form-field-event_id"]')?.value).toBe('e1');
  });
});
