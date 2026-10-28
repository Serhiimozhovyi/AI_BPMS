import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ProjectPageView } from '../../projects/project-pages-api.service';
import { AppPagePage } from './app-page.page';

const base = '/api/v1/projects/p1/pages';

const listView: ProjectPageView = {
  page: {
    id: 'pg1',
    key: 'bookings',
    name: 'Записи',
    kind: 'list',
    definition: {
      table: 'bookings',
      columns: [{ column: 'client' }, { column: 'slot', display: 'slots.starts_at' }, { column: 'status' }],
      search: ['client'],
      sort: { column: 'client' },
      actions: [{ label: 'Відкрити', page: 'booking' }, { label: 'Видалити', operation: 'delete' }],
      create: { page: 'new-booking' },
    },
    roles: [],
    sortOrder: 0,
    updatedAt: '2026-10-28T00:00:00Z',
  },
  tableId: 't1',
  columns: [
    { column: 'client', label: 'Клієнт', type: 'string' },
    { column: 'slot', label: 'Слот', type: 'ref' },
    { column: 'status', label: 'Статус', type: 'enum' },
  ],
  schema: null,
  canEdit: true,
};

const detailView: ProjectPageView = {
  ...listView,
  page: {
    ...listView.page,
    id: 'pg2',
    key: 'booking',
    name: 'Запис',
    kind: 'detail',
    definition: { table: 'bookings', related: [{ table: 'notes', by: 'booking', label: 'Нотатки', actions: [{ label: 'Змінити', page: 'note' }] }] },
  },
};

const formView: ProjectPageView = {
  ...listView,
  page: {
    ...listView.page,
    id: 'pg3',
    key: 'new-booking',
    name: 'Новий запис',
    kind: 'form',
    definition: { table: 'bookings', onSubmit: { operation: 'insert' }, then: { page: 'bookings' } },
  },
  schema: {
    type: 'object',
    required: ['client'],
    properties: { client: { type: 'string', title: 'Клієнт' }, comment: { type: 'string', title: 'Коментар' } },
  },
};

describe('AppPagePage (F8-04)', () => {
  async function setup(params: Record<string, string>) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            paramMap: of(convertToParamMap(params)),
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

  it('список: колонки, показане значення ref, пошук, сортування, дії', async () => {
    const { fixture, http, host, navigate } = await setup({ key: 'bookings' });
    http.expectOne(`${base}/bookings`).flush(listView);
    const first = http.expectOne((r) => r.url === `${base}/bookings/rows`);
    expect(first.request.params.get('sort')).toBe('client');
    expect(first.request.params.get('direction')).toBe('asc');
    first.flush({
      items: [{ key: 'r1', data: { client: 'Олена', slot: 's1', status: 'active' }, display: { slot: '29.10 10:00' }, actions: [0, 1] }],
      page: 1,
      pageSize: 1,
      total: 3,
    });
    await render(fixture);

    expect(host.querySelector('[data-testid="app-page-title"]')?.textContent).toContain('Записи');
    const row = host.querySelector('[data-row="r1"]')!;
    expect(row.textContent).toContain('Олена');
    expect(row.textContent).toContain('29.10 10:00');
    expect(host.querySelector('[data-testid="app-pager"]')?.textContent).toContain('1 / 3');

    const search = host.querySelector<HTMLInputElement>('[data-testid="app-search"]')!;
    search.value = 'Оле';
    search.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    const searched = http.expectOne((r) => r.url === `${base}/bookings/rows`);
    expect(searched.request.params.get('search')).toBe('Оле');
    searched.flush({ items: [], page: 1, pageSize: 20, total: 0 });
    await render(fixture);
    expect(host.querySelector('[data-testid="app-rows-empty"]')).not.toBeNull();

    fixture.componentInstance.sortBy('client');
    const sorted = http.expectOne((r) => r.url === `${base}/bookings/rows`);
    expect(sorted.request.params.get('direction')).toBe('desc');
    sorted.flush({ items: [{ key: 'r1', data: { client: 'Олена' }, display: {}, actions: [0, 1] }], page: 1, pageSize: 20, total: 1 });
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-row="r1"] [data-action="0"]')!.click();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'booking', 'r1']);

    vi.spyOn(window, 'confirm').mockReturnValue(true);
    host.querySelector<HTMLButtonElement>('[data-row="r1"] [data-action="1"]')!.click();
    http.expectOne({ method: 'POST', url: `${base}/bookings/rows/r1/actions/1` }).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne((r) => r.url === `${base}/bookings/rows`).flush({ items: [], page: 1, pageSize: 20, total: 0 });

    host.querySelector<HTMLButtonElement>('[data-testid="app-create"]')!.click();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'new-booking']);
  });

  it('картка: поля запису і пов’язаний список з переходом', async () => {
    const { fixture, http, host, navigate } = await setup({ key: 'booking', rowKey: 'r1' });
    http.expectOne(`${base}/booking`).flush(detailView);
    http.expectOne(`${base}/booking/rows/r1`).flush({
      row: { key: 'r1', data: { client: 'Олена', slot: 's1', status: null }, display: { slot: '29.10 10:00' }, actions: [] },
      related: [{ label: 'Нотатки', columns: [{ column: 'text', label: 'Текст', type: 'string' }], rows: [{ key: 'n1', data: { text: 'Перша' }, display: {}, actions: [0] }] }],
    });
    await render(fixture);

    expect(host.querySelector('[data-field="client"]')?.textContent).toContain('Олена');
    expect(host.querySelector('[data-field="slot"]')?.textContent).toContain('29.10 10:00');
    expect(host.querySelector('[data-field="status"]')?.textContent).toContain('—');
    expect(host.querySelector('[data-related="0"]')?.textContent).toContain('Нотатки');
    host.querySelector<HTMLButtonElement>('[data-row="n1"] button')!.click();
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'note', 'n1']);
  });

  it('недоступна для ролі сторінка — зрозуміле повідомлення', async () => {
    const { fixture, http, host } = await setup({ key: 'secret' });
    http.expectOne(`${base}/secret`).flush({ code: 'NOT_FOUND' }, { status: 404, statusText: 'Not Found' });
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('не для вашої ролі');
  });

  it('форма: поля з типів колонок, помилка поля з Api і перехід після збереження', async () => {
    const { fixture, http, host, navigate } = await setup({ key: 'new-booking' });
    http.expectOne(`${base}/new-booking`).flush(formView);
    await render(fixture);

    const client = host.querySelector<HTMLInputElement>('[data-testid="form-field-client"]')!;
    expect(client).not.toBeNull();
    expect(host.textContent).toContain('Коментар');
    client.value = 'Олена';
    client.dispatchEvent(new Event('input'));
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.click();
    const invalid = http.expectOne({ method: 'POST', url: `${base}/new-booking/submit` });
    expect(invalid.request.body).toEqual({ values: { client: 'Олена' }, rowKey: null });
    invalid.flush(
      { code: 'PAGE_FORM_INVALID', errors: [{ field: 'client', message: 'Такий запис уже є.' }] },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await render(fixture);
    expect(fixture.componentInstance.form.get('client')?.errors?.['server']).toBe('Такий запис уже є.');
    expect(host.querySelector('[data-testid="app-form-error"]')).not.toBeNull();

    client.value = 'Олена К.';
    client.dispatchEvent(new Event('input'));
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.click();
    http
      .expectOne({ method: 'POST', url: `${base}/new-booking/submit` })
      .flush({ row: { key: 'r2', data: {}, display: {}, actions: [] }, then: 'bookings' });
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'bookings']);
  });
});
