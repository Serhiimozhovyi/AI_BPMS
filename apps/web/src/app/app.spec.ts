import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App, isBareUrl } from './app';
import { formMatcher, routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { AuthService, UserInfo } from './auth/auth.service';
import { ProjectContextService } from './projects/project-context.service';

const admin: UserInfo = { id: 'u1', email: 'admin@firma.ua', role: 'admin' };

/** Маршрути з guard'ом: користувач уже ввійшов (або ні — `user: null`). */
function configure(user: UserInfo | null = admin) {
  TestBed.configureTestingModule({
    imports: [App],
    providers: [provideRouter(routes), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
  });
  TestBed.inject(AuthService).user.set(user);
}

describe('App', () => {
  it('публічна форма /form/… — без шапки й меню і без входу (F6-03)', async () => {
    expect(isBareUrl('/form/hr/leave?token=1')).toBe(true);
    expect(isBareUrl('/workflows')).toBe(false);
    expect(formMatcher([{ path: 'form' }, { path: 'hr' }, { path: 'leave' }] as never, null as never, null as never)?.consumed.length).toBe(3);
    expect(formMatcher([{ path: 'form' }] as never, null as never, null as never)).toBeNull();

    configure(null);
    const fixture = TestBed.createComponent(App);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/form/hr/leave');
    TestBed.inject(HttpTestingController).expectOne('/api/v1/forms/hr/leave');
    fixture.detectChanges();
    await fixture.whenStable();
    expect(TestBed.inject(Router).url).toBe('/form/hr/leave');
    expect((fixture.nativeElement as HTMLElement).querySelector('mat-toolbar')).toBeNull();
  });

  it('створюється', () => {
    configure();
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('показує навігацію українською, email і «Вийти» після входу', async () => {
    configure();
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const nav = host.querySelector('nav');
    expect(nav?.textContent).toContain('Сценарії');
    expect(nav?.textContent).toContain('Конектори');
    expect(host.querySelector('[data-testid="current-user"]')?.textContent).toContain('admin@firma.ua');
    expect(host.querySelector('[data-testid="logout"]')?.textContent).toContain('Вийти');
  });

  it('без входу навігації й кнопки виходу немає', async () => {
    configure(null);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('nav')).toBeNull();
    expect(host.querySelector('[data-testid="logout"]')).toBeNull();
  });

  it('«Вийти» закриває сесію і веде на /login', async () => {
    configure();
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('[data-testid="logout"]')!.click();
    TestBed.inject(HttpTestingController).expectOne({ method: 'POST', url: '/api/v1/auth/logout' }).flush(null, { status: 204, statusText: 'No Content' });

    expect(TestBed.inject(AuthService).user()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login']);
  });
});

describe('routes', () => {
  it('/login і публічна форма без guard\'а, сторінки з architecture.md §7 — під guard\'ом', () => {
    expect(routes[0].matcher).toBe(formMatcher);
    expect(routes[0].canActivateChild).toBeUndefined();
    expect(routes[1].path).toBe('login');
    expect(routes[1].canActivateChild).toBeUndefined();
    const app = routes.find((r) => r.children)!;
    expect(app.canActivateChild).toHaveLength(1);
    expect(app.children!.filter((r) => r.loadComponent).map((r) => r.path)).toEqual([
      '',
      'workflows',
      'workflows/:id',
      'projects/:id',
      'executions',
      'tasks',
      'tasks/:id',
      'tables',
      'connectors',
      'credentials',
      'settings',
    ]);
  });

  it('/workflows показує заголовок українською', async () => {
    configure();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/workflows');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Сценарії');
  });

  it('порожній шлях відкриває «Огляд»', async () => {
    configure();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Огляд');
  });

  it('кожен маршрут відкриває сторінку з заголовком', async () => {
    configure();
    const harness = await RouterTestingHarness.create();
    for (const url of ['/workflows/abc', '/executions', '/connectors', '/credentials', '/settings']) {
      await harness.navigateByUrl(url);
      expect(harness.routeNativeElement?.querySelector('h1')?.textContent?.trim()).toBeTruthy();
    }
  });

  it('без сесії сторінка веде на /login з returnUrl', async () => {
    configure(null);
    const harness = await RouterTestingHarness.create();
    const navigation = harness.navigateByUrl('/executions');
    const me = await vi.waitFor(() => TestBed.inject(HttpTestingController).expectOne('/api/v1/auth/me'));
    me.flush({}, { status: 401, statusText: 'Unauthorized' });
    await navigation;
    expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fexecutions');
    expect(harness.routeNativeElement?.querySelector('[data-testid="login-form"]')).not.toBeNull();
  });
});

describe('App: вибір проєкту (F6-14)', () => {
  it('шапка показує доступні проєкти, вибір змінює контекст і відкриває учасників', async () => {
    configure();
    localStorage.removeItem('openbp.project');
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    TestBed.inject(HttpTestingController)
      .expectOne({ method: 'GET', url: '/api/v1/workflow-groups' })
      .flush([{ id: 'p1', name: 'Продажі', sortOrder: 0, workflowCount: 1, role: 'editor' }]);
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const select = host.querySelector<HTMLSelectElement>('[data-testid="project-select"]')!;
    expect([...select.options].map((o) => o.textContent?.trim())).toEqual(['Усі проєкти', 'Спільний простір', 'Продажі']);
    expect(host.querySelector('[data-testid="project-members"]')).toBeNull();

    select.value = 'p1';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(TestBed.inject(ProjectContextService).current()).toBe('p1');
    expect(host.querySelector('[data-testid="project-members"]')?.getAttribute('href')).toBe('/projects/p1');
    localStorage.removeItem('openbp.project');
  });
});
