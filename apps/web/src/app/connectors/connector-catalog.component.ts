import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { catchError, of } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { CredentialFormComponent } from '../credentials/credential-form.component';
import { CredentialOption, CredentialSummary, CredentialTypeInfo, CredentialsApiService } from '../credentials/credentials-api.service';
import { ProjectContextService } from '../projects/project-context.service';
import { credentialTypesFor } from './connector-form.component';
import {
  ConnectorCatalogEntry,
  ConnectorDetail,
  ConnectorManifest,
  ConnectorManifestKey,
  ConnectorManifestSetting,
  ConnectorTransport,
  ConnectorsApiService,
  apiErrorMessage,
  manifestCredentialType,
  manifestKeys,
  manifestSettings,
  manifestTransports,
} from './connectors-api.service';

/** Встановлений конектор, якому ще бракує облікових даних. */
interface PendingCredential {
  detail: ConnectorDetail;
  manifest: ConnectorManifest;
  keys: ConnectorManifestKey[];
}

/** Крок «Підключення» (F3-94): запис каталогу, для якого треба назва, налаштування й облікові дані. */
interface Connecting {
  entry: ConnectorCatalogEntry;
  transport: ConnectorTransport;
  /** Загальний тип облікових даних запису; `null` — облікові дані не потрібні. */
  credentialType: string | null;
  settings: ConnectorManifestSetting[];
}

/**
 * Каталог конекторів (F6-16, F3-94): записи `GET /connectors/catalog`. «Встановити» відкриває крок «Підключення»:
 * назва, налаштування з `settings` маніфесту й облікові дані загального типу — наявні (вибір) або нові (форма типу
 * з підписаними полями, без імен змінних середовища); сервер створює конектор одним запитом. Запис без загального
 * типу й налаштувань ставиться одразу, а старі ключі `env`/`headers` (старі записи каталогу) — формою пар після встановлення.
 * Готовий конектор — `installed`.
 */
