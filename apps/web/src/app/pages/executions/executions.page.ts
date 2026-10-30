import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { DateAdapter, MAT_DATE_FORMATS, MAT_DATE_LOCALE, MAT_NATIVE_DATE_FORMATS } from '@angular/material/core';
import { MatDatepickerModule } from '@angular/material/datepicker';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Subscription, catchError, forkJoin, map, of } from 'rxjs';
import { BusinessViewComponent } from '../../business-view/business-view.component';
import { BusinessRun } from '../../business-view/business-run';
import { EditorView, readEditorView, writeEditorView } from '../../business-view/editor-view';
import { NodeTypeInfo, NodesApiService } from '../../nodes/nodes-api.service';
import { NodeRunDataComponent } from '../../canvas/node-run-data.component';
import { executionErrorPrompt } from '../../assistant/execution-error-prompt';
import { GlobalAssistantService } from '../../assistant/global-assistant.service';
import { StageStripComponent } from '../../executions/stage-strip.component';
import { ChipComponent, ChipTone } from '../../shared/chip/chip.component';
import {
  ExecutionsQuery,
  executionsQueryParams,
  formatExecutionDuration,
  fromIsoDay,
  parseExecutionsQuery,
  sameExecutionsQuery,
  toIsoDay,
} from '../../executions/executions-query';
import { CardTableDirective } from '../../shared/card-table.directive';
import { UkDateAdapter } from '../../shared/uk-date-adapter';
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
 *
 * F3-81: виконання має власну адресу `/executions/:id` — деталі в правій панелі поруч зі списком (на вузьких екранах
 * замість списку), «Назад» і пряме посилання працюють; фільтри живуть у query-параметрах адреси.
 */
