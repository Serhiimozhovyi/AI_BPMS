import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService, UserInfo } from '../../auth/auth.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { DataRow, DataTableSummary } from '../../tables/data-tables-api.service';
import { WorkflowGroup } from '../../workflows/workflows-api.service';
import { TablesPage } from './tables.page';

const clients: DataTableSummary = {
  id: 't1',
  name: 'Клієнти',
  columns: [
    { name: 'email', type: 'string', key: true },
    { name: 'visits', type: 'number' },
    { name: 'tags', type: 'json' },
  ],
  rowCount: 2,
  updatedAt: '2026-10-24T10:00:00Z',
};
const rows: DataRow[] = [
  { key: 'a@x.ua', data: { email: 'a@x.ua', visits: 3, tags: ['Київ'] }, createdAt: '', updatedAt: '' },
  { key: 'b@x.ua', data: { email: 'b@x.ua', visits: 1 }, createdAt: '', updatedAt: '' },
];
const booking: DataRow = { key: 'r1', data: { slot_id: 's1', status: 'active', phone: null }, createdAt: '', updatedAt: '' };
const page = (items: DataRow[], total = items.length) => ({ items, page: 1, pageSize: 50, total });

const psy: WorkflowGroup = { id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 0, role: 'editor' };
const other: WorkflowGroup = { id: 'p2', name: 'Продажі', sortOrder: 1, workflowCount: 0, role: 'viewer' };
const bookings: DataTableSummary = {
  id: 't3',
  name: 'Записи',
  projectId: 'p2',
  canReadSensitive: false,
  columns: [
    { name: 'slot_id', type: 'string' },
    { name: 'status', type: 'enum', values: ['active', 'cancelled'] },
    { name: 'phone', type: 'string', sensitive: true },
  ],
  constraints: {
    required: ['slot_id'],
    unique: [{ columns: ['slot_id'], where: [{ column: 'status', operator: 'eq', value: 'active' }] }],
  },
  rowCount: 1,
  updatedAt: '2026-10-28T10:00:00Z',
};

