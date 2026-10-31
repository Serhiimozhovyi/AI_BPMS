import { ViewAsService } from '../../auth/view-as.service';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { providePublicForm } from '../../forms/form-field-types';
import { formFields, toPayload } from '../../forms/form-fields';
import { localInput, zonedParts, zonedToInstant } from '../../projects/calendar-time';
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
      <button mat-flat-button type="submit" data-testid="app-submit" [disabled]="submitting() || loading() || preview() || readOnly()" [attr.title]="readOnly() ? readOnlyTip : null" i18n="@@app.page.submit">Зберегти</button>
    </form>
  `,
})
export class PageFormComponent implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  /** Перегляд «як користувач» (F12-02): дії, що змінюють дані, неактивні. */
  protected readonly readOnly = inject(ViewAsService).active;
  protected readonly readOnlyTip = $localize`:@@viewAs.disabled:У режимі перегляду дані не змінюються`;

  readonly projectId = input.required<string>();
  readonly view = input.required<ProjectPageView>();
  /** Ключ сторінки для запитів, якщо він не збігається з `view.page.key` (блок `form` головної з власним визначенням). */
  readonly pageKey = input<string | null>(null);
  /** Ключ рядка для форми зміни. */
  readonly rowKey = input<string | null>(null);
  /** Початкові значення нового запису; беруться лише поля, що є у формі. */
  readonly prefill = input<Record<string, unknown>>({});
  /** Перегляд у конструкторі (F8-19): рядок не читається, зберегти не можна. */
  readonly preview = input(false);
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
    if (!rowKey || this.preview()) return;
    this.loading.set(true);
    this.api.row(this.projectId(), this.pageKey() ?? view.page.key, rowKey).subscribe({
      next: (record) => {
        this.model = this.toInputs({ ...record.row.data });
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
    this.api.submit(this.projectId(), this.pageKey() ?? view.page.key, this.fromInputs(toPayload(view.schema, this.model).fields), this.rowKey()).subscribe({
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

  /** Поля `date-time` схеми: значення в формі — час у поясі застосунку (`ГГГГ-ММ-ДДTГГ:ХХ`), у Api — момент часу ISO 8601. */
  private dateTimeFields(): string[] {
    const properties = (this.view().schema?.['properties'] ?? {}) as Record<string, Record<string, unknown>>;
    return Object.keys(properties).filter((name) => properties[name]?.['format'] === 'date-time');
  }

  private toInputs(data: Record<string, unknown>): Record<string, unknown> {
    const tz = this.view().timezone;
    for (const name of this.dateTimeFields()) {
      const value = data[name];
      const date = typeof value === 'string' ? new Date(value) : null;
      if (date && !Number.isNaN(date.getTime())) {
        const p = zonedParts(date, tz);
        data[name] = localInput(p, p.h, p.mi);
      }
    }
    return data;
  }

  private fromInputs(fields: Record<string, unknown>): Record<string, unknown> {
    const tz = this.view().timezone;
    for (const name of this.dateTimeFields()) {
      const m = typeof this.model[name] === 'string' ? /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(this.model[name] as string) : null;
      if (m) fields[name] = zonedToInstant({ y: +m[1], m: +m[2], d: +m[3] }, +m[4], +m[5], tz).toISOString();
    }
    return fields;
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
