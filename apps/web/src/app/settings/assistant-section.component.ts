import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { forkJoin } from 'rxjs';
import { ASSISTANT_PROVIDERS, AssistantApiService } from '../assistant/assistant-api.service';
import { NotifyService } from '../shared/notify.service';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialOption, CredentialsApiService } from '../credentials/credentials-api.service';

/**
 * Блок «Помічник» у `/settings` (F3-60, лише admin): credential моделі (anthropic, openai, openai-compatible) і модель для
 * ШІ-помічника в редакторі. Без credential'а помічник вимкнений.
 */
@Component({
  selector: 'app-assistant-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <h2 class="mb-2 text-lg font-semibold" i18n="@@settings.assistant.title">Помічник</h2>
    <p class="mb-4 max-w-3xl opacity-70" i18n="@@settings.assistant.hint">
      ШІ-помічник у редакторі будує й виправляє сценарії за описом. Оберіть облікові дані моделі (Anthropic, OpenAI або
      OpenAI-сумісний сервер) і назву моделі. Значень облікових даних і журналу виконань помічник не бачить.
    </p>
    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="assistant-error">{{ error() }}</p>
    }
    <div class="mb-4 flex max-w-3xl flex-wrap items-start gap-2" data-testid="assistant-form">
      <mat-form-field appearance="outline" class="w-72">
        <mat-label i18n="@@settings.assistant.credential">Облікові дані моделі</mat-label>
        <mat-select [formControl]="credential" data-testid="assistant-credential">
          <mat-option [value]="null" i18n="@@settings.assistant.off">Вимкнено</mat-option>
          @for (c of options(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }} · {{ c.type }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" class="w-72">
        <mat-label i18n="@@settings.assistant.model">Модель</mat-label>
        <input matInput [formControl]="model" name="model" data-testid="assistant-model" placeholder="claude-sonnet-5-5" />
      </mat-form-field>
      <button mat-flat-button type="button" data-testid="assistant-save" [disabled]="saving()" (click)="save()" i18n="@@settings.assistant.save">Зберегти</button>
      @if (saved()) {
        <span class="self-center text-sm text-green-700" data-testid="assistant-saved" i18n="@@settings.assistant.saved">Збережено</span>
      }
    </div>
  `,
})
export class AssistantSection implements OnInit {
  private readonly api = inject(AssistantApiService);
  private readonly credentials = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notify = inject(NotifyService);

  protected readonly options = signal<CredentialOption[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);
  protected readonly credential = new FormControl<string | null>(null);
  protected readonly model = new FormControl('', { nonNullable: true });

  ngOnInit(): void {
    forkJoin({ settings: this.api.settings(), options: this.credentials.options() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ settings, options }) => {
          this.options.set(options.filter((o) => ASSISTANT_PROVIDERS.includes(o.type)));
          this.credential.setValue(settings.credentialId);
          this.model.setValue(settings.model ?? '');
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  protected save(): void {
    this.saving.set(true);
    this.saved.set(false);
    this.error.set(null);
    const model = this.model.value.trim();
    this.api
      .saveSettings(this.credential.value, model || null)
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
