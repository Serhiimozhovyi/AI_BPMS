import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { PageBuilderPage } from './page-builder.page';
import { chooseOption, matSelectOf, selectOptionValues, selectValue } from '../../shared/select-search/select-testing';

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
      { name: 'starts_at', type: 'datetime' },
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
    const el = host.querySelector<HTMLInputElement>(selector)!;
    if (el.tagName === 'MAT-SELECT') return chooseOption(el, value);
    el.value = value;
    el.dispatchEvent(new Event(event));
  }

  it('збирає список: таблиця проєкту, колонки, умова з $user, дія-сценарій і зберігає', async () => {
    const { fixture, http, host, navigate } = await setup();
    await render(fixture);

    // Таблиці й сценарії інших проєктів у виборі не з'являються.
    expect(matSelectOf(host.querySelector('[data-testid="b-table"]')!).options.map((o) => o.viewValue)).toEqual(['', 'bookings']);

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
    chooseOption(filter.querySelectorAll('mat-select')[0], 'status');
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
    chooseOption(action.querySelectorAll('mat-select')[0], 'workflow');
    await render(fixture);
    const target = host.querySelector('[data-action="0"]')!.querySelectorAll('mat-select')[1];
    expect(selectOptionValues(target)).toEqual(['', 'cancel-booking']);
    chooseOption(target, 'cancel-booking');
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

  it('редагування списку не губить фільтри, обчислювані колонки, картки й expand, а формат колонки змінюється формою', async () => {
    const { fixture, http, host } = await setup('list');
    const definition = {
      table: 'bookings',
      columns: [{ column: 'name' }, { column: 'status', format: 'badge', colors: { active: 'green' } }],
      filters: [{ column: 'status', kind: 'chips', label: 'Статус' }],
      computed: [{ key: 'n', label: 'Записів', table: 'bookings', by: 'name', aggregate: 'count' }],
      layout: 'cards',
      card: { title: 'name' },
      expand: [{ table: 'bookings', by: 'name', label: 'Записи' }],
      empty: 'Нічого',
    };
    http.expectOne('/api/v1/projects/p1/pages/list').flush({
      page: { id: 'l', key: 'list', name: 'Список', kind: 'list', roles: [], sortOrder: 0, updatedAt: '', definition },
      tableId: 't1',
      columns: [],
      schema: null,
      canEdit: true,
    });
    await render(fixture);

    expect(host.querySelector('[data-user-filter="0"]')).not.toBeNull();
    expect(selectValue(host.querySelector('[data-testid="b-layout"]')!)).toBe('cards');
    chooseOption(host.querySelector('[data-column-format="name"]')!.querySelectorAll('mat-select')[0], 'text');
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/list' });
    expect(put.request.body.definition).toEqual({ ...definition, columns: [{ column: 'name', format: 'text' }, definition.columns[1]] });
  });

  it('календар зі зразка: колонки добираються за типами, решта налаштовується формою без JSON', async () => {
    const { fixture, http, host } = await setup();
    await render(fixture);
    pick(host, '[data-testid="b-sample"]', 'calendar');
    await render(fixture);
    expect(host.querySelector<HTMLInputElement>('[data-testid="b-key"]')!.value).toBe('calendar');
    expect(selectValue(host.querySelector('[data-testid="b-kind"]')!)).toBe('calendar');
    expect(selectValue(host.querySelector('[data-testid="bc-start"]')!)).toBe('starts_at');

    host.querySelector<HTMLInputElement>('[data-view="month"]')!.click();
    await render(fixture);
    pick(host, '[data-testid="bc-open"]', 'booking');
    await render(fixture);
    chooseOption(host.querySelector('[data-testid="bc-open"]')!.closest('span')!.querySelectorAll('mat-select')[1], 'drawer');
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/calendar' });
    expect(put.request.body.kind).toBe('calendar');
    expect(put.request.body.definition).toEqual({
      table: 'bookings',
      calendar: {
        start: 'starts_at',
        duration: 'minutes',
        title: { column: 'name' },
        color: { column: 'status', format: 'badge', colors: { active: 'green', cancelled: 'blue' } },
        views: ['week', 'day'],
        default: 'week',
        fromHour: 8,
        toHour: 20,
        open: { page: 'booking', mode: 'drawer' },
      },
      filters: [{ column: 'status', kind: 'chips', label: 'status' }],
    });
  });

  it('головна зі зразка: блоки додаються й переставляються, кнопки блоку відкривають сторінку панеллю', async () => {
    const { fixture, http, host } = await setup();
    await render(fixture);
    pick(host, '[data-testid="b-sample"]', 'dashboard');
    await render(fixture);
    expect(host.querySelectorAll('[data-block]').length).toBe(2);

    host.querySelector<HTMLButtonElement>('[data-testid="b-add-block"]')!.click();
    await render(fixture);
    chooseOption(host.querySelector('[data-block="2"]')!.querySelectorAll('mat-select')[0], 'actions');
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="bd-add-action-2"]')!.click();
    await render(fixture);
    const action = host.querySelector('[data-block-action="0"]')!;
    const label = action.querySelector('input')!;
    label.value = 'Новий запис';
    label.dispatchEvent(new Event('input'));
    await render(fixture);
    const selects = host.querySelector('[data-block-action="0"]')!.querySelectorAll('mat-select');
    chooseOption(selects[1], 'booking');
    await render(fixture);
    chooseOption(host.querySelector('[data-block-action="0"]')!.querySelectorAll('mat-select')[2], 'drawer');
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-block="2"] button[aria-label="Вище"]')!.click();
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="b-save"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/projects/p1/pages/home' });
    const blocks = put.request.body.definition.blocks;
    expect(put.request.body.kind).toBe('dashboard');
    expect(blocks.map((b: { kind: string }) => b.kind)).toEqual(['text', 'actions', 'list']);
    expect(blocks[1].actions).toEqual([{ label: 'Новий запис', page: 'booking', mode: 'drawer' }]);
  });

  it('перегляд: чернетка йде на сервер після паузи, дані показуються тими ж компонентами, помилка — під переглядом', async () => {
    const pause = () => new Promise((resolve) => setTimeout(resolve, 600));
    {
      const { fixture, http, host } = await setup();
      await render(fixture);
      pick(host, '[data-testid="b-table"]', 'bookings');
      await render(fixture);
      host.querySelector<HTMLInputElement>('[data-column="name"]')!.click();
      await render(fixture);
      await pause();
      const req = http.expectOne({ method: 'POST', url: '/api/v1/projects/p1/pages/preview/preview' });
      expect(req.request.body.definition).toEqual({ table: 'bookings', columns: [{ column: 'name' }] });
      req.flush({
        view: {
          page: { id: 'x', key: 'preview', name: 'preview', kind: 'list', roles: [], sortOrder: 0, updatedAt: '', definition: { table: 'bookings', columns: [{ column: 'name' }] } },
          tableId: 't1',
          columns: [{ column: 'name', label: 'Імʼя', type: 'string' }],
          schema: null,
          canEdit: true,
        },
        rows: { items: [{ key: '1', data: { name: 'Оля' }, display: {}, actions: [] }], page: 1, pageSize: 10, total: 1 },
      });
      await render(fixture);
      expect(host.querySelector('[data-testid="preview-table"]')!.textContent).toContain('Оля');

      host.querySelector<HTMLInputElement>('[data-column="status"]')!.click();
      await render(fixture);
      await pause();
      http.expectOne('/api/v1/projects/p1/pages/preview/preview').flush({ title: 'Колонку не знайдено' }, { status: 422, statusText: 'Unprocessable' });
      await render(fixture);
      expect(host.querySelector('[data-testid="preview-error"]')!.textContent).toContain('Колонку не знайдено');
      expect(host.querySelector('[data-testid="preview-table"]')).not.toBeNull();
    }
  });
});
