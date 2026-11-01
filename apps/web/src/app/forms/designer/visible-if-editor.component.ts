import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';
import type { VisibleIf, VisibleIfOperator } from '../form-layout';
import type { FieldKind, OptionRow } from './form-designer.model';

/** Поле, від якого може залежати умова: ключ, підпис, тип і варіанти списку. */
export interface RuleSource {
  key: string;
  label: string;
  kind: FieldKind;
  options: OptionRow[];
}

const VALUELESS: readonly VisibleIfOperator[] = ['isEmpty', 'isNotEmpty'];

/**
 * Блок «Показувати, якщо» (F11-06) у властивостях поля чи секції конструктора форми: поле-джерело (або «Завжди»),
 * оператор і значення — для списку варіанти, для «Так/Ні» — так чи ні, для числа — число. Списки — `mat-select` з
 * пошуком (F9-14).
 */
@Component({
  selector: 'bp-visible-if-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatInputModule, MatSelectModule, SelectSearchComponent],
  template: `
    <div class="vie" role="group" [attr.aria-labelledby]="testId() + '-title'">
      <h4 class="vie__title" [id]="testId() + '-title'" i18n="@@fd.if.title">Показувати, якщо</h4>
      <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
        <mat-label i18n="@@fd.if.field">Поле</mat-label>
        <mat-select [attr.data-testid]="testId() + '-field'" [value]="rule()?.field ?? ''" (selectionChange)="chooseField($event.value)">
          <bp-select-search />
          <mat-option value="" i18n="@@fd.if.always">Завжди</mat-option>
          @for (s of sources(); track s.key) {
            <mat-option [value]="s.key">{{ s.label }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      @if (rule(); as r) {
        <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
          <mat-label i18n="@@fd.if.operator">Умова</mat-label>
          <mat-select [attr.data-testid]="testId() + '-operator'" [value]="r.operator" (selectionChange)="chooseOperator($event.value)">
            @for (o of operators; track o.value) {
              <mat-option [value]="o.value">{{ o.label }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @if (needsValue()) {
          @let s = source();
          @if (s && (s.options.length || s.kind === 'boolean')) {
            <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
              <mat-label i18n="@@fd.if.value">Значення</mat-label>
              <mat-select [attr.data-testid]="testId() + '-value'" [value]="r.value" (selectionChange)="setValue($event.value)">
                <bp-select-search />
                @if (s.kind === 'boolean') {
                  <mat-option [value]="true" i18n="@@fd.if.yes">Так</mat-option>
                  <mat-option [value]="false" i18n="@@fd.if.no">Ні</mat-option>
                } @else {
                  @for (o of s.options; track $index) {
                    <mat-option [value]="o.value">{{ o.label || o.value }}</mat-option>
                  }
                }
              </mat-select>
            </mat-form-field>
          } @else {
            <mat-form-field appearance="outline" class="bp-field-compact w-full" subscriptSizing="dynamic">
              <mat-label i18n="@@fd.if.value">Значення</mat-label>
              <input
                matInput
                [attr.data-testid]="testId() + '-value'"
                [type]="inputType()"
                [value]="r.value === undefined || r.value === null ? '' : r.value"
                (input)="typeValue($event)"
              />
            </mat-form-field>
          }
        }
        @if (missing()) {
          <p class="vie__note" role="status" [attr.data-testid]="testId() + '-missing'" i18n="@@fd.if.missing">Поля, від якого залежить умова, вже немає — оберіть інше.</p>
        }
      }
    </div>
  `,
  styles: `
    .vie {
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .vie__title {
      margin: 12px 0 0;
      font-size: 13px;
      font-weight: 600;
      color: var(--bp-text-muted);
    }
    .vie__note {
      margin: 0;
      font-size: 12px;
      color: var(--bp-danger);
    }
  `,
})
export class VisibleIfEditorComponent {
  /** Поточна умова або `undefined` — «Завжди». */
  readonly rule = input<VisibleIf | undefined>(undefined);
  /** Поля, від яких може залежати умова. */
  readonly sources = input<RuleSource[]>([]);
  /** Префікс `data-testid`. */
  readonly testId = input('fd-if');
  /** Нова умова (`undefined` — показувати завжди). */
  readonly ruleChange = output<VisibleIf | undefined>();

  protected readonly operators: { value: VisibleIfOperator; label: string }[] = [
    { value: 'eq', label: $localize`:@@fd.if.op.eq:дорівнює` },
    { value: 'ne', label: $localize`:@@fd.if.op.ne:не дорівнює` },
    { value: 'contains', label: $localize`:@@fd.if.op.contains:містить` },
    { value: 'isNotEmpty', label: $localize`:@@fd.if.op.isNotEmpty:заповнене` },
    { value: 'isEmpty', label: $localize`:@@fd.if.op.isEmpty:не заповнене` },
    { value: 'gt', label: $localize`:@@fd.if.op.gt:більше ніж` },
    { value: 'lt', label: $localize`:@@fd.if.op.lt:менше ніж` },
  ];

  protected readonly source = computed(() => this.sources().find((s) => s.key === this.rule()?.field));
  protected readonly missing = computed(() => !!this.rule() && !this.source());
  protected readonly needsValue = computed(() => !VALUELESS.includes(this.rule()?.operator ?? 'eq'));
  protected readonly inputType = computed(() => {
    const kind = this.source()?.kind;
    return kind === 'number' ? 'number' : kind === 'date' ? 'date' : 'text';
  });

  protected chooseField(key: string): void {
    if (!key) {
      this.ruleChange.emit(undefined);
      return;
    }
    const source = this.sources().find((s) => s.key === key);
    // Нове джерело — умова спочатку: «дорівнює» першому варіанту списку чи «Так» для прапорця, інакше «заповнене».
    if (source?.kind === 'boolean') this.ruleChange.emit({ field: key, operator: 'eq', value: true });
    else if (source?.options.length) this.ruleChange.emit({ field: key, operator: 'eq', value: source.options[0].value });
    else this.ruleChange.emit({ field: key, operator: 'isNotEmpty' });
  }

  protected chooseOperator(operator: VisibleIfOperator): void {
    const rule = this.rule();
    if (!rule) return;
    if (VALUELESS.includes(operator)) this.ruleChange.emit({ field: rule.field, operator });
    else this.ruleChange.emit({ field: rule.field, operator, value: rule.value ?? this.defaultValue() });
  }

  protected setValue(value: unknown): void {
    const rule = this.rule();
    if (rule) this.ruleChange.emit({ ...rule, value });
  }

  protected typeValue(event: Event): void {
    const text = (event.target as HTMLInputElement).value;
    const n = Number(text);
    this.setValue(this.source()?.kind === 'number' && text.trim() !== '' && Number.isFinite(n) ? n : text);
  }

  private defaultValue(): unknown {
    const source = this.source();
    if (source?.kind === 'boolean') return true;
    return source?.options[0]?.value ?? '';
  }
}
