import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Вид поля credential'а (`CredentialFieldKind`). */
/** `managed` — поля, якими керує OpenBP (токени OAuth2); форма їх не показує. */
export type CredentialFieldKind = 'text' | 'secret' | 'url' | 'secretMap' | 'managed';

/** Поле типу (`CredentialFieldInfo`). */
export interface CredentialFieldInfo {
  name: string;
  title: string;
  kind: CredentialFieldKind;
  required: boolean;
  /** Підказка під полем (F3-93). */
  description?: string | null;
  /** Приклад значення в порожньому полі (F3-93). */
  placeholder?: string | null;
  /** Допустимі значення — вибір зі списку (F3-93). */
  options?: string[] | null;
}

/** Результат `POST /credentials/check` (F3-94): доступ працює або код помилки провайдера. */
export interface CredentialCheckResult {
  ok: boolean;
  error?: string | null;
}

/** Тип credential'а — елемент `GET /api/v1/credentials/types`. Порожні `fields` — довільні пари (`generic`). */
export interface CredentialTypeInfo {
  type: string;
  displayName: string;
  description: string;
  fields: CredentialFieldInfo[];
  schema: Record<string, unknown>;
}

/** Посилання на конектор або сценарій. */
export interface CredentialReference {
  id: string;
  name: string;
}

/** Credential без значень (`CredentialSummary`): `fields` — шляхи заповнених полів (`apiKey`, `headers.Authorization`). */
export interface CredentialSummary {
  id: string;
  name: string;
  type: string;
  fields: string[];
  createdBy: string | null;
  usedBy: { connectors: CredentialReference[]; workflows: CredentialReference[] };
  error: string | null;
  /** Для `oauth2` — стан підключення (F4-04). */
  oauth?: CredentialOAuthStatus | null;
  /** Проєкт (F6-14); `null` — спільний. */
  projectId?: string | null;
}

/** Стан oauth2-credential'а: `notConnected` · `connected` · `expired` · `error`. */
export interface CredentialOAuthStatus {
  status: 'notConnected' | 'connected' | 'expired' | 'error';
  expiresAt: string | null;
  error: string | null;
}

/** Credential для вибору (`GET /credentials/options`, роль editor): без даних. */
export interface CredentialOption {
  id: string;
  name: string;
  type: string;
  /** Проєкт (F6-14); `null` — спільний. */
  projectId?: string | null;
}

/** Дані credential'а: рядки або об'єкти «назва → рядок»; `null` у PATCH видаляє. */
export type CredentialData = Record<string, string | Record<string, string | null> | null>;

/** Клієнт `/api/v1/credentials` (F4-01, docs/architecture.md §10.3). Лише для admin. */
@Injectable({ providedIn: 'root' })
export class CredentialsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/credentials`;

  types(): Observable<CredentialTypeInfo[]> {
    return this.http.get<CredentialTypeInfo[]>(`${this.base}/types`);
  }

  /** Список для вибору у вузлі чи конекторі; доступний editor. */
  options(type?: string): Observable<CredentialOption[]> {
    return this.http.get<CredentialOption[]>(`${this.base}/options`, type ? { params: { type } } : {});
  }

  list(): Observable<CredentialSummary[]> {
    return this.http.get<CredentialSummary[]>(this.base);
  }

  /** `projectId` — проєкт (F6-14); `null` — спільний (лише admin). */
  create(name: string, type: string, data: CredentialData, projectId: string | null = null): Observable<CredentialSummary> {
    return this.http.post<CredentialSummary>(this.base, projectId ? { name, type, data, projectId } : { name, type, data });
  }

  update(id: string, changes: { name?: string; data?: CredentialData }): Observable<CredentialSummary> {
    return this.http.patch<CredentialSummary>(`${this.base}/${encodeURIComponent(id)}`, changes);
  }

  /**
   * Перевіряє доступ без збереження (F3-94; `oauth2.clientCredentials`: запит токена). `credentialId` — збережений
   * credential, чиї значення доповнюють `data` (редагування).
   */
  check(type: string, data: CredentialData, credentialId?: string): Observable<CredentialCheckResult> {
    return this.http.post<CredentialCheckResult>(`${this.base}/check`, credentialId ? { type, data, credentialId } : { type, data });
  }

  /** Починає OAuth2-підключення: адреса, куди перейти браузеру (F4-04). */
  startOAuth(id: string): Observable<{ authorizationUrl: string }> {
    return this.http.post<{ authorizationUrl: string }>(`${this.base}/${encodeURIComponent(id)}/oauth2/start`, null);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }
}
