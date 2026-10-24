import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { LoginPage } from './login.page';

describe('LoginPage', () => {
  async function setup(
    returnUrl: string | null = '/executions',
    extra: Record<string, string> = {},
  ) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap({ ...(returnUrl ? { returnUrl } : {}), ...extra }),
            },
          },
        },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(LoginPage);
    await render(fixture);
    return {
      fixture,
      navigate,
      http: TestBed.inject(HttpTestingController),
      host: fixture.nativeElement as HTMLElement,
    };
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
    expect(host.querySelector('[data-testid="login-password"]')?.getAttribute('type')).toBe(
      'password',
    );
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
    http
      .expectOne('/api/v1/auth/login')
      .flush({ code: 'UNAUTHENTICATED' }, { status: 401, statusText: 'Unauthorized' });
    await render(fixture);

    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain(
      'Невірний email або пароль',
    );
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
    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain(
      'Сервер недоступний',
    );
  });

  it('порожні поля — без запиту', async () => {
    const { fixture, host, http } = await setup();
    submit(host);
    await render(fixture);
    http.expectNone('/api/v1/auth/login');
    expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain(
      'Введіть email і пароль',
    );
  });

  describe('OIDC (F4-14)', () => {
    const providers = {
      passwordLogin: true,
      providers: [
        { key: 'kit', displayName: 'Кіт 2', loginUrl: '/api/v1/auth/oidc/kit/login' },
        { key: 'entra', displayName: 'Microsoft', loginUrl: '/api/v1/auth/oidc/entra/login' },
      ],
    };

    async function withProviders(
      body: object,
      returnUrl: string | null = '/executions',
      extra: Record<string, string> = {},
    ) {
      const ctx = await setup(returnUrl, extra);
      ctx.http.expectOne({ method: 'GET', url: '/api/v1/auth/providers' }).flush(body);
      await render(ctx.fixture);
      return ctx;
    }

    it('кнопки «Увійти через …» ведуть на Api з returnUrl, форма пароля лишається', async () => {
      const { host } = await withProviders(providers, '/workflows/w1?x=1');

      const kit = host.querySelector<HTMLAnchorElement>('[data-testid="login-provider-kit"]')!;
      expect(kit.textContent).toContain('Увійти через Кіт 2');
      expect(kit.getAttribute('href')).toBe(
        '/api/v1/auth/oidc/kit/login?returnUrl=%2Fworkflows%2Fw1%3Fx%3D1',
      );
      expect(host.querySelector('[data-testid="login-provider-entra"]')?.textContent).toContain(
        'Microsoft',
      );
      expect(host.querySelector('[data-testid="login-form"]')).not.toBeNull();
      expect(host.textContent).toContain('або');
    });

    it('зовнішній returnUrl не потрапляє в адресу провайдера', async () => {
      const { host } = await withProviders(providers, '//evil.example');

      expect(host.querySelector('[data-testid="login-provider-kit"]')?.getAttribute('href')).toBe(
        '/api/v1/auth/oidc/kit/login?returnUrl=%2F',
      );
    });

    it('вхід паролем вимкнено — лише кнопки; ?password повертає форму', async () => {
      const disabled = { ...providers, passwordLogin: false };
      const { host } = await withProviders(disabled);
      expect(host.querySelector('[data-testid="login-form"]')).toBeNull();
      expect(host.querySelector('[data-testid="login-providers"]')).not.toBeNull();
      expect(host.textContent).not.toContain('або');

      TestBed.resetTestingModule();
      const emergency = await withProviders(disabled, null, { password: '' });
      expect(emergency.host.querySelector('[data-testid="login-form"]')).not.toBeNull();
    });

    it('без провайдерів або без відповіді Api — лише форма пароля', async () => {
      const { host } = await withProviders({ passwordLogin: true, providers: [] });
      expect(host.querySelector('[data-testid="login-providers"]')).toBeNull();
      expect(host.querySelector('[data-testid="login-form"]')).not.toBeNull();

      TestBed.resetTestingModule();
      const failed = await setup();
      failed.http
        .expectOne('/api/v1/auth/providers')
        .flush({}, { status: 502, statusText: 'Bad Gateway' });
      await render(failed.fixture);
      expect(failed.host.querySelector('[data-testid="login-form"]')).not.toBeNull();
    });

    it.each([
      ['OIDC_EMAIL_NOT_ALLOWED', 'не має доступу'],
      ['OIDC_USER_NOT_FOUND', 'Попросіть адміністратора'],
      ['OIDC_FAILED', 'Не вдалося увійти через провайдера'],
    ])('?oidcError=%s — зрозуміле повідомлення', async (code, text) => {
      const { host } = await withProviders(providers, null, { oidcError: code });

      expect(host.querySelector('[data-testid="login-oidc-error"]')?.textContent).toContain(text);
    });

    it('403 на вхід паролем — повідомлення, що вхід паролем вимкнено', async () => {
      const { fixture, host, http } = await withProviders(providers);
      type(host, 'login-email', 'user@firma.ua');
      type(host, 'login-password', 'x');
      submit(host);
      http
        .expectOne('/api/v1/auth/login')
        .flush({ code: 'PASSWORD_LOGIN_DISABLED' }, { status: 403, statusText: 'Forbidden' });
      await render(fixture);

      expect(host.querySelector('[data-testid="login-error"]')?.textContent).toContain(
        'Вхід паролем вимкнено',
      );
    });
  });
});
