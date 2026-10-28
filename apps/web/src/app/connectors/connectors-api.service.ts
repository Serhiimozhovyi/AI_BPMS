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
  /** Запис каталогу, з якого встановлено (F6-16); `null` — додано вручну. */
  catalogId?: string | null;
}

/** `ConnectorConfig` — налаштування транспорту без секретів. */
export interface ConnectorConfig {
  command?: string | null;
  arguments?: string[] | null;
  environment?: Record<string, string> | null;
  workingDirectory?: string | null;
  url?: string | null;
  headers?: Record<string, string> | null;
  /** Запис каталогу (F6-16). */
  catalog?: string | null;
  /** Поле credential'а → змінна середовища чи заголовок (F3-93); лише назви. */
  credentialMap?: Record<string, string> | null;
}

/** Ключ credential'а з маніфесту (`ConnectorManifestKey`). */
export interface ConnectorManifestKey {
  name: string;
  title?: string | null;
  required: boolean;
  secret: boolean;
}

/** Облікові дані конектора: загальний тип (F3-93, `type` + `map`) або ключі старого `mcp.stdio`/`mcp.http`. */
export interface ConnectorManifestCredential {
  env?: ConnectorManifestKey[] | null;
  headers?: ConnectorManifestKey[] | null;
  type?: string | null;
  map?: Record<string, string> | null;
  headerMap?: Record<string, string> | null;
}

/** Несекретне налаштування підключення з маніфесту (F3-93). */
export interface ConnectorManifestSetting {
  name: string;
  title: string;
  description?: string | null;
  placeholder?: string | null;
  required?: boolean;
  env?: string | null;
  header?: string | null;
}

/** Маніфест `connector.json` (F6-16, docs/connectors.md §5.1). */
export interface ConnectorManifest {
  id: string;
  name: string;
  description: string;
  icon?: string | null;
  transport: ConnectorTransport;
  stdio?: { command: string; arguments?: string[] | null; environment?: Record<string, string> | null } | null;
  http?: { url: string; headers?: Record<string, string> | null } | null;
  credential?: ConnectorManifestCredential | null;
  /** Несекретні налаштування підключення: адреса сервісу тощо (F3-93). */
  settings?: ConnectorManifestSetting[] | null;
  triggers: ConnectorTriggerPreset[];
  /** Дії конектора (F3-92): з маніфесту, а за його відсутності — з кешу tools підключення. */
  actions?: ConnectorManifestAction[] | null;
}

/** Дія конектора в маніфесті: tool без схеми аргументів (її дає discovery підключення). */
export interface ConnectorManifestAction {
  name: string;
  title: string;
  description?: string | null;
}

/** Пресет тригера в `connector.json` (docs/connectors.md §9.2): готовий вузол без `serverId`. */
export interface ConnectorTriggerPreset {
  id: string;
  title: string;
  description?: string | null;
  node: { type: 'mcp.subscribe' | 'mcp.poll'; parameters: Record<string, unknown> };
}

/** Запис `GET /api/v1/connectors/catalog`. */
export interface ConnectorCatalogEntry {
  manifest: ConnectorManifest;
  source: 'builtin' | 'external';
  installed: ConnectorSummary[];
}

/** Ключі credential'а запису для транспорту: `env` для stdio, `headers` для http. */
/** Налаштування підключення, що застосовуються до транспорту (є ціль `env` для stdio чи `header` для http). */
export function manifestSettings(manifest: ConnectorManifest, transport: ConnectorTransport): ConnectorManifestSetting[] {
  return (manifest.settings ?? []).filter((s) => (transport === 'http' ? s.header : s.env));
}

/** Загальний тип облікових даних запису (F3-93); `null` — старий спосіб за ключами або без облікових даних. */
export function manifestCredentialType(manifest: ConnectorManifest): string | null {
  return manifest.credential?.type ?? null;
}

export function manifestKeys(manifest: ConnectorManifest, transport: ConnectorTransport): ConnectorManifestKey[] {
  return (transport === 'http' ? manifest.credential?.headers : manifest.credential?.env) ?? [];
}

/** Транспорти, для яких у записі є налаштування; типовий — першим. */
export function manifestTransports(manifest: ConnectorManifest): ConnectorTransport[] {
  const all = (['stdio', 'http'] as const).filter((t) => manifest[t]);
  return [manifest.transport, ...all.filter((t) => t !== manifest.transport)];
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
/** Ресурс конектора для `mcp.subscribe` (F6-20): `resources/list` або шаблон з `resources/templates/list`. */
export interface ConnectorResource {
  uri: string;
  name: string;
  description?: string | null;
  isTemplate: boolean;
}

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

  update(id: string, input: ConnectorInput): Observable<ConnectorDetail> {
    return this.http.put<ConnectorDetail>(`${this.base}/${encodeURIComponent(id)}`, input);
  }

  /** Каталог конекторів (F6-16). */
  catalog(): Observable<ConnectorCatalogEntry[]> {
    return this.http.get<ConnectorCatalogEntry[]>(`${this.base}/catalog`);
  }

  /** Встановити з каталогу: сервер створює конектор з типовими налаштуваннями й читає tools. */
  install(
    catalogId: string,
    request: {
      name?: string;
      transport?: ConnectorTransport;
      projectId?: string | null;
      /** Облікові дані загального типу запису (F3-93). */
      credentialId?: string;
      /** Значення `settings` маніфесту за назвою. */
      settings?: Record<string, string>;
    },
  ): Observable<ConnectorDetail> {
    return this.http.post<ConnectorDetail>(`${this.base}/catalog/${encodeURIComponent(catalogId)}/install`, request);
  }

  /** Видаляє підключення; 409 `CONNECTOR_IN_USE` — його використовують активні сценарії. */
  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }

  refreshTools(id: string): Observable<ConnectorDetail> {
    return this.http.post<ConnectorDetail>(`${this.base}/${encodeURIComponent(id)}/refresh-tools`, null);
  }

  /** Ресурси подій конектора (кеш на сервері — хвилина; `refresh` читає наново). */
  resources(id: string, refresh = false): Observable<ConnectorResource[]> {
    return this.http.get<ConnectorResource[]>(`${this.base}/${encodeURIComponent(id)}/resources`, { params: refresh ? { refresh: true } : {} });
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
