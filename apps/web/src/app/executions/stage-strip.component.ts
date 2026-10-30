import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, LOCALE_ID, computed, inject, input } from '@angular/core';
import { StageStep, StageStepsComponent } from '../shared/stage-steps/stage-steps.component';
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

/** Етапи виконання (F7-02, ADR-004 §2): дані виконання → спільні «стрілки етапів» `bp-stage-steps` (F9-06). */
@Component({
  selector: 'app-stage-strip',
  imports: [StageStepsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<bp-stage-steps [steps]="steps()" />`,
})
export class StageStripComponent {
  readonly stages = input.required<readonly ExecutionStage[]>();
  private readonly datePipe = new DatePipe(inject(LOCALE_ID));

  protected readonly steps = computed<StageStep[]>(() =>
    this.stages().map((s) => ({
      id: s.stageId,
      caption: $localize`:@@stages.number:Етап ${s.position + 1}:position:`,
      name: s.name,
      status: s.status,
      overdue: s.overdue,
      stateLabel: this.label(s),
      dueLabel:
        s.dueAt && (s.status === 'active' || s.status === 'pending')
          ? $localize`:@@stages.due:до ${this.datePipe.transform(s.dueAt, 'short')}:due:`
          : undefined,
      hint: this.hint(s),
    })),
  );

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
