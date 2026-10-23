import { ChangeDetectionStrategy, Component, DestroyRef, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ConnectorFormValue, parseKeyValues, toConnectorInput } from './connector-input';
import { ConnectorDetail, ConnectorTransport, ConnectorsApiService, apiErrorMessage } from './connectors-api.service';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Валідатор, що діє лише для обраного транспорту. */
function forTransport(transport: ConnectorTransport, validator: ValidatorFn): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null =>
    control.parent?.get('transport')?.value === transport ? validator(control) : null;
}

const requiredText: ValidatorFn = (c) => (String(c.value ?? '').trim() ? null : { required: true });

const httpUrl: ValidatorFn = (c) => {
  const value = String(c.value ?? '').trim();
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:' ? null : { url: true };
  } catch {
    return { url: true };
  }
};

function keyValues(separator: '=' | ':'): ValidatorFn {
  return (c) => (parseKeyValues(String(c.value ?? ''), separator) === null ? { format: true } : null);
}

/** Форма додавання MCP-сервера (stdio або http). Після успішного `POST /connectors` віддає `created`. */
@Component({
  selector: 'app-connector-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatButtonToggleModule],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" class="flex flex-col gap-2" data-testid="connector-form">
      <mat-form-field>
        <mat-label i18n="@@connectors.form.name">Назва</mat-label>
        <input matInput formControlName="name" name="name" />
        @if (form.controls.name.hasError('required')) {
          <mat-error i18n="@@connectors.form.name.required">Вкажіть назву</mat-error>
        }
      </mat-form-field>

      <mat-button-toggle-group
        formControlName="transport"
        aria-label="Транспорт"
        i18n-aria-label="@@connectors.form.transport"
        class="mb-2 self-start"
      >
        <mat-button-toggle value="stdio" i18n="@@connectors.transport.stdio">stdio (процес)</mat-button-toggle>
        <mat-button-toggle value="http" i18n="@@connectors.transport.http">HTTP</mat-button-toggle>
      </mat-button-toggle-group>

      @if (form.controls.transport.value === 'stdio') {
        <mat-form-field>
          <mat-label i18n="@@connectors.form.command">Команда</mat-label>
          <input matInput formControlName="command" name="command" placeholder="npx" />
          @if (form.controls.command.hasError('required')) {
            <mat-error i18n="@@connectors.form.command.required">Вкажіть команду</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label i18n="@@connectors.form.arguments">Аргументи (по одному на рядок)</mat-label>
          <textarea matInput formControlName="arguments" name="arguments" rows="3"></textarea>
        </mat-form-field>
        <mat-form-field>
          <mat-label i18n="@@connectors.form.environment">Змінні середовища (KEY=value, по одній на рядок)</mat-label>
          <textarea matInput formControlName="environment" name="environment" rows="2"></textarea>
          <mat-hint i18n="@@connectors.form.environment.hint">Секрети зберігайте в облікових даних, а не тут.</mat-hint>
          @if (form.controls.environment.hasError('format')) {
            <mat-error i18n="@@connectors.form.environment.format">Кожен рядок — у форматі KEY=value</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label i18n="@@connectors.form.workingDirectory">Робоча тека</mat-label>
          <input matInput formControlName="workingDirectory" name="workingDirectory" />
        </mat-form-field>
      } @else {
        <mat-form-field>
          <mat-label i18n="@@connectors.form.url">Адреса (URL)</mat-label>
          <input matInput formControlName="url" name="url" placeholder="https://example.com/mcp" />
          @if (form.controls.url.hasError('required')) {
            <mat-error i18n="@@connectors.form.url.required">Вкажіть адресу</mat-error>
          } @else if (form.controls.url.hasError('url')) {
            <mat-error i18n="@@connectors.form.url.invalid">Адреса має починатися з http:// або https://</mat-error>
          }
        </mat-form-field>
        <mat-form-field>
          <mat-label i18n="@@connectors.form.headers">Заголовки (Name: value, по одному на рядок)</mat-label>
          <textarea matInput formControlName="headers" name="headers" rows="2"></textarea>
          <mat-hint i18n="@@connectors.form.headers.hint">Токени зберігайте в облікових даних, а не тут.</mat-hint>
          @if (form.controls.headers.hasError('format')) {
            <mat-error i18n="@@connectors.form.headers.format">Кожен рядок — у форматі Name: value</mat-error>
          }
        </mat-form-field>
      }

      <mat-form-field>
        <mat-label i18n="@@connectors.form.credentialId">ID облікових даних (необов'язково)</mat-label>
        <input matInput formControlName="credentialId" name="credentialId" />
        @if (form.controls.credentialId.hasError('pattern')) {
          <mat-error i18n="@@connectors.form.credentialId.invalid">Очікується UUID</mat-error>
        }
      </mat-form-field>

      @if (error()) {
        <p role="alert" class="text-red-700" data-testid="form-error">{{ error() }}</p>
      }

      <div class="flex gap-2">
        <button mat-flat-button type="submit" [disabled]="saving()" i18n="@@connectors.form.submit">Додати</button>
        <button mat-button type="button" (click)="cancelled.emit()" i18n="@@connectors.form.cancel">Скасувати</button>
      </div>
    </form>
  `,
})
export class ConnectorFormComponent {
  private readonly api = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** Конектор створено (discovery вже виконано сервером, статус може бути `error`). */
  readonly created = output<ConnectorDetail>();
  readonly cancelled = output<void>();

  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', requiredText],
    transport: ['stdio' as ConnectorTransport],
    command: ['', forTransport('stdio', requiredText)],
    arguments: [''],
    environment: ['', forTransport('stdio', keyValues('='))],
    workingDirectory: [''],
    url: ['', forTransport('http', (c) => requiredText(c) ?? httpUrl(c))],
    headers: ['', forTransport('http', keyValues(':'))],
    credentialId: ['', Validators.pattern(UUID)],
  });

  constructor() {
    this.form.controls.transport.valueChanges.pipe(takeUntilDestroyed()).subscribe(() => {
      for (const name of ['command', 'environment', 'url', 'headers'] as const) {
        this.form.controls[name].updateValueAndValidity({ emitEvent: false });
      }
    });
  }

  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.api
      .create(toConnectorInput(this.form.getRawValue() as ConnectorFormValue))
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.saving.set(false);
          this.form.reset();
          this.created.emit(detail);
        },
        error: (e: unknown) => {
          this.saving.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }
}
