import { Clipboard } from '@angular/cdk/clipboard';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DOCUMENT, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { NotifyService } from '../../shared/notify.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ApiKeyCreated, ApiKeyInfo, ApiKeysApiService } from '../../settings/api-keys-api.service';
import { isLocalMcpUrl, mcpClientTemplates } from '../../settings/mcp-client-templates';
import { AuthService } from '../../auth/auth.service';
import { AssistantSection } from '../../settings/assistant-section.component';
import { AuditSection } from '../../settings/audit-section.component';
import { NotificationChannelsSection } from '../../settings/notification-channels-section.component';
import { VariablesSection } from '../../settings/variables-section.component';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';

export interface SettingsSectionItem {
  id: 'api-keys' | 'agent' | 'variables' | 'assistant' | 'notifications' | 'audit';
  label: string;
}

/**
 * `/settings`: розділи з навігацією (`?section=`, F3-83) — свої API-ключі (створення, відкликання), підключення ШІ-агента до `/mcp` (F3-07), змінні інстансу (F6-09)
 * канали сповіщень (F6-07) і журнал аудиту (F6-15) для admin.
 */
@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, NgTemplateOutlet, ReactiveFormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, MatSelectModule, BpTabsComponent, VariablesSection, AssistantSection, NotificationChannelsSection, AuditSection, SelectSearchComponent],
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-4" i18n="@@settings.title">Налаштування</h1>

      <div class="flex flex-col gap-6 md:flex-row">
        <nav class="settings-nav" aria-label="Розділи налаштувань" i18n-aria-label="@@settings.nav" data-testid="settings-nav">
          @for (item of sections(); track item.id) {
            <a
              class="settings-nav__item"
              [routerLink]="[]"
              [queryParams]="{ section: item.id }"
              [attr.aria-current]="item.id === section() ? 'page' : null"
              [attr.data-section]="item.id"
              >{{ item.label }}</a
            >
          }
        </nav>

        <div class="min-w-0 flex-1">
          @switch (section()) {
            @case ('api-keys') {
              <ng-container *ngTemplateOutlet="apiKeys" />
            }
            @case ('agent') {
              <ng-container *ngTemplateOutlet="agent" />
            }
            @case ('variables') {
              <app-variables-section />
            }
            @case ('assistant') {
              <div data-testid="assistant-section"><app-assistant-section /></div>
            }
            @case ('notifications') {
              <div data-testid="notifications-section"><app-notification-channels-section /></div>
            }
            @case ('audit') {
              <div data-testid="audit-section"><app-audit-section /></div>
            }
          }
        </div>
      </div>
    </section>

    <ng-template #apiKeys>
      <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.apiKeys.title">API-ключі</h2>
      <p class="mb-4 opacity-70" i18n="@@settings.apiKeys.hint">
        Ключ дає доступ до REST і MCP-сервера AI_BPMS з вашими правами. Зберігайте його як пароль. Ключ проєкту бачить лише
        сценарії, облікові дані й конектори цього проєкту.
      </p>
      <p class="mb-4">
        <a class="underline" href="/swagger" target="_blank" rel="noopener" data-testid="api-docs-link" i18n="@@settings.apiKeys.docs"
          >Документація REST API (Swagger) — усі методи з описами й «Try it out»</a
        >
      </p>

      <div class="mb-4 flex flex-wrap items-start gap-2">
        <mat-form-field appearance="outline" class="w-80">
          <mat-label i18n="@@settings.apiKeys.name">Назва ключа</mat-label>
          <input matInput [formControl]="name" name="name" data-testid="key-name" (keydown.enter)="create()" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="w-64">
          <mat-label i18n="@@settings.apiKeys.scope">Дія ключа</mat-label>
          <mat-select data-testid="key-project" [value]="keyProject()" (selectionChange)="keyProject.set($event.value)">
            <bp-select-search />
            <mat-option value="" i18n="@@settings.apiKeys.scope.all">Увесь інстанс</mat-option>
            @for (p of projects.projects(); track p.id) {
              <mat-option [value]="p.id">{{ p.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <button mat-flat-button type="button" data-testid="create-button" (click)="create()" [disabled]="creating()" i18n="@@settings.apiKeys.create">
          Створити ключ
        </button>
      </div>

      @if (created(); as c) {
        <div class="mb-4 max-w-3xl rounded border border-amber-500 p-4" data-testid="created-key">
          <p class="mb-2 font-semibold" i18n="@@settings.apiKeys.created">
            Ключ «{{ c.name }}» створено. Скопіюйте його зараз — більше він не показуватиметься.
          </p>
          <div class="flex items-center gap-2">
            <code class="break-all font-mono" data-testid="created-key-value">{{ c.key }}</code>
            <button mat-stroked-button type="button" data-testid="copy-key" (click)="copy(c.key)" i18n="@@settings.copy">
              Скопіювати
            </button>
            <button mat-button type="button" data-testid="hide-key" (click)="created.set(null)" i18n="@@settings.apiKeys.hide">
              Готово
            </button>
          </div>
          @if (copied()) {
            <p class="mt-1 text-sm text-green-700" data-testid="copied" i18n="@@settings.copied">Скопійовано</p>
          }
        </div>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && keys().length === 0 && !error()) {
        <p class="mb-6 opacity-70" data-testid="empty" i18n="@@settings.apiKeys.empty">Ще немає жодного ключа.</p>
      }

      @if (keys().length > 0) {
        <table class="mb-6 w-full max-w-3xl text-left" data-testid="keys-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@settings.apiKeys.col.name">Назва</th>
              <th class="p-2" i18n="@@settings.apiKeys.col.lastUsed">Останнє використання</th>
              <th class="p-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (k of keys(); track k.id) {
              <tr [attr.data-key]="k.id">
                <td class="p-2">
                  {{ k.name }}
                  @if (k.projectId) {
                    <span class="text-sm opacity-70" data-testid="key-project-name">· {{ projectName(k.projectId) }}</span>
                  }
                </td>
                <td class="p-2">
                  @if (k.lastUsedAt) {
                    {{ k.lastUsedAt | date: 'short' }}
                  } @else {
                    <span i18n="@@settings.apiKeys.neverUsed">ще не використовувався</span>
                  }
                </td>
                <td class="p-2">
                  <button mat-button type="button" data-testid="revoke" (click)="revoke(k)" i18n="@@settings.apiKeys.revoke">
                    Відкликати
                  </button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      }

    </ng-template>

    <ng-template #agent>
      <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.mcp.title">Підключити ШІ-агента</h2>
      <p class="mb-2 max-w-3xl opacity-70" i18n="@@settings.mcp.hint">
        AI_BPMS — MCP-сервер. Виберіть клієнта, скопіюйте фрагмент і підставте свій API-ключ (щойно створений ключ підставляється сам).
        Агент зможе описувати конектори, створювати й запускати сценарії та тестувати вузли.
      </p>
      <p class="mb-2 font-mono text-sm" data-testid="mcp-url">{{ mcpUrl }}</p>
      <div class="max-w-4xl">
        <bp-tabs variant="icon" [tabs]="mcpTabs" [(value)]="mcpTab" label="Клієнт ШІ-агента" i18n-label="@@settings.mcp.clients" testId="mcp-tabs" />
        @if (mcpTemplate(); as tpl) {
          <div role="tabpanel" class="pt-3" [attr.data-testid]="'mcp-template-' + tpl.id">
            <p class="text-sm"><span class="mr-1 opacity-70" i18n="@@settings.mcp.where">Куди:</span><span data-testid="mcp-where">{{ tpl.where }}</span></p>
            @if (tpl.warning) {
              <p class="mcp-note mcp-note--warn mt-2" role="note" data-testid="mcp-warning">{{ tpl.warning }}</p>
            } @else if (tpl.needsPublicUrl && localUrl) {
              <p class="mcp-note mt-2" role="note" data-testid="mcp-public-hint" i18n="@@settings.mcp.publicHint">
                Цей клієнт звертається до AI_BPMS зі своїх серверів, тому потрібна публічна HTTPS-адреса. Відкрийте AI_BPMS за такою адресою — і фрагмент підставить її сам.
              </p>
            }
            <ol class="mt-2 list-decimal space-y-1 pl-5 text-sm" data-testid="mcp-steps">
              @for (step of tpl.steps; track $index) {
                <li>{{ step }}</li>
              }
            </ol>
            @if (tpl.snippet; as text) {
              <ng-container *ngTemplateOutlet="snippet; context: { $implicit: text, id: tpl.id }" />
            }
          </div>
        }
      </div>

      <h3 class="mb-2 mt-6 font-semibold" i18n="@@settings.llms.title">Інструкція для ШІ-агента</h3>
      <p class="mb-2 max-w-3xl opacity-70" i18n="@@settings.llms.hint">
        Дайте агенту цю адресу: у ній усі інструменти MCP, методи REST і покрокові рецепти роботи з AI_BPMS. Відкривається без
        ключа й завжди відповідає цьому серверу.
      </p>
      <div class="flex max-w-3xl items-center gap-2">
        <a class="flex-1 font-mono text-sm underline" [href]="llmsUrl" target="_blank" rel="noopener" data-testid="llms-url">{{ llmsUrl }}</a>
        <button mat-stroked-button type="button" data-testid="copy-llms-url" (click)="copy(llmsUrl)" i18n="@@settings.copy">Скопіювати</button>
      </div>
    </ng-template>

    <ng-template #snippet let-text let-id="id">
      <div class="flex items-start gap-2 pt-3">
        <pre class="flex-1 overflow-x-auto rounded bg-black/5 p-3 font-mono text-sm" [attr.data-testid]="'mcp-config-' + id">{{ text }}</pre>
        <button mat-stroked-button type="button" [attr.data-testid]="'copy-config-' + id" (click)="copy(text)" i18n="@@settings.copy">
          Скопіювати
        </button>
      </div>
    </ng-template>
  `,
  styles: `
    .mcp-note {
      padding: 8px 12px;
      border-radius: var(--bp-radius-sm);
      background: var(--bp-bg-2);
      font-size: 0.875rem;
    }
    .mcp-note--warn {
      background: var(--bp-warning-15);
      color: var(--bp-warning);
    }
    .settings-nav {
      display: flex;
      gap: 4px;
      overflow-x: auto;
      border-bottom: 1px solid var(--bp-line);
    }
    .settings-nav__item {
      padding: 8px 14px;
      border-radius: var(--bp-radius-sm);
      color: var(--bp-text);
      white-space: nowrap;
      text-decoration: none;
    }
    .settings-nav__item:hover {
      background: var(--bp-bg-2);
    }
    .settings-nav__item[aria-current='page'] {
      background: var(--bp-tonal);
      color: var(--bp-primary);
      font-weight: 600;
    }
    .settings-nav__item:focus-visible {
      outline: 2px solid var(--bp-primary);
      outline-offset: 2px;
    }
    @media (min-width: 768px) {
      .settings-nav {
        flex-direction: column;
        width: 14rem;
        flex-shrink: 0;
        align-self: flex-start;
        border-bottom: 0;
      }
    }
  `,
})
export class SettingsPage implements OnInit {
  private readonly api = inject(ApiKeysApiService);
  private readonly clipboard = inject(Clipboard);
  private readonly notify = inject(NotifyService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);
  protected readonly projects = inject(ProjectContextService);
  /** Проєкт нового ключа (F6-14); порожньо — весь інстанс. */
  readonly keyProject = signal('');
  /** Вкладка клієнта в «Підключити ШІ-агента». */
  readonly mcpTab = signal<string | null>('desktop');

  readonly isAdmin = computed(() => this.auth.user()?.role === 'admin');

  /** Розділи сторінки; три останніх — лише для admin. */
  readonly sections = computed<SettingsSectionItem[]>(() => {
    const common: SettingsSectionItem[] = [
      { id: 'api-keys', label: $localize`:@@settings.nav.apiKeys:API-ключі` },
      { id: 'agent', label: $localize`:@@settings.nav.agent:Підключити ШІ-агента` },
      { id: 'variables', label: $localize`:@@settings.nav.variables:Змінні` },
    ];
    if (!this.isAdmin()) return common;
    return [
      ...common,
      { id: 'assistant', label: $localize`:@@settings.nav.assistant:Помічник` },
      { id: 'notifications', label: $localize`:@@settings.nav.notifications:Сповіщення` },
      { id: 'audit', label: $localize`:@@settings.nav.audit:Журнал аудиту` },
    ];
  });

  private readonly requested = toSignal(inject(ActivatedRoute).queryParamMap.pipe(map((q) => q.get('section'))), { initialValue: null });
  /** Відкритий розділ: з `?section=`, а недоступний (чужий для ролі, невідомий) — перший. */
  readonly section = computed(() => {
    const list = this.sections();
    return list.find((i) => i.id === this.requested())?.id ?? list[0].id;
  });
  readonly keys = signal<ApiKeyInfo[]>([]);
  readonly created = signal<ApiKeyCreated | null>(null);
  readonly loading = signal(false);
  readonly creating = signal(false);
  readonly copied = signal(false);
  readonly error = signal<string | null>(null);

  readonly name = new FormControl('', { nonNullable: true, validators: [Validators.required] });

  /** Адреса `/mcp` на тому ж хості, що й UI. */
  readonly mcpUrl = `${inject(DOCUMENT).location.origin}/mcp`;
  /** Інструкція для ШІ-агентів (F10-02). */
  readonly llmsUrl = this.mcpUrl.replace(/\/mcp$/, '/llms.txt');

  /** Адреса локальна або без HTTPS — хмарним клієнтам (claude.ai, Copilot Studio, OpenAI API) не підійде. */
  readonly localUrl = isLocalMcpUrl(this.mcpUrl);

  /** Шаблони клієнтів (F3-118): зі щойно створеним ключем, інакше із заглушкою. */
  readonly mcpTemplates = computed(() => mcpClientTemplates(this.mcpUrl, this.keyOrPlaceholder()));
  readonly mcpTabs: BpTab[] = mcpClientTemplates('', '').map((t) => ({ value: t.id, icon: t.icon, label: t.label }));
  readonly mcpTemplate = computed(() => {
    const list = this.mcpTemplates();
    return list.find((t) => t.id === this.mcpTab()) ?? list[0];
  });

  private keyOrPlaceholder(): string {
    return this.created()?.key ?? $localize`:@@settings.mcp.keyPlaceholder:<ваш API-ключ>`;
  }

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (keys) => {
          this.keys.set(keys);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  projectName(id: string): string {
    return this.projects.projects().find((p) => p.id === id)?.name ?? id;
  }

  create(): void {
    const name = this.name.value.trim();
    if (!name) {
      this.name.markAsTouched();
      return;
    }
    this.creating.set(true);
    this.error.set(null);
    this.api
      .create(name, this.keyProject() || null)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (created) => {
          this.creating.set(false);
          this.copied.set(false);
          this.created.set(created);
          this.name.reset();
          this.load();
          this.notify.success($localize`:@@settings.keys.created:Ключ створено — скопіюйте його зараз, пізніше він не показується.`);
        },
        error: (e: unknown) => {
          this.creating.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  revoke(key: ApiKeyInfo): void {
    this.error.set(null);
    this.api
      .revoke(key.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.keys.update((list) => list.filter((k) => k.id !== key.id));
          if (this.created()?.id === key.id) this.created.set(null);
          this.notify.success($localize`:@@settings.keys.revoked:Ключ «${key.name}:name:» відкликано.`);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  copy(text: string): void {
    const ok = this.clipboard.copy(text);
    this.copied.set(ok);
    if (ok) this.notify.success($localize`:@@settings.copied:Скопійовано в буфер обміну.`);
    else this.notify.error($localize`:@@settings.copyFailed:Не вдалося скопіювати — виділіть текст і скопіюйте вручну.`);
  }
}
