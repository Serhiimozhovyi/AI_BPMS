import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormControl, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { FieldArrayType, FieldType, FieldTypeConfig, FieldWrapper, FormlyField, FormlyFieldConfig, provideFormlyCore } from '@ngx-formly/core';
import { ValueRules, formatValue, isExpression, parseValue, valueError, valueErrorMessage } from './node-value';
import { ARRAY_FIELD, NodeFieldProps, SECTION_WRAPPER, VALUE_FIELD } from './schema-fields';

/** Правила значення поля; без `rules` — довільний рядок. */
function rulesOf(field: FormlyFieldConfig): ValueRules {
  return (field.props as NodeFieldProps | undefined)?.rules ?? { kind: 'string' };
}

/** Валідатор formly: значення відповідає типу поля або є виразом `{{ }}` (див. {@link valueError}). */
export function nodeValueValidator(control: AbstractControl, field: FormlyFieldConfig): ValidationErrors | null {
  const code = valueError(control.value, rulesOf(field));
  return code ? { [code]: true } : null;
}

/**
 * Поле значення: текстове поле, що приймає `{{ }}` замість будь-якого типу; `boolean` і `enum` — прапорець і
 * список з перемиканням у режим виразу. Текст перетворюється на значення типу зі схеми ({@link parseValue}).
 */
@Component({
  selector: 'app-node-value-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatButtonModule],
  template: `
    <div class="value" [attr.data-field]="key">
      @if (mode() === 'checkbox') {
        <mat-checkbox [formControl]="formControl" [attr.data-testid]="'field-' + key">{{ label }}</mat-checkbox>
      } @else {
        <mat-form-field class="value__field" subscriptSizing="dynamic">
          <mat-label>{{ label }}</mat-label>
          @if (mode() === 'select') {
            <mat-select [formControl]="formControl" [required]="!!props.required" [attr.data-testid]="'field-' + key">
              @if (!props.required) {
                <mat-option [value]="undefined" i18n="@@nodeform.notSet">— не задано —</mat-option>
              }
              @for (o of choices(); track $index) {
                <mat-option [value]="o.value">{{ o.label }}</mat-option>
              }
            </mat-select>
          } @else if (multiline) {
            <textarea
              matInput
              rows="3"
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [attr.data-testid]="'field-' + key"
            ></textarea>
          } @else {
            <input
              matInput
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [attr.data-testid]="'field-' + key"
            />
          }
          @if (props.description) {
            <mat-hint>{{ props.description }}</mat-hint>
          }
          @if (errorText(); as message) {
            <mat-error [attr.data-testid]="'error-' + key">{{ message }}</mat-error>
          }
        </mat-form-field>
      }
      @if (canToggle) {
        <button
          mat-button
          type="button"
          class="value__toggle"
          [class.value__toggle--on]="expression()"
          [attr.aria-pressed]="expression()"
          [attr.data-testid]="'expr-' + key"
          i18n-title="@@nodeform.expression.hint"
          title="Режим виразу"
          (click)="toggleExpression()"
        >{{ expressionLabel }}</button>
      }
    </div>
  `,
  styles: `
    .value {
      display: flex;
      align-items: flex-start;
      gap: 4px;
      margin-bottom: 8px;
    }

    .value__field {
      flex: 1;
      min-width: 0;
    }

    .value__toggle {
      min-width: 0;
      margin-top: 8px;
      font-family: monospace;
      opacity: 0.6;
    }

    .value__toggle--on {
      opacity: 1;
      color: var(--mat-sys-primary);
    }
  `,
})
export class NodeValueFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  /** Текст поля (для всіх видів, крім прапорця і списку поза режимом виразу). */
  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly expression = signal(false);
  protected readonly expressionLabel = '{{ }}';
  private readonly errorVersion = signal(0);

  /** Помилки показуються за станом formly-контрола, а не текстового. */
  protected readonly errorState: ErrorStateMatcher = { isErrorState: () => this.showError };

  protected get rules(): ValueRules {
    return rulesOf(this.field);
  }

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  protected get multiline(): boolean {
    return (this.props as NodeFieldProps).multiline === true;
  }

  /** Перемикач виразу — лише там, де звичайний елемент не приймає тексту. */
  protected get canToggle(): boolean {
    const kind = this.rules.kind;
    return this.rules.allowExpression !== false && (kind === 'boolean' || kind === 'enum');
  }

  protected readonly mode = computed<'checkbox' | 'select' | 'text'>(() => {
    if (this.expression()) return 'text';
    if (this.rules.kind === 'boolean') return 'checkbox';
    if (this.rules.kind === 'enum') return 'select';
    return 'text';
  });

  protected readonly choices = computed(() =>
    (this.rules.options ?? []).map((value) => ({ value, label: typeof value === 'string' ? value : JSON.stringify(value) })),
  );

  protected readonly errorText = computed(() => {
    this.errorVersion();
    const errors = this.formControl?.errors;
    const code = errors ? Object.keys(errors)[0] : null;
    return code ? valueErrorMessage(code, this.rules) : null;
  });

  ngOnInit(): void {
    const value: unknown = this.formControl.value;
    this.expression.set(this.canToggle && isExpression(value));
    this.text.setValue(formatValue(value, this.rules.kind), { emitEvent: false });
    this.text.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((t) => {
      this.formControl.setValue(parseValue(t, this.rules));
      this.formControl.markAsDirty();
      this.formControl.markAsTouched();
    });
    this.formControl.statusChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.errorVersion.update((v) => v + 1));
  }

  protected toggleExpression(): void {
    const on = !this.expression();
    this.expression.set(on);
    if (on) {
      this.text.setValue(formatValue(this.formControl.value, this.rules.kind), { emitEvent: false });
    } else if (valueError(this.formControl.value, { ...this.rules, allowExpression: false })) {
      // Вираз або некоректний текст не показати прапорцем/списком — поле стає незаданим.
      this.formControl.setValue(undefined);
      this.formControl.markAsDirty();
    }
  }
}

