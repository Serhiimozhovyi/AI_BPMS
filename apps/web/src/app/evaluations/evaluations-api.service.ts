import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Спосіб перевірки кейсу — як `EvaluationChecks` в Api. */
export type EvaluationCheck = 'exact' | 'jsonPath' | 'expression' | 'llm';

export const EVALUATION_CHECKS: readonly EvaluationCheck[] = ['exact', 'jsonPath', 'expression', 'llm'];

/** Item на вході вузла. */
export interface EvaluationItem {
  json: Record<string, unknown>;
}

/** Кейс у запиті створення чи зміни. */
export interface EvaluationCaseInput {
  name?: string | null;
  input: EvaluationItem[];
  expected?: unknown;
  check: EvaluationCheck;
  config?: Record<string, unknown>;
}

export interface EvaluationCase extends EvaluationCaseInput {
  id: string;
  name: string;
  config: Record<string, unknown>;
}

export interface EvaluationCaseResult {
  caseId: string;
  name: string;
  passed: boolean;
  actual: unknown;
  reason: string | null;
  error: string | null;
  durationMs: number;
  tokens: number | null;
  executionId: string | null;
}

export interface EvaluationRun {
  id: string;
  evaluationId: string;
  versionId: string;
  version: number;
  startedAt: string;
  finishedAt: string;
  passed: number;
  total: number;
  results: EvaluationCaseResult[];
}

export interface EvaluationSummary {
  id: string;
  workflowId: string;
  nodeId: string;
  name: string;
  caseCount: number;
  lastRun: EvaluationRun | null;
}

export interface EvaluationDetail {
  summary: EvaluationSummary;
  cases: EvaluationCase[];
  runs: EvaluationRun[];
}

/** Оцінки якості вузлів ШІ (F6-17): `/workflows/{id}/evaluations`, `/evaluations/{id}`. */
@Injectable({ providedIn: 'root' })
export class EvaluationsApiService {
  private readonly http = inject(HttpClient);

  list(workflowId: string, nodeId: string): Observable<EvaluationSummary[]> {
    return this.http.get<EvaluationSummary[]>(`${API_BASE}/workflows/${workflowId}/evaluations`, { params: { nodeId } });
  }

  get(id: string): Observable<EvaluationDetail> {
    return this.http.get<EvaluationDetail>(`${API_BASE}/evaluations/${id}`);
  }

  create(workflowId: string, nodeId: string, name: string | null, cases: EvaluationCaseInput[]): Observable<EvaluationDetail> {
    return this.http.post<EvaluationDetail>(`${API_BASE}/workflows/${workflowId}/evaluations`, { nodeId, name, cases });
  }

  update(id: string, name: string | null, cases: EvaluationCaseInput[]): Observable<EvaluationDetail> {
    return this.http.put<EvaluationDetail>(`${API_BASE}/evaluations/${id}`, { name, cases });
  }

  delete(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/evaluations/${id}`);
  }

  run(id: string): Observable<EvaluationRun> {
    return this.http.post<EvaluationRun>(`${API_BASE}/evaluations/${id}/run`, null);
  }
}

/** Як змінився результат кейсу проти попереднього прогону. */
export type CaseTrend = 'fixed' | 'broken' | 'same' | 'new';

/** Порівнює прогін з попереднім за id кейсів. */
export function caseTrends(current: EvaluationRun, previous: EvaluationRun | null | undefined): Record<string, CaseTrend> {
  const before = new Map((previous?.results ?? []).map((r) => [r.caseId, r.passed]));
  const trends: Record<string, CaseTrend> = {};
  for (const r of current.results) {
    const was = before.get(r.caseId);
    trends[r.caseId] = was === undefined ? 'new' : was === r.passed ? 'same' : r.passed ? 'fixed' : 'broken';
  }
  return trends;
}

/** JSON для поля форми: `undefined`/`null` — порожньо. */
export function jsonText(value: unknown): string {
  return value === undefined || value === null ? '' : JSON.stringify(value, null, 2);
}

/** Поле форми → JSON; порожньо — `null`; некоректний JSON — помилка з назвою поля. */
export function parseJsonField(text: string, field: string): unknown {
  if (!text.trim()) return null;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    throw new Error($localize`:@@evaluations.invalidJson:«${field}:field:»: некоректний JSON.`);
  }
}
