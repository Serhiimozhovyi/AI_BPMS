import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { forkJoin } from 'rxjs';
import { ASSISTANT_PROVIDERS, AssistantApiService } from '../assistant/assistant-api.service';
import { NotifyService } from '../shared/notify.service';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialOption, CredentialsApiService } from '../credentials/credentials-api.service';

/** Рядок списку моделей помічника. */
interface Row {
  credentialId: string | null;
  model: string;
  /** Моделі, встановлені в Ollama (F3-101); для інших провайдерів порожньо — модель вводять вручну. */
  suggestions: string[];
}

/**
 * Блок «Помічник» у `/settings` (F3-60, F3-107, лише admin): список підключених моделей (anthropic, openai, gemini,
 * openai-compatible, ollama) і позначка «основна» — її використовують побудова з опису, чат і пояснення сценаріїв.
 * Порожній список — помічник вимкнений.
 */
@Component({
  selector: 'app-assistant-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatRadioModule, MatSelectModule],
  template: `
    <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.assistant.title">Помічник</h2>
    <p class="mb-4 max-w-3xl opacity-70" i18n="@@settings.assistant.hint">
      ШІ-помічник будує й виправляє сценарії за описом. Підключіть одну чи кілька моделей (Anthropic, OpenAI, Google Gemini,
      OpenAI-сумісний сервер або локальна Ollama) і позначте основну. Значень облікових даних і журналу виконань помічник не бачить.
    </p>
    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="assistant-error">{{ error() }}</p>
    }
    <div class="mb-4 max-w-4xl" data-testid="assistant-form">
      @if (rows().length === 0) {
        <p class="mb-3 opacity-70" data-testid="assistant-off" i18n="@@settings.assistant.empty">Помічник вимкнений: жодної моделі не підключено.</p>
      }
      <mat-radio-group [ngModel]="primary()" (ngModelChange)="primary.set($event)" aria-label="Основна модель">
        @for (row of rows(); track $index; let i = $index) {
          <div class="flex flex-wrap items-start gap-2" data-testid="assistant-row">
            <mat-radio-button class="mt-3" [value]="i" [attr.data-testid]="'assistant-primary-' + i" i18n="@@settings.assistant.primary">Основна</mat-radio-button>
            <mat-form-field appearance="outline" class="w-72">
              <mat-label i18n="@@settings.assistant.credential">Облікові дані моделі</mat-label>
              <mat-select [ngModel]="row.credentialId" (ngModelChange)="setCredential(i, $event)" [attr.data-testid]="'assistant-credential-' + i">
                @for (c of options(); track c.id) {
                  <mat-option [value]="c.id">{{ c.name }} · {{ c.type }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline" class="w-72">
              <mat-label i18n="@@settings.assistant.model">Модель</mat-label>
              <input matInput [ngModel]="row.model" (ngModelChange)="setModel(i, $event)" [attr.data-testid]="'assistant-model-' + i" placeholder="claude-sonnet-5-5" [attr.list]="'assistant-models-' + i" />
              <datalist [id]="'assistant-models-' + i" [attr.data-testid]="'assistant-models-' + i">
                @for (m of row.suggestions; track m) {
                  <option [value]="m"></option>
                }
              </datalist>
            </mat-form-field>
            <button mat-button type="button" class="mt-2" [attr.data-testid]="'assistant-remove-' + i" (click)="remove(i)" i18n="@@settings.assistant.remove">Прибрати</button>
          </div>
        }
      </mat-radio-group>
      <div class="flex items-center gap-2">
        <button mat-stroked-button type="button" data-testid="assistant-add" (click)="add()" i18n="@@settings.assistant.add">Додати модель</button>
        <button mat-flat-button type="button" data-testid="assistant-save" [disabled]="saving()" (click)="save()" i18n="@@settings.assistant.save">Зберегти</button>
        @if (saved()) {
          <span class="text-sm text-green-700" data-testid="assistant-saved" i18n="@@settings.assistant.saved">Збережено</span>
        }
      </div>
    </div>
  `,
})
export class AssistantSection implements OnInit {
  private readonly api = inject(AssistantApiService);
  private readonly credentials = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notify = inject(NotifyService);

  protected readonly options = signal<CredentialOption[]>([]);
  protected readonly rows = signal<Row[]>([]);
  /** Індекс основної моделі в `rows`. */
  protected readonly primary = signal(0);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);

  ngOnInit(): void {
    forkJoin({ settings: this.api.settings(), options: this.credentials.options() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ settings, options }) => {
          this.options.set(options.filter((o) => ASSISTANT_PROVIDERS.includes(o.type)));
          const profiles = settings.profiles?.length
            ? settings.profiles
            : settings.credentialId
              ? [{ credentialId: settings.credentialId, model: settings.model ?? '' }]
              : [];
          this.rows.set(profiles.map((p) => ({ credentialId: p.credentialId, model: p.model, suggestions: [] })));
          this.primary.set(settings.primaryIndex ?? 0);
          profiles.forEach((p, i) => this.loadModels(i, p.credentialId));
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  protected add(): void {
    this.rows.update((rows) => [...rows, { credentialId: null, model: '', suggestions: [] }]);
  }

  protected remove(index: number): void {
    this.rows.update((rows) => rows.filter((_, i) => i !== index));
    // Основна не «стрибає» на інший рядок без потреби: після видалення вона лишається тією самою моделлю.
    this.primary.update((p) => (index < p ? p - 1 : Math.min(p, Math.max(this.rows().length - 1, 0))));
  }

  protected setCredential(index: number, id: string | null): void {
    this.patch(index, { credentialId: id, suggestions: [] });
    this.loadModels(index, id);
  }

  protected setModel(index: number, model: string): void {
    this.patch(index, { model });
  }

  private patch(index: number, change: Partial<Row>): void {
    this.rows.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...change } : r)));
  }

  private loadModels(index: number, id: string | null): void {
    if (!id || this.options().find((o) => o.id === id)?.type !== 'ollama') return;
    this.api
      .models(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (models) => this.patch(index, { suggestions: models }),
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  protected save(): void {
    this.error.set(null);
    this.saved.set(false);
    const rows = this.rows();
    if (rows.some((r) => !r.credentialId || !r.model.trim())) {
      this.error.set($localize`:@@settings.assistant.incomplete:У кожної моделі мають бути облікові дані й назва моделі.`);
      return;
    }
    this.saving.set(true);
    this.api
      .saveSettings(
        rows.map((r) => ({ credentialId: r.credentialId!, model: r.model.trim() })),
        this.primary(),
      )
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.saved.set(true);
          this.notify.success($localize`:@@assistant.saved:Налаштування помічника збережено.`);
        },
        error: (e: unknown) => {
          this.saving.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }
}
