import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AbstractControl, FormControl, ReactiveFormsModule, ValidationErrors } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { ErrorStateMatcher } from '@angular/material/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { FieldArrayType, FieldType, FieldTypeConfig, FieldWrapper, FormlyField, FormlyFieldConfig, provideFormlyCore } from '@ngx-formly/core';
import { EMPTY_EXPRESSION_CONTEXT, ExpressionContext, Suggestion, applySuggestion, suggestExpression } from './expression-suggest';
import { insertAt } from '../canvas/data-schema';
import { Assignment, assignmentsFromInput, inputFieldNames, isUnary } from './conditions';
import { ExpressionTarget, ExpressionTargetService } from './expression-target';
import { CodeEditorHandle, MONACO_LOADER } from './monaco-loader';
import { ValueRules, formatValue, isExpression, parseValue, valueError, valueErrorMessage } from './node-value';
import { ARRAY_FIELD, ASSIGNMENTS_FIELD, CODE_FIELD, COMBINE_FIELD, CONDITION_FIELD, NodeFieldProps, SECTION_WRAPPER, VALUE_FIELD } from './schema-fields';

/** Спільний стан форми вузла (`FormlyFormOptions.formState`): контекст підказок виразів. */
export interface NodeFormState {
  expressionContext?: ExpressionContext;
}

