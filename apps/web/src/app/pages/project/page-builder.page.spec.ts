import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { PageBuilderPage } from './page-builder.page';

const tables = [
  {
    id: 't1',
    name: 'bookings',
    projectId: 'p1',
    rowCount: 3,
    updatedAt: '2026-10-28T00:00:00Z',
    columns: [
      { name: 'name', type: 'string' },
      { name: 'status', type: 'enum', values: ['active', 'cancelled'] },
      { name: 'minutes', type: 'number' },
    ],
  },
  { id: 't2', name: 'other', projectId: 'p2', rowCount: 0, updatedAt: '2026-10-28T00:00:00Z', columns: [{ name: 'x', type: 'string' }] },
];

const workflows = {
  items: [
    { id: 'w1', name: 'cancel-booking', active: false, version: 1, updatedAt: '', groupId: 'p1' },
    { id: 'w2', name: 'чужий', active: false, version: 1, updatedAt: '', groupId: 'p2' },
  ],
  total: 2,
  page: 1,
  pageSize: 200,
};

describe('PageBuilderPage (F8-05)', () => {
  async function setup(key: string | null = null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap(key ? { key } : {}) }, parent: { parent: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } } },
        },
      ],
    });
    TestBed.inject(ProjectContextService).projects.set([
      { id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 1, role: 'editor', appRoles: [{ key: 'coordinator', name: 'Координатор' }] },
    ]);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(PageBuilderPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/tables').flush(tables);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush(workflows);
    http.expectOne('/api/v1/projects/p1/pages').flush([
      { id: 'a', key: 'booking', name: 'Запис', kind: 'detail', definition: { table: 'bookings' }, roles: [], sortOrder: 0, updatedAt: '' },
    ]);
    return { fixture, navigate, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function pick(host: HTMLElement, selector: string, value: string, event = 'change') {
    const el = host.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
    el.value = value;
    el.dispatchEvent(new Event(event));
  }

  it('збирає список: таблиця проєкту, колонки, умова з $user, дія-сценарій і зберігає', async () => {
    const { fixture, http, host, navigate } = await setup();
    await render(fixture);

    // Таблиці й сценарії інших проєктів у виборі не з'являються.
    expect([...host.querySelectorAll('[data-testid="b-table"] option')].map((o) => o.textContent?.trim())).toEqual(['', 'bookings']);

    pick(host, '[data-testid="b-key"]', 'my-bookings', 'input');
    pick(host, '[data-testid="b-name"]', 'Мої записи', 'input');
    pick(host, '[data-testid="b-table"]', 'bookings');
    await render(fixture);
    host.querySelector<HTMLInputElement>('[data-column="name"]')!.click();
    host.querySelector<HTMLInputElement>('[data-column="status"]')!.click();
    host.querySelector<HTMLInputElement>('[data-role="coordinator"]')!.click();
    host.querySelector<HTMLButtonElement>('[data-testid="b-add-filter"]')!.click();
    await render(fixture);
    const filter = host.querySelector('[data-filter="0"]')!;
    const column = filter.querySelectorAll('select')[0] as HTMLSelectElement;
    column.value = 'status';
    column.dispatchEvent(new Event('change'));
    await render(fixture);
    const value = host.querySelector<HTMLInputElement>('[data-filter="0"] input')!;
    value.value = 'active';
    value.dispatchEvent(new Event('input'));
    host.querySelector<HTMLButtonElement>('[data-testid="b-add-action"]')!.click();
    await render(fixture);
    const action = host.querySelector('[data-action="0"]')!;
    const label = action.querySelector('input')!;
    label.value = 'Скасувати';
    label.dispatchEvent(new Event('input'));
    const type = action.querySelectorAll('select')[0] as HTMLSelectElement;
    type.value = 'workflow';
    type.dispatchEvent(new Event('change'));
    await render(fixture);
    const target = host.querySelector('[data-action="0"]')!.querySelectorAll('select')[1] as HTMLSelectElement;
    expect([...target.options].map((o) => o.value)).toEqual(['', 'cancel-booking']);
    target.value = 'cancel-booking';
    target.dispatchEvent(new Event('change'));
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/my-bookings' });
    expect(put.request.body).toEqual({
      name: 'Мої записи',
      kind: 'list',
      roles: ['coordinator'],
      definition: {
        table: 'bookings',
        filter: [{ column: 'status', operator: 'eq', value: 'active' }],
        columns: [{ column: 'name' }, { column: 'status' }],
        actions: [{ label: 'Скасувати', workflow: 'cancel-booking' }],
      },
    });
    put.flush({});
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app', 'my-bookings']);
  });

  it('редагує наявну форму: зберігає display і value полів, яких форма не змінює', async () => {
    const { fixture, http, host } = await setup('book');
    http.expectOne('/api/v1/projects/p1/pages/book').flush({
      page: {
        id: 'b',
        key: 'book',
        name: 'Запис',
        kind: 'form',
        roles: [],
        sortOrder: 1,
        updatedAt: '',
        definition: {
          table: 'bookings',
          fields: [{ column: 'name', required: true }, { column: 'status', display: 'statuses.title', value: 'active' }],
          onSubmit: { workflow: 'cancel-booking' },
          then: { page: 'booking' },
        },
      },
      tableId: 't1',
      columns: [],
      schema: null,
      canEdit: true,
    });
    await render(fixture);

    expect(host.querySelector<HTMLInputElement>('[data-testid="b-key"]')!.disabled).toBe(true);
    expect(host.querySelector<HTMLInputElement>('[data-required="name"]')!.checked).toBe(true);
    host.querySelector<HTMLInputElement>('[data-required="name"]')!.click();
    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/book' });
    expect(put.request.body.definition).toEqual({
      table: 'bookings',
      fields: [{ column: 'name' }, { column: 'status', display: 'statuses.title', value: 'active' }],
      onSubmit: { workflow: 'cancel-booking' },
      then: { page: 'booking' },
    });
  });

  it('помилка Api показується, хибний JSON не змінює форму', async () => {
    const { fixture, http, host } = await setup();
    await render(fixture);
    pick(host, '[data-testid="b-key"]', 'x', 'input');
    pick(host, '[data-testid="b-name"]', 'X', 'input');
    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/x' }).flush({ code: 'PAGE_TABLE_UNKNOWN', detail: 'Таблиці «» немає.' }, { status: 422, statusText: 'Unprocessable Entity' });
    await render(fixture);
    expect(host.querySelector('[data-testid="builder-error"]')).not.toBeNull();

    pick(host, '[data-testid="b-json"]', '{ не json', 'input');
    host.querySelector<HTMLButtonElement>('[data-testid="b-json-apply"]')!.click();
    await render(fixture);
    expect(host.querySelector('[data-testid="builder-error"]')?.textContent).toContain('JSON');
  });

  it('метрики: плитка-агрегат і діаграма потрапляють у визначення', async () => {
    const { fixture, http, host } = await setup();
    await render(fixture);
    pick(host, '[data-testid="b-key"]', 'stats', 'input');
    pick(host, '[data-testid="b-name"]', 'Статистика', 'input');
    pick(host, '[data-testid="b-kind"]', 'metrics');
    pick(host, '[data-testid="b-table"]', 'bookings');
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="b-add-tile"]')!.click();
    host.querySelector<HTMLButtonElement>('[data-testid="b-add-chart"]')!.click();
    await render(fixture);
    const tileLabel = host.querySelector<HTMLInputElement>('[data-tile="0"] input')!;
    tileLabel.value = 'Усього';
    tileLabel.dispatchEvent(new Event('input'));
    const groupBy = host.querySelectorAll<HTMLInputElement>('[data-chart="0"] input')[1];
    groupBy.value = 'status';
    groupBy.dispatchEvent(new Event('input'));
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/stats' });
    expect(put.request.body.kind).toBe('metrics');
    expect(put.request.body.definition).toEqual({
      table: 'bookings',
      tiles: [{ label: 'Усього', aggregate: 'count' }],
      charts: [{ groupBy: 'status', aggregate: 'count' }],
    });
  });
});
