import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Subscription, catchError, forkJoin, map, of } from 'rxjs';
import { BusinessViewComponent } from '../../business-view/business-view.component';
import { BusinessRun } from '../../business-view/business-run';
import { EditorView, readEditorView, writeEditorView } from '../../business-view/editor-view';
import { NodeTypeInfo, NodesApiService } from '../../nodes/nodes-api.service';
import { NodeRunDataComponent } from '../../canvas/node-run-data.component';
import { StageStripComponent } from '../../executions/stage-strip.component';
import { ConnectorsApiService, apiErrorMessage } from '../../connectors/connectors-api.service';
import {
  EXECUTION_STATES,
  ExecutionDetail,
  ExecutionEvent,
  ExecutionFilter,
  ExecutionState,
  ExecutionStep,
  ExecutionSummary,
  ExecutionsApiService,
  Violation,
  executionModeLabel,
  executionStateLabel,
  failedStep,
  isFinalState,
  parseItems,
} from '../../executions/executions-api.service';
import { WorkflowDefinition, WorkflowSummary, WorkflowsApiService } from '../../workflows/workflows-api.service';

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
 * з input/output (та сама панель, що в тестовому запуску), живе оновлення через SSE, «Скасувати», «Повторити» і
 * «Продовжити з вузла» (F6-08) з редактором вхідних items вузла, що впав.
 * `?workflowId=` — журнал одного сценарію (посилання з канвасу).
 */