/** Масив: елементи за схемою `items`, кнопки «Додати» і «Видалити». */
@Component({
  selector: 'app-node-array-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormlyField, MatButtonModule],
  template: `
    <fieldset class="array" [attr.data-field]="key">
      <legend class="array__legend">{{ props.label }}@if (props.required) {<span aria-hidden="true"> *</span>}</legend>
      @if (props.description) {
        <p class="array__hint">{{ props.description }}</p>
      }
      @for (item of field.fieldGroup; track item.id; let i = $index) {
        <div class="array__item" [attr.data-testid]="'item-' + key + '-' + i">
          <formly-field class="array__body" [field]="item" />
          <button mat-button type="button" [attr.data-testid]="'remove-' + key + '-' + i" (click)="remove(i)" i18n="@@nodeform.array.remove">Видалити</button>
        </div>
      }
      <button mat-stroked-button type="button" [attr.data-testid]="'add-' + key" (click)="add()" i18n="@@nodeform.array.add">Додати</button>
    </fieldset>
  `,
  styles: `
    .array {
      margin: 0 0 12px;
      padding: 8px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 8px;
    }

    .array__legend {
      padding: 0 4px;
      font-size: 13px;
    }

    .array__hint {
      margin: 0 0 8px;
      font-size: 12px;
      opacity: 0.7;
    }

    .array__item {
      display: flex;
      align-items: flex-start;
      gap: 4px;
      padding-bottom: 8px;
      border-bottom: 1px dashed var(--mat-sys-outline-variant);
      margin-bottom: 8px;
    }

    .array__body {
      flex: 1;
      min-width: 0;
    }
  `,
})
export class NodeArrayFieldComponent extends FieldArrayType {}

/** Вкладений об'єкт: рамка з назвою. */
@Component({
  selector: 'app-node-section-wrapper',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fieldset class="section" [attr.data-field]="key">
      @if (props.label) {
        <legend class="section__legend">{{ props.label }}</legend>
      }
      @if (props.description) {
        <p class="section__hint">{{ props.description }}</p>
      }
      <ng-container #fieldComponent />
    </fieldset>
  `,
  styles: `
    .section {
      margin: 0 0 12px;
      padding: 8px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 8px;
    }

    .section__legend {
      padding: 0 4px;
      font-size: 13px;
    }

    .section__hint {
      margin: 0 0 8px;
      font-size: 12px;
      opacity: 0.7;
    }
  `,
})
export class NodeSectionWrapperComponent extends FieldWrapper {}

/** Типи полів і валідатор форми вузла — провайдер для компонента панелі. */
export function provideNodeForm() {
  return provideFormlyCore({
    types: [
      { name: VALUE_FIELD, component: NodeValueFieldComponent, defaultOptions: { validators: { validation: ['node-value'] } } },
      { name: ARRAY_FIELD, component: NodeArrayFieldComponent },
    ],
    wrappers: [{ name: SECTION_WRAPPER, component: NodeSectionWrapperComponent }],
    validators: [{ name: 'node-value', validation: nodeValueValidator }],
    extras: { lazyRender: true },
  });
}
