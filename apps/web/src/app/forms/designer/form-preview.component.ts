import { ChangeDetectionStrategy, Component, computed, effect, input, signal } from '@angular/core';
import { FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { FormlyForm } from '@ngx-formly/core';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';
import { providePublicForm } from '../form-field-types';
import { type JsonSchema, formFields } from '../form-fields';

/** Ширини перегляду: комп'ютер і телефон (docs/forms-designer.md §3.2). */
export type PreviewWidth = 1024 | 390;

/**
 * Перегляд форми (F11-04): справжній рендер, що й на `/form/…` (`formFields` + типи полів `providePublicForm()`),
 * із заголовком, описом і кнопкою вузла; перемикач ширини 1024 / 390 px. Кнопка нічого не надсилає.
 */
@Component({
  selector: 'bp-form-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatButtonModule, FormlyForm, BpTabsComponent],
  providers: [providePublicForm()],
  template: `
    <div class="fp__bar">
      <bp-tabs variant="pills" [tabs]="widths" [value]="'' + width()" (valueChange)="setWidth($event)" label="Ширина перегляду" i18n-label="@@fp.width" testId="fp-width" />
    </div>
    <div class="fp__stage">
      <section class="fp__page" [style.max-width.px]="width()" [attr.data-width]="width()" data-testid="fp-page">
        <h2 class="fp__title" data-testid="fp-title">{{ title() || fallbackTitle }}</h2>
        @if (description()) {
          <p class="fp__description" data-testid="fp-description">{{ description() }}</p>
        }
        @if (fields().length) {
          <form [formGroup]="form" novalidate (submit)="$event.preventDefault()">
            <formly-form [form]="form" [fields]="fields()" [model]="model" />
          </form>
        } @else {
          <p class="fp__empty" data-testid="fp-empty" i18n="@@fp.empty">Полів ще немає — лише кнопка.</p>
        }
        <button mat-flat-button type="button" data-testid="fp-submit">{{ submitLabel() || fallbackSubmit }}</button>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .fp__bar {
      margin-bottom: 12px;
    }
    .fp__stage {
      padding: 16px;
      border-radius: 12px;
      background: var(--bp-bg);
      overflow-x: auto;
    }
    .fp__page {
      margin: 0 auto;
      padding: 24px;
      border: 1px solid var(--bp-line);
      border-radius: 12px;
      background: var(--bp-white, #fff);
    }
    .fp__title {
      margin: 0 0 8px;
      font-size: 20px;
      font-weight: 600;
      color: var(--bp-text);
    }
    .fp__description,
    .fp__empty {
      margin: 0 0 16px;
      color: var(--bp-text-muted);
    }
  `,
})
export class FormPreviewComponent {
  readonly schema = input<JsonSchema | null>(null);
  readonly title = input('');
  readonly description = input('');
  readonly submitLabel = input('');

  protected readonly fallbackTitle = $localize`:@@fp.title:Форма`;
  protected readonly fallbackSubmit = $localize`:@@fp.submit:Надіслати`;
  protected readonly widths: BpTab[] = [
    { value: '1024', label: $localize`:@@fp.desktop:Комп'ютер · 1024`, testId: 'fp-width-1024' },
    { value: '390', label: $localize`:@@fp.phone:Телефон · 390`, testId: 'fp-width-390' },
  ];
  protected readonly width = signal<PreviewWidth>(1024);
  protected readonly fields = computed(() => formFields(this.schema()));
  protected form = new FormGroup({});
  protected model: Record<string, unknown> = {};

  protected setWidth(value: string | null): void {
    this.width.set(value === '390' ? 390 : 1024);
  }

  constructor() {
    // Нова схема — чиста форма: значення зі старих полів не лишаються.
    effect(() => {
      this.schema();
      this.form = new FormGroup({});
      this.model = {};
    });
  }
}
