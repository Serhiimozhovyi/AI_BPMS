import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output } from '@angular/core';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowValidationError } from '../workflows/workflows-api.service';
import { CanvasSelection, EMPTY_SELECTION, NewConnection, connectNodes } from './canvas-edit';
import { DescriptorIndex, NodePosition, moveNodes, toCanvasModel } from './canvas-model';
import { QuickAddRequest } from './quick-add';
import { MissingFieldRequest, NodeErrors, NodeRunStatuses, VflowCanvasComponent } from './vflow/vflow-canvas.component';

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
    [itemCounts]="itemCounts()"
    [runErrors]="runErrors()"
    [missingFields]="missingFields()"
    [selection]="selection()"
    [readonly]="readonly()"
    (nodesMoved)="moved($event)"
    (connectionCreated)="connected($event)"
    (selectionChange)="selectionChange.emit($event)"
    (quickAdd)="quickAdd.emit($event)"
    (missingClick)="missingClick.emit($event)"
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

  /** Кількість items на виході вузла після запуску за `вузол:порт` — підпис на ребрах (F3-52). */
  readonly itemCounts = input<ReadonlyMap<string, number>>(new Map());

  /** Повідомлення помилок вузлів у тестовому запуску (F3-57). */
  readonly runErrors = input<ReadonlyMap<string, string>>(new Map());

  /** Незаповнені обов'язкові параметри за id вузла (F3-57). */
  readonly missingFields = input<ReadonlyMap<string, readonly string[]>>(new Map());

  /** Виділені вузли й ребра. */
  readonly selection = input<CanvasSelection>(EMPTY_SELECTION);

  /** Лише перегляд: вузли не перетягуються, ребра не створюються; виділення працює. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Нове визначення після змін на канвасі (переміщення вузлів, нове ребро). */
  readonly definitionChange = output<WorkflowDefinition>();

  /** Нове виділення після дій користувача на канвасі. */
  readonly selectionChange = output<CanvasSelection>();

  /** Клік по «не заповнено: N» на картці (F3-57). */
  readonly missingClick = output<MissingFieldRequest>();

  /** Відкрити спливаючу палітру (F3-50); у режимі перегляду не надходить. */
  readonly quickAdd = output<QuickAddRequest>();

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
    if (this.readonly()) return;
    this.definitionChange.emit(moveNodes(this.definition(), positions, this.index()));
  }

  protected connected(connection: NewConnection): void {
    if (this.readonly()) return;
    const next = connectNodes(this.definition(), connection);
    if (next !== this.definition()) this.definitionChange.emit(next);
  }
}
