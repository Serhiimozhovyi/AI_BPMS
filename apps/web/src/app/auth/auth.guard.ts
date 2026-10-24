import { inject } from '@angular/core';
import { CanActivateChildFn, Router } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './auth.service';

/** Сторінки застосунку — лише після входу: без сесії перенаправляє на `/login?returnUrl=…`. */
export const authGuard: CanActivateChildFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const toLogin = () => router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  if (auth.user()) {
    return true;
  }
  return auth.me().pipe(map((user) => (user ? true : toLogin())));
};
