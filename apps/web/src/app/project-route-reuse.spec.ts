import { ActivatedRouteSnapshot, Route } from '@angular/router';
import { ProjectRouteReuseStrategy } from './project-route-reuse';

const snapshot = (routeConfig: Route | null, params: Record<string, string> = {}) => ({ routeConfig, params }) as unknown as ActivatedRouteSnapshot;

describe('ProjectRouteReuseStrategy', () => {
  const strategy = new ProjectRouteReuseStrategy();
  const project: Route = { path: 'projects/:id' };
  const other: Route = { path: 'workflows/:id' };

  it('інший проєкт — нова сторінка, той самий — повторне використання', () => {
    expect(strategy.shouldReuseRoute(snapshot(project, { id: 'p2' }), snapshot(project, { id: 'p1' }))).toBe(false);
    expect(strategy.shouldReuseRoute(snapshot(project, { id: 'p1' }), snapshot(project, { id: 'p1' }))).toBe(true);
  });

  it('решта маршрутів — як у Angular за замовчуванням', () => {
    expect(strategy.shouldReuseRoute(snapshot(other, { id: 'w2' }), snapshot(other, { id: 'w1' }))).toBe(true);
    expect(strategy.shouldReuseRoute(snapshot(other), snapshot(project))).toBe(false);
  });
});
