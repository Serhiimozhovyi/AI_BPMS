import {
  HttpClient,
  HttpDownloadProgressEvent,
  HttpErrorResponse,
  HttpEventType,
} from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, catchError, filter, map, mergeMap, throwError } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';

/** Налаштування помічника (`GET /settings/assistant`). */
export interface AssistantSettings {
  credentialId: string | null;
  credentialName: string | null;
  provider: string | null;
  model: string | null;
  configured: boolean;
  /** Усі підключені моделі (F3-107); основна — `primaryIndex`-та. */
  profiles?: AssistantProfile[];
  primaryIndex?: number | null;
}

/** Підключена модель помічника. */
export interface AssistantProfile {
  credentialId: string;
  credentialName: string | null;
  provider: string | null;
  model: string;
}

/** Модель у тілі збереження налаштувань. */
export interface AssistantProfileInput {
  credentialId: string;
  model: string;
}

/** Репліка розмови. */
export interface AssistantTurn {
  role: 'user' | 'assistant';
  text: string;
}

/** Подія відповіді помічника (SSE `POST /workflows/{id}/assistant`). */
export interface AssistantEvent {
  type: 'step' | 'message' | 'definition' | 'error' | 'done';
  text?: string | null;
  tool?: string | null;
  ok?: boolean | null;
  definition?: WorkflowDefinition | null;
  code?: string | null;
}

/** Що доналаштувати після побудови з опису (F3-103). */
export interface DraftTodoItem {
  nodeId: string;
  text: string;
}

/** Подія побудови сценарію з опису (SSE `POST /workflows/{id}/draft`, F3-103). */
export interface DraftEvent {
  type: 'step' | 'definition' | 'todo' | 'error' | 'done';
  text?: string | null;
  /** Етап кроку: `plan`, `retry`, `compile`, `node`. */
  stage?: string | null;
  index?: number | null;
  total?: number | null;
  ok?: boolean | null;
  definition?: WorkflowDefinition | null;
  todo?: DraftTodoItem[] | null;
  code?: string | null;
}

/** Пояснення сценарію простими словами (`GET /workflows/{id}/explanation`, F3-72). */
export interface WorkflowExplanation {
  status: 'none' | 'pending' | 'ready' | 'failed' | 'not_configured';
  /** Версія, до якої належить `text`. */
  version: number | null;
  currentVersion: number;
  text: string | null;
  error: string | null;
  updatedAt: string | null;
  /** Текст належить старішій версії. */
  stale: boolean;
}

/** Типи credential'ів, з якими працює помічник. */
export const ASSISTANT_PROVIDERS = ['anthropic', 'openai', 'gemini', 'openai-compatible', 'ollama'];

/**
 * Розбирає накопичений текст `text/event-stream` на події; повертає події з повних блоків і довжину розібраного префікса.
 */
export function parseAssistantStream(text: string): { events: AssistantEvent[]; consumed: number } {
  const events: AssistantEvent[] = [];
  let consumed = 0;
  let end = text.indexOf('\n\n');
  while (end >= 0) {
    const block = text.slice(consumed, end);
    const data = block
      .split('\n')
      .filter((l) => l.startsWith('data: '))
      .map((l) => l.slice(6))
      .join('\n');
    if (data) events.push(JSON.parse(data) as AssistantEvent);
    consumed = end + 2;
    end = text.indexOf('\n\n', consumed);
  }
  return { events, consumed };
}

/** Клієнт помічника (F3-60). */
@Injectable({ providedIn: 'root' })
export class AssistantApiService {
  private readonly http = inject(HttpClient);

  settings(): Observable<AssistantSettings> {
    return this.http.get<AssistantSettings>(`${API_BASE}/settings/assistant`);
  }

  /** Зберігає список моделей; `primaryIndex` — основна. Порожній список вимикає помічника. */
  saveSettings(profiles: AssistantProfileInput[], primaryIndex: number): Observable<AssistantSettings> {
    return this.http.put<AssistantSettings>(
      `${API_BASE}/settings/assistant`,
      profiles.length ? { profiles, primaryIndex } : { credentialId: null, model: null },
    );
  }

  /** Моделі credential'а: для Ollama — встановлені (`GET /api/tags`), для інших типів — порожній список. */
  models(credentialId: string): Observable<string[]> {
    return this.http
      .get<{ name: string }[]>(`${API_BASE}/settings/assistant/models`, { params: { credentialId } })
      .pipe(map((models) => models.map((m) => m.name)));
  }

  explanation(workflowId: string): Observable<WorkflowExplanation> {
    return this.http.get<WorkflowExplanation>(
      `${API_BASE}/workflows/${encodeURIComponent(workflowId)}/explanation`,
    );
  }

  /** Пояснити сценарій заново (поточну версію, одразу). */
  requestExplanation(workflowId: string): Observable<WorkflowExplanation> {
    return this.http.post<WorkflowExplanation>(
      `${API_BASE}/workflows/${encodeURIComponent(workflowId)}/explanation`,
      null,
    );
  }

  /** Запит до помічника: події в міру надходження (поточний текст відповіді — `partialText` прогресу). */
  ask(
    workflowId: string,
    body: { message: string; definition: WorkflowDefinition; history: AssistantTurn[] },
  ): Observable<AssistantEvent> {
    return this.stream<AssistantEvent>(workflowId, 'assistant', body);
  }

  /** Побудова сценарію з опису (F3-103): події прогресу, потім `definition` і `todo`. Відписка зупиняє побудову. */
  draft(
    workflowId: string,
    body: { description: string; definition: WorkflowDefinition },
  ): Observable<DraftEvent> {
    return this.stream<DraftEvent>(workflowId, 'draft', body);
  }

  private stream<T>(workflowId: string, action: string, body: object): Observable<T> {
    return postEventStream<T>(this.http, `${API_BASE}/workflows/${encodeURIComponent(workflowId)}/${action}`, body);
  }
}

/**
 * POST із відповіддю `text/event-stream`: події надходять у міру отримання. Тіло помилки (problem+json) приходить текстом —
 * розбираємо, щоб `apiErrorMessage` показав `detail`. Спільне для помічника редактора й загального помічника (F3-110).
 */
export function postEventStream<T>(http: HttpClient, url: string, body: object): Observable<T> {
  let consumed = 0;
  return http
    .post(url, body, { observe: 'events', reportProgress: true, responseType: 'text' })
    .pipe(
      filter((e) => e.type === HttpEventType.DownloadProgress || e.type === HttpEventType.Response),
      map((e) =>
        e.type === HttpEventType.Response ? (e.body ?? '') : ((e as HttpDownloadProgressEvent).partialText ?? ''),
      ),
      mergeMap((text) => {
        const parsed = parseAssistantStream(text.slice(consumed));
        consumed += parsed.consumed;
        return parsed.events as unknown as T[];
      }),
      catchError((e: unknown) =>
        throwError(() => (e instanceof HttpErrorResponse && typeof e.error === 'string' ? withJsonBody(e) : e)),
      ),
    );
}

function withJsonBody(e: HttpErrorResponse): HttpErrorResponse {
  try {
    return new HttpErrorResponse({
      error: JSON.parse(e.error as string),
      headers: e.headers,
      status: e.status,
      statusText: e.statusText,
      url: e.url ?? undefined,
    });
  } catch {
    return e;
  }
}