@Component({
  selector: 'app-executions-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, BusinessViewComponent, NodeRunDataComponent, StageStripComponent],
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
        <label class="flex flex-col text-sm">
          <span i18n="@@executions.filter.correlationKey">Ключ кореляції</span>
          <input
            type="search"
            data-testid="filter-correlation-key"
            class="rounded border p-1"
            placeholder="напр. id угоди"
            i18n-placeholder="@@executions.filter.correlationKey.placeholder"
            [value]="correlationKey()"
            (change)="setCorrelationKey($any($event.target).value)"
          />
        </label>
        <label class="flex items-center gap-2 text-sm">
          <input type="checkbox" data-testid="filter-violations" [checked]="onlyViolations()" (change)="setOnlyViolations($any($event.target).checked)" />
          <span i18n="@@executions.filter.violations">Лише з відкритими порушеннями</span>
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
              <th class="p-2" i18n="@@executions.col.correlationKey">Ключ</th>
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
                <td class="p-2" data-testid="correlation-key">
                  @if (e.correlationKey; as key) {
                    <button mat-button type="button" title="Показати всі виконання з цим ключем" i18n-title="@@executions.correlationKey.filter" (click)="setCorrelationKey(key)">
                      {{ key }}
                    </button>
                  } @else {
                    —
                  }
                </td>
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
            @if (d.summary.state === 'waiting' && waitingUntil(d); as until) {
              <span data-testid="detail-resume-at" i18n="@@executions.waitingUntil">до {{ until | date: 'short' }}</span>
            }
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
            @if (canResume(d.summary.state)) {
              <button mat-stroked-button type="button" data-testid="resume-button" [disabled]="acting() || resumeOpen()" (click)="openResume()" i18n="@@executions.resume">
                Продовжити з вузла
              </button>
            }
          </div>
          @if (d.summary.resumedFromExecutionId; as source) {
            <p class="mb-2 opacity-70" data-testid="resumed-from">
              <ng-container i18n="@@executions.resumedFrom">Продовжує виконання</ng-container>
              <button mat-button type="button" (click)="select(source)">{{ source }}</button>
            </p>
          }
          @if (d.summary.parentExecutionId; as parent) {
            <p class="mb-2 opacity-70" data-testid="parent-execution">
              <ng-container i18n="@@executions.parent">Підсценарій виконання</ng-container>
              <button mat-button type="button" (click)="select(parent)">{{ parent }}</button>
            </p>
          }
          @if (d.children?.length) {
            <div class="mb-2" data-testid="child-executions">
              <p class="opacity-70" i18n="@@executions.children">Запущені підсценарії:</p>
              <ul>
                @for (c of d.children; track c.id) {
                  <li data-testid="child-execution">
                    {{ nodeName(c.parentNodeId ?? '') }} —
                    <span [class]="stateClass(c.state)">{{ stateLabel(c.state) }}</span>
                    <button mat-button type="button" (click)="select(c.id)">{{ c.id }}</button>
                  </li>
                }
              </ul>
            </div>
          }
          @if (resumeOpen()) {
            <div class="mb-4 flex flex-col gap-2 rounded border p-3" data-testid="resume-panel">
              @if (resumeStep(); as failed) {
                <p i18n="@@executions.resume.from">Вузол «{{ nodeName(failed.nodeId) }}» виконається знову, вузли до нього — ні.</p>
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label i18n="@@executions.resume.items">Вхідні items (вхід 0)</mat-label>
                  <textarea matInput rows="8" class="font-mono" data-testid="resume-items" [value]="resumeText()" (input)="resumeText.set(text($event))"></textarea>
                  @if (resumeError(); as message) {
                    <mat-hint class="text-red-700" data-testid="resume-items-error">{{ message }}</mat-hint>
                  } @else {
                    <mat-hint i18n="@@executions.resume.items.hint">Змініть дані, щоб виконати вузол з виправленими items</mat-hint>
                  }
                </mat-form-field>
              } @else {
                <p i18n="@@executions.resume.noFailedNode">Виконання зупинилось між вузлами: продовжаться вузли, що не встигли виконатись.</p>
              }
              <div class="flex gap-2">
                <button mat-flat-button type="button" data-testid="resume-submit" [disabled]="acting() || !!resumeError()" (click)="resume(d.summary.id)" i18n="@@executions.resume.submit">
                  Продовжити
                </button>
                <button mat-button type="button" data-testid="resume-cancel" (click)="resumeOpen.set(false)" i18n="@@executions.resume.cancel">Скасувати</button>
              </div>
            </div>
          }
          @if (d.summary.error) {
            <p role="alert" class="mb-2 text-red-700" data-testid="detail-error">{{ d.summary.error }}</p>
          }
          @if (actionError()) {
            <p role="alert" class="mb-2 text-red-700" data-testid="action-error">{{ actionError() }}</p>
          }

          @if (d.stages; as stages) {
            @if (stages.length > 0) {
              <app-stage-strip [stages]="stages" />
            }
          }
          @if (d.violations?.length) {
            <ul class="mb-3 flex flex-col gap-2" data-testid="violations">
              @for (v of d.violations; track v.id) {
                <li
                  class="rounded border p-3"
                  [class.border-red-700]="v.status === 'open' && v.mode === 'block'"
                  [class.border-amber-600]="v.status === 'open' && v.mode === 'warn'"
                  data-testid="violation"
                  [attr.data-rule]="v.ruleId"
                  [attr.data-status]="v.status"
                  [attr.data-mode]="v.mode"
                >
                  <p>
                    <span class="font-semibold" data-testid="violation-title">{{ violationTitle(v) }}</span>
                    <span class="opacity-70"> · {{ stageName(d, v.stageId) }}</span>
                  </p>
                  @if (v.message) {
                    <p data-testid="violation-message">{{ v.message }}</p>
                  }
                  <p class="opacity-70" data-testid="violation-times">
                    <span i18n="@@violations.createdAt">Виникло {{ v.createdAt | date: 'short' }}</span>
                    @if (v.resolvedAt; as at) {
                      <span> · </span>
                      <span i18n="@@violations.resolvedAt">закрито {{ at | date: 'short' }}</span>
                    }
                  </p>
                  @if (v.status === 'overridden') {
                    <p class="opacity-70" data-testid="violation-reason" i18n="@@violations.reason">Причина: {{ v.reason }}</p>
                    @if (v.actorUserId; as actor) {
                      <p class="opacity-70" data-testid="violation-actor" i18n="@@violations.actor">Відхилив користувач {{ actor }}</p>
                    }
                  }
                  @if (v.status === 'open') {
                    @if (overrideId() === v.id) {
                      <div class="mt-2 flex flex-col gap-2" data-testid="override-panel">
                        <mat-form-field appearance="outline" subscriptSizing="dynamic">
                          <mat-label i18n="@@violations.override.reason">Чому перехід має пройти попри правило</mat-label>
                          <textarea matInput rows="3" data-testid="override-reason" [value]="overrideReason()" (input)="overrideReason.set(text($event))"></textarea>
                          <mat-hint i18n="@@violations.override.hint">Причина й автор потраплять у журнал виконання та аудит</mat-hint>
                        </mat-form-field>
                        <div class="flex gap-2">
                          <button
                            mat-flat-button
                            type="button"
                            data-testid="override-submit"
                            [disabled]="acting() || !overrideReason().trim()"
                            (click)="overrideRule(d.summary.id, v)"
                            i18n="@@violations.override.submit"
                          >
                            Продовжити
                          </button>
                          <button mat-button type="button" data-testid="override-cancel" (click)="overrideId.set(null)" i18n="@@violations.override.cancel">Скасувати</button>
                        </div>
                      </div>
                    } @else {
                      <button mat-stroked-button type="button" class="mt-2" data-testid="override-button" [disabled]="acting()" (click)="openOverride(v.id)">
                        {{ v.mode === 'block' ? continueWithReason : overrideWithReason }}
                      </button>
                    }
                  }
                </li>
              }
            </ul>
          }
          <div class="mb-2 inline-flex overflow-hidden rounded border" role="radiogroup" i18n-aria-label="@@executions.view" aria-label="Вигляд виконання">
            <button type="button" role="radio" class="px-3 py-1" data-testid="view-business" [class.font-semibold]="view() === 'business'" [attr.aria-checked]="view() === 'business'" (click)="setView('business')" i18n-title="@@executions.view.business.hint" title="Де зараз процес — на схемі з доріжками" i18n="@@executions.view.business">Бізнес</button>
            <button type="button" role="radio" class="px-3 py-1" data-testid="view-technical" [class.font-semibold]="view() === 'technical'" [attr.aria-checked]="view() === 'technical'" (click)="setView('technical')" i18n-title="@@executions.view.technical.hint" title="Список кроків з даними" i18n="@@executions.view.technical">Технічний</button>
          </div>
          @if (view() === 'business') {
            @if (definition(); as def) {
              <app-business-view
                class="mb-4 block h-[420px] rounded border"
                [definition]="def"
                [descriptors]="descriptors()"
                [connectors]="connectorNames()"
                [execution]="businessRun()"
                [fileName]="workflowName(d.summary.workflowId)"
                (stepPick)="pickNode($event)"
              />
            }
          }
          <div class="flex flex-wrap gap-4">
            <ol class="min-w-56" data-testid="steps" [hidden]="view() === 'business'">
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
  private readonly nodesApi = inject(NodesApiService);
  private readonly connectorsApi = inject(ConnectorsApiService);
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
  readonly correlationKey = signal('');
  /** Лише виконання з відкритими порушеннями правил переходу (F7-04). */
  readonly onlyViolations = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly selected = signal<ExecutionDetail | null>(null);
  readonly selectedStepId = signal<string | null>(null);
  /** Визначення сценарію вибраного виконання (поточна версія; вузол, якого вже немає, — за id). */
  readonly definition = signal<WorkflowDefinition | null>(null);
  private readonly nodeNames = computed<ReadonlyMap<string, string>>(() => new Map((this.definition()?.nodes ?? []).map((n) => [n.id, n.name])));
  /** «Бізнес | Технічний» (F3-69): бізнес — виконання на схемі з доріжками. */
  readonly view = signal<EditorView>(readEditorView());
  readonly descriptors = signal<NodeTypeInfo[]>([]);
  readonly connectorNames = signal<{ id: string; name: string }[]>([]);
  private businessLoaded = false;
  readonly businessRun = computed((): BusinessRun | null => {
    const detail = this.selected();
    return detail ? { state: detail.summary.state, steps: detail.steps } : null;
  });
  readonly acting = signal(false);
  readonly actionError = signal<string | null>(null);
  readonly resumeOpen = signal(false);
  /** Порушення, для якого відкрито поле причини відхилення (F7-03). */
  readonly overrideId = signal<string | null>(null);
  readonly overrideReason = signal('');
  readonly continueWithReason = $localize`:@@violations.override.block:Продовжити з причиною…`;
  readonly overrideWithReason = $localize`:@@violations.override.warn:Відхилити з причиною…`;
  readonly resumeText = signal('');
  /** Текст редактора на момент відкриття: незмінений — продовжуємо з тими самими входами. */
  private readonly resumeInitial = signal('');
  readonly resumeStep = computed(() => {
    const detail = this.selected();
    return detail ? failedStep(detail) : null;
  });
  readonly resumeError = computed(() => {
    if (!this.resumeStep() || this.resumeText() === this.resumeInitial()) return null;
    const parsed = parseItems(this.resumeText());
    return 'error' in parsed ? parsed.error : null;
  });

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
    if (this.view() === 'business') this.loadBusiness();
    this.workflowId.set(this.route.snapshot.queryParamMap.get('workflowId') || null);
    this.onlyViolations.set(this.route.snapshot.queryParamMap.get('hasOpenViolations') === 'true');
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

  setCorrelationKey(key: string): void {
    this.correlationKey.set(key.trim());
    this.resetPage();
  }

  setOnlyViolations(on: boolean): void {
    this.onlyViolations.set(on);
    this.resetPage();
  }

  goTo(page: number): void {
    this.page.set(Math.min(Math.max(1, page), this.pageCount()));
    this.load();
  }

  select(id: string): void {
    this.stopLive();
    this.actionError.set(null);
    this.resumeOpen.set(false);
    this.overrideId.set(null);
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

  openResume(): void {
    const text = JSON.stringify(this.resumeStep()?.input[0] ?? [], null, 2);
    this.resumeInitial.set(text);
    this.resumeText.set(text);
    this.resumeOpen.set(true);
  }

  resume(id: string): void {
    if (this.resumeError()) return;
    const changed = !!this.resumeStep() && this.resumeText() !== this.resumeInitial();
    const parsed = changed ? parseItems(this.resumeText()) : null;
    const items = parsed && 'items' in parsed ? parsed.items : null;
    this.acting.set(true);
    this.actionError.set(null);
    this.api
      .resume(id, items)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (summary) => {
          this.acting.set(false);
          this.resumeOpen.set(false);
          this.load();
          this.select(summary.id);
        },
        error: (e: unknown) => {
          this.acting.set(false);
          this.actionError.set(apiErrorMessage(e));
        },
      });
  }

  openOverride(violationId: string): void {
    this.overrideReason.set('');
    this.overrideId.set(violationId);
  }

  /** Відхилення правила з причиною: для `block` виконання прокидається, деталі перечитуються з Api. */
  overrideRule(id: string, violation: Violation): void {
    const reason = this.overrideReason().trim();
    if (!reason) return;
    this.acting.set(true);
    this.actionError.set(null);
    this.api
      .overrideRule(id, { ruleId: violation.ruleId, stageId: violation.stageId, reason })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.acting.set(false);
          this.overrideId.set(null);
          this.refreshSummary(id);
        },
        error: (e: unknown) => {
          this.acting.set(false);
          this.actionError.set(apiErrorMessage(e));
        },
      });
  }

  violationTitle(violation: Violation): string {
    switch (violation.status) {
      case 'resolved':
        return $localize`:@@violations.resolved:Правило виконано`;
      case 'overridden':
        return $localize`:@@violations.overridden:Правило відхилено з причиною`;
      default:
        return violation.mode === 'block'
          ? $localize`:@@violations.block:Перехід зупинено правилом`
          : $localize`:@@violations.warn:Попередження правила`;
    }
  }

  stageName(detail: ExecutionDetail, stageId: string): string {
    return detail.stages?.find((s) => s.stageId === stageId)?.name ?? stageId;
  }

  text(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  canResume(state: ExecutionState): boolean {
    return state === 'error' || state === 'timed_out';
  }

  canCancel(state: ExecutionState): boolean {
    return state === 'queued' || state === 'running' || state === 'waiting';
  }

  /** Коли виконання прокинеться: із заголовка або (після події SSE) з останнього кроку `waiting`. */
  waitingUntil(detail: ExecutionDetail): string | null {
    return detail.summary.resumeAt ?? detail.steps.filter((s) => s.status === 'waiting').at(-1)?.resumeAt ?? null;
  }

  canRetry(state: ExecutionState): boolean {
    return isFinalState(state);
  }

  workflowName(id: string): string {
    return this.workflows().find((w) => w.id === id)?.name ?? id;
  }

  setView(view: EditorView): void {
    this.view.set(view);
    writeEditorView(view, true);
    if (view === 'business') this.loadBusiness();
  }

  /** Клік на крок схеми: дані останнього кроку цього вузла під схемою. */
  pickNode(nodeId: string): void {
    const step = this.selected()?.steps.filter((s) => s.nodeId === nodeId).at(-1);
    if (step) this.selectedStepId.set(step.id);
  }

  /** Дескриптори (порти й назви гілок) і назви конекторів (доріжки) — лише коли вперше відкрили бізнес-вигляд. */
  private loadBusiness(): void {
    if (this.businessLoaded) return;
    this.businessLoaded = true;
    forkJoin({
      descriptors: this.nodesApi.list().pipe(catchError(() => of([] as NodeTypeInfo[]))),
      connectors: this.connectorsApi.list().pipe(
        map((page) => page.items.map((c) => ({ id: c.id, name: c.name }))),
        catchError(() => of([])),
      ),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(({ descriptors, connectors }) => {
        this.descriptors.set(descriptors);
        this.connectorNames.set(connectors);
      });
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
      case 'waiting':
        return 'text-blue-700';
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
      case 'waiting':
        return 'text-blue-700';
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
      correlationKey: this.correlationKey() || null,
      hasOpenViolations: this.onlyViolations() || null,
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
        next: (w) => this.definition.set(w.definition),
        error: () => this.definition.set(null),
      });
  }
}
