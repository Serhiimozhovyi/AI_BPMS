import { Injectable } from '@angular/core';
import { ActivatedRouteSnapshot, BaseRouteReuseStrategy, Params } from '@angular/router';

/** Ключ `data` маршруту: при зміні його параметрів компонент (і всі дочірні) створюються заново. */
export const RECREATE_ON_PARAMS = 'recreateOnParams';

/**
 * Типово Angular повторно використовує компонент, коли змінюються лише параметри маршруту: `/projects/A/members` →
 * `/projects/B/members` лишав сторінку проєкту A з її даними (заголовок, учасники) і запитами на A. Для маршрутів з
 * `data: { recreateOnParams: true }` інший набір параметрів — інший екземпляр, разом з усіма вкладками під ним.
 */
@Injectable()
export class ProjectRouteReuseStrategy extends BaseRouteReuseStrategy {
  override shouldReuseRoute(future: ActivatedRouteSnapshot, curr: ActivatedRouteSnapshot): boolean {
    if (future.routeConfig !== curr.routeConfig) return false;
    return !future.routeConfig?.data?.[RECREATE_ON_PARAMS] || sameParams(future.params, curr.params);
  }
}

function sameParams(a: Params, b: Params): boolean {
  const keys = Object.keys(a);
  return keys.length === Object.keys(b).length && keys.every((k) => a[k] === b[k]);
}
