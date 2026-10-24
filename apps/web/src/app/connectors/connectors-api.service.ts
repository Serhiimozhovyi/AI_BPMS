import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

/** Транспорт MCP-сервера (`McpTransportKind`, camelCase). */
export type ConnectorTransport = 'stdio' | 'http';

/** Стан з'єднання (`ConnectorStatus`, camelCase). */
export type ConnectorStatus = 'new' | 'ok' | 'error';

/** `ConnectorSummary` — рядок списку `GET /api/v1/connectors`. */
export interface ConnectorSummary {
  id: string;
  name: string;
  transport: ConnectorTransport;
  status: ConnectorStatus;
  toolCount: number;
  toolsCachedAt: string | null;
  /** Проєкт (F6-14); `null` — спільний. */
  projectId?: string | null;
}

/** `ConnectorConfig` — налаштування транспорту без секретів. */
export interface ConnectorConfig {
  command?: string | null;
  arguments?: string[] | null;
  environment?: Record<string, string> | null;
  workingDirectory?: string | null;
  url?: string | null;
  headers?: Record<string, string> | null;
}

/** `McpToolInfo` — tool із кешу discovery. */
export interface McpToolInfo {
  name: string;
  title?: string | null;
  description?: string | null;
  inputSchema: Record<string, unknown>;
  outputSchema?: Record<string, unknown> | null;
}

/** `ConnectorDetail` — `GET /api/v1/connectors/{id}`. */
export interface ConnectorDetail {
  summary: ConnectorSummary;
  config: ConnectorConfig;
  credentialId: string | null;
  tools: McpToolInfo[];
}

/** `ConnectorInput` — тіло `POST/PUT /api/v1/connectors`. */
export interface ConnectorInput {
  name: string;
  transport: ConnectorTransport;
  config: ConnectorConfig;
  credentialId?: string | null;
  /** Проєкт (F6-14); `null` — спільний (лише admin). */
  projectId?: string | null;
}

/** `ToolCallResult` — відповідь тестового виклику; `elapsed` — TimeSpan (`hh:mm:ss.fffffff`). */
export interface ToolCallResult {
  isError: boolean;
  structuredContent: Record<string, unknown> | null;
  content: Record<string, unknown>[];
  elapsed: string;
}

/** `PageResult<T>`. */
export interface PageResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

/** Базовий шлях REST (docs/architecture.md §10.3). */
export const API_BASE = '/api/v1';

/** Клієнт `/api/v1/connectors` (docs/architecture.md §10.3). */
@Injectable({ providedIn: 'root' })
export class ConnectorsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/connectors`;

  list(page = 1, pageSize = 200): Observable<PageResult<ConnectorSummary>> {
    return this.http.get<PageResult<ConnectorSummary>>(this.base, { params: { page, pageSize } });
  }

  get(id: string): Observable<ConnectorDetail> {
    return this.http.get<ConnectorDetail>(`${this.base}/${encodeURIComponent(id)}`);
  }

  create(input: ConnectorInput): Observable<ConnectorDetail> {
    return this.http.post<ConnectorDetail>(this.base, input);
  }

  refreshTools(id: string): Observable<ConnectorDetail> {
    return this.http.post<ConnectorDetail>(`${this.base}/${encodeURIComponent(id)}/refresh-tools`, null);
  }

  callTool(id: string, tool: string, args: Record<string, unknown>): Observable<ToolCallResult> {
    return this.http.post<ToolCallResult>(
      `${this.base}/${encodeURIComponent(id)}/tools/${encodeURIComponent(tool)}/call`,
      { arguments: args },
    );
  }
}

/**
 * Текст помилки для користувача: `detail` з ProblemDetails Api, інакше — загальне повідомлення за HTTP-статусом.
 */
/** Машинний код помилки: `code` з problem+json Api, інакше `NETWORK_ERROR` (Api недоступний), `HTTP_<статус>` чи `UNKNOWN`. */
export function apiErrorCode(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const code = (error.error as { code?: unknown } | null)?.code;
    if (typeof code === 'string' && code) return code;
    return error.status === 0 ? 'NETWORK_ERROR' : `HTTP_${error.status}`;
  }
  return 'UNKNOWN';
}

export function apiErrorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const body = error.error as { detail?: unknown; title?: unknown } | null;
    if (body && typeof body === 'object') {
      if (typeof body.detail === 'string' && body.detail) return body.detail;
      if (typeof body.title === 'string' && body.title) return body.title;
    }
    switch (error.status) {
      case 0:
        return $localize`:@@api.error.network:Api недоступний. Перевірте з'єднання.`;
      case 401:
        return $localize`:@@api.error.unauthorized:Потрібно увійти в систему.`;
      case 403:
        return $localize`:@@api.error.forbidden:Недостатньо прав для цієї дії.`;
      case 502:
        return $localize`:@@api.error.connectorUnavailable:MCP-сервер недоступний.`;
    }
    return $localize`:@@api.error.status:Помилка Api (HTTP ${error.status}:status:).`;
  }
  return $localize`:@@api.error.unknown:Невідома помилка.`;
}
