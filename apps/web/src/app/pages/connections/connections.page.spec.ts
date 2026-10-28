import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { ConnectionsPage } from './connections.page';

@Component({ template: '' })
class Stub {}

describe('ConnectionsPage (F3-99)', () => {
  /** Лише редіректи з реальних маршрутів плюс сама сторінка: так видно, куди ведуть старі адреси. */
  function configure() {
    const redirects = routes.find((r) => r.children)!.children!.filter((r) => r.redirectTo !== undefined);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'connections', component: ConnectionsPage }, ...redirects, { path: '**', component: Stub }]),
      ],
    });
  }

  async function open(url: string) {
    configure();
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    const http = TestBed.inject(HttpTestingController);
    const host = harness.routeNativeElement as HTMLElement;
    const settle = async () => {
      harness.detectChanges();
      await harness.fixture.whenStable();
    };
    return { harness, http, host, settle };
  }

  function answerPending(http: HttpTestingController) {
    for (const r of http.match(() => true)) {
      const url = r.request.url;
      if (url.endsWith('/credentials')) r.flush([]);
      else if (url.endsWith('/credentials/types')) r.flush([]);
      else if (url.includes('/connectors/catalog')) r.flush([]);
      else r.flush({ items: [], page: 1, pageSize: 200, total: 0 });
    }
  }

  it('за замовчуванням відкриває «Підключено» зі списком конекторів', async () => {
    const { http, host, settle } = await open('/connections');
    answerPending(http);
    await settle();
    expect(host.querySelector('h1')?.textContent).toContain('Підключення');
    expect(host.querySelectorAll('h1')).toHaveLength(1);
    expect(host.querySelector('app-connectors-page')).not.toBeNull();
    expect(host.querySelector('app-connector-catalog')).toBeNull();
    expect(host.querySelector('app-credentials-page')).toBeNull();
  });

  it('?tab=catalog відкриває каталог, ?tab=credentials — облікові дані', async () => {
    const { harness, http, host, settle } = await open('/connections?tab=catalog');
    answerPending(http);
    await settle();
    expect(host.querySelector('app-connector-catalog')).not.toBeNull();

    await harness.navigateByUrl('/connections?tab=credentials');
    answerPending(http);
    await settle();
    expect(host.querySelector('app-credentials-page')).not.toBeNull();
    expect(host.querySelector('app-connectors-page')).toBeNull();
    expect(host.querySelectorAll('h1')).toHaveLength(1);
  });

  it('клік по вкладці змінює адресу', async () => {
    const { http, host, settle } = await open('/connections');
    answerPending(http);
    await settle();
    host.querySelector<HTMLElement>('[data-testid="tab-credentials"] button')!.click();
    await settle();
    answerPending(http);
    expect(TestBed.inject(Router).url).toBe('/connections?tab=credentials');
  });

  it('старі адреси ведуть на вкладки', async () => {
    const { harness, http, settle } = await open('/connectors');
    answerPending(http);
    await settle();
    const router = TestBed.inject(Router);
    expect(router.url).toBe('/connections?tab=connected');

    await harness.navigateByUrl('/connectors?catalog=telegram');
    answerPending(http);
    await settle();
    expect(router.url).toBe('/connections?tab=catalog&catalog=telegram');

    await harness.navigateByUrl('/credentials');
    answerPending(http);
    await settle();
    expect(router.url).toBe('/connections?tab=credentials');
  });
});
