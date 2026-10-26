import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE, PageResult } from '../connectors/connectors-api.service';
import { JsonSchema } from '../forms/form-fields';

/** Статус задачі `core.approval` (F6-02). */
export type TaskStatus = 'open' | 'done' | 'expired' | 'cancelled';

/** Усі статуси — для фільтра. */
export const TASK_STATUSES: readonly TaskStatus[] = ['open', 'done', 'expired', 'cancelled'];

/** Виконавець: користувач, роль або зовнішнє значення (email, Telegram id). */
export interface TaskAssignee {
  userId?: string | null;
  email?: string | null;
  role?: string | null;
  value?: string | null;
}

/** Задача у списку — `GET /api/v1/tasks`. */
export interface TaskSummary {
  id: string;
  executionId: string;
  workflowId: string;
  workflowName: string;
  nodeId: string;
  title: string;
  status: TaskStatus;
  assignee: TaskAssignee;
  decision?: string | null;
  escalated: boolean;
  canComplete: boolean;
  createdAt: string;
  dueAt?: string | null;
  completedAt?: string | null;
}

/** Рішення на вибір. */
export interface TaskDecision {
  key: string;
  label: string;
}

/** Картка задачі — `GET /api/v1/tasks/{id}`. */
export interface TaskDetail {
  task: TaskSummary;
  description?: string | null;
  decisions: TaskDecision[];
  fields?: JsonSchema | null;
  data?: Record<string, unknown> | null;
  completedBy?: { id: string; email: string } | null;
  cancelledByExecutionId?: string | null;
}

/** Фільтри списку. */
export interface TaskFilter {
  scope: 'mine' | 'all';
  status: TaskStatus | null;
  page?: number;
}

/** Підпис статусу задачі. */
export function taskStatusLabel(status: TaskStatus): string {
  switch (status) {
    case 'open':
      return $localize`:@@tasks.status.open:Відкрита`;
    case 'done':
      return $localize`:@@tasks.status.done:Вирішена`;
    case 'expired':
      return $localize`:@@tasks.status.expired:Строк вийшов`;
    case 'cancelled':
      return $localize`:@@tasks.status.cancelled:Скасована`;
  }
}

/** Кому задача — одним рядком. */
export function assigneeLabel(assignee: TaskAssignee): string {
  if (assignee.email) return assignee.email;
  if (assignee.role) return $localize`:@@tasks.assignee.role:Роль: ${assignee.role}:role:`;
  return assignee.value ?? '—';
}

/** Задачі погодження: список, картка, рішення (`/api/v1/tasks`). */
@Injectable({ providedIn: 'root' })
export class TasksApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/tasks`;

  list(filter: TaskFilter): Observable<PageResult<TaskSummary>> {
    let params = new HttpParams().set('scope', filter.scope).set('page', String(filter.page ?? 1));
    if (filter.status) params = params.set('status', filter.status);
    return this.http.get<PageResult<TaskSummary>>(this.base, { params });
  }

  /** Картка за id або токеном з посилання `$execution.taskUrl`. */
  get(idOrToken: string): Observable<TaskDetail> {
    return this.http.get<TaskDetail>(`${this.base}/${encodeURIComponent(idOrToken)}`);
  }

  complete(idOrToken: string, decision: string, data: Record<string, unknown>): Observable<TaskDetail> {
    return this.http.post<TaskDetail>(`${this.base}/${encodeURIComponent(idOrToken)}/complete`, { decision, data });
  }
}
