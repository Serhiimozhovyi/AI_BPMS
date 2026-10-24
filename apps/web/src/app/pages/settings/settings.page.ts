import { Clipboard } from '@angular/cdk/clipboard';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DOCUMENT, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ApiKeyCreated, ApiKeyInfo, ApiKeysApiService, mcpClientConfig } from '../../settings/api-keys-api.service';
import { AuthService } from '../../auth/auth.service';
import { AssistantSection } from '../../settings/assistant-section.component';
import { AuditSection } from '../../settings/audit-section.component';
import { NotificationChannelsSection } from '../../settings/notification-channels-section.component';
import { VariablesSection } from '../../settings/variables-section.component';

/**
 * `/settings`: свої API-ключі (створення, відкликання), підключення ШІ-агента до `/mcp` (F3-07), змінні інстансу (F6-09)
 * канали сповіщень (F6-07) і журнал аудиту (F6-15) для admin.
 */
@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, VariablesSection, AssistantSection, NotificationChannelsSection, AuditSection],
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-4" i18n="@@settings.title">Налаштування</h1>

      <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.apiKeys.title">API-ключі</h2>
      <p class="mb-4 opacity-70" i18n="@@settings.apiKeys.hint">
        Ключ дає доступ до REST і MCP-сервера OpenBP з вашими правами. Зберігайте його як пароль.
      </p>

      <div class="mb-4 flex items-start gap-2">
        <mat-form-field appearance="outline" class="w-80">
          <mat-label i18n="@@settings.apiKeys.name">Назва ключа</mat-label>
          <input matInput [formControl]="name" name="name" data-testid="key-name" (keydown.enter)="create()" />
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
                <td class="p-2">{{ k.name }}</td>
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

      <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.mcp.title">Підключити ШІ-агента</h2>
      <p class="mb-2 max-w-3xl opacity-70" i18n="@@settings.mcp.hint">
        OpenBP — MCP-сервер. Додайте цей фрагмент у конфігурацію MCP-клієнта (Claude, Cursor тощо), підставивши свій API-ключ.
        Агент зможе описувати конектори, створювати й запускати сценарії та тестувати вузли.
      </p>
      <p class="mb-2 font-mono text-sm" data-testid="mcp-url">{{ mcpUrl }}</p>
      <div class="flex max-w-3xl items-start gap-2">
        <pre class="flex-1 overflow-x-auto rounded bg-black/5 p-3 font-mono text-sm" data-testid="mcp-config">{{ config() }}</pre>
        <button mat-stroked-button type="button" data-testid="copy-config" (click)="copy(config())" i18n="@@settings.copy">
          Скопіювати
        </button>
      </div>

      <div class="mt-8">
        <app-variables-section />
      </div>

      @if (isAdmin()) {
        <div class="mt-8" data-testid="assistant-section">
          <app-assistant-section />
        </div>
        <div class="mt-8" data-testid="notifications-section">
          <app-notification-channels-section />
        </div>
        <div class="mt-8" data-testid="audit-section">
          <app-audit-section />
        </div>
      }
    </section>
  `,
})
export class SettingsPage implements OnInit {
  private readonly api = inject(ApiKeysApiService);
  private readonly clipboard = inject(Clipboard);
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);

  readonly isAdmin = computed(() => this.auth.user()?.role === 'admin');
  readonly keys = signal<ApiKeyInfo[]>([]);
  readonly created = signal<ApiKeyCreated | null>(null);
  readonly loading = signal(false);
  readonly creating = signal(false);
  readonly copied = signal(false);
  readonly error = signal<string | null>(null);

  readonly name = new FormControl('', { nonNullable: true, validators: [Validators.required] });

  /** Адреса `/mcp` на тому ж хості, що й UI. */
  readonly mcpUrl = `${inject(DOCUMENT).location.origin}/mcp`;

  /** Фрагмент конфігурації: зі щойно створеним ключем, інакше із заглушкою. */
  readonly config = computed(() =>
    mcpClientConfig(this.mcpUrl, this.created()?.key ?? $localize`:@@settings.mcp.keyPlaceholder:<ваш API-ключ>`),
  );

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

  create(): void {
    const name = this.name.value.trim();
    if (!name) {
      this.name.markAsTouched();
      return;
    }
    this.creating.set(true);
    this.error.set(null);
    this.api
      .create(name)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (created) => {
          this.creating.set(false);
          this.copied.set(false);
          this.created.set(created);
          this.name.reset();
          this.load();
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
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  copy(text: string): void {
    this.copied.set(this.clipboard.copy(text));
  }
}
