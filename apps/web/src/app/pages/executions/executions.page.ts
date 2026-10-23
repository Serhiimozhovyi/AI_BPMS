import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { NodeRunDataComponent } from '../../canvas/node-run-data.component';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import {
  EXECUTION_STATES,
  ExecutionDetail,
  ExecutionEvent,
  ExecutionFilter,
  ExecutionState,
  ExecutionStep,
  ExecutionSummary,
  ExecutionsApiService,
  executionModeLabel,
  executionStateLabel,
  isFinalState,
} from '../../executions/executions-api.service';
import { WorkflowSummary, WorkflowsApiService } from '../../workflows/workflows-api.service';

/** Розмір сторінки журналу. */
export const EXECUTIONS_PAGE_SIZE = 50;

/** Деталі виконання з подією SSE: новий крок додається (або замінює той самий id), стан оновлюється. */
export function applyDetailEvent(detail: ExecutionDetail, event: ExecutionEvent): ExecutionDetail {
  if (event.kind === 'status') {
    if (event.executionId !== detail.summary.id) return detail;
    return { ...detail, summary: { ...detail.summary, state: event.state, error: event.error ?? detail.summary.error } };
  }
  const steps = detail.steps.some((s) => s.id === event.step.id)
    ? detail.steps.map((s) => (s.id === event.step.id ? event.step : s))
    : [...detail.steps, event.step];
  const state = detail.summary.state === 'queued' ? 'running' : detail.summary.state;
  return { ...detail, summary: { ...detail.summary, state }, steps };
}

/** Початок дня `yyyy-mm-dd` (локальний час) в ISO 8601; кінець дня — з `endOfDay`. */
export function dayBoundary(date: string, endOfDay = false): string | null {
  if (!date) return null;
  const [y, m, d] = date.split('-').map(Number);
  const value = endOfDay ? new Date(y, m - 1, d, 23, 59, 59, 999) : new Date(y, m - 1, d);
  return Number.isNaN(value.getTime()) ? null : value.toISOString();
}

/**
 * `/executions` (F3-09): журнал виконань з фільтрами (сценарій, стан, період) і пагінацією; деталі — кроки по вузлах
 * з input/output (та сама панель, що в тестовому запуску), живе оновлення через SSE, «Скасувати» і «Повторити».
 * `?workflowId=` — журнал одного сценарію (посилання з канвасу).
 */
