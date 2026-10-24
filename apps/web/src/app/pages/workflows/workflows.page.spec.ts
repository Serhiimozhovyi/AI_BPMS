import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { GROUPS_COLLAPSE_KEY } from '../../workflows/workflow-groups';
import { WorkflowDetail, WorkflowGroup, WorkflowSummary, emptyWorkflowDefinition } from '../../workflows/workflows-api.service';
import { WorkflowsPage } from './workflows.page';

const draft: WorkflowSummary = { id: 'w1', name: 'Заявки', active: false, version: 2, updatedAt: '2026-10-23T10:00:00Z' };
const live: WorkflowSummary = { id: 'w2', name: 'Розсилка', active: true, version: 5, updatedAt: '2026-10-22T08:00:00Z' };

function detailOf(s: WorkflowSummary): WorkflowDetail {
  return { ...s, versionId: `v-${s.id}`, definition: emptyWorkflowDefinition(s.name) };
}

describe('WorkflowsPage', () => {
  async function render(fixture: ComponentFixture<WorkflowsPage>) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function setup(items: WorkflowSummary[] = [draft, live], groups: WorkflowGroup[] = [], queryParams: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } },
      ],
    });
    const fixture = TestBed.createComponent(WorkflowsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush({ items, page: 1, pageSize: 200, total: items.length });
    http.expectOne('/api/v1/workflow-groups').flush(groups);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  const row = (host: HTMLElement, id: string) => host.querySelector<HTMLElement>(`[data-workflow="${id}"]`)!;
  const toggleInput = (host: HTMLElement, id: string) =>
    row(host, id).querySelector<HTMLButtonElement>('[data-testid="active-toggle"] button[role="switch"]')!;

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує список сценаріїв з посиланням у канвас', async () => {
    const { host } = await setup();
    expect(host.querySelector('h1')?.textContent).toContain('Сценарії');
    expect(host.querySelectorAll('[data-workflow]')).toHaveLength(2);
    const link = row(host, 'w1').querySelector<HTMLAnchorElement>('[data-testid="open-link"]')!;
    expect(link.textContent).toContain('Заявки');
    expect(link.getAttribute('href')).toBe('/workflows/w1');
    expect(toggleInput(host, 'w1').getAttribute('aria-checked')).toBe('false');
    expect(toggleInput(host, 'w2').getAttribute('aria-checked')).toBe('true');
  });

  it('порожній список — підказка', async () => {
    const { host } = await setup([]);
    expect(host.querySelector('[data-testid="empty"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="workflows-table"]')).toBeNull();
  });

  it('помилка завантаження показується', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    const fixture = TestBed.createComponent(WorkflowsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush(null, { status: 0, statusText: 'Unknown Error' });
    http.match('/api/v1/workflow-groups').forEach((r) => (r.cancelled ? null : r.flush([])));
    await render(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="page-error"]')?.textContent).toContain('недоступний');
  });

  it('створення: POST з назвою, рядок у списку і перехід у канвас', async () => {
    const { fixture, http, host } = await setup([]);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    host.querySelector<HTMLButtonElement>('[data-testid="create-button"]')!.click();
    await render(fixture);
    const input = host.querySelector<HTMLInputElement>('[data-testid="create-name"]')!;
    input.value = '  Нові замовлення ';
    input.dispatchEvent(new Event('input'));
    host.querySelector<HTMLFormElement>('[data-testid="create-form"]')!.dispatchEvent(new Event('submit'));

    const req = http.expectOne({ method: 'POST', url: '/api/v1/workflows' });
    expect(req.request.body.name).toBe('Нові замовлення');
    const created: WorkflowSummary = { id: 'w9', name: 'Нові замовлення', active: false, version: 1, updatedAt: '2026-10-23T11:00:00Z' };
    req.flush(detailOf(created), { status: 201, statusText: 'Created' });
    await render(fixture);

    expect(row(host, 'w9').textContent).toContain('Нові замовлення');
    expect(host.querySelector('[data-testid="create-form"]')).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/workflows', 'w9']);
  });

  it('створення з порожньою назвою не надсилає запит', async () => {
    const { fixture, host } = await setup([]);
    fixture.componentInstance.startCreate();
    await render(fixture);
    host.querySelector<HTMLFormElement>('[data-testid="create-form"]')!.dispatchEvent(new Event('submit'));
    await render(fixture);
    expect(host.querySelector('mat-error')?.textContent).toContain('Вкажіть назву');
  });

  it('перейменування: PATCH і оновлений рядок', async () => {
    const { fixture, http, host } = await setup();
    row(host, 'w1').querySelector<HTMLButtonElement>('[data-testid="rename-button"]')!.click();
    await render(fixture);
    const input = row(host, 'w1').querySelector<HTMLInputElement>('[data-testid="rename-input"]')!;
    expect(input.value).toBe('Заявки');
    input.value = 'Заявки з Telegram';
    input.dispatchEvent(new Event('input'));
    row(host, 'w1').querySelector<HTMLFormElement>('[data-testid="rename-form"]')!.dispatchEvent(new Event('submit'));

    const req = http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w1' });
    expect(req.request.body).toEqual({ name: 'Заявки з Telegram' });
    req.flush(detailOf({ ...draft, name: 'Заявки з Telegram' }));
    await render(fixture);

    expect(row(host, 'w1').querySelector('[data-testid="rename-form"]')).toBeNull();
    expect(row(host, 'w1').textContent).toContain('Заявки з Telegram');
  });

  it('перейменування без змін назви не надсилає запит', async () => {
    const { fixture } = await setup();
    const page = fixture.componentInstance;
    page.startRename(draft);
    page.saveRename('w1');
    expect(page.renamingId()).toBeNull();
  });

  it('активація: POST …/activate і перемикач увімкнено', async () => {
    const { fixture, http, host } = await setup();
    toggleInput(host, 'w1').click();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/w1/activate' }).flush(detailOf({ ...draft, active: true }));
    await render(fixture);
    expect(fixture.componentInstance.workflows().find((w) => w.id === 'w1')?.active).toBe(true);
    expect(toggleInput(host, 'w1').getAttribute('aria-checked')).toBe('true');
    expect(row(host, 'w1').querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.disabled).toBe(true);
  });

  it('невдала активація: помилка з Api і перемикач повертається', async () => {
    const { fixture, http, host } = await setup();
    toggleInput(host, 'w1').click();
    http
      .expectOne({ method: 'POST', url: '/api/v1/workflows/w1/activate' })
      .flush({ title: 'Validation', detail: 'Активний сценарій має містити тригер.' }, { status: 422, statusText: 'Unprocessable' });
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('тригер');
    expect(toggleInput(host, 'w1').getAttribute('aria-checked')).toBe('false');
    expect(fixture.componentInstance.workflows().find((w) => w.id === 'w1')?.active).toBe(false);
  });

  it('деактивація: POST …/deactivate', async () => {
    const { fixture, http, host } = await setup();
    toggleInput(host, 'w2').click();
    http.expectOne({ method: 'POST', url: '/api/v1/workflows/w2/deactivate' }).flush(detailOf({ ...live, active: false }));
    await render(fixture);
    expect(toggleInput(host, 'w2').getAttribute('aria-checked')).toBe('false');
  });

  it('видалення з підтвердженням: DELETE і рядок зникає', async () => {
    const { fixture, http, host } = await setup();
    row(host, 'w1').querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.click();
    await render(fixture);
    expect(row(host, 'w1').textContent).toContain('Видалити сценарій');
    row(host, 'w1').querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/workflows/w1' }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    expect(host.querySelector('[data-workflow="w1"]')).toBeNull();
    expect(host.querySelectorAll('[data-workflow]')).toHaveLength(1);
  });

  it('активний сценарій видалити не можна; 409 від Api показується', async () => {
    const { fixture, http, host } = await setup();
    expect(row(host, 'w2').querySelector<HTMLButtonElement>('[data-testid="delete-button"]')!.disabled).toBe(true);

    fixture.componentInstance.startDelete('w2');
    fixture.componentInstance.confirmDelete('w2');
    http
      .expectOne({ method: 'DELETE', url: '/api/v1/workflows/w2' })
      .flush({ title: 'Conflict', detail: 'Активний сценарій не можна видалити.' }, { status: 409, statusText: 'Conflict' });
    await render(fixture);
    expect(host.querySelector('[data-testid="page-error"]')?.textContent).toContain('не можна видалити');
    expect(row(host, 'w2')).not.toBeNull();
  });

  describe('групи (F3-64)', () => {
    const sales: WorkflowGroup = { id: 'g1', name: 'Продажі', sortOrder: 0, workflowCount: 1 };
    const finance: WorkflowGroup = { id: 'g2', name: 'Фінанси', sortOrder: 1, workflowCount: 0 };
    const inSales: WorkflowSummary = { ...draft, groupId: 'g1' };
    const sectionIds = (host: HTMLElement) => [...host.querySelectorAll<HTMLElement>('[data-section]')].map((e) => e.dataset['section']);
    const section = (host: HTMLElement, id: string) => host.querySelector<HTMLElement>(`[data-section="${id}"]`)!;

    beforeEach(() => localStorage.removeItem(GROUPS_COLLAPSE_KEY));

    it('секції груп у їхньому порядку, «Без групи» в кінці, лічильники', async () => {
      const { host } = await setup([inSales, live], [finance, sales]);
      expect(sectionIds(host)).toEqual(['g1', 'g2', 'none']);
      expect(section(host, 'g1').querySelector('[data-workflow="w1"]')).not.toBeNull();
      expect(section(host, 'none').querySelector('[data-workflow="w2"]')).not.toBeNull();
      expect(section(host, 'g2').querySelector('[data-testid="section-empty"]')).not.toBeNull();
      expect(section(host, 'g1').querySelector('[data-testid="section-count"]')?.textContent?.trim()).toBe('1');
    });

    it('без груп — один список без заголовків', async () => {
      const { host } = await setup();
      expect(host.querySelector('[data-testid="section-toggle"]')).toBeNull();
      expect(host.querySelectorAll('[data-workflow]')).toHaveLength(2);
    });

    it('чіпи і пошук: лише вибрані групи, пошук у їхніх межах; ?group= вибирає чіп', async () => {
      const { fixture, host } = await setup([inSales, live], [sales, finance], { group: 'g1' });
      expect(sectionIds(host)).toEqual(['g1']);
      fixture.componentInstance.chip('none', true);
      await render(fixture);
      expect(sectionIds(host)).toEqual(['g1', 'none']);
      fixture.componentInstance.search.set('розс');
      await render(fixture);
      expect(sectionIds(host)).toEqual(['none']);
      fixture.componentInstance.chip('none', false);
      await render(fixture);
      expect(host.querySelector('[data-testid="nothing-found"]')).not.toBeNull();
    });

    it('згортання секції пам’ятається в localStorage', async () => {
      const { fixture, host } = await setup([inSales], [sales]);
      section(host, 'g1').querySelector<HTMLButtonElement>('[data-testid="section-toggle"]')!.click();
      await render(fixture);
      expect(section(host, 'g1').querySelector('table')!.hidden).toBe(true);
      expect(JSON.parse(localStorage.getItem(GROUPS_COLLAPSE_KEY)!)).toEqual({ g1: true });
    });

    it('перенесення: PATCH { groupId }, рядок у новій секції, лічильники перераховано; помилка повертає', async () => {
      const { fixture, http, host } = await setup([inSales, live], [sales, finance]);
      fixture.componentInstance.move(live, 'g2');
      await render(fixture);
      expect(section(host, 'g2').querySelector('[data-workflow="w2"]')).not.toBeNull();
      const req = http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w2' });
      expect(req.request.body).toEqual({ groupId: 'g2' });
      req.flush({ ...detailOf(live), groupId: 'g2' });
      await render(fixture);
      expect(fixture.componentInstance.groups().find((g) => g.id === 'g2')?.workflowCount).toBe(1);

      fixture.componentInstance.move({ ...inSales }, null);
      http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w1' }).flush({ title: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
      await render(fixture);
      expect(section(host, 'g1').querySelector('[data-workflow="w1"]')).not.toBeNull();
    });

    it('перетягування рядка в іншу секцію переносить сценарій', async () => {
      const { fixture, http } = await setup([inSales, live], [sales]);
      fixture.componentInstance.workflowDropped({
        previousContainer: { data: 'none' },
        container: { data: 'g1' },
        item: { data: live },
      } as never);
      expect(http.expectOne({ method: 'PATCH', url: '/api/v1/workflows/w2' }).request.body).toEqual({ groupId: 'g1' });
    });

    it('нова група; «+ Сценарій» у секції створює сценарій у цій групі', async () => {
      const { fixture, http, host } = await setup([inSales], [sales]);
      vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
      host.querySelector<HTMLButtonElement>('[data-testid="new-group-button"]')!.click();
      await render(fixture);
      const input = host.querySelector<HTMLInputElement>('[data-testid="group-name"]')!;
      input.value = ' Склад ';
      input.dispatchEvent(new Event('input'));
      host.querySelector<HTMLFormElement>('[data-testid="group-form"]')!.dispatchEvent(new Event('submit'));
      const created = http.expectOne({ method: 'POST', url: '/api/v1/workflow-groups' });
      expect(created.request.body).toEqual({ name: 'Склад' });
      created.flush({ id: 'g3', name: 'Склад', sortOrder: 1, workflowCount: 0 });
      await render(fixture);
      expect(sectionIds(host)).toEqual(['g1', 'g3', 'none']);

      section(host, 'g3').querySelector<HTMLButtonElement>('[data-testid="section-create"]')!.click();
      await render(fixture);
      expect(host.querySelector('[data-testid="create-group-hint"]')?.textContent).toContain('Склад');
      const name = host.querySelector<HTMLInputElement>('[data-testid="create-name"]')!;
      name.value = 'Залишки';
      name.dispatchEvent(new Event('input'));
      host.querySelector<HTMLFormElement>('[data-testid="create-form"]')!.dispatchEvent(new Event('submit'));
      const post = http.expectOne((r) => r.method === 'POST' && r.url === '/api/v1/workflows');
      expect(post.request.params.get('groupId')).toBe('g3');
    });

    it('видалення групи: підтвердження з кількістю, сценарії — без групи', async () => {
      const { fixture, http, host } = await setup([inSales], [sales]);
      fixture.componentInstance.deletingGroupId.set('g1');
      await render(fixture);
      expect(section(host, 'g1').querySelector('[data-testid="group-delete-text"]')?.textContent).toContain('1');
      section(host, 'g1').querySelector<HTMLButtonElement>('[data-testid="group-delete-confirm"]')!.click();
      http.expectOne({ method: 'DELETE', url: '/api/v1/workflow-groups/g1' }).flush(null, { status: 204, statusText: 'No Content' });
      await render(fixture);
      expect(fixture.componentInstance.groups()).toEqual([]);
      expect(fixture.componentInstance.workflows()[0].groupId).toBeNull();
    });

    it('порядок груп: PUT /workflow-groups/order з новим порядком', async () => {
      const { fixture, http } = await setup([], [sales, finance]);
      fixture.componentInstance.sectionDropped({ previousIndex: 1, currentIndex: 0 } as never);
      const req = http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/order' });
      expect(req.request.body).toEqual({ ids: ['g2', 'g1'] });
      expect(fixture.componentInstance.orderedGroups().map((g) => g.id)).toEqual(['g2', 'g1']);
    });
  });
});
