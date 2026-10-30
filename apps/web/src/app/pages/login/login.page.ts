import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthProvider, AuthService, oidcLoginHref, safeReturnUrl } from '../../auth/auth.service';

/** Повідомлення для `?oidcError=<код>`, з яким Api повертає з невдалого OIDC-входу (F4-14). */
const OIDC_ERRORS: Record<string, string> = {
  OIDC_EMAIL_NOT_ALLOWED: $localize`:@@login.oidc.emailNotAllowed:Пошта з цього домену не має доступу до AI_BPMS.`,
  OIDC_EMAIL_NOT_VERIFIED: $localize`:@@login.oidc.emailNotVerified:Провайдер не підтвердив вашу пошту. Зверніться до адміністратора.`,
  OIDC_USER_NOT_FOUND: $localize`:@@login.oidc.notFound:Вашого користувача немає в AI_BPMS. Попросіть адміністратора додати вас.`,
  OIDC_ACCOUNT_CONFLICT: $localize`:@@login.oidc.conflict:Цю пошту вже прив'язано до іншого облікового запису провайдера.`,
};

/**
 * `/login`: вхід через OIDC-провайдерів (кнопки «Увійти через …», F4-14) і email + пароль (cookie-сесія,
 * `POST /api/v1/auth/login`); після входу — на сторінку з `returnUrl`. Якщо Api вимкнув вхід паролем, форму видно
 * лише з `?password` (аварійний адміністратор).
 */
@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  template: `
    <section class="login">
      <mat-card class="login__card w-full max-w-sm" appearance="outlined">
        <mat-card-header>
          <mat-card-title i18n="@@login.title">Вхід до AI_BPMS</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @if (oidcError()) {
            <p
              role="alert"
              class="mt-4"
              style="color: var(--ac-red)"
              data-testid="login-oidc-error"
            >
              {{ oidcError() }}
            </p>
          }
          @if (providers().length) {
            <div class="login__providers mt-4 flex flex-col gap-3" data-testid="login-providers">
              @for (provider of providers(); track provider.key) {
                <a
                  mat-stroked-button
                  [href]="providerHref(provider)"
                  [attr.data-testid]="'login-provider-' + provider.key"
                >
                  <span i18n="@@login.oidc.button">Увійти через {{ provider.displayName }}</span>
                </a>
              }
            </div>
            @if (showPassword()) {
              <div class="login__divider mt-4" i18n="@@login.or">або</div>
            }
          }
          @if (showPassword()) {
            <form
              class="mt-4 flex flex-col"
              [formGroup]="form"
              (ngSubmit)="submit()"
              data-testid="login-form"
            >
              <mat-form-field appearance="outline">
                <mat-label i18n="@@login.email">Email</mat-label>
                <input
                  matInput
                  type="email"
                  formControlName="email"
                  name="email"
                  autocomplete="username"
                  data-testid="login-email"
                />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label i18n="@@login.password">Пароль</mat-label>
                <input
                  matInput
                  type="password"
                  formControlName="password"
                  name="password"
                  autocomplete="current-password"
                  data-testid="login-password"
                />
              </mat-form-field>
              @if (error()) {
                <p role="alert" class="mb-4" style="color: var(--ac-red)" data-testid="login-error">
                  {{ error() }}
                </p>
              }
              @if (submitting()) {
                <mat-progress-bar class="mb-4" mode="indeterminate" />
              }
              <button
                mat-flat-button
                type="submit"
                [disabled]="submitting()"
                data-testid="login-submit"
                i18n="@@login.submit"
              >
                Увійти
              </button>
            </form>
          }
        </mat-card-content>
      </mat-card>
    </section>
  `,
  styles: `
    .login {
      display: flex;
      justify-content: center;
      padding: 12vh 16px 24px;
    }

    .login__card {
      padding: 16px 12px 8px;
      box-shadow: var(--ac-shadow);
    }

    .login__card .mat-mdc-card-title {
      font:
        700 24px/30px Ubuntu,
        sans-serif;
      color: var(--ac-vivid);
    }

    .login__card button[type='submit'],
    .login__providers a {
      height: 40px;
    }

    .login__divider {
      display: flex;
      align-items: center;
      gap: 12px;
      color: var(--ac-mid);
      font-size: 13px;
    }

    .login__divider::before,
    .login__divider::after {
      content: '';
      flex: 1;
      border-top: 1px solid var(--ac-line);
    }
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly form = new FormGroup({
    email: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    password: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });
  readonly submitting = signal(false);
  readonly error = signal<string | null>(null);

  private readonly methods = toSignal(this.auth.providers());
  /** OIDC-провайдери з Api. */
  readonly providers = computed(() => this.methods()?.providers ?? []);
  /** Форма пароля: поки способи входу не відомі, увімкнена в Api або явно `?password`. */
  readonly showPassword = computed(
    () =>
      this.methods()?.passwordLogin !== false || this.route.snapshot.queryParamMap.has('password'),
  );
  readonly oidcError = computed(() => {
    const code = this.route.snapshot.queryParamMap.get('oidcError');
    return code
      ? (OIDC_ERRORS[code] ??
          $localize`:@@login.oidc.failed:Не вдалося увійти через провайдера. Спробуйте ще раз.`)
      : null;
  });

  providerHref(provider: AuthProvider): string {
    return oidcLoginHref(provider, this.route.snapshot.queryParamMap.get('returnUrl'));
  }

  submit(): void {
    if (this.submitting()) {
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set($localize`:@@login.error.required:Введіть email і пароль.`);
      return;
    }

    const { email, password } = this.form.getRawValue();
    this.submitting.set(true);
    this.error.set(null);
    this.auth.login(email.trim(), password).subscribe({
      next: (user) => {
        this.submitting.set(false);
        if (!user) {
          this.error.set(
            $localize`:@@login.error.session:Не вдалося відкрити сесію. Перевірте, що браузер приймає cookie.`,
          );
          return;
        }
        void this.router.navigateByUrl(
          safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')),
        );
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.form.controls.password.reset();
        this.error.set(
          error instanceof HttpErrorResponse && error.status === 401
            ? $localize`:@@login.error.invalid:Невірний email або пароль.`
            : error instanceof HttpErrorResponse && error.status === 403
              ? $localize`:@@login.error.passwordDisabled:Вхід паролем вимкнено. Увійдіть через провайдера.`
              : $localize`:@@login.error.unavailable:Сервер недоступний. Спробуйте ще раз.`,
        );
      },
    });
  }
}
