import { ChangeDetectionStrategy, Component, OnInit, signal } from '@angular/core';
import { ReactiveFormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatChipsModule } from '@angular/material/chips';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatRadioModule } from '@angular/material/radio';
import { MatSelectModule } from '@angular/material/select';
import { FieldType, FieldTypeConfig, FieldWrapper, FormlyAttributes, FormlyFieldConfig, FormlyValidationMessage, provideFormlyCore } from '@ngx-formly/core';
import { PF_CHECKBOX, PF_CHIPS, PF_FILE, PF_INPUT, PF_RADIO, PF_SECTION, PF_SELECT, PF_TEXTAREA, PublicFieldProps, SectionProps } from './form-fields';
import { maskPhone } from './phone';
import { SelectSearchComponent } from '../shared/select-search/select-search.component';

/** Типи полів публічної форми (F6-03): Angular Material у стилі AC-Kit, підказка і помилка під полем. */

type PublicField = FieldTypeConfig<PublicFieldProps>;

@Component({
  selector: 'app-pf-input',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, FormlyAttributes, FormlyValidationMessage],
  template: `
    <mat-form-field appearance="outline" class="w-full">
      <mat-label>{{ props.label }}</mat-label>
      @if (props.type === 'number') {
        <input matInput type="number" [placeholder]="props.placeholder ?? ''" [formControl]="formControl" [formlyAttributes]="field" [required]="!!props.required" [attr.data-testid]="'form-field-' + key" />
      } @else if (props.phone) {
        <input
          matInput
          type="tel"
          inputmode="tel"
          autocomplete="tel"
          [placeholder]="props.placeholder ?? ''"
          [formControl]="formControl"
          [formlyAttributes]="field"
          [required]="!!props.required"
          [attr.data-testid]="'form-field-' + key"
          (input)="mask($event)"
        />
      } @else {
        <input matInput [type]="props.type ?? 'text'" [placeholder]="props.placeholder ?? ''" [formControl]="formControl" [formlyAttributes]="field" [required]="!!props.required" [attr.data-testid]="'form-field-' + key" />
      }
      @if (props.description) {
        <mat-hint>{{ props.description }}</mat-hint>
      }
      <mat-error><formly-validation-message [field]="field" /></mat-error>
    </mat-form-field>
  `,
})
export class PublicInputFieldComponent extends FieldType<PublicField> {
  /** Телефон (F11-07): маска `+380 67 123 45 67` під час введення; у даних — цифри (`toPayload`). */
  protected mask(event: Event): void {
    const input = event.target as HTMLInputElement;
    const masked = maskPhone(input.value);
    if (masked !== input.value) {
      input.value = masked;
      this.formControl.setValue(masked);
    }
  }
}

/** Варіанти кнопками (F11-07, `x-openbp-ui.widget: "radio"`): до 6 варіантів, стрілки перемикають вибір. */
@Component({
  selector: 'app-pf-radio',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatRadioModule, FormlyAttributes, FormlyValidationMessage],
  template: `
    <div class="pf-choice" role="group" [attr.aria-labelledby]="id + '-label'">
      <div class="pf-choice__label" [id]="id + '-label'">
        {{ props.label }}@if (props.required) {<span aria-hidden="true"> *</span>}
      </div>
      <mat-radio-group class="pf-choice__options" [formControl]="formControl" [formlyAttributes]="field" [required]="!!props.required" [attr.aria-labelledby]="id + '-label'" [attr.data-testid]="'form-field-' + key">
        @for (option of props.options ?? []; track $index) {
          <mat-radio-button [value]="option.value" [attr.data-testid]="'form-option-' + key + '-' + $index">{{ option.label }}</mat-radio-button>
        }
      </mat-radio-group>
      @if (props.description) {
        <div class="pf-hint">{{ props.description }}</div>
      }
      @if (showError) {
        <div class="pf-error" role="alert"><formly-validation-message [field]="field" /></div>
      }
    </div>
  `,
  styles: `
    .pf-choice {
      margin: 0 0 16px;
    }
    .pf-choice__label {
      margin-bottom: 4px;
      font-size: 14px;
      color: var(--bp-shell-2);
    }
    .pf-choice__options {
      display: flex;
      flex-wrap: wrap;
      gap: 0 16px;
    }
    .pf-hint {
      font-size: 12px;
      color: var(--bp-shell-2);
    }
    .pf-error {
      font-size: 12px;
      color: var(--bp-danger);
    }
  `,
})
export class PublicRadioFieldComponent extends FieldType<PublicField> {}