@Component({
  selector: 'app-connector-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, CredentialFormComponent],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="catalog-error">{{ error() }}</p>
    }

    @if (connecting(); as c) {
      <div class="mb-6 max-w-2xl rounded-lg border border-ac-line p-4" data-testid="catalog-connect">
        <h2 class="mb-3 font-semibold" i18n="@@connectors.catalog.connectTitle">Підключити «{{ c.entry.manifest.name }}»</h2>
        <div class="flex flex-col gap-2">
          <mat-form-field appearance="outline">
            <mat-label i18n="@@connectors.catalog.connectName">Назва підключення</mat-label>
            <input matInput data-testid="connect-name" [value]="connectName()" (input)="connectName.set(text($event))" />
          </mat-form-field>
          @for (st of c.settings; track st.name) {
            <mat-form-field appearance="outline" [attr.data-setting]="st.name">
              <mat-label>{{ st.required ? st.title : st.title + ' (' + optionalLabel + ')' }}</mat-label>
              <input
                matInput
                [attr.data-testid]="'setting-' + st.name"
                [value]="settingValues()[st.name] ?? ''"
                [placeholder]="st.placeholder ?? ''"
                (input)="setSetting(st.name, text($event))"
              />
              @if (st.description) {
                <mat-hint>{{ st.description }}</mat-hint>
              }
            </mat-form-field>
          }
          @if (c.credentialType) {
            <mat-form-field appearance="outline">
              <mat-label i18n="@@connectors.catalog.connectCredential">Облікові дані</mat-label>
              <mat-select data-testid="connect-credential" [value]="credentialChoice()" (selectionChange)="credentialChoice.set($event.value)">
                @for (o of existingCredentials(); track o.id) {
                  <mat-option [value]="o.id" [attr.data-credential]="o.id">{{ o.name }}</mat-option>
                }
                <mat-option value="new" data-credential="new" i18n="@@connectors.catalog.connectNewCredential">+ Нові облікові дані</mat-option>
              </mat-select>
              <mat-hint i18n="@@connectors.catalog.connectCredentialHint">Наявні можна використати ще раз — у кількох підключеннях.</mat-hint>
            </mat-form-field>
            @if (credentialChoice() === 'new') {
              <app-credential-form
                [types]="credentialTypes()"
                [allowedTypes]="[c.credentialType]"
                [fixedProjectId]="projectId()"
                [presetName]="c.entry.manifest.name"
                [saveLabel]="connectLabel"
                [saveDisabled]="connectDisabled()"
                (saved)="onCredentialCreated($event)"
                (cancelled)="cancelConnect()"
              />
            }
          }
          @if (connectError()) {
            <p role="alert" class="text-sm text-ac-red" data-testid="connect-error">{{ connectError() }}</p>
          }
          @if (!c.credentialType || credentialChoice() !== 'new') {
            <div class="flex gap-2">
              <button
                mat-flat-button
                type="button"
                data-testid="connect-submit"
                [disabled]="connectDisabled()"
                (click)="connect(c.credentialType ? credentialChoice() : undefined)"
              >
                {{ connectLabel }}
              </button>
              <button mat-button type="button" data-testid="connect-cancel" (click)="cancelConnect()" i18n="@@connectors.catalog.cancel">Скасувати</button>
            </div>
          }
        </div>
      </div>
    }

    @if (pending(); as p) {
      <div class="mb-6 max-w-2xl rounded-lg border border-ac-line p-4" data-testid="catalog-credential">
        <h2 class="mb-1 font-semibold" i18n="@@connectors.catalog.credentialTitle">Облікові дані для «{{ p.detail.summary.name }}»</h2>
        <p class="mb-2 text-sm opacity-70" i18n="@@connectors.catalog.credentialHint">Конектор створено. Заповніть значення, і OpenBP перепідключиться з ними:</p>
        <ul class="mb-3 list-disc pl-5 text-sm">
          @for (k of p.keys; track k.name) {
            <li [attr.data-key]="k.name">
              <code>{{ k.name }}</code>
              @if (k.title) {
                — {{ k.title }}
              }
              @if (!k.required) {
                <span class="opacity-70" i18n="@@connectors.catalog.optional">(необов'язково)</span>
              }
            </li>
          }
        </ul>
        <app-credential-form
          [types]="credentialTypes()"
          [allowedTypes]="[credentialType(p.detail.summary.transport)]"
          [fixedProjectId]="p.detail.summary.projectId ?? null"
          [presetName]="p.manifest.name"
          [presetKeys]="keyNames(p.keys)"
          (saved)="attach(p, $event)"
          (cancelled)="finish(p.detail)"
        />
        <button mat-button type="button" class="mt-2" data-testid="catalog-skip" (click)="finish(p.detail)" i18n="@@connectors.catalog.skip">
          Пізніше
        </button>
      </div>
    }

    <ul class="grid gap-3 md:grid-cols-2" data-testid="catalog-list">
      @for (e of entries(); track e.manifest.id) {
        <li
          class="flex flex-col gap-2 rounded-lg border p-4"
          [class.border-ac-kit]="e.manifest.id === highlight()"
          [class.border-ac-line]="e.manifest.id !== highlight()"
          [attr.data-catalog]="e.manifest.id"
        >
          <div class="flex items-center gap-2">
            <span class="text-2xl" aria-hidden="true">{{ e.manifest.icon || '🔌' }}</span>
            <h3 class="font-semibold">{{ e.manifest.name }}</h3>
            @if (e.source === 'external') {
              <span class="rounded bg-ac-cool-grey px-2 text-xs" i18n="@@connectors.catalog.external">зовнішній</span>
            }
          </div>
          <p class="text-sm opacity-80">{{ e.manifest.description }}</p>
          @if (e.installed.length > 0) {
            <p class="text-sm text-green-700" data-testid="catalog-installed" i18n="@@connectors.catalog.installed">Встановлено: {{ installedNames(e) }}</p>
          }
          <div class="mt-auto flex flex-wrap items-center gap-2">
            @if (transports(e).length > 1) {
              <mat-button-toggle-group
                [value]="transportOf(e)"
                (change)="chooseTransport(e, $event.value)"
                [attr.aria-label]="transportLabel"
                data-testid="catalog-transport"
              >
                @for (t of transports(e); track t) {
                  <mat-button-toggle [value]="t">{{ t }}</mat-button-toggle>
                }
              </mat-button-toggle-group>
            }
            <button
              mat-flat-button
              type="button"
              data-testid="catalog-install"
              [disabled]="installing() !== null || connecting() !== null || !canInstall()"
              (click)="begin(e)"
              i18n="@@connectors.catalog.install"
            >
              Встановити
            </button>
          </div>
        </li>
      } @empty {
        @if (!loading() && !error()) {
          <li class="opacity-70" i18n="@@connectors.catalog.empty">Каталог порожній.</li>
        }
      }
    </ul>
  `,
})
export class ConnectorCatalogComponent implements OnInit {
  private readonly api = inject(ConnectorsApiService);
  private readonly credentialsApi = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly context = inject(ProjectContextService);
  private readonly auth = inject(AuthService);

  /** Запис, який підсвітити (посилання з шаблону сценарію). */
  readonly highlight = input<string | null>(null);
  /** Конектор встановлено (з обліковими даними або без них). */
  readonly installed = output<ConnectorDetail>();

  readonly entries = signal<ConnectorCatalogEntry[]>([]);
  readonly credentialTypes = signal<CredentialTypeInfo[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly installing = signal<string | null>(null);
  readonly pending = signal<PendingCredential | null>(null);
  readonly connecting = signal<Connecting | null>(null);
  readonly connectName = signal('');
  readonly settingValues = signal<Record<string, string>>({});
  /** Вибір облікових даних у кроці: id наявних або `new`. */
  readonly credentialChoice = signal<string>('new');
  readonly existingCredentials = signal<CredentialOption[]>([]);
  readonly connectError = signal<string | null>(null);
  private readonly chosen = signal<Record<string, ConnectorTransport>>({});

  private readonly isAdmin = computed(() => this.auth.user()?.role === 'admin');
  /** Спільний конектор ставить admin, проєктний — editor проєкту. */
  readonly canInstall = computed(() => this.isAdmin() || this.context.manageable().length > 0);

  readonly transportLabel = $localize`:@@connectors.catalog.transport:Транспорт`;
  readonly connectLabel = $localize`:@@connectors.catalog.connect:Підключити`;
  readonly optionalLabel = $localize`:@@connectors.catalog.optionalShort:необов'язково`;

  /** Проєкт нового підключення (заданий контекстом); `null` — спільний. */
  readonly projectId = computed(() => this.context.initialTarget(this.isAdmin()) ?? null);
  /** Не вистачає назви чи обов'язкових налаштувань. */
  readonly connectDisabled = computed(() => {
    const c = this.connecting();
    if (!c || this.installing() !== null || !this.connectName().trim()) return true;
    return c.settings.some((s) => s.required && !(this.settingValues()[s.name] ?? '').trim());
  });

  ngOnInit(): void {
    this.load();
    this.credentialsApi
      .types()
      .pipe(
        catchError(() => of([] as CredentialTypeInfo[])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((types) => this.credentialTypes.set(types));
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .catalog()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.entries.set(list);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  transports(entry: ConnectorCatalogEntry): ConnectorTransport[] {
    return manifestTransports(entry.manifest);
  }

  transportOf(entry: ConnectorCatalogEntry): ConnectorTransport {
    return this.chosen()[entry.manifest.id] ?? entry.manifest.transport;
  }

  chooseTransport(entry: ConnectorCatalogEntry, transport: ConnectorTransport): void {
    this.chosen.update((c) => ({ ...c, [entry.manifest.id]: transport }));
  }

  installedNames(entry: ConnectorCatalogEntry): string {
    return entry.installed.map((c) => c.name).join(', ');
  }

  keyNames(keys: ConnectorManifestKey[]): string[] {
    return keys.map((k) => k.name);
  }

  credentialType(transport: ConnectorTransport): string {
    return credentialTypesFor(transport)[0];
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  setSetting(name: string, value: string): void {
    this.settingValues.update((v) => ({ ...v, [name]: value }));
  }

  /**
   * «Встановити»: запис із загальним типом облікових даних чи налаштуваннями відкриває крок «Підключення»,
   * решта (без облікових даних, старі `env`/`headers`) ставиться одразу.
   */
  begin(entry: ConnectorCatalogEntry): void {
    const transport = this.transportOf(entry);
    const credentialType = manifestCredentialType(entry.manifest);
    const settings = manifestSettings(entry.manifest, transport);
    if (!credentialType && settings.length === 0) {
      this.install(entry);
      return;
    }
    this.error.set(null);
    this.pending.set(null);
    this.connectError.set(null);
    this.connectName.set(entry.manifest.name);
    this.settingValues.set({});
    this.existingCredentials.set([]);
    this.credentialChoice.set('new');
    this.connecting.set({ entry, transport, credentialType, settings });
    if (!credentialType) return;
    this.credentialsApi
      .options(credentialType)
      .pipe(
        catchError(() => of([] as CredentialOption[])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => {
        this.existingCredentials.set(options);
        if (options.length > 0 && this.credentialChoice() === 'new') this.credentialChoice.set(options[0].id);
      });
  }

  cancelConnect(): void {
    this.connecting.set(null);
  }

  /** Облікові дані створено формою кроку — відразу підключаємо; після помилки вони лишаються вибраними. */
  onCredentialCreated(credential: CredentialSummary): void {
    this.existingCredentials.update((list) => [...list, { id: credential.id, name: credential.name, type: credential.type, projectId: credential.projectId ?? null }]);
    this.credentialChoice.set(credential.id);
    this.connect(credential.id);
  }

  /** Створює підключення одним запитом: назва, значення налаштувань і вибрані облікові дані. */
  connect(credentialId?: string): void {
    const c = this.connecting();
    if (!c || this.connectDisabled()) return;
    const settings = Object.fromEntries(
      Object.entries(this.settingValues())
        .map(([k, v]) => [k, v.trim()] as const)
        .filter(([, v]) => v !== ''),
    );
    const request: Parameters<ConnectorsApiService['install']>[1] = { name: this.connectName().trim(), transport: c.transport };
    const projectId = this.projectId();
    if (projectId) request.projectId = projectId;
    if (credentialId && credentialId !== 'new') request.credentialId = credentialId;
    if (Object.keys(settings).length > 0) request.settings = settings;
    this.installing.set(c.entry.manifest.id);
    this.connectError.set(null);
    this.api
      .install(c.entry.manifest.id, request)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.installing.set(null);
          this.connecting.set(null);
          this.entries.update((list) =>
            list.map((e) => (e.manifest.id === c.entry.manifest.id ? { ...e, installed: [...e.installed, detail.summary] } : e)),
          );
          this.installed.emit(detail);
        },
        error: (e: unknown) => {
          this.installing.set(null);
          this.connectError.set(apiErrorMessage(e));
        },
      });
  }

  install(entry: ConnectorCatalogEntry): void {
    const transport = this.transportOf(entry);
    this.installing.set(entry.manifest.id);
    this.error.set(null);
    const projectId = this.context.initialTarget(this.isAdmin());
    this.api
      .install(entry.manifest.id, projectId ? { transport, projectId } : { transport })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.installing.set(null);
          this.entries.update((list) =>
            list.map((e) => (e.manifest.id === entry.manifest.id ? { ...e, installed: [...e.installed, detail.summary] } : e)),
          );
          const keys = manifestKeys(entry.manifest, transport);
          if (keys.length === 0) {
            this.installed.emit(detail);
          } else {
            this.pending.set({ detail, manifest: entry.manifest, keys });
          }
        },
        error: (e: unknown) => {
          this.installing.set(null);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  /** Прив'язує новий credential до конектора; сервер перечитує tools з ним. */
  attach(pending: PendingCredential, credential: CredentialSummary): void {
    const { summary, config } = pending.detail;
    this.api
      .update(summary.id, {
        name: summary.name,
        transport: summary.transport,
        config,
        credentialId: credential.id,
        projectId: summary.projectId ?? null,
      })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => this.finish(detail),
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  finish(detail: ConnectorDetail): void {
    this.pending.set(null);
    this.installed.emit(detail);
  }
}
