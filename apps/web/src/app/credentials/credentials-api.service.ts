import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Вид поля credential'а (`CredentialFieldKind`). */
export type CredentialFieldKind = 'text' | 'secret' | 'url' | 'secretMap';

/** Поле типу (`CredentialFieldInfo`). */
export interface CredentialFieldInfo {
  name: string;
  title: string;
  kind: CredentialFieldKind;
  required: boolean;
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

  list(): Observable<CredentialSummary[]> {
    return this.http.get<CredentialSummary[]>(this.base);
  }

  create(name: string, type: string, data: CredentialData): Observable<CredentialSummary> {
    return this.http.post<CredentialSummary>(this.base, { name, type, data });
  }

  update(id: string, changes: { name?: string; data?: CredentialData }): Observable<CredentialSummary> {
    return this.http.patch<CredentialSummary>(`${this.base}/${encodeURIComponent(id)}`, changes);
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }
}
