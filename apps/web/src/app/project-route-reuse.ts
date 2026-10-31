import { ActivatedRouteSnapshot, BaseRouteReuseStrategy } from '@angular/router';

/**
 * Проєкт (`projects/:id`) з іншим id — нова сторінка, а не та сама з новим параметром: вкладки проєкту читають id один
 * раз, тож при переході на інший проєкт (вибір у шапці, «Учасники проєкту») сторінка й вкладки створюються заново.
 */
export class ProjectRouteReuseStrategy extends BaseRouteReuseStrategy {
  override shouldReuseRoute(future: ActivatedRouteSnapshot, curr: ActivatedRouteSnapshot): boolean {
    if (future.routeConfig !== curr.routeConfig) return false;
    return future.routeConfig?.path !== 'projects/:id' || future.params['id'] === curr.params['id'];
  }
}
