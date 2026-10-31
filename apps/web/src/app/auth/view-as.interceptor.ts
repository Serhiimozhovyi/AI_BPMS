import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { NotifyService } from '../shared/notify.service';
import { VIEW_AS_HEADER, ViewAsService } from './view-as.service';

/** Коди Api, після яких режим перегляду не має сенсу: права змінились чи проєкт зник. */
const LOST_CODES = new Set(['VIEW_AS_INVALID', 'VIEW_AS_FORBIDDEN']);

/**
 * Режим «переглянути як» (F12-02): до кожного запиту `/api/v1` додає `X-OpenBP-View-As`, і сервер відповідає так, як
 * відповів би учаснику обраної ролі (F12-01). Спроба змінити дані (403 `VIEW_AS_READ_ONLY`) — пояснення в снекбарі;
 * втрата права на перегляд — вихід із режиму.
 */
export const viewAsInterceptor: HttpInterceptorFn = (request, next) => {
  const viewAs = inject(ViewAsService);
  const header = viewAs.header();
  if (!header || !request.url.startsWith(`${API_BASE}/`)) return next(request);
  const notify = inject(NotifyService);
  return next(request.clone({ setHeaders: { [VIEW_AS_HEADER]: header } })).pipe(
    catchError((error: unknown) => {
      const code = error instanceof HttpErrorResponse ? (error.error as { code?: string } | null)?.code : undefined;
      if (code === 'VIEW_AS_READ_ONLY') {
        notify.error($localize`:@@viewAs.readOnly:У режимі перегляду дані не змінюються. Вийдіть із перегляду, щоб зберегти зміни.`);
      } else if (code && LOST_CODES.has(code)) {
        viewAs.clear();
        notify.error($localize`:@@viewAs.lost:Перегляд «як користувач» вимкнено: немає прав на нього в цьому проєкті.`);
      }
      return throwError(() => error);
    }),
  );
};
