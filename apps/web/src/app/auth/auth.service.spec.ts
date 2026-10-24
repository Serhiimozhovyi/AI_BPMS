import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { AuthService, UserInfo, safeReturnUrl } from './auth.service';

const editor: UserInfo = { id: 'u2', email: 'buh@firma.ua', role: 'editor' };

describe('AuthService', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    return { auth: TestBed.inject(AuthService), http: TestBed.inject(HttpTestingController) };
  }

  it('me: користувач запам\'ятовується в сигналі', async () => {
    const { auth, http } = setup();
    const result = firstValueFrom(auth.me());
    http.expectOne({ method: 'GET', url: '/api/v1/auth/me' }).flush(editor);
    expect(await result).toEqual(editor);
    expect(auth.user()).toEqual(editor);
  });

  it('me: 401 — не помилка, а null', async () => {
    const { auth, http } = setup();
    auth.user.set(editor);
    const result = firstValueFrom(auth.me());
    http.expectOne('/api/v1/auth/me').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(await result).toBeNull();
    expect(auth.user()).toBeNull();
  });

  it('me: інша помилка пробрасується', async () => {
    const { auth, http } = setup();
    const result = firstValueFrom(auth.me());
    http.expectOne('/api/v1/auth/me').flush({}, { status: 500, statusText: 'Server Error' });
    await expect(result).rejects.toMatchObject({ status: 500 });
  });

  it('login: POST з email і паролем, потім me', async () => {
    const { auth, http } = setup();
    const result = firstValueFrom(auth.login('buh@firma.ua', 'pass'));
    const login = http.expectOne({ method: 'POST', url: '/api/v1/auth/login' });
    expect(login.request.body).toEqual({ email: 'buh@firma.ua', password: 'pass' });
    login.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/v1/auth/me').flush(editor);
    expect(await result).toEqual(editor);
  });

  it('logout: POST і користувача забуто', async () => {
    const { auth, http } = setup();
    auth.user.set(editor);
    const result = firstValueFrom(auth.logout());
    http.expectOne({ method: 'POST', url: '/api/v1/auth/logout' }).flush(null, { status: 204, statusText: 'No Content' });
    await result;
    expect(auth.user()).toBeNull();
  });
});

describe('safeReturnUrl', () => {
  it.each([
    ['/executions?status=error', '/executions?status=error'],
    ['/workflows/abc', '/workflows/abc'],
    [null, '/'],
    ['', '/'],
    ['https://evil.example', '/'],
    ['//evil.example', '/'],
    ['/\\evil.example', '/'],
    ['/login?returnUrl=%2F', '/'],
    ['javascript:alert(1)', '/'],
  ])('%s → %s', (value, expected) => {
    expect(safeReturnUrl(value)).toBe(expected);
  });
});
