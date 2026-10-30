import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AuthService } from '../auth/auth.service';
import { NotifyService } from '../shared/notify.service';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { VARIABLE_KEY_PATTERN, VARIABLE_MAX_VALUE, VariableInfo, VariablesApiService } from './variables-api.service';
import { BpButtonDirective } from '../shared/button.directive';

/**
 * Блок «Змінні» у `/settings` (F6-09): `$vars.key` у виразах і `core.code`. Список бачать усі; створює, змінює й видаляє
 * лише admin. Секрети сюди не кладуть — для них credentials.
 */
@Component({
  selector: 'app-variables-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.vars.title">Змінні</h2>
    <p class="mb-4 max-w-3xl opacity-70" i18n="@@settings.vars.hint">
      Значення, спільні для всіх сценаріїв: у виразах і коді — <code>$vars.ключ</code>. Не для паролів і токенів —
      їх зберігайте в облікових даних.
    </p>

    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="vars-error">{{ error() }}</p>
    }

    @if (isAdmin()) {
      <div class="mb-4 flex max-w-3xl flex-wrap items-start gap-2" data-testid="vars-form">
        <mat-form-field appearance="outline" class="w-56">
          <mat-label i18n="@@settings.vars.key">Ключ</mat-label>
          <input matInput [formControl]="key" name="key" data-testid="var-key" [readonly]="editing() !== null" />
          @if (key.touched && key.invalid) {
            <mat-error data-testid="var-key-error" i18n="@@settings.vars.keyInvalid">Латиниця, цифри й «_», не з цифри</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline" class="w-72">
          <mat-label i18n="@@settings.vars.value">Значення</mat-label>
          <input matInput [formControl]="value" name="value" data-testid="var-value" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="w-72">
          <mat-label i18n="@@settings.vars.description">Опис</mat-label>
          <input matInput [formControl]="description" name="description" data-testid="var-description" />
        </mat-form-field>
        <button mat-flat-button type="button" data-testid="var-save" (click)="save()" [disabled]="saving()" i18n="@@settings.vars.save">
          Зберегти
        </button>
        @if (editing() !== null) {
          <button mat-button type="button" data-testid="var-cancel" (click)="reset()" i18n="@@settings.vars.cancel">Скасувати</button>
        }
      </div>
    }

    @if (!loading() && variables().length === 0 && !error()) {
      <p class="mb-6 opacity-70" data-testid="vars-empty" i18n="@@settings.vars.empty">Змінних ще немає.</p>
    }

    @if (variables().length > 0) {
      <table class="mb-6 w-full max-w-3xl text-left" data-testid="vars-table">
        <thead>
          <tr>
            <th class="p-2" i18n="@@settings.vars.col.key">Ключ</th>
            <th class="p-2" i18n="@@settings.vars.col.value">Значення</th>
            <th class="p-2" i18n="@@settings.vars.col.description">Опис</th>
            @if (isAdmin()) {
              <th class="p-2"></th>
            }
          </tr>
        </thead>
        <tbody>
          @for (v of variables(); track v.key) {
            <tr [attr.data-var]="v.key">
              <td class="p-2 font-mono">{{ v.key }}</td>
              <td class="max-w-xs truncate p-2 font-mono" [title]="v.value">{{ v.value }}</td>
              <td class="p-2">{{ v.description }}</td>
              @if (isAdmin()) {
                <td class="whitespace-nowrap p-2">
                  <button mat-button type="button" data-testid="var-edit" (click)="edit(v)" i18n="@@settings.vars.edit">Змінити</button>
                  <button bpVariant="danger" mat-button type="button" data-testid="var-delete" (click)="remove(v)" i18n="@@settings.vars.delete">Видалити</button>
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class VariablesSection implements OnInit {
  private readonly api = inject(VariablesApiService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notify = inject(NotifyService);

  readonly variables = signal<VariableInfo[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  /** Ключ змінної, яку редагують; `null` — створення нової. */
  readonly editing = signal<string | null>(null);
  readonly isAdmin = computed(() => this.auth.user()?.role === 'admin');

  readonly key = new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.pattern(VARIABLE_KEY_PATTERN)] });
  readonly value = new FormControl('', { nonNullable: true, validators: [Validators.maxLength(VARIABLE_MAX_VALUE)] });
  readonly description = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    this.loading.set(true);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.variables.set(list);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  edit(v: VariableInfo): void {
    this.editing.set(v.key);
    this.key.setValue(v.key);
    this.value.setValue(v.value);
    this.description.setValue(v.description ?? '');
  }

  reset(): void {
    this.editing.set(null);
    this.key.reset();
    this.value.reset();
    this.description.reset();
  }

  save(): void {
    const key = this.key.value.trim();
    this.key.setValue(key);
    if (this.key.invalid || this.value.invalid) {
      this.key.markAsTouched();
      return;
    }
    this.saving.set(true);
    this.error.set(null);
    this.api
      .set(key, this.value.value, this.description.value.trim() || null)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (saved) => {
          this.saving.set(false);
          this.variables.update((list) =>
            [...list.filter((v) => v.key !== saved.key), saved].sort((a, b) => (a.key < b.key ? -1 : a.key > b.key ? 1 : 0)),
          );
          this.reset();
          this.notify.success($localize`:@@settings.vars.saved:Змінну «${saved.key}:key:» збережено.`);
        },
        error: (e: unknown) => {
          this.saving.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  remove(v: VariableInfo): void {
    this.error.set(null);
    this.api
      .remove(v.key)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.variables.update((list) => list.filter((x) => x.key !== v.key));
          if (this.editing() === v.key) this.reset();
          this.notify.success($localize`:@@settings.vars.removed:Змінну «${v.key}:key:» видалено.`);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }
}
