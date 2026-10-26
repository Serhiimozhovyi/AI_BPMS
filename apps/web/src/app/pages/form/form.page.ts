import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { FormlyFieldConfig, FormlyForm } from '@ngx-formly/core';
import { providePublicForm } from '../../forms/form-field-types';
import { formFields, toPayload } from '../../forms/form-fields';
import { FormDefinition, FormFieldError, FormsApiService } from '../../forms/forms-api.service';

/** Стан сторінки. */
type FormState = 'loading' | 'ready' | 'done' | 'error';

/** Тіло problem+json від Api. */
interface Problem {
  code?: string;
  detail?: string;
  loginUrl?: string;
  errors?: FormFieldError[];
}

/**
 * `/form/{path}` — публічна сторінка форми без меню застосунку (F6-03): тригер `core.formTrigger` або крок
 * `core.wait` у режимі `form` (`/form/resume/{token}`). Поля — ngx-formly за JSON Schema з Api; після надсилання —
 * повідомлення або перехід на `redirectUrl`. Форма лише для користувачів (`auth: login`) веде на вхід з поверненням сюди;
 * токен посилання (`?token=`) передається в заголовку.
 */
@Component({
  selector: 'app-form-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatProgressBarModule, FormlyForm],
  providers: [providePublicForm()],
  template: `
    <section class="pform">
      <mat-card class="pform__card w-full" appearance="outlined">
        @switch (state()) {
          @case ('loading') {
            <mat-progress-bar mode="indeterminate" data-testid="form-loading" />
          }
          @case ('error') {
            <mat-card-content>
              <p class="pform__notice" role="alert" data-testid="form-error">{{ error() }}</p>
            </mat-card-content>
          }
          @case ('done') {
            <mat-card-content>
              <h1 class="pform__title">{{ definition()?.title }}</h1>
              <p class="pform__notice" role="status" data-testid="form-done">{{ doneMessage() }}</p>
            </mat-card-content>
          }
          @case ('ready') {
            <mat-card-content>
              <h1 class="pform__title" data-testid="form-title">{{ definition()?.title }}</h1>
              @if (definition()?.description; as description) {
                <p class="pform__description" data-testid="form-description">{{ description }}</p>
              }
              <form [formGroup]="form" (ngSubmit)="submit()" novalidate data-testid="form">
                <formly-form [form]="form" [fields]="fields()" [model]="model" />
                @if (submitError()) {
                  <p class="pform__submit-error" role="alert" data-testid="form-submit-error">{{ submitError() }}</p>
                }
                @if (submitting()) {
                  <mat-progress-bar class="mb-4" mode="indeterminate" />
                }
                <button mat-flat-button type="submit" [disabled]="submitting()" data-testid="form-submit">
                  {{ definition()?.submitLabel }}
                </button>
              </form>
            </mat-card-content>
          }
        }
      </mat-card>
    </section>
  `,
  styles: `
    .pform {
      display: flex;
      justify-content: center;
      padding: 8vh 16px 24px;
    }

    .pform__card {
      max-width: 560px;
      padding: 16px 12px 8px;
      box-shadow: var(--ac-shadow);
    }

    .pform__title {
      margin: 0 0 8px;
      font:
        700 24px/30px Ubuntu,
        sans-serif;
      color: var(--ac-vivid);
    }

    .pform__description {
      margin: 0 0 20px;
      white-space: pre-line;
      color: var(--ac-mid);
    }

    .pform__notice {
      margin: 8px 0 12px;
      white-space: pre-line;
    }

    .pform__submit-error {
      margin: 0 0 12px;
      color: var(--ac-red);
    }

    button[type='submit'] {
      height: 40px;
      margin-bottom: 8px;
    }
  `,
})
export class FormPage implements OnInit {
  private readonly api = inject(FormsApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly state = signal<FormState>('loading');
  protected readonly definition = signal<FormDefinition | null>(null);
  protected readonly fields = signal<FormlyFieldConfig[]>([]);
  protected readonly error = signal('');
  protected readonly submitError = signal('');
  protected readonly submitting = signal(false);
  protected readonly doneMessage = signal('');
  protected readonly form = new FormGroup({});
  protected model: Record<string, unknown> = {};

  private path = '';
  private token: string | null = null;

  ngOnInit(): void {
    // Маршрут забирає всі сегменти разом з «form»: шлях форми — решта.
    this.path = this.route.snapshot.url.slice(1).map((s) => s.path).join('/');
    this.token = this.route.snapshot.queryParamMap.get('token');
    this.api.definition(this.path, this.token).subscribe({
      next: (definition) => {
        this.definition.set(definition);
        this.fields.set(formFields(definition.schema));
        this.state.set('ready');
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  protected submit(): void {
    const definition = this.definition();
    if (!definition || this.submitting()) return;
    this.submitError.set('');
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      this.submitError.set($localize`:@@form.invalid:Перевірте позначені поля.`);
      return;
    }
    this.submitting.set(true);
    this.api.submit(this.path, toPayload(definition.schema, this.model), this.token).subscribe({
      next: (submitted) => {
        this.submitting.set(false);
        if (submitted.redirectUrl && /^https?:\/\//i.test(submitted.redirectUrl)) {
          window.location.assign(submitted.redirectUrl);
          return;
        }
        this.doneMessage.set(submitted.message || $localize`:@@form.done:Дякуємо! Форму надіслано.`);
        this.state.set('done');
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        const problem = this.problemOf(error);
        if (problem?.code === 'FORM_INVALID' && problem.errors?.length) {
          this.showFieldErrors(problem.errors);
          return;
        }
        if (error instanceof HttpErrorResponse && error.status === 413) {
          this.submitError.set(problem?.detail ?? $localize`:@@form.tooLarge:Форма завелика: зменште файли.`);
          return;
        }
        this.fail(error);
      },
    });
  }

  /** Помилки полів з Api — під полями; помилки без поля — над кнопкою. */
  private showFieldErrors(errors: FormFieldError[]): void {
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

  private fail(error: unknown): void {
    const problem = this.problemOf(error);
    if (problem?.code === 'FORM_LOGIN_REQUIRED' && problem.loginUrl?.startsWith('/login')) {
      void this.router.navigateByUrl(problem.loginUrl);
      return;
    }
    const status = error instanceof HttpErrorResponse ? error.status : 0;
    this.error.set(
      status === 404
        ? $localize`:@@form.notFound:Форму не знайдено: посилання застаріле або процес вимкнено.`
        : status === 401
          ? $localize`:@@form.unauthorized:Посилання недійсне. Відкрийте форму за повним посиланням, яке вам надіслали.`
          : problem?.code === 'RESUME_TOKEN_USED'
            ? $localize`:@@form.used:Цю форму вже надіслано. Дякуємо!`
            : (problem?.detail ?? $localize`:@@form.failed:Не вдалося відкрити форму. Спробуйте пізніше.`),
    );
    this.state.set('error');
  }

  private problemOf(error: unknown): Problem | null {
    return error instanceof HttpErrorResponse && typeof error.error === 'object' && error.error !== null ? (error.error as Problem) : null;
  }
}