@Component({
  selector: 'app-executions-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatProgressBarModule, NodeRunDataComponent],
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-4" i18n="@@executions.title">Виконання</h1>

      <div class="mb-4 flex flex-wrap items-end gap-4" data-testid="filters">
        <label class="flex flex-col text-sm">
          <span i18n="@@executions.filter.workflow">Сценарій</span>
          <select data-testid="filter-workflow" class="rounded border p-1" [value]="workflowId() ?? ''" (change)="setWorkflow($any($event.target).value)">
            <option value="" i18n="@@executions.filter.allWorkflows">Усі сценарії</option>
            @for (w of workflows(); track w.id) {
              <option [value]="w.id" [selected]="w.id === workflowId()">{{ w.name }}</option>
            }
          </select>
        </label>
        <label class="flex flex-col text-sm">
          <span i18n="@@executions.filter.status">Стан</span>
          <select data-testid="filter-status" class="rounded border p-1" [value]="status() ?? ''" (change)="setStatus($any($event.target).value)">
            <option value="" i18n="@@executions.filter.allStatuses">Усі стани</option>
            @for (s of states; track s) {
              <option [value]="s">{{ stateLabel(s) }}</option>
            }
          </select>
        </label>
        <label class="flex flex-col text-sm">
          <span i18n="@@executions.filter.from">З</span>
          <input type="date" data-testid="filter-from" class="rounded border p-1" [value]="from()" (change)="setFrom($any($event.target).value)" />
        </label>
        <label class="flex flex-col text-sm">
          <span i18n="@@executions.filter.to">По</span>
          <input type="date" data-testid="filter-to" class="rounded border p-1" [value]="to()" (change)="setTo($any($event.target).value)" />
        </label>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && executions().length === 0 && !error()) {
        <p class="opacity-70" data-testid="empty" i18n="@@executions.empty">Виконань за цими умовами немає.</p>
      }

      @if (executions().length > 0) {
        <table class="mb-2 w-full text-left" data-testid="executions-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@executions.col.startedAt">Початок</th>
              <th class="p-2" i18n="@@executions.col.workflow">Сценарій</th>
              <th class="p-2" i18n="@@executions.col.mode">Режим</th>
              <th class="p-2" i18n="@@executions.col.state">Стан</th>
              <th class="p-2" i18n="@@executions.col.finishedAt">Завершено</th>
            </tr>
          </thead>
          <tbody>
            @for (e of executions(); track e.id) {
              <tr [attr.data-execution]="e.id" [class.font-semibold]="selected()?.summary?.id === e.id">
                <td class="p-2">
                  <button mat-button type="button" (click)="select(e.id)">{{ e.startedAt ? (e.startedAt | date: 'short') : '—' }}</button>
                </td>
                <td class="p-2">{{ workflowName(e.workflowId) }}</td>
                <td class="p-2">{{ modeLabel(e.mode) }}</td>
                <td class="p-2" [class]="stateClass(e.state)" data-testid="state">{{ stateLabel(e.state) }}</td>
                <td class="p-2">{{ e.finishedAt ? (e.finishedAt | date: 'short') : '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }

      @if (total() > pageSize) {
        <div class="mb-6 flex items-center gap-2" data-testid="pager">
          <button mat-button type="button" data-testid="prev-page" [disabled]="page() <= 1" (click)="goTo(page() - 1)" i18n="@@executions.prev">Назад</button>
          <span data-testid="page-info" i18n="@@executions.pageInfo">Сторінка {{ page() }} з {{ pageCount() }}</span>
          <button mat-button type="button" data-testid="next-page" [disabled]="page() >= pageCount()" (click)="goTo(page() + 1)" i18n="@@executions.next">Далі</button>
        </div>
      }

      @if (selected(); as d) {
        <article class="mt-4" data-testid="execution-detail" [attr.data-state]="d.summary.state">
          <div class="mb-2 flex flex-wrap items-center gap-4">
            <h2 class="text-lg font-semibold">
              <a [routerLink]="['/workflows', d.summary.workflowId]" data-testid="detail-workflow">{{ workflowName(d.summary.workflowId) }}</a>
            </h2>
            <span [class]="stateClass(d.summary.state)" data-testid="detail-state">{{ stateLabel(d.summary.state) }}</span>
            <span class="opacity-70">{{ modeLabel(d.summary.mode) }}</span>
            @if (canCancel(d.summary.state)) {
              <button mat-stroked-button type="button" data-testid="cancel-button" [disabled]="acting()" (click)="cancel(d.summary.id)" i18n="@@executions.cancel">
                Скасувати
              </button>
            }
            @if (canRetry(d.summary.state)) {
              <button mat-stroked-button type="button" data-testid="retry-button" [disabled]="acting()" (click)="retry(d.summary.id)" i18n="@@executions.retry">
                Повторити
              </button>
            }
          </div>
          @if (d.summary.error) {
            <p role="alert" class="mb-2 text-red-700" data-testid="detail-error">{{ d.summary.error }}</p>
          }
          @if (actionError()) {
            <p role="alert" class="mb-2 text-red-700" data-testid="action-error">{{ actionError() }}</p>
          }

          <div class="flex flex-wrap gap-4">
            <ol class="min-w-56" data-testid="steps">
              @for (s of d.steps; track s.id) {
                <li [attr.data-step]="s.nodeId">
                  <button mat-button type="button" [class.font-semibold]="selectedStep()?.id === s.id" (click)="selectedStepId.set(s.id)">
                    <span [class]="stepClass(s)">●</span> {{ nodeName(s.nodeId) }}@if (s.runIndex > 0) { #{{ s.runIndex + 1 }} }
                  </button>
                </li>
              } @empty {
                <li class="opacity-70" i18n="@@executions.noSteps">Кроків ще немає.</li>
              }
            </ol>
            @if (selectedStep(); as step) {
              <app-node-run-data class="min-w-80 flex-1" [step]="step" />
            }
          </div>
        </article>
      }
    </section>
  `,
})
export class ExecutionsPage implements OnInit {
  private readonly api = inject(ExecutionsApiService);
  private readonly workflowsApi = inject(WorkflowsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly pageSize = EXECUTIONS_PAGE_SIZE;
  readonly states = EXECUTION_STATES;

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly executions = signal<ExecutionSummary[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly workflowId = signal<string | null>(null);
  readonly status = signal<ExecutionState | null>(null);
  readonly from = signal('');
  readonly to = signal('');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly selected = signal<ExecutionDetail | null>(null);
  readonly selectedStepId = signal<string | null>(null);
  /** Назви вузлів сценарію вибраного виконання (поточна версія; вузол, якого вже немає, — за id). */
  private readonly nodeNames = signal<ReadonlyMap<string, string>>(new Map());
  readonly acting = signal(false);
  readonly actionError = signal<string | null>(null);

  readonly pageCount = computed(() => Math.max(1, Math.ceil(this.total() / this.pageSize)));
  readonly selectedStep = computed<ExecutionStep | null>(() => {
    const detail = this.selected();
    if (!detail) return null;
    return detail.steps.find((s) => s.id === this.selectedStepId()) ?? null;
  });

  private live: Subscription | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.stopLive());
  }

  ngOnInit(): void {
    this.workflowId.set(this.route.snapshot.queryParamMap.get('workflowId') || null);
    this.workflowsApi
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (page) => this.workflows.set(page.items), error: () => this.workflows.set([]) });
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list(this.filter(), this.page(), this.pageSize)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.executions.set(page.items);
          this.total.set(page.total);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  setWorkflow(id: string): void {
    this.workflowId.set(id || null);
    this.resetPage();
  }

  setStatus(status: string): void {
    this.status.set((status || null) as ExecutionState | null);
    this.resetPage();
  }

  setFrom(date: string): void {
    this.from.set(date);
    this.resetPage();
  }

  setTo(date: string): void {
    this.to.set(date);
    this.resetPage();
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pageCount()));
    this.load();
  }

  select(id: string): void {
    this.stopLive();
    this.actionError.set(null);
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => this.show(detail),
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  cancel(id: string): void {
    this.acting.set(true);
    this.actionError.set(null);
    this.api
      .cancel(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (summary) => {
          this.acting.set(false);
          this.selected.update((d) => (d && d.summary.id === summary.id ? { ...d, summary } : d));
          this.updateRow(summary);
        },
        error: (e: unknown) => {
          this.acting.set(false);
          this.actionError.set(apiErrorMessage(e));
        },
      });
  }

  retry(id: string): void {
    this.acting.set(true);
    this.actionError.set(null);
    this.api
      .retry(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (summary) => {
          this.acting.set(false);
          // Нове виконання — нагорі журналу; одразу показуємо його деталі з живим оновленням.
          this.load();
          this.select(summary.id);
        },
        error: (e: unknown) => {
          this.acting.set(false);
          this.actionError.set(apiErrorMessage(e));
        },
      });
  }

  canCancel(state: ExecutionState): boolean {
    return state === 'queued' || state === 'running';
  }

  canRetry(state: ExecutionState): boolean {
    return isFinalState(state);
  }

  workflowName(id: string): string {
    return this.workflows().find((w) => w.id === id)?.name ?? id;
  }

  nodeName(nodeId: string): string {
    return this.nodeNames().get(nodeId) ?? nodeId;
  }

  stateLabel(state: ExecutionState): string {
    return executionStateLabel(state);
  }

  modeLabel(mode: ExecutionSummary['mode']): string {
    return executionModeLabel(mode);
  }

  stateClass(state: ExecutionState): string {
    switch (state) {
      case 'success':
        return 'text-green-700';
      case 'error':
      case 'timed_out':
        return 'text-red-700';
      default:
        return 'opacity-70';
    }
  }

  stepClass(step: ExecutionStep): string {
    switch (step.status) {
      case 'success':
        return 'text-green-700';
      case 'error':
        return 'text-red-700';
      default:
        return 'opacity-50';
    }
  }

  private filter(): ExecutionFilter {
    return {
      workflowId: this.workflowId(),
      status: this.status(),
      from: dayBoundary(this.from()),
      to: dayBoundary(this.to(), true),
    };
  }

  private resetPage(): void {
    this.page.set(1);
    this.load();
  }

  private show(detail: ExecutionDetail): void {
    this.selected.set(detail);
    const failed = detail.steps.find((s) => s.status === 'error');
    this.selectedStepId.set((failed ?? detail.steps.at(-1))?.id ?? null);
    this.loadNodeNames(detail.summary.workflowId);
    if (!isFinalState(detail.summary.state)) this.startLive(detail.summary.id);
  }

  private startLive(id: string): void {
    this.live = this.api.events(id).subscribe({
      next: (event) => {
        const detail = this.selected();
        if (!detail || detail.summary.id !== id) return;
        const updated = applyDetailEvent(detail, event);
        this.selected.set(updated);
        if (event.kind === 'step' && (!this.selectedStepId() || event.step.status === 'error')) this.selectedStepId.set(event.step.id);
        this.updateRow(updated.summary);
        // Час завершення і підсумок — з Api, коли виконання завершилось.
        if (isFinalState(updated.summary.state)) this.refreshSummary(id);
      },
      error: (e: unknown) => this.actionError.set(e instanceof Error ? e.message : apiErrorMessage(e)),
    });
  }

  private refreshSummary(id: string): void {
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          if (this.selected()?.summary.id === id) this.selected.set(detail);
          this.updateRow(detail.summary);
        },
      });
  }

  private stopLive(): void {
    this.live?.unsubscribe();
    this.live = null;
  }

  private updateRow(summary: ExecutionSummary): void {
    this.executions.update((list) => list.map((e) => (e.id === summary.id ? summary : e)));
  }

  private loadNodeNames(workflowId: string): void {
    this.workflowsApi
      .get(workflowId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (w) => this.nodeNames.set(new Map(w.definition.nodes.map((n) => [n.id, n.name]))),
        error: () => this.nodeNames.set(new Map()),
      });
  }
}
