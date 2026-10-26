import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { ExecutionStage, StageStatus } from './executions-api.service';

/** Підпис стану етапу для людини. */
export function stageStatusLabel(stage: Pick<ExecutionStage, 'status' | 'overdue'>): string {
  if (stage.overdue) return $localize`:@@stages.status.overdue:Прострочено`;
  const labels: Record<StageStatus, string> = {
    pending: $localize`:@@stages.status.pending:Ще не почався`,
    active: $localize`:@@stages.status.active:Триває`,
    done: $localize`:@@stages.status.done:Пройдено`,
    skipped: $localize`:@@stages.status.skipped:Не знадобився`,
    cancelled: $localize`:@@stages.status.cancelled:Перервано`,
  };
  return labels[stage.status];
}

/**
 * Етапи виконання (F7-02, ADR-004 §2) — «стрілки етапів» AC-Kit (Figma 1777:6516): пройдено — зелений з білим текстом,
 * ще ні — світлий Cool Grey з рамкою і текстом Kit; триває — Kit, прострочено — червона рамка, перервано — червоний,
 * не знадобився — пунктир.
 */
@Component({
  selector: 'app-stage-strip',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="stages" data-testid="stage-strip" i18n-aria-label="@@stages.aria" aria-label="Етапи процесу">
      @for (s of stages(); track s.stageId) {
        <li
          class="stage stage--{{ s.status }}"
          [class.stage--overdue]="s.overdue"
          [attr.data-stage]="s.stageId"
          [attr.data-status]="s.status"
          [attr.data-overdue]="s.overdue"
          [attr.title]="hint(s)"
        >
          <span class="stage__number" i18n="@@stages.number">Етап {{ s.position + 1 }}</span>
          <span class="stage__name">{{ s.name }}</span>
          <span class="stage__state" data-testid="stage-state">{{ label(s) }}</span>
          @if (s.dueAt && (s.status === 'active' || s.status === 'pending')) {
            <span class="stage__due" data-testid="stage-due" i18n="@@stages.due">до {{ s.dueAt | date: 'short' }}</span>
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
      border: 1px solid var(--ac-cool-grey-3);
      background: var(--ac-cool-grey-2);
      color: var(--ac-kit);
      clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%, 12px 50%);
      font-size: 13px;
      line-height: 1.3;
    }
    .stage:first-child {
      clip-path: polygon(0 0, calc(100% - 12px) 0, 100% 50%, calc(100% - 12px) 100%, 0 100%);
    }
    .stage__number {
      font-size: 11px;
      opacity: 0.8;
    }
    .stage__name {
      font-weight: 500;
    }
    .stage__state,
    .stage__due {
      font-size: 11px;
    }
    .stage--done {
      border-color: var(--ac-green);
      background: var(--ac-green);
      color: var(--ac-white);
    }
    .stage--active {
      border-color: var(--ac-kit);
      background: var(--ac-kit);
      color: var(--ac-white);
    }
    .stage--overdue {
      background: var(--ac-red);
      border-color: var(--ac-red);
    }
    .stage--cancelled {
      border-color: var(--ac-red);
      background: var(--ac-red-15);
      color: var(--ac-red);
    }
    .stage--skipped {
      border-style: dashed;
      background: var(--ac-stone);
      color: var(--ac-cool-grey-4);
    }
  `,
})
export class StageStripComponent {
  readonly stages = input.required<readonly ExecutionStage[]>();

  protected label(stage: ExecutionStage): string {
    return stageStatusLabel(stage);
  }

  protected hint(stage: ExecutionStage): string {
    const parts = [stageStatusLabel(stage)];
    if (stage.enteredAt) parts.push($localize`:@@stages.hint.entered:почався ${new Date(stage.enteredAt).toLocaleString()}:entered:`);
    if (stage.exitedAt) parts.push($localize`:@@stages.hint.exited:завершився ${new Date(stage.exitedAt).toLocaleString()}:exited:`);
    return parts.join(' · ');
  }
}
