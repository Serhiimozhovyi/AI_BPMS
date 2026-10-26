import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { providePublicForm } from '../../forms/form-field-types';
import { formFields, toPayload } from '../../forms/form-fields';
import { FormFieldError } from '../../forms/forms-api.service';
import { TaskDecision, TaskDetail, TasksApiService, assigneeLabel, taskStatusLabel } from '../../tasks/tasks-api.service';

/** Тіло problem+json від Api. */
interface Problem {
  code?: string;
  detail?: string;
  errors?: FormFieldError[];
}

/**
 * `/tasks/:id` (F6-02): картка задачі `core.approval` — за id зі списку або токеном з `$execution.taskUrl`. Поля рішення —
 * ngx-formly за `fields` задачі (той самий рендер, що й публічні форми, F6-03), кнопка на кожне рішення. Чужа задача —
 * повідомлення замість форми (403 `TASK_FORBIDDEN`).
 */
@Component({
  selector: 'app-task-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, ReactiveFormsModule, MatButtonModule, MatCardModule, MatProgressBarModule, FormlyForm],
  providers: [providePublicForm()],
  template: `
    <section class="p-6">
      <a class="mb-4 inline-block text-sm underline" routerLink="/tasks" i18n="@@task.back">← До задач</a>
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" data-testid="task-loading" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="task-error">{{ error() }}</p>
      }
      @if (detail(); as d) {
        <mat-card class="task-card" appearance="outlined">
          <mat-card-content>
            <h1 class="mat-font-headline-sm mb-2" data-testid="task-title">{{ d.task.title }}</h1>
            <p class="mb-2 text-sm opacity-70">
              {{ d.task.workflowName }} · <span data-testid="task-status">{{ statusLabel(d) }}</span> ·
              <span i18n="@@task.assignee">виконавець</span> {{ assignee(d) }}
              @if (d.task.dueAt) {
                · <span i18n="@@task.due">до</span> {{ d.task.dueAt | date: 'dd.MM.yyyy HH:mm' }}
              }
            </p>
            @if (d.description) {
              <p class="task-card__description mb-4" data-testid="task-description">{{ d.description }}</p>
            }

            @if (d.task.canComplete) {
              <form [formGroup]="form" novalidate data-testid="task-form" (ngSubmit)="$event.preventDefault()">
                <formly-form [form]="form" [fields]="fields()" [model]="model" />
                @if (submitError()) {
                  <p class="mb-3 text-red-700" role="alert" data-testid="task-submit-error">{{ submitError() }}</p>
                }
                @if (submitting()) {
                  <mat-progress-bar class="mb-4" mode="indeterminate" />
                }
                <div class="flex flex-wrap gap-2">
                  @for (decision of d.decisions; track decision.key; let first = $first) {
                    @if (first) {
                      <button mat-flat-button type="button" [disabled]="submitting()" [attr.data-testid]="'decision-' + decision.key" (click)="decide(decision)">
                        {{ decision.label }}
                      </button>
                    } @else {
                      <button mat-stroked-button type="button" [disabled]="submitting()" [attr.data-testid]="'decision-' + decision.key" (click)="decide(decision)">
                        {{ decision.label }}
                      </button>
                    }
                  }
                </div>
              </form>
            } @else if (d.task.status === 'done') {
              <p data-testid="task-decided">
                <span i18n="@@task.decided">Рішення:</span> <strong>{{ decisionLabel(d) }}</strong>
                @if (d.completedBy) {
                  · {{ d.completedBy.email }}
                }
                @if (d.task.completedAt) {
                  · {{ d.task.completedAt | date: 'dd.MM.yyyy HH:mm' }}
                }
              </p>
            }
          </mat-card-content>
        </mat-card>
      }
    </section>
  `,
  styles: `
    .task-card {
      max-width: 640px;
      padding: 8px;
    }

    .task-card__description {
      white-space: pre-line;
    }
  `,
})
export class TaskPage implements OnInit {
  private readonly api = inject(TasksApiService);
  private readonly route = inject(ActivatedRoute);

  protected readonly detail = signal<TaskDetail | null>(null);
  protected readonly fields = signal<FormlyFieldConfig[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly submitError = signal('');
  protected readonly submitting = signal(false);
  protected readonly form = new FormGroup({});
  protected model: Record<string, unknown> = {};

  private id = '';

  ngOnInit(): void {
    this.id = this.route.snapshot.paramMap.get('id') ?? '';
    this.api.get(this.id).subscribe({
      next: (detail) => this.show(detail),
      error: (error: unknown) => {
        this.loading.set(false);
        this.error.set(this.problemMessage(error));
      },
    });
  }

  protected statusLabel(detail: TaskDetail): string {
    return taskStatusLabel(detail.task.status);
  }

  protected assignee(detail: TaskDetail): string {
    return assigneeLabel(detail.task.assignee);
  }

  protected decisionLabel(detail: TaskDetail): string {
    return detail.decisions.find((d) => d.key === detail.task.decision)?.label ?? detail.task.decision ?? '';
  }

  protected decide(decision: TaskDecision): void {
    const detail = this.detail();
    if (!detail || this.submitting()) return;
    this.submitError.set('');
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.submitError.set($localize`:@@task.invalid:Перевірте позначені поля.`);
      return;
    }
    this.submitting.set(true);
    const data = detail.fields ? toPayload(detail.fields, this.model).fields : {};
    this.api.complete(detail.task.id, decision.key, data).subscribe({
      next: (done) => {
        this.submitting.set(false);
        this.show(done);
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const problem = this.problemOf(error);
        if (problem?.code === 'FORM_INVALID' && problem.errors?.length) {
          for (const { field, message } of problem.errors) {
            const control = field ? this.form.get(field) : null;
            control?.setErrors({ ...(control.errors ?? {}), server: message });
            control?.markAsTouched();
          }
          this.submitError.set($localize`:@@task.invalid:Перевірте позначені поля.`);
          return;
        }
        this.submitError.set(this.problemMessage(error));
      },
    });
  }

  private show(detail: TaskDetail): void {
    this.detail.set(detail);
    this.fields.set(detail.fields ? formFields(detail.fields) : []);
    this.loading.set(false);
  }

  private problemMessage(error: unknown): string {
    const problem = this.problemOf(error);
    if (problem?.code === 'TASK_FORBIDDEN') {
      return $localize`:@@task.forbidden:Це задача іншого виконавця: рішення приймає він або адміністратор.`;
    }
    if (error instanceof HttpErrorResponse && error.status === 404) {
      return $localize`:@@task.notFound:Задачу не знайдено: посилання застаріле або виконання видалено.`;
    }
    return apiErrorMessage(error);
  }

  private problemOf(error: unknown): Problem | null {
    return error instanceof HttpErrorResponse && typeof error.error === 'object' && error.error !== null ? (error.error as Problem) : null;
  }
}
