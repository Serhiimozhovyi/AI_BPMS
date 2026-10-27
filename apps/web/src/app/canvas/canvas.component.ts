import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, output, viewChild } from '@angular/core';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowValidationError } from '../workflows/workflows-api.service';
import { CanvasSelection, EMPTY_SELECTION, NewConnection, connectNodes } from './canvas-edit';
import { DescriptorIndex, NodePosition, moveNodes, toCanvasModel } from './canvas-model';
import { moveNotes, notesOf, updateNote } from './canvas-notes';
import { QuickAddRequest } from './quick-add';
import { CanvasContextMenuRequest, MissingFieldRequest, NODE_WIDTH, NodeActionRequest, NodeErrors, NodeRenameRequest, NodeRunStatuses, NoteChangeRequest, NoteEditRequest, VflowCanvasComponent, nodeHeight } from './vflow/vflow-canvas.component';
import { layoutLeftToRight } from './auto-layout';

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
    [runTimes]="runTimes()"
    [viewportKey]="viewportKey()"
    [lastRunToggle]="lastRunToggle()"
    (lastRunToggled)="lastRunToggled.emit($event)"
    [missingFields]="missingFields()"
    [selection]="selection()"
    [renaming]="renaming()"
    [readonly]="readonly()"
    [notes]="notes()"
    [editingNote]="editingNote()"
    (nodesMoved)="moved($event)"
    (notesMoved)="notesMoved($event)"
    (noteChange)="noteChanged($event)"
    (noteEdit)="noteEdit.emit($event)"
    (noteEditStart)="noteEditStart.emit($event)"
    (noteDelete)="noteDelete.emit($event)"
    (connectionCreated)="connected($event)"
    (selectionChange)="selectionChange.emit($event)"
    (quickAdd)="quickAdd.emit($event)"
    (missingClick)="missingClick.emit($event)"
    (nodeAction)="nodeAction.emit($event)"
    (renamed)="renamed.emit($event)"
    (contextMenu)="contextMenu.emit($event)"
    (arrange)="arrange()"
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

  /** Час завершення кроку на картці вузла (F3-76). */
  readonly runTimes = input<ReadonlyMap<string, string>>(new Map());

  /** Ключ збереження положення полотна — id сценарію (F3-76); `null` — не запам'ятовувати. */
  readonly viewportKey = input<string | null>(null);

  /** Перемикач «Останній запуск» (F3-76): `null` — кнопки немає. */
  readonly lastRunToggle = input<boolean | null>(null);

  readonly lastRunToggled = output<boolean>();

  /** Незаповнені обов'язкові параметри за id вузла (F3-57). */
  readonly missingFields = input<ReadonlyMap<string, readonly string[]>>(new Map());

  /** Вузол з inline-полем назви (F3-58). */
  readonly renaming = input<string | null>(null);

  /** Виділені вузли й ребра. */
  readonly selection = input<CanvasSelection>(EMPTY_SELECTION);

  /** Лише перегляд: вузли не перетягуються, ребра не створюються; виділення працює. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Нове визначення після змін на канвасі (переміщення вузлів, нове ребро). */
  readonly definitionChange = output<WorkflowDefinition>();

  /** Нове виділення після дій користувача на канвасі. */
  readonly selectionChange = output<CanvasSelection>();

  /** Дія з тулбару вузла (F3-58). */
  readonly nodeAction = output<NodeActionRequest>();

  /** Inline-перейменування на картці завершено (`name: null` — скасовано). */
  readonly renamed = output<NodeRenameRequest>();

  /** ПКМ на канвасі (F3-58). */
  readonly contextMenu = output<CanvasContextMenuRequest>();

  /** Клік по «не заповнено: N» на картці (F3-57). */
  readonly missingClick = output<MissingFieldRequest>();

  /** Відкрити спливаючу палітру (F3-50); у режимі перегляду не надходить. */
  readonly quickAdd = output<QuickAddRequest>();

  /** Нотатка з відкритим полем тексту (F3-62); `null` — немає. */
  readonly editingNote = input<string | null>(null);

  /** Текст нотатки збережено (`text`) або редагування скасовано (`null`) — вирішує редактор (F3-62). */
  readonly noteEdit = output<NoteEditRequest>();

  /** Подвійний клік чи «Редагувати» на нотатці. */
  readonly noteEditStart = output<string>();

  /** «Видалити» з палітри нотатки. */
  readonly noteDelete = output<string>();

  private readonly vflow = viewChild(VflowCanvasComponent);

  private readonly index = computed(() => new DescriptorIndex(this.descriptors()));

  protected readonly model = computed(() => toCanvasModel(this.definition(), this.index()));

  protected readonly pinnedNodes = computed(() => new Set(Object.keys(this.definition().pinData ?? {})));

  /** Нотатки сценарію (F3-62); поле може бути відсутнє. */
  protected readonly notes = computed(() => notesOf(this.definition()));

  /** Лівий верхній кут для нової нотатки в центрі видимої частини канвасу (`Shift+S`). */
  viewportCenter(): [number, number] {
    return this.vflow()?.viewportCenter() ?? [0, 0];
  }

  /** Масштаб полотна з клавіш (F3-90). */
  zoom(action: 'in' | 'out' | 'reset' | 'fit'): void {
    const flow = this.vflow();
    if (!flow) return;
    if (action === 'fit') flow.fit();
    else if (action === 'reset') flow.zoomReset();
    else flow.zoomStep(action);
  }

  /** Підсунути вузол у видиму частину полотна (панель вузла відкрилася поруч). */
  revealNode(nodeId: string): void {
    this.vflow()?.revealNode(nodeId);
  }

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

  /** «Вирівняти»: шарове розміщення зліва направо; змінюються лише позиції вузлів. */
  protected arrange(): void {
    if (this.readonly()) return;
    const cards = new Map(this.model().nodes.map((n) => [n.id, n]));
    const next = layoutLeftToRight(this.definition(), NODE_WIDTH, (node) => {
      const card = cards.get(node.id);
      return card ? nodeHeight(card) : nodeHeight({ inputs: ['main'], outputs: ['main'] });
    });
    if (next !== this.definition()) this.definitionChange.emit(next);
  }

  protected moved(positions: NodePosition[]): void {
    if (this.readonly()) return;
    this.definitionChange.emit(moveNodes(this.definition(), positions, this.index()));
  }

  protected notesMoved(positions: NodePosition[]): void {
    if (this.readonly()) return;
    const next = moveNotes(this.definition(), positions);
    if (next !== this.definition()) this.definitionChange.emit(next);
  }

  /** Колір чи розмір нотатки з канвасу — одна зміна визначення. */
  protected noteChanged(change: NoteChangeRequest): void {
    if (this.readonly()) return;
    const next = updateNote(this.definition(), change.id, { color: change.color, size: change.size });
    if (next !== this.definition()) this.definitionChange.emit(next);
  }

  protected connected(connection: NewConnection): void {
    if (this.readonly()) return;
    const next = connectNodes(this.definition(), connection);
    if (next !== this.definition()) this.definitionChange.emit(next);
  }
}
