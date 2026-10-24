import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, catchError, map, of, tap, throwError } from 'rxjs';
import { Api, Me } from './api';

/** Поточний користувач адмінки (cookie-сесія бекенду). */
@Injectable({ providedIn: 'root' })
export class Session {
    private readonly api = inject(Api);

    readonly me = signal<Me | null>(null);
    readonly isCoordinator = computed(() => this.me()?.role === 'coordinator');
    /** Часовий пояс правил застосунку (типово Europe/Kyiv) — для показу й введення часу. */
    readonly timezone = computed(() => this.me()?.timezone ?? 'Europe/Kyiv');

    /** Перевіряє сесію; 401 → <c>null</c>. */
    load(): Observable<Me | null> {
        return this.api.me().pipe(
            catchError((e: unknown) =>
                e instanceof HttpErrorResponse && e.status === 401 ? of(null) : throwError(() => e),
            ),
            tap((me) => this.me.set(me)),
        );
    }

    set(me: Me): void {
        this.me.set(me);
    }

    forget(): void {
        this.me.set(null);
    }
}

/** Сторінки — лише після входу. */
export const authGuard: CanActivateFn = (_route, state) => {
    const session = inject(Session);
    const router = inject(Router);
    if (session.me()) {
        return true;
    }
    return session
        .load()
        .pipe(
            map((me) =>
                me
                    ? true
                    : router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }),
            ),
        );
};

/** Сторінки координатора: психолога перенаправляє на розклад. */
export const coordinatorGuard: CanActivateFn = () => {
    const session = inject(Session);
    return session.isCoordinator() ? true : inject(Router).createUrlTree(['/schedule']);
};

/** 401 на будь-якому запиті (крім входу) — сесія закінчилась: на сторінку входу. */
export const sessionInterceptor: HttpInterceptorFn = (req, next) => {
    const session = inject(Session);
    const router = inject(Router);
    return next(req).pipe(
        catchError((e: unknown) => {
            if (e instanceof HttpErrorResponse && e.status === 401 && !req.url.includes('/auth/')) {
                session.forget();
                void router.navigate(['/login'], { queryParams: { returnUrl: router.url } });
            }
            return throwError(() => e);
        }),
    );
};

/** Лише внутрішній шлях застосунку — щоб `returnUrl` не став відкритим редиректом. */
export function safeReturnUrl(value: string | null | undefined): string {
    if (
        !value ||
        !value.startsWith('/') ||
        value.startsWith('//') ||
        value.startsWith('/\\') ||
        value.startsWith('/login') ||
        value.startsWith('/invite')
    ) {
        return '/';
    }
    return value;
}
