import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** `VariableInfo` — рядок `GET /api/v1/variables` (F6-09). Значення — не секрет. */
export interface VariableInfo {
  key: string;
  value: string;
  description: string | null;
  updatedAt: string;
}

/** Ключ змінної: латиниця, цифри й `_`, не з цифри, до 64 символів — як перевіряє Api. */
export const VARIABLE_KEY_PATTERN = /^[A-Za-z_][A-Za-z0-9_]{0,63}$/;

/** Найдовше значення змінної (Api: `VariableService.MaxValueLength`). */
export const VARIABLE_MAX_VALUE = 10_000;

/** Клієнт `/api/v1/variables`: читають усі, змінює admin. */
@Injectable({ providedIn: 'root' })
export class VariablesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/variables`;

  list(): Observable<VariableInfo[]> {
    return this.http.get<VariableInfo[]>(this.base);
  }

  set(key: string, value: string, description: string | null): Observable<VariableInfo> {
    return this.http.put<VariableInfo>(`${this.base}/${encodeURIComponent(key)}`, { value, description });
  }

  remove(key: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(key)}`);
  }
}
