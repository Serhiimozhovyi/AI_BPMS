import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Injector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { RedirectFunction, Route, Router, UrlTree, provideRouter } from '@angular/router';
import { routes } from '../../app.routes';
import { projectDefaultTab } from './project-default-tab';

const pagesUrl = '/api/v1/projects/p1/pages';

describe('projectDefaultTab', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] }));
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  async function flushPages(body: object, status = 200): Promise<void> {
    // Запит іде після мікрозадач (динамічний import, firstValueFrom).
    for (let i = 0; i < 20; i++) {
      const reqs = TestBed.inject(HttpTestingController).match(pagesUrl);
      if (reqs.length) {
        reqs[0].flush(body, status === 200 ? undefined : { status, statusText: 'Error' });
        return;
      }
      await new Promise((r) => setTimeout(r));
    }
    throw new Error('no request');
  }

  it('«Застосунок», коли в проєкті є сторінки застосунку', async () => {
    const tab = projectDefaultTab(TestBed.inject(Injector), 'p1');
    await flushPages([{ id: 'x', key: 'home', name: 'Головна' }]);
    expect(await tab).toBe('app');
  });

  it('«Огляд», коли сторінок немає або список не прочитався', async () => {
    const empty = projectDefaultTab(TestBed.inject(Injector), 'p1');
    await flushPages([]);
    expect(await empty).toBe('overview');

    const failed = projectDefaultTab(TestBed.inject(Injector), 'p1');
    await flushPages({ message: 'no' }, 403);
    expect(await failed).toBe('overview');
  });

  it('маршрут `/projects/:id` без вкладки веде на типову вкладку', async () => {
    const shell = routes.find((r) => r.children?.some((c) => c.path === 'projects/:id'));
    const project = (shell?.children ?? []).find((c) => c.path === 'projects/:id') as Route;
    const redirect = project.children?.find((c) => c.path === '')?.redirectTo as RedirectFunction;
    const result = TestBed.runInInjectionContext(() =>
      redirect({ params: { id: 'p1' } } as unknown as Parameters<RedirectFunction>[0]),
    ) as Promise<UrlTree>;
    await flushPages([{ id: 'x', key: 'home', name: 'Головна' }]);
    expect(TestBed.inject(Router).serializeUrl(await result)).toBe('/projects/p1/app');
  });
});
