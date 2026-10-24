import { HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { OverlayContainer } from '@angular/cdk/overlay';
import { API_BASE } from '../app/connectors/connectors-api.service';
import { EventSourceLike } from '../app/executions/executions-api.service';
import { FetchEventSource } from './fetch-event-source';

/**
 * Налаштування `<openbp-canvas>` (F4-13): адреса Api і токен. Спільні для всіх екземплярів на сторінці —
 * клієнти Api (`WorkflowsApiService` тощо) — синглтони застосунку елемента.
 */
@Injectable({ providedIn: 'root' })
export class ElementConfig {
  /** Корінь Api без `/` у кінці (`https://openbp.example.com`); порожньо — той самий origin, що й сторінка. */
  readonly apiBase = signal('');
  /** API-ключ або OIDC access token для `Authorization: Bearer`; порожньо — cookie сесії. */
  readonly token = signal('');
  /** Shadow root, у який монтуються оверлеї (діалоги, випадні списки), щоб на них діяли стилі елемента. */
  overlayHost: ShadowRoot | null = null;

  /** `/api/v1/...` → `<apiBase>/api/v1/...`; інші адреси — без змін. */
  resolve(url: string): string {
    return url.startsWith(`${API_BASE}/`) || url === API_BASE ? this.apiBase().replace(/\/+$/, '') + url : url;
  }
}

/** Адреса Api і `Authorization` для запитів клієнтів Api; cookie — і для іншого origin (`withCredentials`). */
export const elementApiInterceptor: HttpInterceptorFn = (request, next) => {
  const config = inject(ElementConfig);
  const url = config.resolve(request.url);
  if (url === request.url && !request.url.startsWith(`${API_BASE}/`)) return next(request);
  const token = config.token();
  return next(
    request.clone({
      url,
      withCredentials: !token && url !== request.url,
      setHeaders: token ? { Authorization: `Bearer ${token}` } : {},
    }),
  );
};

/** SSE виконань: з токеном — fetch із заголовком (браузерний `EventSource` заголовків не має), інакше — `EventSource` з cookie. */
export function elementEventSourceFactory(): (url: string) => EventSourceLike {
  const config = inject(ElementConfig);
  return (url) => {
    const full = config.resolve(url);
    const token = config.token();
    if (token) return new FetchEventSource(full, { Authorization: `Bearer ${token}` });
    return new EventSource(full, { withCredentials: full !== url }) as unknown as EventSourceLike;
  };
}

/** Контейнер оверлеїв CDK усередині Shadow DOM елемента, а не в `document.body` (інакше без стилів). */
@Injectable()
export class ShadowOverlayContainer extends OverlayContainer {
  private readonly config = inject(ElementConfig);

  protected override _createContainer(): void {
    super._createContainer();
    if (this._containerElement) this.config.overlayHost?.appendChild(this._containerElement);
  }
}