/** Кілька варіантів чипами (F11-07, `x-openbp-ui.widget: "chips"`): Tab — до списку, стрілки й пробіл — вибір. */
@Component({
  selector: 'app-pf-chips',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatChipsModule, FormlyAttributes, FormlyValidationMessage],
  template: `
    <div class="pf-choice">
      <div class="pf-choice__label" [id]="id + '-label'">
        {{ props.label }}@if (props.required) {<span aria-hidden="true"> *</span>}
      </div>
      <mat-chip-listbox multiple [formControl]="formControl" [formlyAttributes]="field" [attr.aria-labelledby]="id + '-label'" [attr.data-testid]="'form-field-' + key">
        @for (option of props.options ?? []; track $index) {
          <mat-chip-option [value]="option.value" [attr.data-testid]="'form-option-' + key + '-' + $index">{{ option.label }}</mat-chip-option>
        }
      </mat-chip-listbox>
      @if (props.description) {
        <div class="pf-hint">{{ props.description }}</div>
      }
      @if (showError) {
        <div class="pf-error" role="alert"><formly-validation-message [field]="field" /></div>
      }
    </div>
  `,
  styles: `
    .pf-choice {
      margin: 0 0 16px;
    }
    .pf-choice__label {
      margin-bottom: 6px;
      font-size: 14px;
      color: var(--bp-shell-2);
    }
    .pf-hint {
      margin-top: 4px;
      font-size: 12px;
      color: var(--bp-shell-2);
    }
    .pf-error {
      margin-top: 4px;
      font-size: 12px;
      color: var(--bp-danger);
    }
  `,
})
export class PublicChipsFieldComponent extends FieldType<PublicField> {}

@Component({
  selector: 'app-pf-textarea',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, FormlyAttributes, FormlyValidationMessage],
  template: `
    <mat-form-field appearance="outline" class="w-full">
      <mat-label>{{ props.label }}</mat-label>
      <textarea matInput [rows]="props.rows ?? 4" [placeholder]="props.placeholder ?? ''" [formControl]="formControl" [formlyAttributes]="field" [required]="!!props.required" [attr.data-testid]="'form-field-' + key"></textarea>
      @if (props.description) {
        <mat-hint>{{ props.description }}</mat-hint>
      }
      <mat-error><formly-validation-message [field]="field" /></mat-error>
    </mat-form-field>
  `,
})
export class PublicTextareaFieldComponent extends FieldType<PublicField> {}

@Component({
  selector: 'app-pf-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatSelectModule, FormlyAttributes, FormlyValidationMessage, SelectSearchComponent],
  template: `
    <mat-form-field appearance="outline" class="w-full">
      <mat-label>{{ props.label }}</mat-label>
      <mat-select [formControl]="formControl" [formlyAttributes]="field" [multiple]="!!props.multiple" [required]="!!props.required" [attr.data-testid]="'form-field-' + key">
        <bp-select-search />
        @for (option of props.options ?? []; track $index) {
          <mat-option [value]="option.value">{{ option.label }}</mat-option>
        }
      </mat-select>
      @if (props.description) {
        <mat-hint>{{ props.description }}</mat-hint>
      }
      <mat-error><formly-validation-message [field]="field" /></mat-error>
    </mat-form-field>
  `,
})
export class PublicSelectFieldComponent extends FieldType<PublicField> {}

@Component({
  selector: 'app-pf-checkbox',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatCheckboxModule, FormlyAttributes, FormlyValidationMessage],
  template: `
    <div class="pf-checkbox">
      <mat-checkbox [formControl]="formControl" [formlyAttributes]="field" [attr.data-testid]="'form-field-' + key">{{ props.label }}</mat-checkbox>
      @if (props.description) {
        <div class="pf-hint">{{ props.description }}</div>
      }
      @if (showError) {
        <div class="pf-error" role="alert"><formly-validation-message [field]="field" /></div>
      }
    </div>
  `,
  styles: `
    .pf-checkbox {
      margin: 0 0 16px;
    }
    .pf-hint {
      margin-left: 40px;
      font-size: 12px;
      color: var(--bp-shell-2);
    }
    .pf-error {
      margin-left: 40px;
      font-size: 12px;
      color: var(--bp-danger);
    }
  `,
})
export class PublicCheckboxFieldComponent extends FieldType<PublicField> {}

@Component({
  selector: 'app-pf-file',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, FormlyValidationMessage],
  template: `
    <div class="pf-file">
      <div class="pf-file__label">
        {{ props.label }}@if (props.required) {<span aria-hidden="true"> *</span>}
      </div>
      <div class="pf-file__row">
        <button mat-stroked-button type="button" (click)="input.click()" i18n="@@form.file.choose">Обрати файл</button>
        <span class="pf-file__name" [attr.data-testid]="'form-file-name-' + key">{{ fileName() }}</span>
      </div>
      <input #input class="sr-only" type="file" [attr.data-testid]="'form-field-' + key" [attr.aria-label]="props.label" (change)="pick(input)" />
      @if (props.description) {
        <div class="pf-hint">{{ props.description }}</div>
      }
      @if (showError) {
        <div class="pf-error" role="alert"><formly-validation-message [field]="field" /></div>
      }
    </div>
  `,
  styles: `
    .pf-file {
      margin: 0 0 20px;
    }
    .pf-file__label {
      margin-bottom: 6px;
      font-size: 14px;
      color: var(--bp-shell-2);
    }
    .pf-file__row {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    .pf-file__name {
      font-size: 14px;
      overflow-wrap: anywhere;
    }
    .pf-hint {
      margin-top: 4px;
      font-size: 12px;
      color: var(--bp-shell-2);
    }
    .pf-error {
      margin-top: 4px;
      font-size: 12px;
      color: var(--bp-danger);
    }
  `,
})
export class PublicFileFieldComponent extends FieldType<PublicField> {
  /** Назва обраного файлу або «не обрано». */
  protected fileName(): string {
    const value: unknown = this.formControl.value;
    return value instanceof File ? value.name : $localize`:@@form.file.none:Файл не обрано`;
  }

