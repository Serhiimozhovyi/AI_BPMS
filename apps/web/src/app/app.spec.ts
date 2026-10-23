import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from './app';
import { routes } from './app.routes';

describe('App', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter(routes)],
    });
  });

  it('створюється', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('показує навігацію українською', async () => {
    const fixture = TestBed.createComponent(App);
    await fixture.whenStable();
    const nav = (fixture.nativeElement as HTMLElement).querySelector('nav');
    expect(nav?.textContent).toContain('Сценарії');
    expect(nav?.textContent).toContain('Конектори');
  });
});

describe('routes', () => {
  it('містить шість маршрутів з architecture.md §7', () => {
    const paths = routes.filter((r) => r.loadComponent).map((r) => r.path);
    expect(paths).toEqual([
      'workflows',
      'workflows/:id',
      'executions',
      'connectors',
      'credentials',
      'settings',
    ]);
  });

  it('/workflows показує заголовок українською', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes)] });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/workflows');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Сценарії');
  });

  it('порожній шлях перенаправляє на /workflows', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes)] });
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/');
    expect(harness.routeNativeElement?.querySelector('h1')?.textContent).toContain('Сценарії');
  });

  it('кожен маршрут відкриває сторінку з заголовком', async () => {
    TestBed.configureTestingModule({ providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()] });
    const harness = await RouterTestingHarness.create();
    for (const url of ['/workflows/abc', '/executions', '/connectors', '/credentials', '/settings']) {
      await harness.navigateByUrl(url);
      expect(harness.routeNativeElement?.querySelector('h1')?.textContent?.trim()).toBeTruthy();
    }
  });
});
