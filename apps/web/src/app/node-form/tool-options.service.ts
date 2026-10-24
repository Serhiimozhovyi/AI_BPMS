import { Injectable, InjectionToken, Injector, inject } from '@angular/core';
import { Observable, catchError, map, shareReplay, throwError } from 'rxjs';
import { ConnectorsApiService, McpToolInfo, ToolCallResult, apiErrorMessage } from '../connectors/connectors-api.service';
import { OptionItem, OptionsSource, SEARCH_ARGUMENT, acceptsSearch, extractOptions } from './dynamic-options';

/** Виклик tool'а конектора — наявний `POST /connectors/{id}/tools/{tool}/call`; у тестах підміняється. */
export type ToolCaller = (serverId: string, tool: string, args: Record<string, unknown>) => Observable<ToolCallResult>;

export const TOOL_CALLER = new InjectionToken<ToolCaller>('TOOL_CALLER', {
  providedIn: 'root',
  factory: () => {
    const api = inject(ConnectorsApiService);
    return (serverId, tool, args) => api.callTool(serverId, tool, args);
  },
});

/** Конектор вузла (`serverId`) і його tools — для анотацій `x-openbp-options` (F3-54). */
export interface OptionsConnector {
  id: string;
  tools: readonly McpToolInfo[];
}

/** Завантажений список: варіанти, чи обрізаний до `MAX_OPTIONS` і чи можна шукати на сервері. */
export interface LoadedOptions {
  items: OptionItem[];
  truncated: boolean;
  searchable: boolean;
}

/**
 * Варіанти полів зі списком (F3-54) з кешем у межах панелі вузла: однаковий виклик (конектор, tool, аргументи)
 * виконується раз, «Оновити» — повторно. Помилка конектора — `Error` з текстом для користувача.
 */
@Injectable()
export class ToolOptionsService {
  private readonly injector = inject(Injector);
  private caller: ToolCaller | null = null;
  private readonly cache = new Map<string, Observable<LoadedOptions>>();

  load(connector: OptionsConnector, source: OptionsSource, args: Record<string, unknown>, options: { refresh?: boolean; query?: string } = {}): Observable<LoadedOptions> {
    const searchable = acceptsSearch(connector.tools.find((t) => t.name === source.tool)?.inputSchema);
    const callArgs = searchable && options.query ? { ...args, [SEARCH_ARGUMENT]: options.query } : args;
    const key = JSON.stringify([connector.id, source.tool, callArgs]);
    const cached = this.cache.get(key);
    if (cached && !options.refresh) return cached;

    // Токен — ліниво: панель без полів зі списком не потребує HTTP.
    this.caller ??= this.injector.get(TOOL_CALLER);
    const request = this.caller(connector.id, source.tool, callArgs).pipe(
      catchError((e: unknown) => throwError(() => new Error(apiErrorMessage(e)))),
      map((result) => {
        if (result.isError) throw new Error(errorText(result) || $localize`:@@options.error.tool:Tool ${source.tool}:tool: повернув помилку.`);
        const extracted = extractOptions(result.structuredContent, source);
        if (!extracted) throw new Error($localize`:@@options.error.shape:Tool ${source.tool}:tool: повернув не список.`);
        return { ...extracted, searchable };
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.cache.set(key, request);
    // Помилка не кешується: наступна спроба знову звернеться до конектора.
    request.subscribe({ error: () => this.cache.delete(key) });
    return request;
  }
}

function errorText(result: ToolCallResult): string {
  return result.content
    .map((block) => (block['type'] === 'text' && typeof block['text'] === 'string' ? block['text'] : ''))
    .filter(Boolean)
    .join(' ');
}