/** Підказка з готовим текстом поля після її вибору. */
interface SuggestionOption extends Suggestion {
  /** Увесь текст поля після вставки — значення `mat-option`. */
  value: string;
  cursor: number;
}

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
  imports: [ReactiveFormsModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatButtonModule, MatAutocompleteModule],
  template: `
    <div class="value" [attr.data-field]="key">
      @if (mode() === 'checkbox') {
        <mat-checkbox [formControl]="formControl" [attr.data-testid]="'field-' + key">{{ label }}</mat-checkbox>
      } @else {
        <mat-form-field appearance="outline" class="value__field" subscriptSizing="dynamic">
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
              #textEl
              matInput
              rows="3"
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [matAutocomplete]="suggest"
              [matAutocompleteDisabled]="!canSuggest"
              [attr.data-testid]="'field-' + key"
              (input)="updateSuggestions(textEl)"
              (click)="updateSuggestions(textEl)"
              (keyup)="caretMoved($event, textEl)"
              (focus)="activateTarget()"
            ></textarea>
          } @else {
            <input
              #textEl
              matInput
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [matAutocomplete]="suggest"
              [matAutocompleteDisabled]="!canSuggest"
              [attr.data-testid]="'field-' + key"
              (input)="updateSuggestions(textEl)"
              (click)="updateSuggestions(textEl)"
              (keyup)="caretMoved($event, textEl)"
              (focus)="activateTarget()"
            />
          }
          <mat-autocomplete #suggest="matAutocomplete" class="expr-suggest" (optionSelected)="suggestionPicked($event)">
            @for (o of suggestions(); track o.label) {
              <mat-option [value]="o.value" [attr.data-testid]="'suggest-' + o.label">
                <span class="expr-suggest__label">{{ o.label }}</span>
                @if (o.detail) {
                  <span class="expr-suggest__detail">{{ o.detail }}</span>
                }
              </mat-option>
            }
          </mat-autocomplete>
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

    .expr-suggest__label {
      font-family: monospace;
    }

    .expr-suggest__detail {
      margin-left: 8px;
      font-size: 12px;
      opacity: 0.6;
    }
  `,
})
export class NodeValueFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  /** Ціль вставки виразів з панелі входу (F3-52); поза редактором — немає. */
  private readonly targets = inject(ExpressionTargetService, { optional: true });
  private readonly target: ExpressionTarget = {
    label: () => this.label,
    insert: (expression) => this.insertExpression(expression),
  };

  /** Текст поля (для всіх видів, крім прапорця і списку поза режимом виразу). */
  protected readonly text = new FormControl('', { nonNullable: true });
  protected readonly expression = signal(false);
  protected readonly expressionLabel = '{{ }}';
  private readonly errorVersion = signal(0);

  /** Підказки виразу для поточної позиції курсора (F2-19). */
  protected readonly suggestions = signal<SuggestionOption[]>([]);
  private readonly textEl = viewChild<ElementRef<HTMLInputElement | HTMLTextAreaElement>>('textEl');

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
    (this.rules.options ?? []).map((value, i) => ({
      value,
      label: this.rules.labels?.[i] ?? (typeof value === 'string' ? value : JSON.stringify(value)),
    })),
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
    this.destroyRef.onDestroy(() => this.targets?.release(this.target));
  }

  /** Поле з фокусом стає ціллю вставки виразів з панелі входу. */
  protected activateTarget(): void {
    if (this.canSuggest) this.targets?.activate(this.target);
  }

  /** Вставка виразу з панелі входу в позицію курсора (замість виділення); курсор — за вставкою. */
  private insertExpression(expression: string): void {
    const el = this.textEl()?.nativeElement;
    const text = this.text.value;
    const start = el?.selectionStart ?? text.length;
    const result = insertAt(text, start, el?.selectionEnd ?? start, expression);
    this.text.setValue(result.text);
    if (!el) return;
    // Поле видно (три колонки) — фокус повертається в нього; сховане вкладкою — лише курсор за вставкою.
    if (el.offsetParent !== null) el.focus({ preventScroll: true });
    el.setSelectionRange(result.cursor, result.cursor);
  }

  /** Підказки — у полях, що приймають `{{ }}`. */
  protected get canSuggest(): boolean {
    return this.rules.allowExpression !== false;
  }

  private get expressionContext(): ExpressionContext {
    return (this.options?.formState as NodeFormState | undefined)?.expressionContext ?? EMPTY_EXPRESSION_CONTEXT;
  }

  /** Перераховує підказки за текстом і курсором поля. */
  protected updateSuggestions(el: HTMLInputElement | HTMLTextAreaElement): void {
    if (!this.canSuggest) return;
    const text = el.value;
    const result = suggestExpression(text, el.selectionStart ?? text.length, this.expressionContext);
    if (!result && (this.props as NodeFieldProps).suggestInputFields) {
      // Назва присвоєння (F3-53): імена полів входу.
      this.suggestions.set(inputFieldNames(this.expressionContext.input, text).map((name) => ({ label: name, insert: name, kind: 'field', value: name, cursor: name.length })));
      return;
    }
    this.suggestions.set(
      result
        ? result.items.map((s) => {
            const applied = applySuggestion(text, result, s);
            return { ...s, value: applied.text, cursor: applied.cursor };
          })
        : [],
    );
  }

  /** Курсор пересунули клавішами — підказки для нової позиції (стрілки вгору/вниз гортають список). */
  protected caretMoved(event: KeyboardEvent, el: HTMLInputElement | HTMLTextAreaElement): void {
    if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) this.updateSuggestions(el);
  }

  /** Текст поля вже замінено значенням опції — ставимо курсор за вставкою і показуємо наступні підказки. */
  protected suggestionPicked(event: MatAutocompleteSelectedEvent): void {
    const option = this.suggestions().find((o) => o.value === event.option.value);
    const el = this.textEl()?.nativeElement;
    if (!option || !el) return;
    el.focus();
    el.setSelectionRange(option.cursor, option.cursor);
    this.updateSuggestions(el);
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

/**
 * Код `core.code` у Monaco (F2-19). Monaco вантажиться lazy; не завантажився — звичайне текстове поле.
 * Порожній код — `undefined` (поле не задано), як у {@link parseValue}.
 */
@Component({
  selector: 'app-node-code-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="code" [attr.data-field]="key">
      <span class="code__label" [id]="labelId">{{ label }}@if (props.required) {<span aria-hidden="true"> *</span>}</span>
      @if (status() === 'fallback') {
        <p class="code__note" role="status" data-testid="code-fallback" i18n="@@nodeform.code.fallback">
          Редактор коду не завантажився — звичайне текстове поле.
        </p>
        <textarea
          class="code__text"
          rows="12"
          spellcheck="false"
          [attr.aria-labelledby]="labelId"
          [value]="fallbackText()"
          [attr.data-testid]="'field-' + key"
          (input)="setCode($any($event.target).value)"
        ></textarea>
      }
      <div
        #host
        class="code__editor"
        [class.code__editor--loading]="status() === 'loading'"
        [hidden]="status() === 'fallback'"
        [attr.data-testid]="'code-' + key"
        [attr.data-status]="status()"
      ></div>
      @if (props.description) {
        <p class="code__note">{{ props.description }}</p>
      }
      @if (errorText(); as message) {
        <p class="code__error" role="alert" [attr.data-testid]="'error-' + key">{{ message }}</p>
      }
    </div>
  `,
  styles: `
    .code {
      margin-bottom: 12px;
    }

    .code__label {
      display: block;
      margin-bottom: 4px;
      font-size: 13px;
    }

    .code__editor {
      height: 280px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 4px;
    }

    .code__editor--loading {
      opacity: 0.5;
    }

    .code__text {
      box-sizing: border-box;
      width: 100%;
      font-family: monospace;
    }

    .code__note {
      margin: 4px 0;
      font-size: 12px;
      opacity: 0.7;
    }

    .code__error {
      margin: 4px 0;
      font-size: 12px;
      color: var(--mat-sys-error);
    }
  `,
})
export class NodeCodeFieldComponent extends FieldType<FieldTypeConfig> implements OnInit, AfterViewInit, OnDestroy {
  private static nextId = 0;
  private readonly loader = inject(MONACO_LOADER);
  private readonly destroyRef = inject(DestroyRef);
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private editor: CodeEditorHandle | null = null;
  private subscription: { dispose(): void } | null = null;
  private destroyed = false;
  private readonly errorVersion = signal(0);

  protected readonly labelId = `node-code-label-${++NodeCodeFieldComponent.nextId}`;
  protected readonly status = signal<'loading' | 'ready' | 'fallback'>('loading');
  protected readonly fallbackText = signal('');

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  protected readonly errorText = computed(() => {
    this.errorVersion();
    const errors = this.formControl?.errors;
    const code = errors && this.showError ? Object.keys(errors)[0] : null;
    return code ? valueErrorMessage(code) : null;
  });

  ngOnInit(): void {
    this.fallbackText.set(this.currentCode());
    this.formControl.statusChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.errorVersion.update((v) => v + 1));
    // Зміна значення ззовні (не з редактора) — у редактор.
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      const code = this.currentCode();
      if (this.editor && this.editor.getValue() !== code) this.editor.setValue(code);
      if (this.fallbackText() !== code) this.fallbackText.set(code);
    });
  }

  ngAfterViewInit(): void {
    this.loader().then(
      (create) => {
        if (this.destroyed) return;
        const editor = create(this.host().nativeElement, { value: this.currentCode(), language: 'javascript', ariaLabel: this.label });
        this.editor = editor;
        this.subscription = editor.onDidChangeModelContent(() => this.setCode(editor.getValue()));
        this.status.set('ready');
      },
      () => {
        if (!this.destroyed) this.status.set('fallback');
      },
    );
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.subscription?.dispose();
    this.editor?.dispose();
    this.editor = null;
  }

  protected setCode(code: string): void {
    const value = code === '' ? undefined : code;
    if (value === this.formControl.value) return;
    this.formControl.setValue(value);
    this.formControl.markAsDirty();
    this.formControl.markAsTouched();
  }

  private currentCode(): string {
    const value: unknown = this.formControl.value;
    return typeof value === 'string' ? value : '';
  }
}

/**
 * Масив: елементи за схемою `items`, кнопки «Додати» і «Видалити» в межах `minItems`/`maxItems`. Умови й правила
 * (F3-53): «Додати умову», «Додати правило», над правилом `core.switch` — його вихідний порт.
 */
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
          @if (fieldProps.rulePorts) {
            <div class="array__port" data-testid="rule-port" i18n="@@nodeform.array.rulePort">Правило {{ i + 1 }} → вихід rule{{ i }}</div>
          }
          <div class="array__row">
            <formly-field class="array__body" [field]="item" />
            @if (canRemove()) {
              @if (fieldProps.compact) {
                <button mat-button type="button" class="array__remove" [attr.data-testid]="'remove-' + key + '-' + i" i18n-title="@@nodeform.array.remove" title="Видалити" (click)="remove(i)">✕</button>
              } @else {
                <button mat-button type="button" [attr.data-testid]="'remove-' + key + '-' + i" (click)="remove(i)" i18n="@@nodeform.array.remove">Видалити</button>
              }
            }
          </div>
        </div>
      }
      <button mat-stroked-button type="button" [attr.data-testid]="'add-' + key" [disabled]="!canAdd()" (click)="add()">
        {{ fieldProps.addLabel ?? addLabel }}
      </button>
      @if (fieldProps.rulePorts) {
        <p class="array__hint array__hint--after" i18n="@@nodeform.array.fallback">Жодне правило не виконалось — item іде на вихід fallback.</p>
      }
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

    .array__hint--after {
      margin: 8px 0 0;
    }

    .array__item {
      padding-bottom: 8px;
      border-bottom: 1px dashed var(--mat-sys-outline-variant);
      margin-bottom: 8px;
    }

    .array__row {
      display: flex;
      align-items: flex-start;
      gap: 4px;
    }

    .array__remove {
      min-width: 0;
      margin-top: 8px;
      padding: 0 8px;
    }

    .array__port {
      margin-bottom: 4px;
      font-size: 12px;
      color: var(--ac-kit);
    }

    .array__body {
      flex: 1;
      min-width: 0;
    }
  `,
})
export class NodeArrayFieldComponent extends FieldArrayType {
  protected readonly addLabel = $localize`:@@nodeform.array.add:Додати`;

  protected get fieldProps(): NodeFieldProps {
    return this.props as NodeFieldProps;
  }

  protected canAdd(): boolean {
    const max = this.fieldProps.maxItems;
    return max === undefined || (this.field.fieldGroup?.length ?? 0) < max;
  }

  protected canRemove(): boolean {
    const min = this.fieldProps.minItems;
    return min === undefined || (this.field.fieldGroup?.length ?? 0) > min;
  }
}

/**
 * Умова «значення · оператор · порівняти з» в один ряд (F3-53). Для операторів без правого значення (`isEmpty`…)
 * поле «порівняти з» ховається, але значення в моделі лишається — відкриття чи перемикання оператора не губить даних.
 */
@Component({
  selector: 'app-node-condition-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormlyField],
  template: `
    <div class="cond" data-testid="condition" [attr.data-operator]="operator()">
      @for (part of parts(); track part.key) {
        @if (part.key !== 'right' || !unary()) {
          <formly-field class="cond__part cond__part--{{ part.key }}" [field]="part" />
        }
      }
    </div>
  `,
  styles: `
    .cond {
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      gap: 0 8px;
    }

    /* Вузька панель: «значення · оператор» у першому рядку, «порівняти з» — під ними; широка — усе в один ряд. */
    .cond__part {
      flex: 1 1 140px;
      min-width: 0;
    }

    /* Оператор з перемикачем {{ }} поруч — ширше, щоб «більше або дорівнює» не обрізалось. */
    .cond__part--operator {
      flex: 0 1 220px;
    }
  `,
})
export class NodeConditionFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  protected readonly operator = signal<unknown>(undefined);
  protected readonly unary = computed(() => isUnary(this.operator()));

  protected parts(): FormlyFieldConfig[] {
    return this.field.fieldGroup ?? [];
  }

  ngOnInit(): void {
    const control = this.formControl as AbstractControl<Record<string, unknown> | null>;
    this.operator.set((this.model as Record<string, unknown> | undefined)?.['operator']);
    control.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.operator.set(v?.['operator']));
  }
}

