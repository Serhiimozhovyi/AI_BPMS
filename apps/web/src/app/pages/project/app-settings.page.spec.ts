import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectContextService } from '../../projects/project-context.service';
import { AppSettingsPage } from './app-settings.page';
import { chooseOption } from '../../shared/select-search/select-testing';

describe('AppSettingsPage (F8-19)', () => {
  async function setup(app: object | null) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { parent: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } } },
      ],
    });
    TestBed.inject(ProjectContextService).projects.set([{ id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 1, role: 'editor', app } as never]);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(AppSettingsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/projects/p1/pages').flush([
      { id: 'a', key: 'today', name: 'Сьогодні', kind: 'dashboard', definition: { table: '' }, roles: [], sortOrder: 0, updatedAt: '' },
      { id: 'b', key: 'calendar', name: 'Розклад', kind: 'calendar', definition: { table: 'slots' }, roles: [], sortOrder: 1, updatedAt: '' },
      { id: 'c', key: 'slot-edit', name: 'Слот — зміна', kind: 'form', definition: { table: 'slots', fields: [], onSubmit: { operation: 'update' } }, roles: [], sortOrder: 2, updatedAt: '' },
    ]);
    await render(fixture);
    return { fixture, http, navigate, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function put(host: HTMLElement, selector: string, value: string, event = 'input') {
    const el = host.querySelector<HTMLInputElement>(selector)!;
    if (el.tagName === 'MAT-SELECT') return chooseOption(el, value);
    el.value = value;
    el.dispatchEvent(new Event(event));
  }

  it('без оболонки: назва за замовчуванням — назва проєкту; зберігає назву, головну, пояс і групи меню', async () => {
    const { fixture, http, host } = await setup(null);
    expect(host.querySelector<HTMLInputElement>('[data-testid="as-title"]')!.value).toBe('Психологи');
    expect(host.querySelector('[data-testid="as-clear"]')).toBeNull();

    put(host, '[data-testid="as-title"]', 'Астарта · Психологи');
    put(host, '[data-testid="as-icon"]', 'psychology');
    put(host, '[data-testid="as-home"]', 'today', 'change');
    put(host, '[data-testid="as-tz"]', 'Europe/Kyiv');
    host.querySelector<HTMLButtonElement>('[data-testid="as-add-group"]')!.click();
    await render(fixture);
    put(host, '[data-nav-group="0"] input[type="text"], [data-nav-group="0"] input:not([type])', 'Робота');
    host.querySelector<HTMLInputElement>('[data-nav-page="calendar"]')!.click();
    host.querySelector<HTMLInputElement>('[data-nav-page="today"]')!.click();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="as-save"]')!.click();

    const request = http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/p1/app' });
    expect(request.request.body).toEqual({
      title: 'Астарта · Психологи',
      icon: 'psychology',
      home: 'today',
      timezone: 'Europe/Kyiv',
      nav: [{ label: 'Робота', pages: ['today', 'calendar'] }],
    });
    request.flush({});
    http.expectOne('/api/v1/workflow-groups').flush([]);
    await render(fixture);
    expect(host.querySelector('[data-testid="appset-saved"]')).not.toBeNull();
  });

  it('з оболонкою: поля заповнені, порожні групи не зберігаються, «Прибрати оболонку» повертає до списку', async () => {
    const { fixture, http, host, navigate } = await setup({ title: 'Кабінет', icon: 'home', home: 'today', timezone: 'Europe/Kyiv', nav: [{ label: 'Робота', pages: ['today'] }] });
    expect(host.querySelector<HTMLInputElement>('[data-testid="as-title"]')!.value).toBe('Кабінет');
    expect(host.querySelector<HTMLInputElement>('[data-nav-page="today"]')!.checked).toBe(true);
    host.querySelector<HTMLButtonElement>('[data-testid="as-add-group"]')!.click();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="as-save"]')!.click();
    const request = http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/p1/app' });
    expect(request.request.body.nav).toEqual([{ label: 'Робота', pages: ['today'] }]);
    request.flush({});
    http.expectOne('/api/v1/workflow-groups').flush([]);

    vi.spyOn(window, 'confirm').mockReturnValue(true);
    host.querySelector<HTMLButtonElement>('[data-testid="as-clear"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/workflow-groups/p1/app' }).flush(null);
    http.expectOne('/api/v1/workflow-groups').flush([]);
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app']);
  });

  it('група без назви зберігається (пункти без підпису), службові сторінки в меню не пропонуються', async () => {
    const { fixture, http, host } = await setup({ title: 'Кабінет', nav: [{ label: '', pages: ['today', 'calendar'] }, { label: 'Записи', pages: ['slot-edit'] }] });
    expect(host.querySelector('[data-nav-page="slot-edit"]')).toBeNull();
    await render(fixture);
    host.querySelector<HTMLButtonElement>('[data-testid="as-save"]')!.click();
    const request = http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/p1/app' });
    expect(request.request.body.nav).toEqual([{ label: '', pages: ['today', 'calendar'] }]);
  });

  it('помилка Api показується над формою', async () => {
    const { fixture, http, host } = await setup(null);
    host.querySelector<HTMLButtonElement>('[data-testid="as-save"]')!.click();
    http.expectOne({ method: 'PUT', url: '/api/v1/workflow-groups/p1/app' }).flush({ detail: 'Часовий пояс невідомий' }, { status: 422, statusText: 'Unprocessable' });
    await render(fixture);
    expect(host.querySelector('[data-testid="appset-error"]')!.textContent).toContain('Часовий пояс невідомий');
  });
});
