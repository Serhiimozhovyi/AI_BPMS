import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { WorkflowSummary, WorkflowValidationError, WorkflowsApiService } from '../workflows/workflows-api.service';
import { LimitField, SettingsFormValue, WorkflowSettings, applySettings, limitErrors, settingsFormValue, timeoutError, timezones } from './workflow-settings';
import { BpButtonDirective } from '../shared/button.directive';
import { MatIconModule } from '@angular/material/icon';

export interface WorkflowSettingsDialogData {
  /** Сценарій, що редагується, — його немає у виборі сценарію помилок. */
  workflowId: string;
  settings: WorkflowSettings;
  /** Помилки останнього збереження: помилка `/settings/…` показується біля поля. */
  errors?: readonly WorkflowValidationError[];
}

/**
 * Діалог «Налаштування» сценарію (F4-05): часовий пояс, таймаут виконання, сценарій помилок, збереження даних
 * успішних виконань, ліміти навантаження (F6-06). Закривається новими `settings` (редактор застосовує їх як звичайну зміну — з історією й
 * автозбереженням) або `undefined` при скасуванні. Межу таймауту перевіряє Api: її помилка приходить з `path`.
 */
@Component({
  selector: 'app-workflow-settings-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatIconModule, MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatSlideToggleModule],
  template: `
    <h2 mat-dialog-title i18n="@@workflowsettings.title">Налаштування сценарію</h2>
    <button mat-icon-button type="button" bpVariant="close" class="bp-dialog-close" mat-dialog-close data-testid="dialog-close" i18n-aria-label="@@dialog.close" aria-label="Закрити вікно"><mat-icon>close</mat-icon></button>
    <mat-dialog-content>
      <div class="flex flex-col gap-2 pt-2" data-testid="workflow-settings">
        <mat-form-field appearance="outline">
          <mat-label i18n="@@workflowsettings.timezone">Часовий пояс</mat-label>
          <mat-select data-testid="settings-timezone" [value]="value().timezone" (selectionChange)="patch({ timezone: $event.value })">
            @for (z of zones(); track z) {
              <mat-option [value]="z">{{ z }}</mat-option>
            }
          </mat-select>
          <mat-hint i18n="@@workflowsettings.timezone.hint">Для розкладів і дат у виразах</mat-hint>
        </mat-form-field>

        <mat-form-field appearance="outline">
          <mat-label i18n="@@workflowsettings.timeout">Таймаут виконання, с</mat-label>
          <input
            matInput
            inputmode="numeric"
            data-testid="settings-timeout"
            [value]="value().executionTimeoutSec"
            (input)="patch({ executionTimeoutSec: text($event) })"
          />
          @if (timeoutMessage(); as message) {
            <!-- mat-error видно лише з FormControl у стані помилки; тут поле без форми, тож підказка кольору помилки. -->
            <mat-hint class="settings__error" data-testid="settings-timeout-error">{{ message }}</mat-hint>
          } @else {
            <mat-hint i18n="@@workflowsettings.timeout.hint">Порожньо — без власного ліміту, діє серверна межа</mat-hint>
          }
        </mat-form-field>

        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label i18n="@@workflowsettings.errorWorkflow">Сценарій помилок</mat-label>
          <mat-select data-testid="settings-error-workflow" [value]="value().errorWorkflowId" (selectionChange)="patch({ errorWorkflowId: $event.value })">
            <mat-option [value]="null" i18n="@@workflowsettings.errorWorkflow.none">Немає</mat-option>
            @for (w of errorWorkflows(); track w.id) {
              <mat-option [value]="w.id" [attr.data-id]="w.id">{{ w.name }}</mat-option>
            }
          </mat-select>
          <mat-hint i18n="@@workflowsettings.errorWorkflow.hint">Запускається, коли виконання завершилось помилкою або перевищило час</mat-hint>
        </mat-form-field>

        <mat-slide-toggle
          data-testid="settings-save-successful"
          [checked]="value().saveSuccessfulData"
          (change)="patch({ saveSuccessfulData: $event.checked })"
          i18n="@@workflowsettings.saveSuccessfulData"
          >Зберігати дані успішних виконань</mat-slide-toggle
        >

        <h3 class="settings__section" i18n="@@workflowsettings.limits">Навантаження</h3>
        <p class="settings__note" i18n="@@workflowsettings.limits.note">
          Ліміти діють на запуски тригерами, розкладом і через API; ручний запуск з редактора їх не враховує.
        </p>

        <div class="flex flex-wrap gap-2">
          <mat-form-field appearance="outline" class="settings__number">
            <mat-label i18n="@@workflowsettings.concurrency">Одночасних виконань</mat-label>
            <input matInput inputmode="numeric" data-testid="settings-concurrency-max" [value]="value().concurrencyMax" (input)="patch({ concurrencyMax: text($event) })" />
            @if (limitMessage('concurrencyMax'); as message) {
              <mat-hint class="settings__error" data-testid="settings-concurrency-max-error">{{ message }}</mat-hint>
            } @else {
              <mat-hint i18n="@@workflowsettings.concurrency.hint">Максимум; порожньо — без ліміту</mat-hint>
            }
          </mat-form-field>
          <mat-form-field appearance="outline" class="settings__action">
            <mat-label i18n="@@workflowsettings.onLimit">Понад ліміт</mat-label>
            <mat-select data-testid="settings-concurrency-on-limit" [value]="value().concurrencyOnLimit" [disabled]="!value().concurrencyMax.trim()" (selectionChange)="patch({ concurrencyOnLimit: $event.value })">
              <mat-option value="queue" i18n="@@workflowsettings.onLimit.queue">Чекати в черзі</mat-option>
              <mat-option value="drop" i18n="@@workflowsettings.onLimit.drop">Відкинути</mat-option>
            </mat-select>
          </mat-form-field>
        </div>

        <div class="flex flex-wrap gap-2">
          <mat-form-field appearance="outline" class="settings__number">
            <mat-label i18n="@@workflowsettings.throttle">Запусків за період</mat-label>
            <input matInput inputmode="numeric" data-testid="settings-throttle-limit" [value]="value().throttleLimit" (input)="patch({ throttleLimit: text($event) })" />
            @if (limitMessage('throttleLimit'); as message) {
              <mat-hint class="settings__error" data-testid="settings-throttle-limit-error">{{ message }}</mat-hint>
            } @else {
              <mat-hint i18n="@@workflowsettings.throttle.hint">Максимум; порожньо — без ліміту</mat-hint>
            }
          </mat-form-field>
          <mat-form-field appearance="outline" class="settings__number">
            <mat-label i18n="@@workflowsettings.throttlePeriod">Період, с</mat-label>
            <input matInput inputmode="numeric" data-testid="settings-throttle-period" placeholder="60" [disabled]="!value().throttleLimit.trim()" [value]="value().throttlePeriodSec" (input)="patch({ throttlePeriodSec: text($event) })" />
            @if (limitMessage('throttlePeriodSec'); as message) {
              <mat-hint class="settings__error" data-testid="settings-throttle-period-error">{{ message }}</mat-hint>
            }
          </mat-form-field>
          <mat-form-field appearance="outline" class="settings__action">
            <mat-label i18n="@@workflowsettings.onLimit">Понад ліміт</mat-label>
            <mat-select data-testid="settings-throttle-on-limit" [value]="value().throttleOnLimit" [disabled]="!value().throttleLimit.trim()" (selectionChange)="patch({ throttleOnLimit: $event.value })">
              <mat-option value="queue" i18n="@@workflowsettings.onLimit.queue">Чекати в черзі</mat-option>
              <mat-option value="drop" i18n="@@workflowsettings.onLimit.drop">Відкинути</mat-option>
            </mat-select>
          </mat-form-field>
        </div>

        <mat-form-field appearance="outline">
            <mat-label i18n="@@workflowsettings.disableAfterErrors">Вимкнути після помилок</mat-label>
            <input matInput inputmode="numeric" data-testid="settings-disable-after-errors" [value]="value().disableAfterErrors" (input)="patch({ disableAfterErrors: text($event) })" />
            @if (limitMessage('disableAfterErrors'); as message) {
              <mat-hint class="settings__error" data-testid="settings-disable-after-errors-error">{{ message }}</mat-hint>
            } @else {
              <mat-hint i18n="@@workflowsettings.disableAfterErrors.hint">Скільки помилкових запусків тригера поспіль деактивують сценарій; порожньо — ніколи</mat-hint>
            }
          </mat-form-field>

        <mat-slide-toggle
          data-testid="settings-skip-if-active"
          [checked]="value().skipIfActive"
          (change)="patch({ skipIfActive: $event.checked })"
          i18n="@@workflowsettings.skipIfActive"
          >Не запускати дубль, поки йде виконання з тим самим ключем кореляції</mat-slide-toggle
        >
      </div>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" data-testid="settings-cancel" (click)="cancel()" i18n="@@workflowsettings.cancel">Скасувати</button>
      <button mat-flat-button type="button" data-testid="settings-apply" [disabled]="invalid()" (click)="apply()" i18n="@@workflowsettings.apply">
        Застосувати
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .settings__error {
      color: var(--mat-sys-error);
    }
    .settings__section {
      margin: 8px 0 0;
      font: var(--mat-sys-title-small);
    }
    .settings__note {
      margin: 0 0 4px;
      font: var(--mat-sys-body-small);
      color: var(--mat-sys-on-surface-variant);
    }
    .settings__number {
      flex: 1 1 14rem;
    }
    .settings__action {
      flex: 1 1 14rem;
    }
  `,
})
export class WorkflowSettingsDialogComponent implements OnInit {
  private readonly data = inject<WorkflowSettingsDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject<MatDialogRef<WorkflowSettingsDialogComponent, WorkflowSettings>>(MatDialogRef);
  private readonly workflows = inject(WorkflowsApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly value = signal<SettingsFormValue>(settingsFormValue(this.data.settings));
  private readonly others = signal<WorkflowSummary[]>([]);

  protected readonly zones = computed(() => timezones(this.data.settings.timezone));

  /** Інші сценарії; збережений id, якого вже немає, лишається у списку з позначкою. */
  protected readonly errorWorkflows = computed(() => {
    const list = this.others().filter((w) => w.id !== this.data.workflowId);
    const current = this.data.settings.errorWorkflowId;
    if (current && !list.some((w) => w.id === current)) {
      list.push({ id: current, name: $localize`:@@workflowsettings.errorWorkflow.missing:${current}:id: (не знайдено)`, active: false, version: 0, updatedAt: '' });
    }
    return list;
  });

  protected readonly localTimeoutError = computed(() => timeoutError(this.value().executionTimeoutSec));
  private readonly limitErrors = computed(() => limitErrors(this.value()));
  protected readonly invalid = computed(() => !!this.localTimeoutError() || Object.keys(this.limitErrors()).length > 0);

  /** Помилка поля ліміту: ціле число в межах схеми. */
  protected limitMessage(field: LimitField): string | null {
    const range = this.limitErrors()[field];
    return range ? $localize`:@@workflowsettings.limits.range:Ціле число від ${range[0]}:min: до ${range[1]}:max:` : null;
  }

  /** Локальна помилка поля або помилка Api для `/settings/executionTimeoutSec`, доки значення не змінили. */
  protected readonly timeoutMessage = computed(() => {
    if (this.localTimeoutError()) return $localize`:@@workflowsettings.timeout.integer:Ціле число секунд ≥ 0`;
    const unchanged = this.value().executionTimeoutSec === settingsFormValue(this.data.settings).executionTimeoutSec;
    const server = (this.data.errors ?? []).find((e) => e.path === '/settings/executionTimeoutSec');
    return unchanged && server ? server.message : null;
  });

  ngOnInit(): void {
    this.workflows
      .list({}, 1, 200)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (page) => this.others.set(page.items), error: () => this.others.set([]) });
  }

  protected patch(change: Partial<SettingsFormValue>): void {
    this.value.update((v) => ({ ...v, ...change }));
  }

  protected text(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }

  protected apply(): void {
    if (this.invalid()) return;
    this.dialogRef.close(applySettings(this.data.settings, this.value()));
  }

  protected cancel(): void {
    this.dialogRef.close(undefined);
  }
}
