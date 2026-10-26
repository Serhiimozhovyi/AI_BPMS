import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { AuthService } from './auth.service';

describe('authInterceptor', () => {
  function setup(currentUrl = '/workflows/abc') {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    const router = TestBed.inject(Router);
    vi.spyOn(router, 'url', 'get').mockReturnValue(currentUrl);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    const auth = TestBed.inject(AuthService);
    auth.user.set({ id: 'u1', email: 'admin@firma.ua', role: 'admin' });
    return { http: TestBed.inject(HttpClient), controller: TestBed.inject(HttpTestingController), navigate, auth };
  }

  it('401 від Api — на /login з поверненням на поточну сторінку, помилка йде далі', async () => {
    const { http, controller, navigate, auth } = setup();
    const request = firstValueFrom(http.get('/api/v1/workflows'));
    controller.expectOne('/api/v1/workflows').flush({}, { status: 401, statusText: 'Unauthorized' });

    await expect(request).rejects.toMatchObject({ status: 401 });
    expect(auth.user()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { returnUrl: '/workflows/abc' } });
  });

  it('401 від публічної форми не перенаправляє: це вирішує сторінка форми (F6-03)', async () => {
    const { http, controller, navigate, auth } = setup('/form/leave');
    const request = firstValueFrom(http.get('/api/v1/forms/leave'));
    controller.expectOne('/api/v1/forms/leave').flush({ code: 'UNAUTHENTICATED' }, { status: 401, statusText: 'Unauthorized' });

    await expect(request).rejects.toMatchObject({ status: 401 });
    expect(navigate).not.toHaveBeenCalled();
    expect(auth.user()).not.toBeNull();
  });

  it.each(['/api/v1/auth/login', '/api/v1/auth/me'])('401 на %s не перенаправляє', async (url) => {
    const { http, controller, navigate, auth } = setup();
    const request = firstValueFrom(http.post(url, {}));
    controller.expectOne(url).flush({}, { status: 401, statusText: 'Unauthorized' });

    await expect(request).rejects.toMatchObject({ status: 401 });
    expect(navigate).not.toHaveBeenCalled();
    expect(auth.user()).not.toBeNull();
  });

  it('403 і 500 не чіпають сесію', async () => {
    const { http, controller, navigate, auth } = setup();
    for (const status of [403, 500]) {
      const request = firstValueFrom(http.get('/api/v1/connectors'));
      controller.expectOne('/api/v1/connectors').flush({}, { status, statusText: 'x' });
      await expect(request).rejects.toMatchObject({ status });
    }
    expect(navigate).not.toHaveBeenCalled();
    expect(auth.user()).not.toBeNull();
  });

  it('на самій сторінці входу не перенаправляє вдруге', async () => {
    const { http, controller, navigate } = setup('/login?returnUrl=%2Fexecutions');
    const request = firstValueFrom(http.get('/api/v1/nodes'));
    controller.expectOne('/api/v1/nodes').flush({}, { status: 401, statusText: 'Unauthorized' });
    await expect(request).rejects.toMatchObject({ status: 401 });
    expect(navigate).not.toHaveBeenCalled();
  });
});
