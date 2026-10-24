import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService, safeReturnUrl } from '../../auth/auth.service';

/** `/login`: вхід email + пароль (cookie-сесія, `POST /api/v1/auth/login`); після входу — на сторінку з `returnUrl`. */
@Component({
  selector: 'app-login-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <section class="flex justify-center p-6">
      <mat-card class="w-full max-w-sm">
        <mat-card-header>
          <mat-card-title i18n="@@login.title">Вхід до OpenBP</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          <form class="mt-4 flex flex-col" [formGroup]="form" (ngSubmit)="submit()" data-testid="login-form">
            <mat-form-field>
              <mat-label i18n="@@login.email">Email</mat-label>
              <input matInput type="email" formControlName="email" name="email" autocomplete="username" data-testid="login-email" />
            </mat-form-field>
            <mat-form-field>
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
              <p role="alert" class="mb-4" style="color: var(--mat-sys-error)" data-testid="login-error">{{ error() }}</p>
            }
            @if (submitting()) {
              <mat-progress-bar class="mb-4" mode="indeterminate" />
            }
            <button mat-flat-button type="submit" [disabled]="submitting()" data-testid="login-submit" i18n="@@login.submit">Увійти</button>
          </form>
        </mat-card-content>
      </mat-card>
    </section>
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
          this.error.set($localize`:@@login.error.session:Не вдалося відкрити сесію. Перевірте, що браузер приймає cookie.`);
          return;
        }
        void this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
      },
      error: (error: unknown) => {
        this.submitting.set(false);
        this.form.controls.password.reset();
        this.error.set(
          error instanceof HttpErrorResponse && error.status === 401
            ? $localize`:@@login.error.invalid:Невірний email або пароль.`
            : $localize`:@@login.error.unavailable:Сервер недоступний. Спробуйте ще раз.`,
        );
      },
    });
  }
}
