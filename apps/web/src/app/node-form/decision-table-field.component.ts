import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { FieldType, FieldTypeConfig } from '@ngx-formly/core';
import { isUnary, operatorLabel } from './conditions';
import {
  ANY_CELL,
  CELL_OPERATORS,
  DecisionCell,
  DecisionRule,
  columnNames,
  emptyRule,
  moveRule,
  normalizeRules,
  replaceRule,
  serializeRules,
  withOperator,
  withRight,
} from './decision-table';
import { formatValue, isPlainObject, parseValue } from './node-value';
import { NodeFieldProps } from './schema-fields';
import { BpButtonDirective } from '../shared/button.directive';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../shared/select-search/select-search.component';

/**
 * Правила `core.decisionTable` таблицею (F6-19): рядок — правило, колонки «якщо <вхід>» — оператор (`-` — будь-яке)
 * і праве значення, колонки «→ <вихід>» — значення або вираз. Назви колонок — з полів «Входи» й «Виходи» тієї самої
 * форми; коли їх кількість змінюється, комірки правил доповнюються чи обрізаються з кінця.
 */
@Component({
  selector: 'app-node-decision-rules-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    <div class="dt" [attr.data-field]="key">
      <span class="dt__label">{{ label }}</span>
      @if (inputs().length === 0 || outputs().length === 0) {
        <p class="dt__hint" data-testid="dt-hint" i18n="@@dt.hint">Спершу додайте хоча б один вхід і один вихід.</p>
      }
      <div class="dt__scroll">
        <table class="dt__table" data-testid="dt-table">
          <thead>
            <tr>
              <th class="dt__num">#</th>
              @for (name of inputs(); track $index) {
                <th data-testid="dt-input-col" i18n="@@dt.when">якщо {{ name }}</th>
              }
              @for (name of outputs(); track $index) {
                <th class="dt__out" data-testid="dt-output-col">→ {{ name }}</th>
              }
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (rule of rules(); track $index; let r = $index) {
              <tr [attr.data-rule]="r">
                <td class="dt__num">{{ r + 1 }}</td>
                @for (name of inputs(); track $index; let c = $index) {
                  <td>
                    <div class="dt__cell">
                      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                        <mat-select
                          [aria-label]="operatorAria(r, name)"
                          [attr.data-testid]="'dt-op-' + r + '-' + c"
                          [value]="operatorOf(rule.when[c])"
                          (selectionChange)="setOperator(r, c, $event.value)"
                        >
                          <bp-select-search />
                          <mat-option [value]="any" i18n="@@dt.any">- (будь-яке)</mat-option>
                          @for (op of operators; track op) {
                            <mat-option [value]="op">{{ opLabel(op) }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                      @if (needsRight(rule.when[c])) {
                        <input
                          class="dt__input"
                          [attr.aria-label]="rightAria(r, name)"
                          [attr.data-testid]="'dt-right-' + r + '-' + c"
                          [value]="rightText(rule.when[c])"
                          (change)="setRight(r, c, $any($event.target).value)"
                        />
                      }
                    </div>
                  </td>
                }
                @for (name of outputs(); track $index; let o = $index) {
                  <td class="dt__out">
                    <input
                      class="dt__input"
                      [attr.aria-label]="thenAria(r, name)"
                      [attr.data-testid]="'dt-then-' + r + '-' + o"
                      [value]="text(rule.then[o])"
                      placeholder="{{ '{{ }}' }}"
                      (change)="setThen(r, o, $any($event.target).value)"
                    />
                  </td>
                }
                <td class="dt__actions">
                  <button type="button" class="dt__icon" [disabled]="r === 0" [attr.data-testid]="'dt-up-' + r" i18n-title="@@dt.up" title="Вище" (click)="move(r, -1)">↑</button>
                  <button
                    type="button"
                    class="dt__icon"
                    [disabled]="r === rules().length - 1"
                    [attr.data-testid]="'dt-down-' + r"
                    i18n-title="@@dt.down"
                    title="Нижче"
                    (click)="move(r, 1)"
                  >↓</button>
                  <button type="button" class="dt__icon" [attr.data-testid]="'dt-remove-' + r" i18n-title="@@dt.remove" title="Видалити правило" (click)="remove(r)">✕</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
      <button mat-stroked-button type="button" data-testid="dt-add" (click)="add()" i18n="@@dt.add" bpVariant="tonal">Додати правило</button>
    </div>
  `,
  styles: `
    .dt {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin: 0 0 12px;
    }

    .dt__label {
      font-size: 13px;
    }

    .dt__hint {
      margin: 0;
      color: var(--bp-text-muted);
      font-size: 12px;
    }

    .dt__scroll {
      overflow-x: auto;
    }

    .dt__table {
      border-collapse: collapse;
      font: var(--bp-font-p3);
    }

    .dt__table th,
    .dt__table td {
      padding: 4px;
      border: 1px solid var(--bp-line);
      text-align: left;
      vertical-align: top;
      white-space: nowrap;
    }

    .dt__table th {
      background: var(--bp-bg);
      font-weight: 500;
    }

    .dt__out {
      background: var(--bp-bg-2);
    }

    .dt__num {
      width: 24px;
      color: var(--bp-text-muted);
      text-align: center;
    }

    .dt__cell {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }

    .dt__cell .mat-mdc-form-field {
      min-width: 140px;
    }

    .dt__input {
      min-width: 120px;
      min-height: 28px;
      padding: 0 6px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius-sm);
      background: var(--bp-white);
      font: inherit;
    }

    .dt__actions {
      white-space: nowrap;
    }

    .dt__icon {
      padding: 2px 6px;
      border: none;
      background: none;
      color: var(--bp-text);
      cursor: pointer;
    }

    .dt__icon:disabled {
      opacity: 0.3;
      cursor: default;
    }
  `,
})
export class NodeDecisionRulesFieldComponent extends FieldType<FieldTypeConfig> implements OnInit {
  private readonly destroyRef = inject(DestroyRef);

  protected readonly any = ANY_CELL;
  protected readonly operators = CELL_OPERATORS;
  protected readonly inputs = signal<string[]>([]);
  protected readonly outputs = signal<string[]>([]);
  private readonly value = signal<unknown>(undefined);
  protected readonly rules = computed(() => normalizeRules(this.value(), this.inputs().length, this.outputs().length));

  protected get label(): string {
    return (this.props as NodeFieldProps).label ?? String(this.key ?? '');
  }

  ngOnInit(): void {
    this.value.set(this.formControl.value);
    this.readColumns();
    this.formControl.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.value.set(v));
    this.form.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.readColumns());
  }

  /** Назви входів і виходів із сусідніх полів; змінилась кількість — правила вирівнюються під неї. */
  private readColumns(): void {
    const parent = isPlainObject(this.model) ? this.model : {};
    const inputs = columnNames(parent['inputs']);
    const outputs = columnNames(parent['outputs']);
    if (inputs.join('\u0000') !== this.inputs().join('\u0000')) this.inputs.set(inputs);
    if (outputs.join('\u0000') !== this.outputs().join('\u0000')) this.outputs.set(outputs);
    const current = Array.isArray(this.value()) ? (this.value() as unknown[]) : [];
    const aligned = this.rules().map((r) => ({ when: r.when.slice(0, inputs.length), then: r.then.slice(0, outputs.length) }));
    if (current.length > 0 && JSON.stringify(serializeRules(aligned)) !== JSON.stringify(current)) this.write(aligned);
  }

  protected operatorOf(cell: DecisionCell | typeof ANY_CELL): string {
    return cell === ANY_CELL ? ANY_CELL : cell.operator;
  }

  protected needsRight(cell: DecisionCell | typeof ANY_CELL): boolean {
    return cell !== ANY_CELL && !isUnary(cell.operator);
  }

  protected rightText(cell: DecisionCell | typeof ANY_CELL): string {
    return cell === ANY_CELL ? '' : this.text(cell.right);
  }

  protected text(value: unknown): string {
    return formatValue(value, 'json');
  }

  protected opLabel(op: string): string {
    return operatorLabel(op);
  }

  protected operatorAria(rule: number, input: string): string {
    return $localize`:@@dt.aria.op:Правило ${rule + 1}:rule:, умова на ${input}:input:`;
  }

  protected rightAria(rule: number, input: string): string {
    return $localize`:@@dt.aria.right:Правило ${rule + 1}:rule:, значення для ${input}:input:`;
  }

  protected thenAria(rule: number, output: string): string {
    return $localize`:@@dt.aria.then:Правило ${rule + 1}:rule:, вихід ${output}:output:`;
  }

  protected setOperator(r: number, c: number, operator: string): void {
    const rule = this.rules()[r];
    this.write(replaceRule(this.rules(), r, { ...rule, when: rule.when.map((cell, i) => (i === c ? withOperator(cell, operator) : cell)) }));
  }

  protected setRight(r: number, c: number, text: string): void {
    const rule = this.rules()[r];
    const right = parseValue(text, { kind: 'json' });
    this.write(replaceRule(this.rules(), r, { ...rule, when: rule.when.map((cell, i) => (i === c ? withRight(cell, right) : cell)) }));
  }

  protected setThen(r: number, o: number, text: string): void {
    const rule = this.rules()[r];
    const value = parseValue(text, { kind: 'json' });
    this.write(replaceRule(this.rules(), r, { ...rule, then: rule.then.map((v, i) => (i === o ? value : v)) }));
  }

  protected add(): void {
    this.write([...this.rules(), emptyRule(this.inputs().length, this.outputs().length)]);
  }

  protected remove(r: number): void {
    this.write(this.rules().filter((_, i) => i !== r));
  }

  protected move(r: number, delta: number): void {
    this.write(moveRule(this.rules(), r, delta));
  }

  private write(rules: DecisionRule[]): void {
    this.formControl.setValue(serializeRules(rules));
    this.formControl.markAsDirty();
  }
}
