import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { toCanvasModel } from '../canvas/canvas-model';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessConnector, buildBusinessView } from './business-model';
import { moveStepToLane, setStepHidden, setStepSubtitle, setStepTitle } from './business-edit';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../shared/select-search/select-search.component';

/**
 * Розділ «Бізнес-вигляд» у панелі вузла технічного вигляду (F3-68): назва й підрядок для бізнесу, доріжка і
 * «сховати». Ті самі правки, що й на бізнес-схемі; кожна — нове визначення в `definitionChange`.
 */
@Component({
  selector: 'app-business-node-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatInputModule, MatSelectModule, SelectSearchComponent],
  template: `
    @if (node(); as n) {
      <section class="bnf" data-testid="business-node-fields" [attr.inert]="readonly() ? '' : null">
        <h3 class="bnf__title" i18n="@@business.node.section">Бізнес-вигляд</h3>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label i18n="@@business.node.title">Назва для бізнесу</mat-label>
          <input matInput data-testid="business-node-title" [value]="n.business?.title ?? ''" [placeholder]="n.name" (change)="title(value($event))" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label i18n="@@business.node.subtitle">Підрядок</mat-label>
          <input matInput data-testid="business-node-subtitle" [value]="n.business?.subtitle ?? ''" (change)="subtitle(value($event))" />
        </mat-form-field>
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label i18n="@@business.node.lane">Доріжка</mat-label>
          <mat-select data-testid="business-node-lane" [value]="currentLane()" (selectionChange)="lane($event.value)">
            <bp-select-search />
            <mat-option value="" i18n="@@business.node.laneDefault">За типом вузла</mat-option>
            @for (l of lanes(); track l.id) {
              <mat-option [value]="l.id">{{ l.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <label class="bnf__check">
          <input type="checkbox" data-testid="business-node-hidden" [checked]="hidden()" (change)="hide($any($event.target).checked)" />
          <span i18n="@@business.node.hidden">Сховати з бізнес-вигляду</span>
        </label>
      </section>
    }
  `,
  styles: `
    :host {
      display: block;
    }

    .bnf {
      display: flex;
      flex-direction: column;
      gap: 10px;
      padding: 0 16px 16px;
    }

    .bnf__title {
      margin: 8px 0 0;
      font-size: 14px;
    }

    .bnf__check {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      font-size: 13px;
    }
  `,
})
export class BusinessNodeFieldsComponent {
  readonly definition = input.required<WorkflowDefinition>();
  readonly nodeId = input.required<string>();
  readonly descriptors = input<readonly NodeTypeInfo[]>([]);
  readonly connectors = input<readonly BusinessConnector[]>([]);
  readonly readonly = input(false, { transform: booleanAttribute });
  readonly definitionChange = output<WorkflowDefinition>();

  protected readonly node = computed(() => this.definition().nodes.find((n) => n.id === this.nodeId()) ?? null);
  private readonly view = computed(() => buildBusinessView(this.definition(), toCanvasModel(this.definition(), this.descriptors()), this.connectors()));
  protected readonly lanes = computed(() => this.view().lanes);
  /** Явна доріжка вузла; доріжка за типом — порожньо. */
  protected readonly currentLane = computed(() => {
    const lane = this.node()?.business?.lane;
    return lane && this.lanes().some((l) => l.id === lane) ? lane : '';
  });
  protected readonly hidden = computed(() => !this.view().steps.some((s) => s.id === this.nodeId()));

  protected value(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  protected title(value: string): void {
    this.emit(setStepTitle(this.definition(), this.nodeId(), value));
  }

  protected subtitle(value: string): void {
    this.emit(setStepSubtitle(this.definition(), this.nodeId(), value));
  }

  protected lane(laneId: string): void {
    this.emit(moveStepToLane(this.definition(), this.view(), this.nodeId(), laneId));
  }

  protected hide(hidden: boolean): void {
    this.emit(setStepHidden(this.definition(), this.nodeId(), hidden));
  }

  private emit(next: WorkflowDefinition): void {
    if (!this.readonly() && next !== this.definition()) this.definitionChange.emit(next);
  }
}
