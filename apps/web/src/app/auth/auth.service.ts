import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, catchError, of, switchMap, tap } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** `UserInfo` — відповідь `GET /api/v1/auth/me`. */
export interface UserInfo {
  id: string;
  email: string;
  role: 'admin' | 'editor' | 'viewer';
}

/** Вхід і вихід cookie-сесією (`/api/v1/auth/*`, docs/architecture.md §10.3); поточний користувач — сигнал для shell і guard. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/auth`;

  /** Поточний користувач; `null` — не ввійшов або сесія закінчилась. */
  readonly user = signal<UserInfo | null>(null);

  /** Перевіряє сесію: користувач або `null` на 401. Інші помилки пробрасуються. */
  me(): Observable<UserInfo | null> {
    return this.http.get<UserInfo>(`${this.base}/me`).pipe(
      catchError((error: unknown) => {
        if (error instanceof HttpErrorResponse && error.status === 401) {
          return of(null);
        }
        throw error;
      }),
      tap((user) => this.user.set(user)),
    );
  }

  /** Входить і одразу читає користувача; невірний email чи пароль — `HttpErrorResponse` 401. */
  login(email: string, password: string): Observable<UserInfo | null> {
    return this.http.post<void>(`${this.base}/login`, { email, password }).pipe(switchMap(() => this.me()));
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${this.base}/logout`, {}).pipe(tap(() => this.user.set(null)));
  }

  /** Сесія закінчилась (401 на будь-якому запиті): забути користувача без запиту до Api. */
  forget(): void {
    this.user.set(null);
  }
}

/**
 * Куди повернутися після входу: лише шлях усередині застосунку (`/…`, не `//host` і не `/login`), інакше — головна.
 * `returnUrl` приходить з адресного рядка, тож зовнішнє посилання не має стати відкритим редиректом.
 */
export function safeReturnUrl(value: string | null | undefined): string {
  if (!value || !value.startsWith('/') || value.startsWith('//') || value.startsWith('/\\') || value.startsWith('/login')) {
    return '/';
  }
  return value;
}
