import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowValidationError } from '../workflows/workflows-api.service';
import { CanvasSelection, EMPTY_SELECTION, NewConnection, connectNodes } from './canvas-edit';
import { DescriptorIndex, NodePosition, moveNodes, toCanvasModel } from './canvas-model';
import { NodeErrors, NodeRunStatuses, VflowCanvasComponent } from './vflow/vflow-canvas.component';

/**
 * Канвас сценарію з власним API (docs/architecture.md §7, ADR-003): вхід — `WorkflowDefinition`, дескриптори
 * вузлів, виділення і помилки валідації; вихід — змінене визначення і виділення. Бібліотека канвасу (ngx-vflow)
 * схована за `VflowCanvasComponent`, тож її можна замінити, не чіпаючи редактор.
 */
@Component({
  selector: 'app-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [VflowCanvasComponent],
  template: `<app-vflow-canvas
    [model]="model()"
    [virtualization]="virtualization()"
    [nodeErrors]="nodeErrors()"
    [runStatuses]="runStatuses()"
    [pinnedNodes]="pinnedNodes()"
    [selection]="selection()"
    (nodesMoved)="moved($event)"
    (connectionCreated)="connected($event)"
    (selectionChange)="selectionChange.emit($event)"
  />`,
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

  /** Помилки валідації з Api; ті, що мають `nodeId`, показуються на вузлах. */
  readonly errors = input<readonly WorkflowValidationError[]>([]);

  /** Статуси вузлів тестового запуску (F2-20). */
  readonly runStatuses = input<NodeRunStatuses>(new Map());

  /** Виділені вузли й ребра. */
  readonly selection = input<CanvasSelection>(EMPTY_SELECTION);

  /** Нове визначення після змін на канвасі (переміщення вузлів, нове ребро). */
  readonly definitionChange = output<WorkflowDefinition>();

  /** Нове виділення після дій користувача на канвасі. */
  readonly selectionChange = output<CanvasSelection>();

  private readonly index = computed(() => new DescriptorIndex(this.descriptors()));

  protected readonly model = computed(() => toCanvasModel(this.definition(), this.index()));

  protected readonly pinnedNodes = computed(() => new Set(Object.keys(this.definition().pinData ?? {})));

  protected readonly nodeErrors = computed<NodeErrors>(() => {
    const byNode = new Map<string, string[]>();
    for (const e of this.errors()) {
      if (!e.nodeId) continue;
      const list = byNode.get(e.nodeId) ?? [];
      list.push(e.message);
      byNode.set(e.nodeId, list);
    }
    return byNode;
  });

  protected moved(positions: NodePosition[]): void {
    this.definitionChange.emit(moveNodes(this.definition(), positions, this.index()));
  }

  protected connected(connection: NewConnection): void {
    const next = connectNodes(this.definition(), connection);
    if (next !== this.definition()) this.definitionChange.emit(next);
  }
}