describe('TablesPage', () => {
  async function setup(role: UserInfo['role'], list: DataTableSummary[] = [clients], projects: WorkflowGroup[] = [], current: string | null = null) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@openbp.test', role });
    const context = TestBed.inject(ProjectContextService);
    context.projects.set(projects);
    context.select(current);
    const fixture = TestBed.createComponent(TablesPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: '/api/v1/tables' }).flush(list);
    const first = fixture.componentInstance.visibleTables()[0];
    if (first) {
      http.expectOne((r) => r.url === `/api/v1/tables/${first.id}/rows`).flush(page(first === bookings ? [booking] : rows));
    }
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function type(host: HTMLElement, selector: string, value: string, event = 'input') {
    const input = host.querySelector<HTMLInputElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event(event));
  }

  function click(host: HTMLElement, testid: string) {
    host.querySelector<HTMLButtonElement>(`[data-testid="${testid}"]`)!.click();
  }

  afterEach(() => {
    TestBed.inject(HttpTestingController).verify();
    TestBed.inject(ProjectContextService).select(null);
  });

  it('viewer бачить таблиці й рядки без форм', async () => {
    const { host } = await setup('viewer');
    expect(host.querySelector('[data-table="Клієнти"]')?.textContent).toContain('2');
    const cells = host.querySelectorAll('[data-row="a@x.ua"] td');
    expect(cells[1].textContent?.trim()).toBe('3');
    expect(cells[2].textContent?.trim()).toBe('["Київ"]');
    expect(host.querySelector('[data-testid="row-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="table-add"]')).toBeNull();
    expect(host.querySelector('[data-testid="import-form"]')).toBeNull();
  });

  it('порожній список — підказка', async () => {
    const { host } = await setup('editor', []);
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
  });

  it('editor створює таблицю: перевірка колонок до запиту, потім POST', async () => {
    const { fixture, http, host } = await setup('editor');
    click(host, 'table-add');
    await render(fixture);
    type(host, '[data-testid="table-name"]', 'Замовлення');
    click(host, 'column-add');
    await render(fixture);
    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-name"]', '1bad');
    click(host, 'table-save');
    await render(fixture);
    expect(host.querySelector('[data-testid="table-form-error"]')?.textContent).toContain('1bad');

    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-name"]', 'total');
    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-type"]', 'number', 'change');
    click(host, 'table-save');
    const req = http.expectOne({ method: 'POST', url: '/api/v1/tables' });
    expect(req.request.body).toEqual({
      name: 'Замовлення',
      columns: [
        { name: 'id', type: 'string', key: true },
        { name: 'total', type: 'number' },
      ],
    });
    req.flush({ ...clients, id: 't2', name: 'Замовлення', columns: req.request.body.columns, rowCount: 0 });
    http.expectOne((r) => r.url === '/api/v1/tables/t2/rows').flush(page([]));
    await render(fixture);
    expect(host.querySelector('[data-testid="table-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="rows-empty"]')).not.toBeNull();
    expect(host.querySelectorAll('[data-table]')).toHaveLength(2);
  });

  it('editor додає рядок: типи з полів, порожні не надсилаються', async () => {
    const { fixture, http, host } = await setup('editor');
    type(host, '[data-input="email"]', 'c@x.ua');
    type(host, '[data-input="visits"]', '7');
    click(host, 'row-save');
    const req = http.expectOne({ method: 'POST', url: '/api/v1/tables/t1/rows' });
    expect(req.request.body).toEqual({ values: { email: 'c@x.ua', visits: 7 } });
    req.flush(rows[0]);
    http.expectOne((r) => r.url === '/api/v1/tables/t1/rows').flush(page(rows));
    await render(fixture);
  });

  it('некоректний JSON у полі — помилка без запиту', async () => {
    const { fixture, host } = await setup('editor');
    type(host, '[data-input="email"]', 'c@x.ua');
    type(host, '[data-input="tags"]', '[');
    click(host, 'row-save');
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('tags');
  });

  it('editor змінює рядок: PATCH без ключа, порожнє поле очищає значення', async () => {
    const { fixture, http, host } = await setup('editor');
    host.querySelector<HTMLButtonElement>('[data-row="a@x.ua"] [data-testid="row-edit"]')!.click();
    await render(fixture);
    expect(host.querySelector<HTMLInputElement>('[data-input="email"]')!.readOnly).toBe(true);
    type(host, '[data-input="visits"]', '4');
    type(host, '[data-input="tags"]', '');
    click(host, 'row-save');
    const req = http.expectOne({ method: 'PATCH', url: '/api/v1/tables/t1/rows/a%40x.ua' });
    expect(req.request.body).toEqual({ values: { visits: 4, tags: null } });
    req.flush(rows[0]);
    http.expectOne((r) => r.url === '/api/v1/tables/t1/rows').flush(page(rows));
    await render(fixture);
  });

  it('пошук передається в запит рядків', async () => {
    const { fixture, http, host } = await setup('viewer');
    type(host, '[data-testid="rows-search"]', 'київ');
    host.querySelector<HTMLInputElement>('[data-testid="rows-search"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    const req = http.expectOne((r) => r.url === '/api/v1/tables/t1/rows');
    expect(req.request.params.get('search')).toBe('київ');
    req.flush(page([rows[0]]));
    await render(fixture);
    expect(host.querySelectorAll('[data-row]')).toHaveLength(1);
  });

  it('імпорт CSV показує підсумок і перечитує рядки', async () => {
    const { fixture, http, host } = await setup('editor');
    type(host, '[data-testid="import-text"]', 'email;visits\nc@x.ua;2');
    await render(fixture);
    click(host, 'import-run');
    const req = http.expectOne({ method: 'POST', url: '/api/v1/tables/t1/import' });
    expect(req.request.body).toEqual({ csv: 'email;visits\nc@x.ua;2' });
    req.flush({ inserted: 1, updated: 0 });
    http.expectOne((r) => r.url === '/api/v1/tables/t1/rows').flush(page(rows, 3));
    await render(fixture);
    expect(host.querySelector('[data-testid="import-result"]')?.textContent).toContain('1');
    expect(host.querySelector('[data-table="Клієнти"]')?.textContent).toContain('3');
  });

  it('видалення таблиці — після підтвердження', async () => {
    const { fixture, http, host } = await setup('admin');
    click(host, 'table-delete');
    await render(fixture);
    click(host, 'delete-yes');
    http.expectOne({ method: 'DELETE', url: '/api/v1/tables/t1' }).flush(null);
    await render(fixture);
    expect(host.querySelector('[data-testid="table-view"]')).toBeNull();
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
  });

  it('проєкт у шапці: його таблиці й спільні, назва проєкту в списку', async () => {
    const { fixture } = await setup('editor', [clients, bookings, { ...clients, id: 't4', name: 'Психологи', projectId: 'p1' }], [psy, other], 'p1');
    const host = fixture.nativeElement as HTMLElement;
    const names = [...host.querySelectorAll('[data-table]')].map((b) => b.getAttribute('data-table'));
    expect(names).toEqual(['Клієнти', 'Психологи']);
    expect(host.querySelector('[data-table="Психологи"] [data-project]')?.textContent).toContain('Психологічна підтримка');
    expect(host.querySelector('[data-table="Клієнти"] [data-project]')?.textContent).toContain('спільна');
  });

  it('viewer проєкту: чутливі колонки приховані, правила показані, форм немає навіть з глобальною роллю editor', async () => {
    const { host } = await setup('editor', [bookings], [psy, other]);
    const cells = host.querySelectorAll('[data-row="r1"] td');
    expect(cells[1].textContent?.trim()).toBe('active');
    expect(cells[2].textContent?.trim()).toBe('приховано');
    expect(host.querySelector('[data-testid="table-rules"]')?.textContent).toContain('slot_id серед рядків, де status = active');
    expect(host.querySelector('[data-testid="row-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="table-edit"]')).toBeNull();
  });

  it('editor створює таблицю в проєкті з переліком, чутливою колонкою й правилами', async () => {
    const { fixture, http, host } = await setup('viewer', [], [psy, other], 'p1');
    click(host, 'table-add');
    await render(fixture);
    const project = host.querySelector<HTMLSelectElement>('[data-testid="table-project"]')!;
    expect([...project.options].map((o) => o.textContent?.trim())).toEqual(['Психологічна підтримка']);
    type(host, '[data-testid="table-name"]', 'Записи');
    type(host, '[data-testid="column-row"]:nth-child(1) [data-testid="column-name"]', 'slot_id');
    host.querySelector<HTMLInputElement>('[data-testid="column-row"]:nth-child(1) [data-testid="column-key"]')!.click();
    host.querySelector<HTMLInputElement>('[data-testid="column-row"]:nth-child(1) [data-testid="column-required"]')!.click();
    click(host, 'column-add');
    click(host, 'column-add');
    await render(fixture);
    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-name"]', 'status');
    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-type"]', 'enum', 'change');
    type(host, '[data-testid="column-row"]:nth-child(3) [data-testid="column-name"]', 'phone');
    await render(fixture);
    click(host, 'table-save');
    await render(fixture);
    expect(host.querySelector('[data-testid="table-form-error"]')?.textContent).toContain('status');

    type(host, '[data-testid="column-row"]:nth-child(2) [data-testid="column-values"]', 'active, cancelled');
    host.querySelector<HTMLInputElement>('[data-testid="column-row"]:nth-child(3) [data-testid="column-sensitive"]')!.click();
    click(host, 'unique-add');
    await render(fixture);
    type(host, '[data-testid="unique-columns"]', 'slot_id');
    type(host, '[data-testid="unique-where-column"]', 'status');
    type(host, '[data-testid="unique-where-value"]', 'active');
    click(host, 'table-save');

    const req = http.expectOne({ method: 'POST', url: '/api/v1/tables' });
    expect(req.request.body).toEqual({
      name: 'Записи',
      projectId: 'p1',
      columns: [
        { name: 'slot_id', type: 'string' },
        { name: 'status', type: 'enum', values: ['active', 'cancelled'] },
        { name: 'phone', type: 'string', sensitive: true },
      ],
      constraints: {
        required: ['slot_id'],
        unique: [{ columns: ['slot_id'], where: [{ column: 'status', operator: 'eq', value: 'active' }] }],
      },
    });
    req.flush({ ...bookings, projectId: 'p1', canReadSensitive: true, rowCount: 0 });
    http.expectOne((r) => r.url === '/api/v1/tables/t3/rows').flush(page([]));
    await render(fixture);
    expect(host.querySelector('[data-testid="table-form"]')).toBeNull();
  });
});
