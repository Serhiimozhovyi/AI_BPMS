import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { catchError, of } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { CredentialFormComponent } from '../credentials/credential-form.component';
import { CredentialOption, CredentialSummary, CredentialTypeInfo, CredentialsApiService } from '../credentials/credentials-api.service';
import { ProjectContextService } from '../projects/project-context.service';
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

/**
 * Типи credential'а конектора за транспортом (architecture.md §10.7): `{ headers }` або OAuth2-токен (F4-04) для http,
 * `{ env }` для stdio. Перший — тип нових облікових даних за замовчуванням.
 */
export function credentialTypesFor(transport: ConnectorTransport): string[] {
  return transport === 'http' ? ['mcp.http', 'oauth2'] : ['mcp.stdio'];
}

/**
 * Форма додавання MCP-сервера (stdio або http). Після успішного `POST /connectors` віддає `created`.
 * Облікові дані обираються зі списку потрібного типу або створюються тут же (F4-03).
 */
@Component({
  selector: 'app-connector-form',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatButtonToggleModule, CredentialFormComponent],
  template: `
    <form [formGroup]="form" (ngSubmit)="submit()" class="flex flex-col gap-2" data-testid="connector-form">
      <mat-form-field appearance="outline">
        <mat-label i18n="@@connectors.form.name">Назва</mat-label>
        <input matInput formControlName="name" name="name" />
        @if (form.controls.name.hasError('required')) {
          <mat-error i18n="@@connectors.form.name.required">Вкажіть назву</mat-error>
        }
      </mat-form-field>

      @if (targets().length > 1) {
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.project">Проєкт</mat-label>
          <mat-select data-testid="connector-project" [value]="projectId()" (selectionChange)="selectProject($event.value)">
            @for (t of targets(); track t.id) {
              <mat-option [value]="t.id" [attr.data-project]="t.id ?? 'shared'">{{ t.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }

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
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.command">Команда</mat-label>
          <input matInput formControlName="command" name="command" placeholder="npx" />
          @if (form.controls.command.hasError('required')) {
            <mat-error i18n="@@connectors.form.command.required">Вкажіть команду</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.arguments">Аргументи (по одному на рядок)</mat-label>
          <textarea matInput formControlName="arguments" name="arguments" rows="3"></textarea>
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.environment">Змінні середовища (KEY=value, по одній на рядок)</mat-label>
          <textarea matInput formControlName="environment" name="environment" rows="2"></textarea>
          <mat-hint i18n="@@connectors.form.environment.hint">Секрети зберігайте в облікових даних, а не тут.</mat-hint>
          @if (form.controls.environment.hasError('format')) {
            <mat-error i18n="@@connectors.form.environment.format">Кожен рядок — у форматі KEY=value</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.workingDirectory">Робоча тека</mat-label>
          <input matInput formControlName="workingDirectory" name="workingDirectory" />
        </mat-form-field>
      } @else {
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.url">Адреса (URL)</mat-label>
          <input matInput formControlName="url" name="url" placeholder="https://example.com/mcp" />
          @if (form.controls.url.hasError('required')) {
            <mat-error i18n="@@connectors.form.url.required">Вкажіть адресу</mat-error>
          } @else if (form.controls.url.hasError('url')) {
            <mat-error i18n="@@connectors.form.url.invalid">Адреса має починатися з http:// або https://</mat-error>
          }
        </mat-form-field>
        <mat-form-field appearance="outline">
          <mat-label i18n="@@connectors.form.headers">Заголовки (Name: value, по одному на рядок)</mat-label>
          <textarea matInput formControlName="headers" name="headers" rows="2"></textarea>
          <mat-hint i18n="@@connectors.form.headers.hint">Токени зберігайте в облікових даних, а не тут.</mat-hint>
          @if (form.controls.headers.hasError('format')) {
            <mat-error i18n="@@connectors.form.headers.format">Кожен рядок — у форматі Name: value</mat-error>
          }
        </mat-form-field>
      }

      <div class="flex items-start gap-2">
        <mat-form-field appearance="outline" class="flex-1">
          <mat-label i18n="@@connectors.form.credentialId">Облікові дані (необов'язково)</mat-label>
          <mat-select formControlName="credentialId" data-testid="connector-credential">
            <mat-option value="" i18n="@@connectors.form.credential.none">— без облікових даних —</mat-option>
            @for (c of matchingCredentials(); track c.id) {
              <mat-option [value]="c.id" [attr.data-credential-option]="c.id">{{ c.name }}</mat-option>
            }
          </mat-select>
          @if (form.controls.credentialId.hasError('pattern')) {
            <mat-error i18n="@@connectors.form.credentialId.invalid">Очікується UUID</mat-error>
          }
          <mat-hint i18n="@@connectors.form.credential.hint">Секретні заголовки (HTTP) або змінні середовища (stdio).</mat-hint>
        </mat-form-field>
        @if (credentialTypes().length > 0 && !creatingCredential()) {
          <button mat-stroked-button type="button" class="mt-2" data-testid="create-credential" (click)="creatingCredential.set(true)" i18n="@@connectors.form.credential.create">
            Створити
          </button>
        }
      </div>
      @if (creatingCredential()) {
        <div class="rounded-lg border border-bp-line p-3" data-testid="credential-dialog">
          <h3 class="mb-2 font-semibold" i18n="@@connectors.form.credential.newTitle">Нові облікові дані</h3>
          <app-credential-form
            [types]="credentialTypes()"
            [allowedTypes]="allowedCredentialTypes()"
            [fixedProjectId]="projectId()"
            (saved)="credentialCreated($event)"
            (cancelled)="creatingCredential.set(false)"
          />
        </div>
      }

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
export class ConnectorFormComponent implements OnInit {
  private readonly api = inject(ConnectorsApiService);
  private readonly credentialsApi = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly context = inject(ProjectContextService);
  private readonly auth = inject(AuthService);

  /** Облікові дані для вибору; помилка завантаження — порожній список. */
  readonly credentials = signal<CredentialOption[]>([]);
  /** Каталог типів для створення тут же; недоступний (не admin) — кнопки «Створити» немає. */
  readonly credentialTypes = signal<CredentialTypeInfo[]>([]);
  readonly creatingCredential = signal(false);

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

  private readonly transport = signal<ConnectorTransport>('stdio');
  /** Проєкт нового конектора (F6-14); `null` — спільний (лише admin). */
  readonly projectId = signal<string | null>(null);
  readonly targets = computed(() => this.context.targets(this.auth.user()?.role === 'admin'));
  readonly allowedCredentialTypes = computed(() => credentialTypesFor(this.transport()));
  /** Credential потрібного типу, спільний або з того самого проєкту. */
  readonly matchingCredentials = computed(() =>
    this.credentials().filter(
      (c) => this.allowedCredentialTypes().includes(c.type) && (!c.projectId || c.projectId === this.projectId()),
    ),
  );

  constructor() {
    this.form.controls.transport.valueChanges.pipe(takeUntilDestroyed()).subscribe((transport) => {
      for (const name of ['command', 'environment', 'url', 'headers'] as const) {
        this.form.controls[name].updateValueAndValidity({ emitEvent: false });
      }
      this.transport.set(transport);
      // Credential іншого транспорту не підходить (headers ↔ env).
      if (this.form.controls.credentialId.value && !this.matchingCredentials().some((c) => c.id === this.form.controls.credentialId.value)) {
        this.form.controls.credentialId.setValue('');
      }
    });
  }

  ngOnInit(): void {
    this.projectId.set(this.context.initialTarget(this.auth.user()?.role === 'admin'));
    this.credentialsApi
      .options()
      .pipe(
        catchError(() => of([] as CredentialOption[])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((list) => this.credentials.set(list));
    this.credentialsApi
      .types()
      .pipe(
        catchError(() => of([] as CredentialTypeInfo[])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((types) => this.credentialTypes.set(types));
  }

  selectProject(projectId: string | null): void {
    this.projectId.set(projectId);
    // Credential іншого проєкту для цього конектора недоступний.
    if (this.form.controls.credentialId.value && !this.matchingCredentials().some((c) => c.id === this.form.controls.credentialId.value)) {
      this.form.controls.credentialId.setValue('');
    }
  }

  credentialCreated(created: CredentialSummary): void {
    this.credentials.update((list) => [...list, { id: created.id, name: created.name, type: created.type, projectId: created.projectId ?? null }]);
    this.form.controls.credentialId.setValue(created.id);
    this.creatingCredential.set(false);
  }

  submit(): void {
    this.form.markAllAsTouched();
    if (this.form.invalid || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);
    this.api
      .create({ ...toConnectorInput(this.form.getRawValue() as ConnectorFormValue), projectId: this.projectId() })
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
