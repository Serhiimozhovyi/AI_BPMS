import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService, safeReturnUrl } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';

/** Найкоротший пароль — як `AuthService.MinPasswordLength` в Api. */
export const MIN_PASSWORD_LENGTH = 8;

/**
 * `/account/password` (F8-13): зміна свого пароля. Користувача з тимчасовим паролем (його додали з вкладки
 * «Учасники») guard веде сюди одразу після входу; після зміни — на сторінку з `returnUrl`.
 */
@Component({
  selector: 'app-change-password-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatCardModule, MatFormFieldModule, MatInputModule],
  template: `
    <section class="flex justify-center px-4 py-12">
      <mat-card class="w-full max-w-sm" appearance="outlined">
        <mat-card-header>
          <mat-card-title i18n="@@account.password.title">Зміна пароля</mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @if (mustChange()) {
            <p class="mt-4" data-testid="password-temporary" i18n="@@account.password.temporary">
              Ви ввійшли з тимчасовим паролем. Придумайте свій, щоб продовжити.
            </p>
          }
          <form class="mt-4 flex flex-col" [formGroup]="form" (ngSubmit)="submit()" data-testid="password-form">
            <mat-form-field appearance="outline">
              <mat-label i18n="@@account.password.current">Поточний пароль</mat-label>
              <input matInput type="password" formControlName="current" autocomplete="current-password" data-testid="password-current" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label i18n="@@account.password.new">Новий пароль</mat-label>
              <input matInput type="password" formControlName="next" autocomplete="new-password" data-testid="password-new" />
              <mat-hint i18n="@@account.password.hint">Щонайменше {{ minLength }} символів</mat-hint>
            </mat-form-field>
            <mat-form-field appearance="outline" class="mt-2">
              <mat-label i18n="@@account.password.repeat">Повторіть новий пароль</mat-label>
              <input matInput type="password" formControlName="repeat" autocomplete="new-password" data-testid="password-repeat" />
            </mat-form-field>
            @if (error()) {
              <p role="alert" class="mb-4" style="color: var(--ac-red)" data-testid="password-error">{{ error() }}</p>
            }
            <button mat-flat-button type="submit" [disabled]="saving()" data-testid="password-submit" i18n="@@account.password.submit">
              Змінити пароль
            </button>
          </form>
        </mat-card-content>
      </mat-card>
    </section>
  `,
})
export class ChangePasswordPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly minLength = MIN_PASSWORD_LENGTH;
  readonly mustChange = computed(() => this.auth.user()?.mustChangePassword === true);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = new FormGroup({
    current: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
    next: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.minLength(MIN_PASSWORD_LENGTH)] }),
    repeat: new FormControl('', { nonNullable: true, validators: [Validators.required] }),
  });

  submit(): void {
    const { current, next, repeat } = this.form.getRawValue();
    if (next.length < MIN_PASSWORD_LENGTH) {
      this.error.set($localize`:@@account.password.tooShort:Новий пароль — щонайменше ${MIN_PASSWORD_LENGTH}:min: символів.`);
      return;
    }
    if (next !== repeat) {
      this.error.set($localize`:@@account.password.mismatch:Паролі не збігаються.`);
      return;
    }
    if (!current) {
      this.error.set($localize`:@@account.password.currentRequired:Введіть поточний пароль.`);
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.auth.changePassword(current, next).subscribe({
      next: () => {
        this.saving.set(false);
        void this.router.navigateByUrl(safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')));
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }
}
