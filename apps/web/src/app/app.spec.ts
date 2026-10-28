import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { BreakpointObserver } from '@angular/cdk/layout';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App, isBareUrl } from './app';
import { formMatcher, routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { ConnectivityService } from './connectivity/connectivity.service';
import { AuthService, UserInfo } from './auth/auth.service';
import { BehaviorSubject } from 'rxjs';
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
    expect(nav?.textContent).toContain('Підключення');
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
      'projects',
      'projects/:id',
      undefined, // /executions[/:id] — matcher (F3-81)
      'tasks',
      'tasks/:id',
      'tables',
      'connections',
      'settings',
      '**',
    ]);
  });

  it('/workflows показує заголовок українською', async () => {
    configure();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/workflows');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Сценарії');
  });

  it('невідома адреса показує «Не знайдено» з посиланням на «Огляд» (F3-84)', async () => {
    configure();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/no/such/page');
    expect(TestBed.inject(Router).url).toBe('/no/such/page');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Сторінку не знайдено');
    expect(harness.routeNativeElement?.querySelector('[data-testid="not-found-home"]')?.getAttribute('href')).toBe('/');
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
    host.querySelector<HTMLElement>('[data-testid="project-select"] .mat-mdc-select-trigger')!.click();
    fixture.detectChanges();
    const options = [...document.querySelectorAll<HTMLElement>('mat-option')];
    expect(options.map((o) => o.textContent?.trim())).toEqual(['Усі проєкти', 'Спільний простір', 'Продажі']);
    expect(host.querySelector('[data-testid="project-members"]')).toBeNull();
    expect(host.querySelector('select')).toBeNull();

    options[2].click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(TestBed.inject(ProjectContextService).current()).toBe('p1');
    expect(host.querySelector('[data-testid="project-members"]')?.getAttribute('href')).toBe('/projects/p1/members');
    localStorage.removeItem('openbp.project');
  });

  it('користувач лише з роллю застосунку: з головної — у застосунок проєкту, меню «Застосунок» і «Огляд» (F8-04)', async () => {
    configure({ id: 'u9', email: 'operator@firma.ua', role: 'viewer' });
    const fixture = TestBed.createComponent(App);
    const harness = await RouterTestingHarness.create();
    const http = TestBed.inject(HttpTestingController);
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    await harness.navigateByUrl('/');
    http
      .expectOne({ method: 'GET', url: '/api/v1/workflow-groups' })
      .flush([{ id: 'p1', name: 'Психологічна підтримка', sortOrder: 0, workflowCount: 2, role: 'viewer', appRole: 'operator' }]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(navigate).toHaveBeenCalledWith(['/projects', 'p1', 'app'], { replaceUrl: true });
    const nav = (fixture.nativeElement as HTMLElement).querySelector('nav')!;
    expect([...nav.querySelectorAll('a')].map((a) => a.textContent?.trim())).toEqual(['appsЗастосунок', 'monitoringОгляд']);
    expect(nav.querySelector('[data-testid="nav-app"]')?.getAttribute('href')).toBe('/projects/p1/app');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="project-select"]')).toBeNull();
  });

  it('viewer з кількома проєктами чи без ролі застосунку бачить звичайне меню', async () => {
    configure({ id: 'u8', email: 'viewer@firma.ua', role: 'viewer' });
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    TestBed.inject(HttpTestingController)
      .expectOne({ method: 'GET', url: '/api/v1/workflow-groups' })
      .flush([{ id: 'p1', name: 'Продажі', sortOrder: 0, workflowCount: 1, role: 'viewer' }]);
    fixture.detectChanges();
    await fixture.whenStable();
    expect((fixture.nativeElement as HTMLElement).querySelector('nav')?.textContent).toContain('Сценарії');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="nav-app"]')).toBeNull();
  });
});

describe('App: телефон і зв’язок (F3-84)', () => {
  function configureMobile(matches: boolean) {
    const state = new BehaviorSubject({ matches, breakpoints: {} });
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: BreakpointObserver, useValue: { observe: () => state } },
      ],
    });
    TestBed.inject(AuthService).user.set(admin);
    return state;
  }

  it('на телефоні навігація й «Вийти» — у drawer за бургером', async () => {
    configureMobile(true);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('.shell-nav')).toBeNull();
    expect(host.querySelector('[data-testid="logout"]')).toBeNull();

    host.querySelector<HTMLButtonElement>('[data-testid="nav-burger"]')!.click();
    fixture.detectChanges();
    const drawer = host.querySelector('[data-testid="nav-drawer"]')!;
    expect(drawer.textContent).toContain('Сценарії');
    expect(drawer.querySelector('[data-testid="logout"]')).not.toBeNull();
    expect(drawer.querySelector('[data-testid="project-select"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="current-user"]')?.textContent).toContain('admin@firma.ua');

    host.querySelector<HTMLElement>('[data-testid="drawer-backdrop"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="nav-drawer"]')).toBeNull();
  });

  it('перехід за пунктом drawer закриває його', async () => {
    configureMobile(true);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector<HTMLButtonElement>('[data-testid="nav-burger"]')!.click();
    fixture.detectChanges();
    await TestBed.inject(Router).navigateByUrl('/workflows');
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="nav-drawer"]')).toBeNull();
  });

  it('на десктопі бургера немає, навігація в шапці', async () => {
    configureMobile(false);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="nav-burger"]')).toBeNull();
    expect(host.querySelector('.shell-nav')).not.toBeNull();
  });

  it('кнопка загального помічника в шапці (F3-110): відкриває панель; для viewer і на публічній формі її немає', async () => {
    configureMobile(false);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="global-assistant"]')).toBeNull();
    host.querySelector<HTMLButtonElement>('[data-testid="global-assistant-toggle"]')!.click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="global-assistant"]')).not.toBeNull();
    expect(host.querySelector('[data-testid="global-assistant-toggle"]')?.getAttribute('aria-pressed')).toBe('true');

    TestBed.inject(AuthService).user.set({ id: 'u2', email: 'v@firma.ua', role: 'viewer' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="global-assistant-toggle"]')).toBeNull();
    expect(host.querySelector('[data-testid="global-assistant"]')).toBeNull();
  });

  it('на телефоні кнопка помічника — поруч із бургером', async () => {
    configureMobile(true);
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const toggle = host.querySelector<HTMLButtonElement>('[data-testid="global-assistant-toggle"]')!;
    expect(toggle.classList).toContain('shell-assistant--mobile');
    expect(toggle.nextElementSibling?.getAttribute('data-testid')).toBe('nav-burger');
  });

  it('банер «Немає зв’язку» з «Повторити» з’являється, коли Api недоступний', async () => {
    configure();
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="offline-banner"]')).toBeNull();

    const connectivity = TestBed.inject(ConnectivityService);
    connectivity.offline.set(true);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="offline-banner"]')?.textContent).toContain('Немає зв’язку із сервером OpenBP');
    const retry = vi.spyOn(connectivity, 'retry').mockImplementation(() => undefined);
    host.querySelector<HTMLButtonElement>('[data-testid="offline-retry"]')!.click();
    expect(retry).toHaveBeenCalled();
  });
});
