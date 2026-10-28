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
export const ASSISTANT_PROVIDERS = ['anthropic', 'openai', 'openai-compatible', 'ollama'];

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

  saveSettings(credentialId: string | null, model: string | null): Observable<AssistantSettings> {
    return this.http.put<AssistantSettings>(`${API_BASE}/settings/assistant`, {
      credentialId,
      model,
    });
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
    let consumed = 0;
    return this.http
      .post(`${API_BASE}/workflows/${encodeURIComponent(workflowId)}/assistant`, body, {
        observe: 'events',
        reportProgress: true,
        responseType: 'text',
      })
      .pipe(
        filter(
          (e) => e.type === HttpEventType.DownloadProgress || e.type === HttpEventType.Response,
        ),
        map((e) =>
          e.type === HttpEventType.Response
            ? (e.body ?? '')
            : ((e as HttpDownloadProgressEvent).partialText ?? ''),
        ),
        mergeMap((text) => {
          const parsed = parseAssistantStream(text.slice(consumed));
          consumed += parsed.consumed;
          return parsed.events;
        }),
        // Тіло помилки (problem+json) прийшло текстом — розбираємо, щоб apiErrorMessage показав detail.
        catchError((e: unknown) =>
          throwError(() =>
            e instanceof HttpErrorResponse && typeof e.error === 'string' ? withJsonBody(e) : e,
          ),
        ),
      );
  }
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
