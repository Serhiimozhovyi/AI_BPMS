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
import { Subject, Subscription, debounceTime, distinctUntilChanged } from 'rxjs';
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
import { OptionItem, OptionsMode, OptionsSource, dependencies, filterOptions, findOption, modeFor, resolveArguments } from './dynamic-options';
import { OptionsConnector, ToolOptionsService } from './tool-options.service';
import { ExpressionTarget, ExpressionTargetService } from './expression-target';
import { ExpressionEditorService } from './expression-editor-dialog.component';
import { ExpressionSegment, expressionSegments, expressionSyntaxError, hasExpression } from './expression-tools';
import { CodeEditorHandle, MONACO_LOADER } from './monaco-loader';
import { ValueRules, formatValue, isExpression, isPlainObject, parseValue, valueError, valueErrorMessage } from './node-value';
import { ARRAY_FIELD, ASSIGNMENTS_FIELD, CODE_FIELD, COMBINE_FIELD, CONDITION_FIELD, DECISION_RULES_FIELD, NodeFieldProps, SECTION_WRAPPER, VALUE_FIELD } from './schema-fields';
import { NodeDecisionRulesFieldComponent } from './decision-table-field.component';

/** Спільний стан форми вузла (`FormlyFormOptions.formState`): контекст підказок виразів і конектор вузла. */
export interface NodeFormState {
  expressionContext?: ExpressionContext;
  /** Вузол форми — для попереднього перегляду в редакторі виразів (F3-56). */
  nodeId?: string;
  /** Конектор `mcp.tool`/`mcp.poll` — звідки поля з `x-openbp-options` беруть варіанти (F3-54). */
  connector?: OptionsConnector | null;
}

