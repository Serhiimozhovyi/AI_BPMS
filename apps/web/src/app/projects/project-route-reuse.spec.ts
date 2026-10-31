import { ActivatedRouteSnapshot, Route } from '@angular/router';
import { ProjectRouteReuseStrategy, RECREATE_ON_PARAMS } from './project-route-reuse';

function snapshot(routeConfig: Route, params: Record<string, string>): ActivatedRouteSnapshot {
  return { routeConfig, params } as unknown as ActivatedRouteSnapshot;
}

describe('ProjectRouteReuseStrategy', () => {
  const strategy = new ProjectRouteReuseStrategy();
  const project: Route = { path: 'projects/:id', data: { [RECREATE_ON_PARAMS]: true } };
  const editor: Route = { path: 'workflows/:id' };

  it('інший проєкт — нова сторінка, той самий — та сама', () => {
    expect(strategy.shouldReuseRoute(snapshot(project, { id: 'p2' }), snapshot(project, { id: 'p1' }))).toBe(false);
    expect(strategy.shouldReuseRoute(snapshot(project, { id: 'p1' }), snapshot(project, { id: 'p1' }))).toBe(true);
  });

  it('маршрути без позначки поводяться як типово в Angular', () => {
    expect(strategy.shouldReuseRoute(snapshot(editor, { id: 'w2' }), snapshot(editor, { id: 'w1' }))).toBe(true);
    expect(strategy.shouldReuseRoute(snapshot(editor, { id: 'w1' }), snapshot(project, { id: 'w1' }))).toBe(false);
  });
});
