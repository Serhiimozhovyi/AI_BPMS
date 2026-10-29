import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { providePublicForm } from '../../forms/form-field-types';
import { formFields, toPayload } from '../../forms/form-fields';
import { PageSubmitResult, ProjectPageView, ProjectPagesApiService } from '../../projects/project-pages-api.service';

interface FieldError {
  field: string;
  message: string;
}

/**
 * Форма сторінки `form` (F8-04): поля з JSON Schema сторінки тим самим рушієм форм, що й публічні форми F6-03; значення
 * рядка для зміни читає сама, `prefill` підставляє початкові значення (напр. ключ батьківського рядка підлеглого списку).
 * Використовується на сторінці застосунку й у боковій панелі (F8-16).
 */
@Component({
  selector: 'app-page-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatProgressBarModule, FormlyForm],
  providers: [providePublicForm()],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (loadError()) {
      <p role="alert" class="mb-2 text-red-700" data-testid="app-form-load-error">{{ loadError() }}</p>
    }
    <form class="max-w-xl" [formGroup]="form" (submit)="$event.preventDefault(); submit()" novalidate data-testid="app-form">
      <formly-form [form]="form" [fields]="fields()" [model]="model" />
      @if (submitError()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="app-form-error">{{ submitError() }}</p>
      }
      @if (saved()) {
        <p role="status" class="mb-2" data-testid="app-form-saved" i18n="@@app.page.saved">Збережено.</p>
      }
      <button mat-flat-button type="submit" data-testid="app-submit" [disabled]="submitting() || loading()" i18n="@@app.page.submit">Зберегти</button>
    </form>
  `,
})
export class PageFormComponent implements OnInit {
  private readonly api = inject(ProjectPagesApiService);

  readonly projectId = input.required<string>();
  readonly view = input.required<ProjectPageView>();
  /** Ключ рядка для форми зміни. */
  readonly rowKey = input<string | null>(null);
  /** Початкові значення нового запису; беруться лише поля, що є у формі. */
  readonly prefill = input<Record<string, unknown>>({});
  /** Форму збережено (або сценарій форми відповів). */
  readonly submitted = output<PageSubmitResult>();

  readonly form = new FormGroup({});
  readonly fields = signal<FormlyFieldConfig[]>([]);
  model: Record<string, unknown> = {};
  readonly loading = signal(false);
  readonly loadError = signal('');
  readonly submitting = signal(false);
  readonly submitError = signal('');
  readonly saved = signal(false);

  ngOnInit(): void {
    const view = this.view();
    this.form.reset();
    const known = new Set(Object.keys((view.schema?.['properties'] as Record<string, unknown> | undefined) ?? {}));
    this.model = Object.fromEntries(Object.entries(this.prefill()).filter(([name]) => known.has(name)));
    this.fields.set(formFields(view.schema));
    const rowKey = this.rowKey();
    if (!rowKey) return;
    this.loading.set(true);
    this.api.row(this.projectId(), view.page.key, rowKey).subscribe({
      next: (record) => {
        this.model = { ...record.row.data };
        this.fields.set(formFields(view.schema));
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.loadError.set(
          e instanceof HttpErrorResponse && e.status === 404
            ? $localize`:@@app.page.notFound:Сторінку чи запис не знайдено або вони не для вашої ролі.`
            : apiErrorMessage(e),
        );
      },
    });
  }

  submit(): void {
    const view = this.view();
    if (this.submitting()) return;
    this.submitError.set('');
    this.saved.set(false);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.submitError.set($localize`:@@form.invalid:Перевірте позначені поля.`);
      return;
    }
    this.submitting.set(true);
    this.api.submit(this.projectId(), view.page.key, toPayload(view.schema, this.model).fields, this.rowKey()).subscribe({
      next: (result) => {
        this.submitting.set(false);
        if (!result.then && !result.result?.message) this.saved.set(true);
        this.submitted.emit(result);
      },
      error: (e: unknown) => {
        this.submitting.set(false);
        const problem = e instanceof HttpErrorResponse && typeof e.error === 'object' && e.error ? (e.error as { code?: string; errors?: FieldError[] }) : null;
        if (problem?.code === 'PAGE_FORM_INVALID' && problem.errors?.length) {
          this.showFieldErrors(problem.errors);
          return;
        }
        this.submitError.set(apiErrorMessage(e));
      },
    });
  }

  /** Помилки полів з Api — під полями; без поля — над кнопкою. */
  private showFieldErrors(errors: FieldError[]): void {
    const general: string[] = [];
    for (const { field, message } of errors) {
      const control = field ? this.form.get(field) : null;
      if (control) {
        control.setErrors({ ...(control.errors ?? {}), server: message });
        control.markAsTouched();
      } else {
        general.push(message);
      }
    }
    this.submitError.set(general.length ? general.join(' ') : $localize`:@@form.invalid:Перевірте позначені поля.`);
  }
}