@Component({
  selector: 'app-executions-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CardTableDirective,
    DatePipe,
    RouterLink,
    MatButtonModule,
    MatDatepickerModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    BusinessViewComponent,
    NodeRunDataComponent,
    StageStripComponent,
    ChipComponent,
  ],
  // Дати фільтра — `дд.мм.рррр` (F3-81): нативний `input[type=date]` показував би формат локалі браузера.
  providers: [
    { provide: MAT_DATE_LOCALE, useValue: 'uk-UA' },
    { provide: DateAdapter, useClass: UkDateAdapter },
    { provide: MAT_DATE_FORMATS, useValue: MAT_NATIVE_DATE_FORMATS },
  ],
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-4" i18n="@@executions.title">Виконання</h1>

      <div class="flex flex-col gap-6 lg:flex-row lg:items-start">
      <div [class]="listClass()" data-testid="journal">
      <div class="mb-4 flex flex-wrap items-start gap-3" data-testid="filters">
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-56">
          <mat-label i18n="@@executions.filter.workflow">Сценарій</mat-label>
          <mat-select data-testid="filter-workflow" [value]="workflowId()" (selectionChange)="setWorkflow($event.value ?? '')">
            <mat-option [value]="null" i18n="@@executions.filter.allWorkflows">Усі сценарії</mat-option>
            @for (w of workflows(); track w.id) {
              <mat-option [value]="w.id">{{ w.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-44">
          <mat-label i18n="@@executions.filter.status">Стан</mat-label>
          <mat-select data-testid="filter-status" [value]="status()" (selectionChange)="setStatus($event.value ?? '')">
            <mat-option [value]="null" i18n="@@executions.filter.allStatuses">Усі стани</mat-option>
            @for (s of states; track s) {
              <mat-option [value]="s">{{ stateLabel(s) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-44">
          <mat-label i18n="@@executions.filter.from">З</mat-label>
          <input matInput [matDatepicker]="fromPicker" data-testid="filter-from" placeholder="дд.мм.рррр" i18n-placeholder="@@executions.filter.datePlaceholder" [value]="fromDate()" (dateChange)="setFromDate($event.value)" />
          <mat-datepicker-toggle matIconSuffix [for]="fromPicker" />
          <mat-datepicker #fromPicker />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-44">
          <mat-label i18n="@@executions.filter.to">По</mat-label>
          <input matInput [matDatepicker]="toPicker" data-testid="filter-to" placeholder="дд.мм.рррр" i18n-placeholder="@@executions.filter.datePlaceholder" [value]="toDate()" (dateChange)="setToDate($event.value)" />
          <mat-datepicker-toggle matIconSuffix [for]="toPicker" />
          <mat-datepicker #toPicker />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="w-48">
          <mat-label i18n="@@executions.filter.correlationKey">Ключ кореляції</mat-label>
          <input
            matInput
            type="search"
            data-testid="filter-correlation-key"
            placeholder="напр. id угоди"
            i18n-placeholder="@@executions.filter.correlationKey.placeholder"
            [value]="correlationKey()"
            (change)="setCorrelationKey($any($event.target).value)"
          />
        </mat-form-field>
        <label class="flex items-center gap-2 pt-4 text-sm">
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
        <div class="overflow-x-auto">
        <table appCardTable class="mb-2 w-full text-left" data-testid="executions-table">
          <thead>
            <tr>
              <th class="p-2" data-no-label i18n="@@executions.col.startedAt">Початок</th>
              <th class="p-2" i18n="@@executions.col.workflow">Сценарій</th>
              <th class="p-2" i18n="@@executions.col.mode">Режим</th>
              <th class="p-2" i18n="@@executions.col.state">Стан</th>
              <th class="p-2" i18n="@@executions.col.duration">Тривалість</th>
              <th class="p-2" i18n="@@executions.col.finishedAt">Завершено</th>
              <th class="p-2" i18n="@@executions.col.correlationKey">Ключ</th>
            </tr>
          </thead>
          <tbody>
            @for (e of executions(); track e.id) {
              <tr [attr.data-execution]="e.id" [class.font-semibold]="selected()?.summary?.id === e.id">
                <td class="p-2">
                  <button mat-button type="button" data-testid="open-execution" (click)="select(e.id)">{{ e.startedAt ? (e.startedAt | date: dateTime) : '—' }}</button>
                </td>
                <td class="p-2">
                  <a class="hover:underline" data-testid="workflow-link" [routerLink]="['/workflows', e.workflowId]">{{ workflowName(e.workflowId) }}</a>
                </td>
                <td class="p-2">{{ modeLabel(e.mode) }}</td>
                <td class="p-2" data-testid="state"><bp-chip [tone]="stateTone(e.state)">{{ stateLabel(e.state) }}</bp-chip></td>
                <td class="p-2 whitespace-nowrap" data-testid="duration">{{ duration(e) }}</td>
                <td class="p-2">{{ e.finishedAt ? (e.finishedAt | date: dateTime) : '—' }}</td>
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
        </div>
      }

      @if (total() > pageSize) {
        <div class="mb-6 flex items-center gap-2" data-testid="pager">
          <button mat-button type="button" data-testid="prev-page" [disabled]="page() <= 1" (click)="goTo(page() - 1)" i18n="@@executions.prev">Назад</button>
          <span data-testid="page-info" i18n="@@executions.pageInfo">Сторінка {{ page() }} з {{ pageCount() }}</span>
          <button mat-button type="button" data-testid="next-page" [disabled]="page() >= pageCount()" (click)="goTo(page() + 1)" i18n="@@executions.next">Далі</button>
        </div>
      }

      </div>
      @if (selectedId()) {
      <div class="w-full min-w-0 lg:w-1/2 xl:w-7/12" data-testid="detail-pane">
        <button mat-button type="button" class="mb-2" data-testid="detail-back" (click)="close()">
          <span class="material-symbols-outlined" aria-hidden="true">arrow_back</span>
          <span i18n="@@executions.back">Назад</span>
        </button>
        @if (detailLoading()) {
          <mat-progress-bar mode="indeterminate" />
        }
      @if (selected(); as d) {
        <article data-testid="execution-detail" [attr.data-state]="d.summary.state">
          <div class="mb-2 flex flex-wrap items-center gap-4">
            <h2 class="text-lg font-semibold">
              <a [routerLink]="['/workflows', d.summary.workflowId]" data-testid="detail-workflow">{{ workflowName(d.summary.workflowId) }}</a>
            </h2>
            <bp-chip [tone]="stateTone(d.summary.state)" data-testid="detail-state">{{ stateLabel(d.summary.state) }}</bp-chip>
            <span class="opacity-70">{{ modeLabel(d.summary.mode) }}</span>
            <a mat-stroked-button data-testid="open-in-editor" [routerLink]="['/workflows', d.summary.workflowId]" [queryParams]="editorParams()" i18n="@@executions.openInEditor">Відкрити в редакторі</a>
            @if (d.summary.state === 'waiting' && waitingUntil(d); as until) {
              <span data-testid="detail-resume-at" i18n="@@executions.waitingUntil">до {{ until | date: dateTime }}</span>
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
                    <bp-chip [tone]="stateTone(c.state)">{{ stateLabel(c.state) }}</bp-chip>
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
          @if (assistant.available() && d.summary.state === 'error') {
            <button mat-stroked-button type="button" class="mb-2" data-testid="explain-error" (click)="explainError(d)" i18n="@@executions.explainError">
              Розібрати з помічником
            </button>
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
                    <span i18n="@@violations.createdAt">Виникло {{ v.createdAt | date: dateTime }}</span>
                    @if (v.resolvedAt; as at) {
                      <span> · </span>
                      <span i18n="@@violations.resolvedAt">закрито {{ at | date: dateTime }}</span>
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
      </div>
      }
      </div>
    </section>
  `,
})
export class ExecutionsPage implements OnInit {
  private readonly api = inject(ExecutionsApiService);
  private readonly workflowsApi = inject(WorkflowsApiService);
  private readonly nodesApi = inject(NodesApiService);
  private readonly connectorsApi = inject(ConnectorsApiService);
  private readonly route = inject(ActivatedRoute);
  protected readonly assistant = inject(GlobalAssistantService);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** Формат дати й часу в журналі — по-українськи, незалежно від локалі браузера. */
  readonly dateTime = 'dd.MM.yyyy HH:mm:ss';
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

  /** `:id` з адреси `/executions/:id` — виконання в правій панелі (F3-81). */
  readonly selectedId = signal<string | null>(null);
  readonly detailLoading = signal(false);
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

  readonly fromDate = computed(() => fromIsoDay(this.from()));
  readonly toDate = computed(() => fromIsoDay(this.to()));
  /** Класи колонки списку: поруч із деталями — половина ширини, на вузьких екранах деталі замість списку. */
  readonly listClass = computed(() => (this.selectedId() ? 'hidden min-w-0 lg:block lg:w-1/2 xl:w-5/12' : 'w-full min-w-0'));
  /** `?node=` для «Відкрити в редакторі»: вузол, що впав, виділяється на канвасі. */
  readonly editorParams = computed(() => {
    const failed = this.resumeStep();
    return failed ? { node: failed.nodeId } : null;
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
    this.workflowsApi
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (page) => this.workflows.set(page.items), error: () => this.workflows.set([]) });
    // Адреса — джерело фільтрів і вибраного виконання: перший збіг завантажує журнал, наступні (перехід з «Огляду»,
    // «Назад», посилання на інше виконання) — лише коли справді щось змінилось.
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => this.applyQuery(parseExecutionsQuery(params)));
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => this.showId(params.get('id')));
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

  setFromDate(date: Date | null): void {
    this.setFrom(date ? toIsoDay(date) : '');
  }

  setToDate(date: Date | null): void {
    this.setTo(date ? toIsoDay(date) : '');
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
    this.syncUrl();
  }

  /** Відкрити виконання: перехід на `/executions/:id` із збереженням фільтрів; деталі завантажує {@link showId}. */
  select(id: string): void {
    void this.router.navigate(['/executions', id], { queryParamsHandling: 'preserve' });
  }

  /** «Назад»: закрити деталі, фільтри лишаються. */
  close(): void {
    void this.router.navigate(['/executions'], { queryParamsHandling: 'preserve' });
  }

  duration(execution: ExecutionSummary): string {
    return formatExecutionDuration(execution.startedAt, execution.finishedAt);
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

  /** «Розібрати з помічником»: панель відкривається з запитом про помилку; id виконання йде в контексті з адреси (F3-112). */
  protected explainError(detail: ExecutionDetail): void {
    const prompt = executionErrorPrompt(detail, this.workflowName(detail.summary.workflowId));
    this.assistant.show({ text: prompt.text, send: true, nodeId: prompt.nodeId ?? undefined });
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

  stateTone(state: ExecutionState): ChipTone {
    switch (state) {
      case 'success':
        return 'green';
      case 'error':
      case 'timed_out':
        return 'red';
      case 'waiting':
        return 'blue';
      case 'running':
      case 'queued':
        return 'teal';
      default:
        return 'gray';
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
    this.syncUrl();
  }

  private query(): ExecutionsQuery {
    return {
      workflowId: this.workflowId(),
      status: this.status(),
      from: this.from(),
      to: this.to(),
      correlationKey: this.correlationKey(),
      hasOpenViolations: this.onlyViolations(),
      page: this.page(),
    };
  }

  private queryApplied = false;

  /** Фільтри з адреси в стан сторінки; збіг із поточним станом (наш власний `syncUrl`) — нічого не робить. */
  private applyQuery(query: ExecutionsQuery): void {
    const first = !this.queryApplied;
    this.queryApplied = true;
    if (!first && sameExecutionsQuery(query, this.query())) return;
    this.workflowId.set(query.workflowId);
    this.status.set(query.status);
    this.from.set(query.from);
    this.to.set(query.to);
    this.correlationKey.set(query.correlationKey);
    this.onlyViolations.set(query.hasOpenViolations);
    this.page.set(query.page);
    this.load();
  }

  /** Поточні фільтри — у query-параметри адреси (без запису в історію): посилання відтворює той самий журнал. */
  private syncUrl(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: executionsQueryParams(this.query()),
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  /** Вибране в адресі виконання: завантажити деталі або закрити панель. */
  private showId(id: string | null): void {
    if (id === this.selectedId()) return;
    this.selectedId.set(id);
    this.stopLive();
    this.actionError.set(null);
    this.resumeOpen.set(false);
    this.overrideId.set(null);
    this.selected.set(null);
    if (!id) {
      this.definition.set(null);
      this.detailLoading.set(false);
      return;
    }
    this.detailLoading.set(true);
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.detailLoading.set(false);
          if (this.selectedId() === id) this.show(detail);
        },
        error: (e: unknown) => {
          this.detailLoading.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
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
