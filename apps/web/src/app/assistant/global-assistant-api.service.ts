import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { postEventStream } from './assistant-api.service';

/** Розмова в списку (`GET /assistant/conversations`, F3-108). */
export interface AssistantConversationSummary {
  id: string;
  title: string;
  userId: string;
  messageCount: number;
  createdAt: string;
  updatedAt: string;
}

/** Репліка розмови: `tool` — крок інструмента, `note` — рішення щодо пропозиції. */
export interface AssistantMessageView {
  role: 'user' | 'assistant' | 'tool' | 'note';
  text: string | null;
  tool: string | null;
  ok: boolean | null;
  at: string;
  /** Для кроку, що завершився пропозицією змін, — її id: на цьому місці картка. */
  proposalId?: string | null;
}

/** Пропозиція змін: інструмент, який людина застосовує чи скасовує (картки — F3-111). */
export interface AssistantProposalView {
  id: string;
  tool: string;
  arguments: Record<string, unknown>;
  plan: unknown;
  status: 'pending' | 'applied' | 'rejected' | 'failed';
  result: string | null;
  createdAt: string;
  decidedAt: string | null;
}

/** Розмова повністю (`GET /assistant/conversations/{id}`). */
export interface AssistantConversation extends AssistantConversationSummary {
  messages: AssistantMessageView[];
  proposals: AssistantProposalView[];
}

/** Де користувач зараз: лише ідентифікатори й сторінка, решту помічник бере інструментами. */
export interface AssistantPageContext {
  page?: string;
  workflowId?: string;
  nodeId?: string;
  executionId?: string;
  projectId?: string;
  table?: string;
}

/** Подія відповіді (SSE `POST /assistant/conversations/{id}/messages`). */
export interface AssistantChatEvent {
  type: 'step' | 'proposal' | 'message' | 'error' | 'done';
  text?: string | null;
  tool?: string | null;
  ok?: boolean | null;
  proposal?: AssistantProposalView | null;
  code?: string | null;
}

/** Клієнт `/assistant/conversations` (F3-108): лише editor і admin. */
@Injectable({ providedIn: 'root' })
export class GlobalAssistantApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/assistant/conversations`;

  list(): Observable<AssistantConversationSummary[]> {
    return this.http.get<AssistantConversationSummary[]>(this.base);
  }

  get(id: string): Observable<AssistantConversation> {
    return this.http.get<AssistantConversation>(`${this.base}/${encodeURIComponent(id)}`);
  }

  create(): Observable<AssistantConversation> {
    return this.http.post<AssistantConversation>(this.base, {});
  }

  remove(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${encodeURIComponent(id)}`);
  }

  /** Повідомлення в розмову: події в міру надходження; відписка зупиняє відповідь. */
  send(id: string, message: string, context: AssistantPageContext): Observable<AssistantChatEvent> {
    return postEventStream<AssistantChatEvent>(this.http, `${this.base}/${encodeURIComponent(id)}/messages`, {
      message,
      context,
    });
  }

  /** Застосовує чи скасовує пропозицію. */
  decide(id: string, proposalId: string, apply: boolean): Observable<AssistantProposalView> {
    return this.http.post<AssistantProposalView>(
      `${this.base}/${encodeURIComponent(id)}/proposals/${encodeURIComponent(proposalId)}`,
      { apply },
    );
  }
}
