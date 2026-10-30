import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Стан кроку-етапу (збігається зі `StageStatus` виконання). */
export type StageStepStatus = 'pending' | 'active' | 'done' | 'skipped' | 'cancelled';

/** Один крок «стрілки етапів». Підписи готує викликач (i18n лишається біля даних). */
export interface StageStep {
  id: string;
  /** Малий підпис над назвою, напр. «Етап 2». */
  caption?: string;
  name: string;
  status: StageStepStatus;
  /** Активний крок із простроченим строком. */
  overdue?: boolean;
  /** Підпис стану для людини. */
  stateLabel?: string;
  /** Підпис строку, напр. «до 31.10.26». */
  dueLabel?: string;
  /** Підказка (title). */
  hint?: string;
}

/**
 * Стрілки етапів (F9-06, Figma AC-Kit 2787:14405 / 1777:6516): пройдено — зелений, триває — primary,
 * ще ні — світлий `--bp-bg-2`, прострочено — червоний, перервано — червона рамка, не знадобився — пунктир.
 * Спільний для виконань (F7-02) і бізнес-вигляду; відомий лише `StageStep`, не знає про API.
 */
@Component({
  selector: 'bp-stage-steps',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="stages" data-testid="stage-strip" [attr.aria-label]="label()">
      @for (s of steps(); track s.id) {
        <li
          class="stage stage--{{ s.status }}"
          [class.stage--overdue]="s.overdue"
          [attr.data-stage]="s.id"
          [attr.data-status]="s.status"
          [attr.data-overdue]="!!s.overdue"
          [attr.title]="s.hint"
          [attr.aria-current]="s.status === 'active' ? 'step' : null"
        >
          @if (s.caption) {
            <span class="stage__number">{{ s.caption }}</span>
          }
          <span class="stage__name">{{ s.name }}</span>
          @if (s.stateLabel) {
            <span class="stage__state" data-testid="stage-state">{{ s.stateLabel }}</span>
          }
          @if (s.dueLabel) {
            <span class="stage__due" data-testid="stage-due">{{ s.dueLabel }}</span>
          }
        </li>
      }
    </ol>
  `,
  styles: `
    .stages {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
      margin: 0 0 12px;
      padding: 0;
      list-style: none;
    }
    .stage {
      position: relative;
      display: flex;
      flex-direction: column;
      min-width: 150px;
      padding: 6px 24px 6px 16px;
      border: 1px solid var(--bp-line-3);
      background: var(--bp-bg-2);
      color: var(--bp-text-muted);
      clip-path: polygon(
        0 0,
        calc(100% - 12px) 0,
        100% 50%,
        calc(100% - 12px) 100%,
        0 100%,
        12px 50%
      );
      font-size: 13px;
      line-height: 1.3;
    }
    .stage:first-child {
      clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%);
    }
    .stage__number {
      font-size: 11px;
    }
    .stage__name {
      font-weight: 500;
    }
    .stage__state,
    .stage__due {
      font-size: 11px;
    }
    .stage--done {
      border-color: #1b6e45;
      background: #1b6e45;
      color: var(--bp-white);
    }
    .stage--active {
      border-color: var(--bp-primary);
      background: var(--bp-primary);
      color: var(--bp-white);
    }
    .stage--overdue {
      background: #b3261e;
      border-color: #b3261e;
      color: var(--bp-white);
    }
    .stage--cancelled {
      border-color: #b3261e;
      background: var(--ac-red-15);
      color: #b3261e;
    }
    .stage--skipped {
      border-style: dashed;
      background: var(--bp-surface-2);
      color: var(--bp-text-muted);
    }
  `,
})
export class StageStepsComponent {
  readonly steps = input.required<readonly StageStep[]>();
  readonly label = input($localize`:@@stages.aria:Етапи процесу`);
}
