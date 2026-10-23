import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { DescriptorIndex, NodePosition, moveNodes, toCanvasModel } from './canvas-model';
import { VflowCanvasComponent } from './vflow/vflow-canvas.component';

/**
 * Канвас сценарію з власним API (docs/architecture.md §7, ADR-003): вхід — `WorkflowDefinition` і дескриптори
 * вузлів, вихід — змінене визначення. Бібліотека канвасу (ngx-vflow) схована за `VflowCanvasComponent`,
 * тож її можна замінити, не чіпаючи редактор.
 */
@Component({
  selector: 'app-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [VflowCanvasComponent],
  template: `<app-vflow-canvas [model]="model()" [virtualization]="virtualization()" (nodesMoved)="moved($event)" />`,
  styles: `
    :host {
      display: block;
      width: 100%;
      height: 100%;
    }
  `,
})
export class CanvasComponent {
  /** Визначення сценарію — джерело правди. */
  readonly definition = input.required<WorkflowDefinition>();

  /** Дескриптори з `GET /nodes`: порти й групи вузлів. */
  readonly descriptors = input<readonly NodeTypeInfo[]>([]);

  /** Віртуалізація viewport'у (ADR-003: увімкнено за замовчуванням). */
  readonly virtualization = input(true);

  /** Нове визначення після змін на канвасі (переміщення вузлів). */
  readonly definitionChange = output<WorkflowDefinition>();

  private readonly index = computed(() => new DescriptorIndex(this.descriptors()));

  protected readonly model = computed(() => toCanvasModel(this.definition(), this.index()));

  protected moved(positions: NodePosition[]): void {
    this.definitionChange.emit(moveNodes(this.definition(), positions, this.index()));
  }
}