/** «Усі умови (AND) / будь-яка (OR)» чіпами (F3-53); незадане значення показується як типове `and` і не пишеться. */
@Component({
  selector: 'app-node-combine-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="combine" role="radiogroup" [attr.aria-label]="label" [attr.data-field]="key">
      <span class="combine__label">{{ label }}:</span>
      @if (expression()) {
        <input
          class="combine__expr"
          data-testid="field-combine"
          [value]="value() ?? ''"
          [placeholder]="expressionPlaceholder"
          (input)="setExpression($any($event.target).value)"
        />
      } @else {
        @for (o of choices; track o.value) {
          <button
            type="button"
            role="radio"
            class="combine__chip"
            [class.combine__chip--on]="current() === o.value"
            [attr.aria-checked]="current() === o.value"
            [attr.data-testid]="'combine-' + o.value"
            (click)="choose(o.value)"
          >
            {{ o.label }}
          </button>
        }
      }
      <button
        type="button"
        class="combine__chip combine__chip--expr"
        [class.combine__chip--on]="expression()"
        [attr.aria-pressed]="expression()"
        data-testid="expr-combine"
        i18n-title="@@nodeform.expression.hint"
        title="Режим виразу"
        (click)="toggleExpression()"
      >{{ expressionLabel }}</button>
    </div>
  `,
  styles: `
    .combine {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin: 0 0 12px;
    }

    .combine__label {
      align-self: center;
      font-size: 13px;
    }

    .combine__chip {
      padding: 4px 12px;
      border: 1px solid var(--ac-line);
      border-radius: 16px;
      background: var(--ac-white);
      color: var(--ac-vivid);
      font: 400 13px/18px Ubuntu, sans-serif;
      cursor: pointer;
    }

    .combine__chip--expr {
      font-family: monospace;
    }

    .combine__expr {
      flex: 1;
      min-width: 160px;
      min-height: 28px;
      padding: 0 8px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-sm);
      font: 400 13px/18px monospace;
    }

    .combine__chip--on {
      border-color: var(--ac-kit);
      background: var(--ac-button);
      color: var(--ac-kit);
    }
  `,
})
export class NodeCombineFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);
  protected readonly value = signal<unknown>(undefined);
  protected readonly current = computed(() => this.value() ?? 'and');
  /** Значення — вираз `{{ }}` (формат дозволяє його в будь-якому полі): замість чіпів — текстове поле. */
  protected readonly expression = signal(false);
  protected readonly expressionLabel = '{{ }}';
  protected readonly expressionPlaceholder = $localize`:@@cond.combine.expr:{{ вираз, що дає and або or }}`;
  protected readonly choices = [
    { value: 'and', label: $localize`:@@cond.combine.and:усі умови (AND)` },
    { value: 'or', label: $localize`:@@cond.combine.or:будь-яка (OR)` },
  ];

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  ngOnInit(): void {
    this.value.set(this.formControl.value);
    this.expression.set(isExpression(this.formControl.value));
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.value.set(v));
  }

  protected choose(value: string): void {
    if (value === this.current()) return;
    this.formControl.setValue(value);
    this.formControl.markAsDirty();
  }

  protected setExpression(text: string): void {
    this.formControl.setValue(text === '' ? undefined : text);
    this.formControl.markAsDirty();
  }

  /** Вихід з режиму виразу: вираз чіпами не показати — значення стає незаданим (типове `and`). */
  protected toggleExpression(): void {
    const on = !this.expression();
    this.expression.set(on);
    if (!on && isExpression(this.formControl.value)) this.setExpression('');
  }
}

/**
 * Таблиця присвоєнь `core.set` «поле = значення» (F3-53): назва з автодоповненням полями входу, значення приймає
 * вирази (клік/перетягування з панелі входу); «Додати всі поля зі входу» — `поле = {{ $json.поле }}` для відсутніх.
 */
@Component({
  selector: 'app-node-assignments-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormlyField, MatButtonModule],
  template: `
    <fieldset class="set" [attr.data-field]="key">
      <legend class="set__legend">{{ props.label }}@if (props.required) {<span aria-hidden="true"> *</span>}</legend>
      @for (item of field.fieldGroup; track item.id; let i = $index) {
        <div class="set__row" [attr.data-testid]="'item-' + key + '-' + i">
          @for (part of item.fieldGroup ?? []; track part.key; let first = $first) {
            @if (!first) {
              <span class="set__eq" aria-hidden="true">=</span>
            }
            <formly-field class="set__cell" [field]="part" />
          }
          <button mat-button type="button" class="set__remove" [attr.data-testid]="'remove-' + key + '-' + i" (click)="remove(i)" i18n-title="@@set.remove" title="Видалити присвоєння">✕</button>
        </div>
      } @empty {
        <p class="set__empty" i18n="@@set.empty">Ще немає полів.</p>
      }
      <div class="set__actions">
        <button mat-stroked-button type="button" [attr.data-testid]="'add-' + key" (click)="add()" i18n="@@set.add">Додати поле</button>
        @if (fromInput().length > 0) {
          <button mat-button type="button" data-testid="add-all-input" (click)="addAllFromInput()" i18n="@@set.addAll">Додати всі поля зі входу ({{ fromInput().length }})</button>
        }
      </div>
    </fieldset>
  `,
  styles: `
    .set {
      margin: 0 0 12px;
      padding: 8px 12px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 8px;
    }

    .set__legend {
      padding: 0 4px;
      font-size: 13px;
    }

    .set__row {
      display: flex;
      align-items: flex-start;
      gap: 6px;
    }

    .set__cell {
      flex: 1;
      min-width: 0;
    }

    .set__eq {
      margin-top: 16px;
      color: var(--ac-cool-grey-4);
    }

    .set__remove {
      min-width: 0;
      margin-top: 8px;
    }

    .set__actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }

    .set__empty {
      margin: 0 0 8px;
      font-size: 12px;
      opacity: 0.6;
    }
  `,
})
export class NodeAssignmentsFieldComponent extends FieldArrayType {
  /** Присвоєння з полів входу, яких ще немає; перераховується при кожній перевірці шаблону (вхід приходить ззовні). */
  protected fromInput(): Assignment[] {
    const input = (this.options?.formState as NodeFormState | undefined)?.expressionContext?.input;
    return assignmentsFromInput(input, Array.isArray(this.model) ? this.model : []);
  }

  protected addAllFromInput(): void {
    for (const assignment of this.fromInput()) this.add(undefined, assignment);
  }
}

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
      { name: CODE_FIELD, component: NodeCodeFieldComponent },
      { name: CONDITION_FIELD, component: NodeConditionFieldComponent },
      { name: COMBINE_FIELD, component: NodeCombineFieldComponent },
      { name: ASSIGNMENTS_FIELD, component: NodeAssignmentsFieldComponent },
    ],
    wrappers: [{ name: SECTION_WRAPPER, component: NodeSectionWrapperComponent }],
    validators: [{ name: 'node-value', validation: nodeValueValidator }],
    extras: { lazyRender: true },
  });
}
