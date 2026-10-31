import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/** Семантичний колір чипа (AC-Kit «Atoms / Chips»: 15% фон, рамка, затемнений текст ≥ 4.5). */
export type ChipTone = 'gray' | 'green' | 'orange' | 'red' | 'yellow' | 'blue' | 'teal' | 'violet';

/** Пріоритет завдання → колір чипа. */
export type ChipPriority = 'low' | 'medium' | 'high' | 'critical';

const PRIORITY_TONE: Record<ChipPriority, ChipTone> = {
  low: 'gray',
  medium: 'blue',
  high: 'orange',
  critical: 'red',
};

/** Колір чипа для пріоритету. */
export function priorityTone(priority: ChipPriority): ChipTone {
  return PRIORITY_TONE[priority];
}

/**
 * Чип (F9-06, Figma «Atoms / Chips» 1710:6623): стан за семантичним кольором (`tone`), пріоритет (`priority`
 * додає крапку) і мітка із закриттям (`removable` + подія `removed`). Текст — у проєкції контенту.
 */
@Component({
  selector: 'bp-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'bp-chip',
    '[class]': '"bp-chip bp-chip--" + effectiveTone()',
    '[attr.data-tone]': 'effectiveTone()',
  },
  template: `
    @if (priority()) {
      <span class="bp-chip__dot" aria-hidden="true"></span>
    }
    <span class="bp-chip__text"><ng-content /></span>
    @if (removable()) {
      <button
        type="button"
        class="bp-chip__remove"
        data-testid="chip-remove"
        [attr.aria-label]="removeLabel()"
        (click)="removed.emit()"
      >
        ×
      </button>
    }
  `,
  styles: `
    :host {
      --chip-fg: var(--bp-text-muted);
      --chip-bg: var(--bp-bg-2);
      --chip-line: var(--bp-line);
      display: inline-flex;
      align-items: center;
      gap: 6px;
      max-width: 100%;
      padding: 2px 10px;
      border: 1px solid var(--chip-line);
      border-radius: 999px;
      background: var(--chip-bg);
      color: var(--chip-fg);
      font-size: 13px;
      line-height: 20px;
      white-space: nowrap;
    }
    :host(.bp-chip--green) {
      --chip-fg: #1b6e45;
      --chip-bg: var(--bp-success-15);
      --chip-line: var(--bp-success);
    }
    :host(.bp-chip--orange) {
      --chip-fg: #9a4a00;
      --chip-bg: var(--bp-warning-15);
      --chip-line: var(--bp-warning);
    }
    :host(.bp-chip--red) {
      --chip-fg: #b3261e;
      --chip-bg: var(--bp-danger-15);
      --chip-line: var(--bp-danger);
    }
    :host(.bp-chip--yellow) {
      --chip-fg: #7a5c00;
      --chip-bg: var(--bp-yellow-15);
      --chip-line: var(--bp-yellow);
    }
    :host(.bp-chip--blue) {
      --chip-fg: #0b5c8a;
      --chip-bg: var(--bp-info-15);
      --chip-line: var(--bp-info);
    }
    :host(.bp-chip--teal) {
      --chip-fg: var(--bp-primary);
      --chip-bg: var(--bp-tonal);
      --chip-line: var(--bp-accent);
    }
    :host(.bp-chip--violet) {
      --chip-fg: #6f3a8c;
      --chip-bg: #f1e6f7;
      --chip-line: var(--bp-cat-violet);
    }
    .bp-chip__dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: currentColor;
    }
    .bp-chip__text {
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .bp-chip__remove {
      margin: 0 -4px 0 0;
      padding: 0 4px;
      border: 0;
      background: none;
      color: inherit;
      font-size: 16px;
      line-height: 1;
      cursor: pointer;
    }
    .bp-chip__remove:focus-visible {
      outline: 2px solid var(--bp-primary);
      border-radius: 4px;
    }
  `,
})
export class ChipComponent {
  readonly tone = input<ChipTone>('gray');
  readonly priority = input<ChipPriority | null>(null);
  readonly removable = input(false);
  readonly removeLabel = input($localize`:@@chip.remove:Прибрати`);
  readonly removed = output<void>();

  protected effectiveTone(): ChipTone {
    const p = this.priority();
    return p ? priorityTone(p) : this.tone();
  }
}
