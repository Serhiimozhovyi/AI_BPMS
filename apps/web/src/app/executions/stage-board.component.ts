import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { providePublicForm } from '../forms/form-field-types';
import { formFields, toPayload } from '../forms/form-fields';
import { TaskDecision } from '../tasks/tasks-api.service';
import { Violation } from './executions-api.service';
import { ContractStage, ExecutionStages, StageAction, StageActionKind, StageActionRequest, StagesApiService } from './stages-api.service';
import { stageStatusLabel } from './stage-strip.component';

/** Подія `acted` (у `<openbp-canvas>` — `openbp-action`): дію етапу виконано. */
export interface StageActedEvent {
  executionId: string;
  stageId: string;
  actionId: string;
  kind: StageActionKind;
  /** Рішення задачі (для `task`). */
  decision?: string | null;
}

/** Подія `failed`: дію не виконано або етапи не завантажились. */
export interface StageFailedEvent {
  code: string;
  message: string;
}

/** Форма полів рішення задачі. */
interface TaskForm {
  form: FormGroup;
  model: Record<string, unknown>;
  fields: FormlyFieldConfig[];
}

const REASON_REQUIRED = 'STAGE_RULE_REASON_REQUIRED';

/**
 * Етапи виконання колонками (F7-05, ADR-004 §4) — бізнес-вигляд `<openbp-canvas view="business" execution-id>`. Усе з
 * контракту `GET /executions/{id}/stages`: стан і строк етапу, порушення, невиконані правила наступного переходу й дії,
 * доступні користувачу. Рішення задачі — поля ngx-formly (як картка задачі) і кнопка на кожне рішення; причина —
 * коли правило не виконане (одразу або після 422 `STAGE_RULE_REASON_REQUIRED`) і для відхилення.
 */
