import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { ConnectivityService } from './connectivity.service';
import { connectivityInterceptor } from './connectivity.interceptor';

describe('connectivityInterceptor', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([connectivityInterceptor])), provideHttpClientTesting()],
    });
    return {
      http: TestBed.inject(HttpClient),
      controller: TestBed.inject(HttpTestingController),
      connectivity: TestBed.inject(ConnectivityService),
    };
  }

  it('мережева помилка вмикає банер, наступна успішна відповідь — вимикає', async () => {
    const { http, controller, connectivity } = setup();
    const failed = firstValueFrom(http.get('/api/v1/workflows'));
    controller.expectOne('/api/v1/workflows').error(new ProgressEvent('error'));
    await expect(failed).rejects.toMatchObject({ status: 0 });
    expect(connectivity.offline()).toBe(true);

    const ok = firstValueFrom(http.get('/api/v1/workflows'));
    controller.expectOne('/api/v1/workflows').flush({ items: [] });
    await ok;
    expect(connectivity.offline()).toBe(false);
  });

  it.each([502, 503, 504])('%i вмикає банер', async (status) => {
    const { http, controller, connectivity } = setup();
    const failed = firstValueFrom(http.get('/api/v1/nodes'));
    controller.expectOne('/api/v1/nodes').flush('bad gateway', { status, statusText: 'x' });
    await expect(failed).rejects.toMatchObject({ status });
    expect(connectivity.offline()).toBe(true);
  });

  it('401/404/500 від живого Api банер не вмикають, а наявний — гасять', async () => {
    const { http, controller, connectivity } = setup();
    connectivity.offline.set(true);
    for (const status of [401, 404, 500]) {
      const failed = firstValueFrom(http.get('/api/v1/nodes'));
      controller.expectOne('/api/v1/nodes').flush({}, { status, statusText: 'x' });
      await expect(failed).rejects.toMatchObject({ status });
      expect(connectivity.offline()).toBe(false);
    }
  });

  it('запити не до Api банера не змінюють', async () => {
    const { http, controller, connectivity } = setup();
    const failed = firstValueFrom(http.get('/assets/x.json'));
    controller.expectOne('/assets/x.json').error(new ProgressEvent('error'));
    await expect(failed).rejects.toMatchObject({ status: 0 });
    expect(connectivity.offline()).toBe(false);
  });

  it('«Повторити»: Api відповів — сторінка перезавантажується', () => {
    const { controller, connectivity } = setup();
    const reload = vi.spyOn(connectivity, 'reload').mockImplementation(() => undefined);
    connectivity.retry();
    controller.expectOne('/api/v1/auth/providers').flush({});
    expect(reload).toHaveBeenCalled();
    expect(connectivity.checking()).toBe(false);
  });

  it('«Повторити»: Api все ще недоступний — без перезавантаження', () => {
    const { controller, connectivity } = setup();
    const reload = vi.spyOn(connectivity, 'reload').mockImplementation(() => undefined);
    connectivity.offline.set(true);
    connectivity.retry();
    controller.expectOne('/api/v1/auth/providers').error(new ProgressEvent('error'));
    expect(reload).not.toHaveBeenCalled();
    expect(connectivity.checking()).toBe(false);
  });
});
