import { inject } from '@angular/core';
import { CanActivateChildFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService, UserInfo } from './auth.service';

/** Адреса зміни пароля (F8-13). */
export const CHANGE_PASSWORD_PATH = '/account/password';

/**
 * Сторінки застосунку — лише після входу: без сесії перенаправляє на `/login?returnUrl=…`. З тимчасовим паролем
 * (F8-13) пускає лише на зміну пароля.
 */
export const authGuard: CanActivateChildFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const toLogin = () => router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  const decide = (user: UserInfo | null) => {
    if (!user) return toLogin();
    if (user.mustChangePassword && !state.url.startsWith(CHANGE_PASSWORD_PATH)) {
      return router.createUrlTree([CHANGE_PASSWORD_PATH], { queryParams: { returnUrl: state.url } });
    }
    return true;
  };
  const current = auth.user();
  return current ? decide(current) : auth.me().pipe(map(decide));
};