@Component({
  selector: 'app-stage-board',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReactiveFormsModule, MatButtonModule, MatProgressBarModule, FormlyForm],
  providers: [providePublicForm()],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" data-testid="stage-board-loading" />
    }
    @if (error()) {
      <p role="alert" class="board__error" data-testid="stage-board-error">{{ error() }}</p>
    }
    @if (data(); as d) {
      @if (d.stages.length === 0) {
        <p class="board__empty" role="status" data-testid="stage-board-empty" i18n="@@stageBoard.empty">У сценарії немає етапів.</p>
      }
      <div class="board" data-testid="stage-board">
        @for (s of d.stages; track s.id) {
          <section class="column column--{{ s.status }}" [class.column--overdue]="s.overdue" [attr.data-stage]="s.id" [attr.data-status]="s.status">
            <header class="column__head">
              <span class="column__number" i18n="@@stages.number">Етап {{ s.position + 1 }}</span>
              <span class="column__name">{{ s.name }}</span>
              <span class="column__state" data-testid="stage-state">{{ label(s) }}</span>
              @if (s.dueAt && (s.status === 'active' || s.status === 'pending')) {
                <span class="column__due" data-testid="stage-due" i18n="@@stages.due">до {{ s.dueAt | date: 'short' }}</span>
              }
            </header>
            @if (owner(s); as o) {
              <p class="column__owner" data-testid="stage-owner"><span i18n="@@stageBoard.owner">Виконавець:</span> {{ o }}</p>
            }
            @for (u of s.unmet; track u.ruleId) {
              <p class="note note--{{ u.mode }}" data-testid="stage-unmet" [attr.data-rule]="u.ruleId">{{ u.message || u.ruleId }}</p>
            }
            @for (v of s.violations; track v.id) {
              <p class="violation violation--{{ v.status }}" data-testid="stage-violation" [attr.data-rule]="v.ruleId">
                {{ v.message || v.ruleId }} · {{ violationLabel(v) }}
                @if (v.reason) {
                  <span class="violation__reason">«{{ v.reason }}»</span>
                }
              </p>
            }
            @for (a of s.actions; track a.id) {
              <div class="action" data-testid="stage-action" [attr.data-action]="a.id">
                <p class="action__title">{{ a.title }}</p>
                @if (a.kind === 'task' && taskForm(a); as t) {
                  <form [formGroup]="t.form" novalidate (ngSubmit)="$event.preventDefault()">
                    <formly-form [form]="t.form" [fields]="t.fields" [model]="t.model" />
                  </form>
                }
                @if (a.kind === 'override' || a.reasonRequired || reasonAsked(a)) {
                  <label class="action__reason">
                    <span i18n="@@stageBoard.reason">Причина</span>
                    <textarea
                      rows="2"
                      data-testid="stage-reason"
                      [value]="reason(a)"
                      (input)="setReason(a, $any($event.target).value)"
                    ></textarea>
                  </label>
                }
                @if (actionError(a); as message) {
                  <p role="alert" class="action__error" data-testid="stage-action-error">{{ message }}</p>
                }
                <div class="action__buttons">
                  @switch (a.kind) {
                    @case ('task') {
                      @for (decision of a.decisions ?? []; track decision.key; let first = $first) {
                        @if (first) {
                          <button mat-flat-button type="button" [disabled]="busy() !== null" [attr.data-testid]="'decision-' + decision.key" (click)="decide(s, a, decision)">
                            {{ decision.label }}
                          </button>
                        } @else {
                          <button mat-stroked-button type="button" [disabled]="busy() !== null" [attr.data-testid]="'decision-' + decision.key" (click)="decide(s, a, decision)">
                            {{ decision.label }}
                          </button>
                        }
                      }
                    }
                    @case ('override') {
                      <button mat-stroked-button type="button" data-testid="stage-override" [disabled]="busy() !== null" (click)="run(s, a)" i18n="@@stageBoard.override">
                        Відхилити правило
                      </button>
                    }
                    @case ('resume') {
                      <button mat-stroked-button type="button" data-testid="stage-resume" [disabled]="busy() !== null" (click)="run(s, a)" i18n="@@stageBoard.resume">
                        Продовжити
                      </button>
                    }
                  }
                </div>
              </div>
            }
          </section>
        }
      </div>
    }
  `,
  styles: `
    .board {
      display: flex;
      gap: 12px;
      overflow-x: auto;
      padding-bottom: 8px;
    }
    .column {
      display: flex;
      flex: 0 0 260px;
      flex-direction: column;
      gap: 8px;
      padding: 12px;
      border: 1px solid var(--bp-line-2);
      border-top: 4px solid var(--bp-line-3);
      border-radius: var(--bp-radius);
      background: var(--bp-white);
      color: var(--bp-text);
      font-size: 13px;
    }
    .column--active {
      border-top-color: var(--bp-primary);
    }
    .column--done {
      border-top-color: var(--bp-success);
    }
    .column--cancelled,
    .column--overdue {
      border-top-color: var(--bp-danger);
    }
    .column--skipped {
      border-top-style: dashed;
      background: var(--bp-surface-2);
      color: var(--bp-text-muted);
    }
    .column__head {
      display: flex;
      flex-direction: column;
    }
    .column__number,
    .column__state,
    .column__due,
    .column__owner {
      font-size: 11px;
      color: var(--bp-text-muted);
    }
    .column__name {
      font-weight: 500;
      font-size: 14px;
    }
    .note,
    .violation {
      margin: 0;
      padding: 6px 8px;
      border-radius: var(--bp-radius-sm);
    }
    .note--warn {
      background: var(--bp-yellow-15);
    }
    .note--block,
    .violation--open {
      background: var(--bp-danger-15);
    }
    .violation--resolved,
    .violation--overridden {
      background: var(--bp-bg);
    }
    .violation__reason {
      display: block;
      font-style: italic;
    }
    .action {
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding-top: 8px;
      border-top: 1px solid var(--bp-line-2);
    }
    .action__title {
      margin: 0;
      font-weight: 500;
    }
    .action__reason {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .action__reason textarea {
      padding: 6px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius-sm);
      font: inherit;
    }
    .action__buttons {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .action__error,
    .board__error {
      margin: 0;
      color: var(--bp-danger);
    }
  `,
})
export class StageBoardComponent {
  private readonly api = inject(StagesApiService);

  /** Виконання. */
  readonly executionId = input.required<string>();

  /** Дію виконано. */
  readonly acted = output<StageActedEvent>();

  /** Помилка завантаження або дії. */
  readonly failed = output<StageFailedEvent>();

  protected readonly data = signal<ExecutionStages | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  protected readonly busy = signal<string | null>(null);
  private readonly reasons = signal<Record<string, string>>({});
  private readonly asked = signal<ReadonlySet<string>>(new Set());
  private readonly errors = signal<Record<string, string>>({});
  private readonly forms = new Map<string, TaskForm>();

  constructor() {
    effect(() => this.load(this.executionId()));
  }

  protected label(stage: ContractStage): string {
    return stageStatusLabel(stage);
  }

  protected owner(stage: ContractStage): string {
    return stage.owner?.lane || stage.owner?.role || stage.owner?.expression || '';
  }

  protected violationLabel(violation: Violation): string {
    switch (violation.status) {
      case 'open':
        return $localize`:@@stageBoard.violation.open:не виправлено`;
      case 'resolved':
        return $localize`:@@stageBoard.violation.resolved:виправлено`;
      default:
        return $localize`:@@stageBoard.violation.overridden:відхилено з причиною`;
    }
  }

  protected taskForm(action: StageAction): TaskForm | null {
    if (!action.fields) return null;
    let form = this.forms.get(action.id);
    if (!form) {
      form = { form: new FormGroup({}), model: {}, fields: formFields(action.fields) };
      this.forms.set(action.id, form);
    }
    return form;
  }

  protected reason(action: StageAction): string {
    return this.reasons()[action.id] ?? '';
  }

  protected setReason(action: StageAction, value: string): void {
    this.reasons.update((r) => ({ ...r, [action.id]: value }));
  }

  protected reasonAsked(action: StageAction): boolean {
    return this.asked().has(action.id);
  }

  protected actionError(action: StageAction): string {
    return this.errors()[action.id] ?? '';
  }

  protected decide(stage: ContractStage, action: StageAction, decision: TaskDecision): void {
    const form = this.taskForm(action);
    if (form) {
      form.form.markAllAsTouched();
      if (form.form.invalid) {
        this.setError(action, $localize`:@@task.invalid:Перевірте позначені поля.`);
        return;
      }
    }
    const data = form && action.fields ? toPayload(action.fields, form.model).fields : null;
    this.act(stage, action, { decision: decision.key, data }, decision.key);
  }

  protected run(stage: ContractStage, action: StageAction): void {
    this.act(stage, action, {});
  }

  private act(stage: ContractStage, action: StageAction, request: StageActionRequest, decision?: string): void {
    if (this.busy() !== null) return;
    const reason = this.reason(action).trim();
    if ((action.kind === 'override' || action.reasonRequired || this.reasonAsked(action)) && !reason) {
      this.setError(action, $localize`:@@stageBoard.reasonRequired:Вкажіть причину, чому процес має йти далі.`);
      return;
    }
    this.setError(action, '');
    this.busy.set(action.id);
    const executionId = this.executionId();
    this.api.act(executionId, stage.id, action.id, { ...request, reason: reason || null }).subscribe({
      next: (stages) => {
        this.busy.set(null);
        this.forms.delete(action.id);
        this.data.set(stages);
        this.acted.emit({ executionId, stageId: stage.id, actionId: action.id, kind: action.kind, decision: decision ?? null });
      },
      error: (error: unknown) => {
        this.busy.set(null);
        const code = this.code(error);
        if (code === REASON_REQUIRED) this.asked.update((s) => new Set([...s, action.id]));
        const message = apiErrorMessage(error);
        this.setError(action, message);
        this.failed.emit({ code: code || 'ERROR', message });
      },
    });
  }

  private load(executionId: string): void {
    this.loading.set(true);
    this.error.set('');
    this.forms.clear();
    this.api.get(executionId).subscribe({
      next: (stages) => {
        this.loading.set(false);
        this.data.set(stages);
      },
      error: (error: unknown) => {
        this.loading.set(false);
        const message = apiErrorMessage(error);
        this.error.set(message);
        this.failed.emit({ code: this.code(error) || 'ERROR', message });
      },
    });
  }

  private setError(action: StageAction, message: string): void {
    this.errors.update((e) => ({ ...e, [action.id]: message }));
  }

  private code(error: unknown): string {
    const body = error instanceof HttpErrorResponse ? (error.error as { code?: unknown } | null) : null;
    return body && typeof body.code === 'string' ? body.code : '';
  }
}
