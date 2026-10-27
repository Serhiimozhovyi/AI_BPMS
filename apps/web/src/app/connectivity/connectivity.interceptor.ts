import { HttpErrorResponse, HttpEventType, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, tap, throwError } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { ConnectivityService, isServerDown } from './connectivity.service';

/** Мережева помилка чи 502/503/504 від Api → банер «Немає зв'язку»; перша відповідь Api (будь-яка) його знімає (F3-84). */
export const connectivityInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith(`${API_BASE}/`)) return next(request);
  const connectivity = inject(ConnectivityService);
  return next(request).pipe(
    tap((event) => {
      if (event.type === HttpEventType.Response) connectivity.offline.set(false);
    }),
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) connectivity.offline.set(isServerDown(error.status));
      return throwError(() => error);
    }),
  );
};
