import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { WorkflowDetail, WorkflowSummary, emptyWorkflowDefinition } from '../../workflows/workflows-api.service';
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

  async function setup(items: WorkflowSummary[] = [draft, live]) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(WorkflowsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush({ items, page: 1, pageSize: 200, total: items.length });
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
    TestBed.inject(HttpTestingController)
      .expectOne((r) => r.url === '/api/v1/workflows')
      .flush(null, { status: 0, statusText: 'Unknown Error' });
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
});