/** Стан списку варіантів поля з `x-openbp-options`. */
type OptionsStatus = 'idle' | 'loading' | 'ready' | 'error' | 'missing';

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
    @if (source) {
      <div class="value__modes" role="radiogroup" [attr.aria-label]="label">
        @for (m of modes; track m.id) {
          <button
            type="button"
            role="radio"
            class="value__mode"
            [class.value__mode--on]="optionsMode() === m.id"
            [attr.aria-checked]="optionsMode() === m.id"
            [attr.data-testid]="'mode-' + m.id + '-' + key"
            (click)="setOptionsMode(m.id)"
          >{{ m.label }}</button>
        }
        <button
          type="button"
          class="value__mode value__refresh"
          [disabled]="optionsStatus() === 'loading'"
          [attr.data-testid]="'refresh-' + key"
          i18n="@@options.refresh"
          (click)="loadOptions(true)"
        >Оновити</button>
      </div>
    }
    <div class="value" [attr.data-field]="key">
      @if (mode() === 'checkbox') {
        <mat-checkbox [formControl]="formControl" [attr.data-testid]="'field-' + key">{{ label }}</mat-checkbox>
      } @else {
        <mat-form-field appearance="outline" class="value__field" subscriptSizing="dynamic">
          <mat-label>{{ label }}</mat-label>
          @if (mode() === 'options') {
            <input
              matInput
              [formControl]="search"
              [required]="!!props.required"
              [errorStateMatcher]="errorState"
              [matAutocomplete]="optionsList"
              [attr.data-testid]="'field-' + key"
              i18n-placeholder="@@options.search"
              placeholder="Пошук у списку"
              (focus)="openOptions($any($event.target))"
              (input)="searchTyped($any($event.target).value)"
              (blur)="restoreSearch()"
            />
            <mat-autocomplete #optionsList="matAutocomplete" [displayWith]="optionLabel" (optionSelected)="optionPicked($event)">
              @for (o of filteredOptions(); track $index) {
                <mat-option [value]="o" [attr.data-testid]="'option-' + key" [attr.data-value]="optionKey(o)">
                  <span>{{ o.label }}</span>
                  @if (o.label !== optionKey(o)) {
                    <span class="expr-suggest__detail">{{ optionKey(o) }}</span>
                  }
                </mat-option>
              }
            </mat-autocomplete>
          } @else if (mode() === 'select') {
            <mat-select [formControl]="formControl" [required]="!!props.required" [attr.data-testid]="'field-' + key">
              @if (!props.required) {
                <mat-option [value]="undefined" i18n="@@nodeform.notSet">— не задано —</mat-option>
              }
              @for (o of choices(); track $index) {
                <mat-option [value]="o.value">{{ o.label }}</mat-option>
              }
            </mat-select>
          } @else if (multiline) {
            <div class="value__hl">
            @if (highlight(); as segments) {
              <div #backdrop class="value__backdrop value__backdrop--multiline" aria-hidden="true" [attr.data-testid]="'highlight-' + key">@for (s of segments; track $index) {@if (s.expression) {<mark class="value__mark">{{ s.text }}</mark>} @else {<span>{{ s.text }}</span>}}</div>
            }
            <textarea
              #textEl
              matInput
              class="value__text"
              (scroll)="syncBackdrop(textEl)"
              rows="3"
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [matAutocomplete]="suggest"
              [matAutocompleteDisabled]="!canSuggest"
              [attr.data-testid]="'field-' + key"
              (input)="updateSuggestions(textEl); syncBackdrop(textEl)"
              (click)="updateSuggestions(textEl)"
              (keyup)="caretMoved($event, textEl); syncBackdrop(textEl)"
              (focus)="activateTarget()"
            ></textarea>
            </div>
          } @else {
            <div class="value__hl">
            @if (highlight(); as segments) {
              <div #backdrop class="value__backdrop" aria-hidden="true" [attr.data-testid]="'highlight-' + key">@for (s of segments; track $index) {@if (s.expression) {<mark class="value__mark">{{ s.text }}</mark>} @else {<span>{{ s.text }}</span>}}</div>
            }
            <input
              #textEl
              matInput
              class="value__text"
              (scroll)="syncBackdrop(textEl)"
              [formControl]="text"
              [required]="!!props.required"
              [placeholder]="props.placeholder ?? ''"
              [errorStateMatcher]="errorState"
              [matAutocomplete]="suggest"
              [matAutocompleteDisabled]="!canSuggest"
              [attr.data-testid]="'field-' + key"
              (input)="updateSuggestions(textEl); syncBackdrop(textEl)"
              (click)="updateSuggestions(textEl)"
              (keyup)="caretMoved($event, textEl); syncBackdrop(textEl)"
              (focus)="activateTarget()"
            />
            </div>
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
        @if (canExpand()) {
          <button
            type="button"
            class="value__expand"
            [attr.data-testid]="'expand-' + key"
            i18n-title="@@nodeform.expression.expand"
            title="Розгорнути редактор виразу"
            i18n-aria-label="@@nodeform.expression.expand"
            aria-label="Розгорнути редактор виразу"
            (click)="openExpressionEditor()"
          ><span class="material-symbols-outlined" aria-hidden="true">open_in_full</span></button>
        }
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
    @if (!errorText() && syntaxError(); as message) {
      <p class="value__status value__status--error" role="status" [attr.data-testid]="'expr-error-' + key">{{ message }}</p>
    }
    @if (source && optionsMessage(); as message) {
      <p class="value__status" role="status" [class.value__status--error]="optionsStatus() === 'error'" [attr.data-testid]="'options-status-' + key">{{ message }}</p>
    }
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

    .value__hl {
      position: relative;
    }

    .value__text {
      position: relative;
      z-index: 1;
      background: transparent;
    }

    /* Підсвітка {{ … }} (F3-56): копія тексту під прозорим полем, вирази — фоном AC-Kit «Buttun». */
    .value__backdrop {
      position: absolute;
      inset: 0;
      overflow: hidden;
      white-space: pre;
      color: transparent;
      pointer-events: none;
      font: inherit;
      letter-spacing: inherit;
    }

    .value__backdrop--multiline {
      white-space: pre-wrap;
      overflow-wrap: break-word;
    }

    .value__mark {
      border-radius: 3px;
      background: var(--ac-button);
      color: transparent;
    }

    .value__expand {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 28px;
      height: 28px;
      margin-top: 12px;
      padding: 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: none;
      color: var(--ac-cool-grey-4);
      cursor: pointer;
    }

    .value__expand:hover,
    .value__expand:focus-visible {
      background: var(--ac-cool-grey-2);
      color: var(--ac-kit);
    }

    .value__expand .material-symbols-outlined {
      font-size: 18px;
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

    .value__modes {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin-bottom: 4px;
    }

    .value__mode {
      padding: 2px 10px;
      border: 1px solid var(--mat-sys-outline-variant);
      border-radius: 999px;
      background: transparent;
      color: inherit;
      font: inherit;
      font-size: 12px;
      cursor: pointer;
    }

    .value__mode--on {
      border-color: var(--mat-sys-primary);
      background: var(--mat-sys-secondary-container);
      color: var(--mat-sys-on-secondary-container);
    }

    .value__refresh {
      margin-left: auto;
    }

    .value__refresh:disabled {
      opacity: 0.5;
      cursor: default;
    }

    .value__status {
      margin: -4px 0 8px;
      font-size: 12px;
      opacity: 0.8;
    }

    .value__status--error {
      color: var(--mat-sys-error);
      opacity: 1;
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

  /** Повноекранний редактор виразів (F3-56); поза редактором сценарію — немає. */
  private readonly expressionEditor = inject(ExpressionEditorService, { optional: true });

  /** Текст поля (для всіх видів, крім прапорця і списку поза режимом виразу). */
  protected readonly text = new FormControl('', { nonNullable: true });
  /** Значення {@link text} сигналом — для підсвітки й перевірки синтаксису. */
  private readonly textValue = signal('');
  private readonly backdrop = viewChild<ElementRef<HTMLElement>>('backdrop');
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

  protected readonly mode = computed<'checkbox' | 'select' | 'text' | 'options'>(() => {
    if (this.source) return this.optionsMode() === 'list' ? 'options' : 'text';
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

  /** Частини `{{ … }}` текстового поля для підсвітки; `null` — виразів немає. */
  protected readonly highlight = computed<ExpressionSegment[] | null>(() => {
    const text = this.textValue();
    return this.canSuggest && this.mode() === 'text' && hasExpression(text) ? expressionSegments(text) : null;
  });

  /** Синтаксична помилка виразу — під полем, доки немає іншої помилки значення. */
  protected readonly syntaxError = computed(() => (this.canSuggest && this.mode() === 'text' ? expressionSyntaxError(this.textValue()) : null));

  /** Кнопка «розгорнути» — у текстовому полі, що приймає вирази, в редакторі сценарію. */
  protected readonly canExpand = computed(() => !!this.expressionEditor && this.canSuggest && this.mode() === 'text');

  ngOnInit(): void {
    const value: unknown = this.formControl.value;
    this.expression.set(this.canToggle && isExpression(value));
    this.text.setValue(formatValue(value, this.rules.kind), { emitEvent: false });
    this.textValue.set(this.text.value);
    this.initOptions(value);
    this.text.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((t) => {
      this.textValue.set(t);
      this.formControl.setValue(parseValue(t, this.rules));
      this.formControl.markAsDirty();
      this.formControl.markAsTouched();
    });
    this.formControl.statusChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.errorVersion.update((v) => v + 1));
    this.destroyRef.onDestroy(() => this.targets?.release(this.target));
  }

  /** Копія тексту під полем прокручується разом із ним. */
  protected syncBackdrop(el: HTMLInputElement | HTMLTextAreaElement): void {
    const backdrop = this.backdrop()?.nativeElement;
    if (!backdrop) return;
    backdrop.scrollLeft = el.scrollLeft;
    backdrop.scrollTop = el.scrollTop;
  }

  /** «Розгорнути»: редактор виразів з поточним текстом; «Застосувати» записує новий текст у поле. */
  protected openExpressionEditor(): void {
    const state = this.options?.formState as NodeFormState | undefined;
    this.expressionEditor
      ?.open({ value: this.text.value, label: this.label, nodeId: state?.nodeId ?? null, context: this.expressionContext, readonly: this.formControl.disabled })
      .subscribe((value) => {
        if (value === undefined || value === this.text.value) return;
        this.text.setValue(value);
      });
  }

  /** Поле з фокусом стає ціллю вставки виразів з панелі входу. */
  protected activateTarget(): void {
    if (this.canSuggest) this.targets?.activate(this.target);
  }

  /** Вставка виразу з панелі входу в позицію курсора (замість виділення); курсор — за вставкою. */
  private insertExpression(expression: string): void {
    if (this.source && this.optionsMode() === 'list') {
      // Зі списку вираз не вставити: поле переходить у режим виразу з цим виразом.
      this.optionsMode.set('expression');
      this.text.setValue(expression);
      return;
    }
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

  // ——— Список варіантів з конектора (F3-54) ———

  /** Анотація `x-openbp-options`, якщо поле в панелі з конектором; інакше — звичайне поле. */
  protected source: OptionsSource | null = null;
  private readonly toolOptions = inject(ToolOptionsService, { optional: true });
  protected readonly modes: readonly { id: OptionsMode; label: string }[] = [
    { id: 'list', label: $localize`:@@options.mode.list:Зі списку` },
    { id: 'text', label: $localize`:@@options.mode.text:Ввести значення` },
    { id: 'expression', label: $localize`:@@options.mode.expression:Вираз` },
  ];
  protected readonly optionsMode = signal<OptionsMode>('list');
  protected readonly optionsStatus = signal<OptionsStatus>('idle');
  private readonly optionsError = signal<string | null>(null);
  private readonly missingParams = signal<string[]>([]);
  private readonly optionItems = signal<OptionItem[] | null>(null);
  private readonly truncated = signal(false);
  private readonly searchable = signal(false);
  private readonly filterText = signal('');
  /** Текст у полі списку: підпис обраного варіанта або пошук; вибір — сам варіант. */
  protected readonly search = new FormControl<string | OptionItem>('', { nonNullable: true });
  private readonly serverSearch = new Subject<string>();
  private loading: Subscription | null = null;
  /** Режим ще не уточнено за списком: після першого завантаження значення поза списком — «Ввести значення». */
  private inferMode = true;
  private argumentsKey: string | null = null;

  protected readonly filteredOptions = computed(() => filterOptions(this.optionItems() ?? [], this.filterText()));

  protected readonly optionsMessage = computed(() => {
    switch (this.optionsStatus()) {
      case 'loading':
        return $localize`:@@options.loading:Завантаження списку…`;
      case 'error':
        return $localize`:@@options.error:Список недоступний: ${this.optionsError()}:message: Значення можна ввести вручну.`;
      case 'missing':
        return $localize`:@@options.missing:Список залежить від ${this.missingParams().join(', ')}:params: — спершу задайте ці параметри (не виразом).`;
      case 'ready':
        if (this.truncated()) {
          return this.searchable()
            ? $localize`:@@options.truncated.search:Показано перші 500 — введіть текст для пошуку на сервері.`
            : $localize`:@@options.truncated:Показано перші 500 — уточніть пошук або введіть значення.`;
        }
        return this.optionItems()?.length === 0 ? $localize`:@@options.empty:Список порожній.` : null;
      default:
        return null;
    }
  });

  protected readonly optionLabel = (o: string | OptionItem | null): string => (typeof o === 'string' ? o : (o?.label ?? ''));

  protected optionKey(o: OptionItem): string {
    return typeof o.value === 'string' ? o.value : JSON.stringify(o.value);
  }

  private get connector(): OptionsConnector | null {
    return (this.options?.formState as NodeFormState | undefined)?.connector ?? null;
  }

  private initOptions(value: unknown): void {
    const source = (this.props as NodeFieldProps).optionsSource;
    if (!source || !this.toolOptions || !this.connector) return;
    this.source = source;
    this.destroyRef.onDestroy(() => this.loading?.unsubscribe());
    this.optionsMode.set(modeFor(value, null));
    this.showSelected();
    // Залежні списки: зміна параметрів з dependsOn / {{ $params.x }} — нові аргументи, новий список.
    if (dependencies(source).length > 0) {
      this.form.root.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
        if (this.argumentsKey !== null && this.argumentsKey !== JSON.stringify(this.resolvedArguments())) this.loadOptions();
      });
    }
    this.serverSearch
      .pipe(debounceTime(300), distinctUntilChanged(), takeUntilDestroyed(this.destroyRef))
      .subscribe((query) => this.loadOptions(false, query));
    // Вираз — список не потрібен, доки користувач не перейде в «Зі списку».
    if (this.optionsMode() !== 'expression') this.loadOptions();
  }

  /** Параметри для `{{ $params.x }}`: аргументи `mcp.tool` або параметри вузла. */
  private resolvedArguments(): ReturnType<typeof resolveArguments> {
    let root: FormlyFieldConfig = this.field;
    while (root.parent) root = root.parent;
    const model: unknown = root.model;
    const params = isPlainObject(model) ? (isPlainObject(model['arguments']) ? model['arguments'] : model) : {};
    return resolveArguments(this.source?.arguments ?? {}, params);
  }

  /** Завантажує варіанти (з кешу панелі; `refresh` — повторний виклик tool'а). */
  loadOptions(refresh = false, query?: string): void {
    const source = this.source;
    const connector = this.connector;
    if (!source || !connector || !this.toolOptions) return;
    const resolved = this.resolvedArguments();
    this.argumentsKey = JSON.stringify(resolved);
    this.loading?.unsubscribe();
    if (!resolved.ok) {
      this.missingParams.set(resolved.missing);
      this.optionItems.set([]);
      this.optionsStatus.set('missing');
      return;
    }
    this.optionsStatus.set('loading');
    this.loading = this.toolOptions.load(connector, source, resolved.arguments, { refresh, query }).subscribe({
      next: (loaded) => {
        this.optionItems.set(loaded.items);
        this.truncated.set(loaded.truncated);
        this.searchable.set(loaded.searchable);
        this.optionsStatus.set('ready');
        if (this.inferMode && !query) {
          this.inferMode = false;
          if (this.optionsMode() === 'list') this.setMode(modeFor(this.formControl.value, loaded.items));
        }
        this.showSelected();
      },
      error: (e: unknown) => {
        this.optionsError.set(e instanceof Error ? e.message : String(e));
        this.optionItems.set([]);
        this.optionsStatus.set('error');
        // Деградація: поле стає текстовим, значення і сценарій не змінюються.
        if (this.optionsMode() === 'list') this.setMode('text');
      },
    });
  }

  /** Перемикач режиму користувачем. */
  protected setOptionsMode(mode: OptionsMode): void {
    if (mode === this.optionsMode()) return;
    this.inferMode = false;
    this.setMode(mode);
    if (mode === 'list' && this.optionsStatus() !== 'ready' && this.optionsStatus() !== 'loading') this.loadOptions();
    if (mode === 'expression' && !isExpression(this.formControl.value)) {
      // Порожнє поле — заготовка `{{ }}`; введене значення лишається текстом шаблону.
      if (this.text.value.trim() === '') this.text.setValue('{{  }}');
      queueMicrotask(() => {
        const el = this.textEl()?.nativeElement;
        if (!el) return;
        el.focus();
        const cursor = el.value.startsWith('{{  }}') ? 3 : el.value.length;
        el.setSelectionRange(cursor, cursor);
      });
    }
  }

  private setMode(mode: OptionsMode): void {
    this.optionsMode.set(mode);
    if (mode === 'list') this.showSelected();
    else {
      this.text.setValue(formatValue(this.formControl.value, this.rules.kind), { emitEvent: false });
      this.textValue.set(this.text.value);
    }
  }

  /** Поле списку показує підпис поточного значення (або саме значення, якщо його немає в списку). */
  private showSelected(): void {
    const value: unknown = this.formControl.value;
    const option = value === undefined || value === null ? undefined : findOption(value, this.optionItems() ?? []);
    this.search.setValue(option ? option.label : formatValue(value, this.rules.kind), { emitEvent: false });
    this.filterText.set('');
  }

  /** Фокус — увесь список, а текст виділено: введення одразу шукає. */
  protected openOptions(el: HTMLInputElement): void {
    this.filterText.set('');
    el.select();
  }

  protected searchTyped(text: string): void {
    this.filterText.set(text);
    if (this.truncated() && this.searchable() && text.trim()) this.serverSearch.next(text.trim());
  }

  protected optionPicked(event: MatAutocompleteSelectedEvent): void {
    const option = event.option.value as OptionItem;
    this.formControl.setValue(option.value);
    this.formControl.markAsDirty();
    this.formControl.markAsTouched();
    this.showSelected();
  }

  /** Пошуковий текст без вибору не стає значенням: поле повертає підпис обраного. */
  protected restoreSearch(): void {
    // Після кліку по варіанту blur приходить раніше за optionSelected — відкладаємо.
    setTimeout(() => {
      if (typeof this.search.value === 'string' && this.optionsMode() === 'list') this.showSelected();
    }, 200);
  }

  protected toggleExpression(): void {
    const on = !this.expression();
    this.expression.set(on);
    if (on) {
      this.text.setValue(formatValue(this.formControl.value, this.rules.kind), { emitEvent: false });
      this.textValue.set(this.text.value);
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
      { name: DECISION_RULES_FIELD, component: NodeDecisionRulesFieldComponent },
    ],
    wrappers: [{ name: SECTION_WRAPPER, component: NodeSectionWrapperComponent }],
    validators: [{ name: 'node-value', validation: nodeValueValidator }],
    extras: { lazyRender: true },
  });
}
