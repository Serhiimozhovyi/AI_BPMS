import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { JsonSchema } from '../forms/form-fields';
import { TaskDecision } from '../tasks/tasks-api.service';
import { ExecutionSummary, StageStatus, Violation } from './executions-api.service';

/** Вид дії етапу: рішення задачі, відхилення правила, пробудження вебхуком. */
export type StageActionKind = 'task' | 'override' | 'resume';

/** Дія, доступна поточному користувачу (`stages[].actions[]`). */
export interface StageAction {
  /** `task:{taskId}`, `override:{ruleId}`, `resume:{nodeId}`. */
  id: string;
  kind: StageActionKind;
  title: string;
  /** Без `reason` сервер відповість 422 `STAGE_RULE_REASON_REQUIRED`. */
  reasonRequired: boolean;
  taskId?: string | null;
  decisions?: TaskDecision[] | null;
  fields?: JsonSchema | null;
  ruleId?: string | null;
  nodeId?: string | null;
}

/** Невиконане правило наступного переходу. */
export interface UnmetRule {
  ruleId: string;
  mode: 'block' | 'warn';
  message: string | null;
  /** Етап-призначення; `null` — вихід без етапу. */
  to: string | null;
}

/** Етап у контракті взаємодії (ADR-004 §4, F7-05). */
export interface ContractStage {
  id: string;
  name: string;
  position: number;
  status: StageStatus;
  overdue: boolean;
  enteredAt: string | null;
  dueAt: string | null;
  exitedAt: string | null;
  owner: { lane?: string | null; role?: string | null; expression?: string | null } | null;
  violations: Violation[];
  actions: StageAction[];
  unmet: UnmetRule[];
}

/** `GET /executions/{id}/stages`. */
export interface ExecutionStages {
  execution: ExecutionSummary;
  stages: ContractStage[];
}

/** Тіло дії етапу. */
export interface StageActionRequest {
  decision?: string | null;
  data?: Record<string, unknown> | null;
  reason?: string | null;
}

/** Клієнт контракту етапів — той самий, що `get_execution_stages`/`act_on_stage` у `/mcp` (docs/architecture.md §10.3). */
@Injectable({ providedIn: 'root' })
export class StagesApiService {
  private readonly http = inject(HttpClient);

  get(executionId: string): Observable<ExecutionStages> {
    return this.http.get<ExecutionStages>(this.url(executionId));
  }

  /** `POST …/stages/{stageId}/actions/{actionId}` — виконати дію; відповідь — етапи після неї. */
  act(executionId: string, stageId: string, actionId: string, request: StageActionRequest = {}): Observable<ExecutionStages> {
    return this.http.post<ExecutionStages>(
      `${this.url(executionId)}/${encodeURIComponent(stageId)}/actions/${encodeURIComponent(actionId)}`,
      request,
    );
  }

  private url(executionId: string): string {
    return `${API_BASE}/executions/${encodeURIComponent(executionId)}/stages`;
  }
}
