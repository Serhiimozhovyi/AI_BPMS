import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from './app';
import { routes } from './app.routes';
import { authInterceptor } from './auth/auth.interceptor';
import { AuthService, UserInfo } from './auth/auth.service';

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
  it('/login без guard\'а, сторінки з architecture.md §7 — під guard\'ом', () => {
    expect(routes[0].path).toBe('login');
    expect(routes[0].canActivateChild).toBeUndefined();
    const app = routes.find((r) => r.children)!;
    expect(app.canActivateChild).toHaveLength(1);
    expect(app.children!.filter((r) => r.loadComponent).map((r) => r.path)).toEqual([
      '',
      'workflows',
      'workflows/:id',
      'executions',
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
