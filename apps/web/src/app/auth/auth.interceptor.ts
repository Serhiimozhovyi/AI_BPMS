import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { AuthService } from './auth.service';

/** Запити, де 401 — звичайна відповідь (невірний пароль, перевірка сесії), а не «сесія закінчилась». */
const AUTH_PATHS = [`${API_BASE}/auth/login`, `${API_BASE}/auth/me`, `${API_BASE}/auth/providers`];

/** Публічні форми (F6-03): 401 означає «потрібен токен посилання» або «увійдіть» — це вирішує сама сторінка форми. */
const FORMS_PREFIX = `${API_BASE}/forms/`;

/** 401 від Api посеред роботи (сесія закінчилась, користувача видалили) — на `/login` з поверненням на поточну сторінку. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        request.url.startsWith(`${API_BASE}/`) &&
        !AUTH_PATHS.includes(request.url) &&
        !request.url.startsWith(FORMS_PREFIX)
      ) {
        auth.forget();
        const current = router.url;
        if (!current.startsWith('/login')) {
          void router.navigate(['/login'], { queryParams: { returnUrl: current } });
        }
      }
      return throwError(() => error);
    }),
  );
};