  protected pick(input: HTMLInputElement): void {
    this.formControl.setValue(input.files?.[0] ?? null);
    this.formControl.markAsTouched();
  }
}

/**
 * Секція форми з розміткою (F11-01): заголовок і опис над рядками полів; `collapsible` — заголовок стає кнопкою
 * згортання (`collapsed` — згорнута спочатку). Згорнута секція лише ховає поля: значення й перевірки лишаються.
 */
@Component({
  selector: 'app-pf-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pf-section" [class.pf-section--titled]="!!props.label" data-testid="form-section">
      @if (props.label) {
        @if (props.collapsible) {
          <button type="button" class="pf-section__toggle" [attr.aria-expanded]="open()" (click)="open.set(!open())" data-testid="form-section-toggle">
            <span class="material-symbols-outlined" aria-hidden="true">{{ open() ? 'expand_less' : 'expand_more' }}</span>
            <span class="pf-section__title">{{ props.label }}</span>
          </button>
        } @else {
          <h2 class="pf-section__title">{{ props.label }}</h2>
        }
      }
      @if (props.description) {
        <p class="pf-section__description">{{ props.description }}</p>
      }
      <div class="pf-section__body" [hidden]="!open()">
        <ng-container #fieldComponent></ng-container>
      </div>
    </section>
  `,
  styles: `
    .pf-section--titled {
      margin: 0 0 16px;
      padding: 16px 16px 0;
      border: 1px solid var(--bp-line);
      border-radius: 12px;
      background: #fff;
    }
    .pf-section__title {
      margin: 0 0 4px;
      font-size: 16px;
      font-weight: 600;
      color: var(--bp-text);
    }
    .pf-section__toggle {
      display: flex;
      align-items: center;
      gap: 4px;
      margin: 0 0 4px -4px;
      padding: 2px 4px;
      border: 0;
      border-radius: 8px;
      background: none;
      cursor: pointer;
      color: var(--bp-text);
    }
    .pf-section__toggle:focus-visible {
      outline: 2px solid var(--bp-primary);
      outline-offset: 2px;
    }
    .pf-section__toggle .pf-section__title {
      margin: 0;
    }
    .pf-section__description {
      margin: 0 0 12px;
      font-size: 14px;
      color: var(--bp-text-muted);
    }
    .pf-section__body {
      padding-top: 8px;
    }
  `,
})
export class PublicSectionWrapperComponent extends FieldWrapper<FormlyFieldConfig<SectionProps>> implements OnInit {
  protected readonly open = signal(true);

  ngOnInit(): void {
    this.open.set(!(this.props.collapsible && this.props.collapsed));
  }
}

/** Formly для публічної форми: типи полів і тексти помилок українською; `server` — помилка поля з відповіді Api. */
export function providePublicForm() {
  return provideFormlyCore({
    types: [
      { name: PF_INPUT, component: PublicInputFieldComponent },
      { name: PF_TEXTAREA, component: PublicTextareaFieldComponent },
      { name: PF_SELECT, component: PublicSelectFieldComponent },
      { name: PF_CHECKBOX, component: PublicCheckboxFieldComponent },
      { name: PF_FILE, component: PublicFileFieldComponent },
      { name: PF_RADIO, component: PublicRadioFieldComponent },
      { name: PF_CHIPS, component: PublicChipsFieldComponent },
    ],
    wrappers: [{ name: PF_SECTION, component: PublicSectionWrapperComponent }],
    validationMessages: [
      { name: 'required', message: $localize`:@@form.error.required:Заповніть це поле.` },
      { name: 'minLength', message: (_e: unknown, f: FormlyFieldConfig) => $localize`:@@form.error.minLength:Щонайменше ${f.props?.minLength}:min: символів.` },
      { name: 'maxLength', message: (_e: unknown, f: FormlyFieldConfig) => $localize`:@@form.error.maxLength:Не більше ${f.props?.maxLength}:max: символів.` },
      { name: 'min', message: (_e: unknown, f: FormlyFieldConfig) => $localize`:@@form.error.min:Значення має бути не менше ${f.props?.min}:min:.` },
      { name: 'max', message: (_e: unknown, f: FormlyFieldConfig) => $localize`:@@form.error.max:Значення має бути не більше ${f.props?.max}:max:.` },
      { name: 'pattern', message: $localize`:@@form.error.pattern:Значення не відповідає потрібному формату.` },
      { name: 'email', message: $localize`:@@form.error.email:Вкажіть коректну адресу пошти.` },
      { name: 'phone', message: $localize`:@@form.error.phone:Вкажіть номер телефону, напр. +380 67 123 45 67.` },
      { name: 'server', message: (error: unknown) => String(error) },
    ],
  });
}
