import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  async function setup(returnUrl: string | null = '/executions') {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(returnUrl ? { returnUrl } : {}) } } },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(LoginPage);
    await render(fixture);
    return { fixture, navigate, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function type(host: HTMLElement, testId: string, value: string) {
    const input = host.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function submit(host: HTMLElement) {
    host.querySelector<HTMLButtonElement>('[data-testid="login-submit"]')!.click();
  }

  it('форма українською: email, пароль, «Увійти»', async () => {
    const { host } = await setup();
    expect(host.textContent).toContain('Вхід до OpenBP');
    expect(host.querySelector('[data-testid="login-password"]')?.getAttribute('type')).toBe('password');
    expect(host.querySelector('[data-testid="login-submit"]')?.textContent).toContain('Увійти');
  });

  it('успішний вхід веде на returnUrl', async () => {
    const { fixture, host, http, navigate } = await setup('/executions');
    type(host, 'login-email', ' admin@firma.ua ');
    type(host, 'login-password', 'Admin-pass-123');
    submit(host);

    const login = http.expectOne({ method: 'POST', url: '/api/v1/auth/login' });
    expect(login.request.body).toEqual({ email: 'admin@firma.ua', password: 'Admin-pass-123' });
    login.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/v1/auth/me').flush({ id: 'u1', email: 'admin@firma.ua', role: 'admin' });
    await render(fixture);

    expect(navigate).toHaveBeenCalledWith('/executions');
    expect(TestBed.inject(AuthService).user()?.email).toBe('admin@firma.ua');
  });

  it('зовнішній returnUrl ігнорується — на головну', async () => {
    const { fixture, host, http, navigate } = await setup('https://evil.example');
    type(host, 'login-email', 'admin@firma.ua');
    type(host, 'login-password', 'x');
    submit(host);
    http.expectOne('/api/v1/auth/login').flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/v1/auth/me').flush({ id: 'u1', email: 'admin@firma.ua', role: 'admin' });
    await render(fixture);
    expect(navigate).toHaveBeenCalledWith('/');
  });

  it('невірний пароль — повідомлення, пароль очищено, переходу немає', async () => {
    const { fixture, host, http, navigate } = await setup();
    type(host, 'login-email', 'admin@firma.ua');
    type(host, 'login-password', 'wrong');
    submit(host);
    http.expectOne('/api/v1/auth/login').flush({ code: 'UNAUTHENTICATED' }, { status: 401, statusText: 'Unauthorized' });
    await render(fixture);

    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain('Невірний email або пароль');
    expect(host.querySelector<HTMLInputElement>('[data-testid="login-password"]')!.value).toBe('');
    expect(navigate).not.toHaveBeenCalled();
  });

  it('сервер недоступний — інше повідомлення', async () => {
    const { fixture, host, http } = await setup();
    type(host, 'login-email', 'admin@firma.ua');
    type(host, 'login-password', 'x');
    submit(host);
    http.expectOne('/api/v1/auth/login').flush({}, { status: 502, statusText: 'Bad Gateway' });
    await render(fixture);
    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain('Сервер недоступний');
  });

  it('порожні поля — без запиту', async () => {
    const { fixture, host, http } = await setup();
    submit(host);
    await render(fixture);
    http.expectNone('/api/v1/auth/login');
    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain('Введіть email і пароль');
  });
});
