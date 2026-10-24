import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** `ApiKeyInfo` — рядок `GET /api/v1/api-keys`; самого ключа тут немає. */
export interface ApiKeyInfo {
  id: string;
  name: string;
  createdBy: string;
  lastUsedAt: string | null;
  /** Проєкт, до якого обмежено ключ (F6-14); `null` — весь інстанс. */
  projectId?: string | null;
}

/** `ApiKeyCreated` — відповідь `POST /api/v1/api-keys`: `key` показується лише один раз. */
export interface ApiKeyCreated {
  id: string;
  name: string;
  key: string;
  projectId?: string | null;
}

/** Клієнт `/api/v1/api-keys` (docs/architecture.md §10.3, §10.6). */
@Injectable({ providedIn: 'root' })
export class ApiKeysApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/api-keys`;

  list(): Observable<ApiKeyInfo[]> {
    return this.http.get<ApiKeyInfo[]>(this.base);
  }

  /** `projectId` — обмежити ключ проєктом (F6-14); `null` — ключ на весь інстанс з вашими правами. */
  create(name: string, projectId: string | null = null): Observable<ApiKeyCreated> {
    return this.http.post<ApiKeyCreated>(this.base, projectId ? { name, projectId } : { name });
  }

  revoke(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }
}

/** Фрагмент конфігурації MCP-клієнта (Claude Code, Claude Desktop, Cursor тощо) для підключення до `/mcp`. */
export function mcpClientConfig(mcpUrl: string, key: string): string {
  return JSON.stringify(
    { mcpServers: { openbp: { type: 'http', url: mcpUrl, headers: { Authorization: `Bearer ${key}` } } } },
    null,
    2,
  );
}
