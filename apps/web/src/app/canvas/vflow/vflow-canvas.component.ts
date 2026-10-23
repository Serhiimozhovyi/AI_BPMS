import { ChangeDetectionStrategy, Component, computed, effect, input, output, untracked } from '@angular/core';
import { Connection, ConnectionSettings, Edge, Node, Vflow, createEdges, createNodes } from 'ngx-vflow';
import { CanvasSelection, EMPTY_SELECTION, NewConnection } from '../canvas-edit';
import { CanvasModel, CanvasNode, CanvasNodeGroup, NodePosition } from '../canvas-model';
import { groupLabel } from '../palette';
import { NodeRunStatus } from '../test-run';

interface NodeData {
  node: CanvasNode;
  color: string;
  groupLabel: string;
  height: number;
  /** Помилки валідації з Api для цього вузла. */
  errors: readonly string[];
  /** Статус вузла в останньому тестовому запуску; `null` — не виконувався. */
  runStatus: NodeRunStatus | null;
  /** Output вузла закріплено (`pinData`): у тестовому запуску вузол не виконується. */
  pinned: boolean;
}

/** Помилки валідації за id вузла. */
export type NodeErrors = ReadonlyMap<string, readonly string[]>;

const NO_ERRORS: NodeErrors = new Map();

/** Статуси вузлів тестового запуску за id вузла. */
export type NodeRunStatuses = ReadonlyMap<string, NodeRunStatus>;

const NO_RUN: NodeRunStatuses = new Map();

const NO_PINNED: ReadonlySet<string> = new Set();

export const NODE_WIDTH = 220;
const MIN_NODE_HEIGHT = 72;
/** Вертикальний крок між портами: стільки, щоб підписи портів не накладались. */
const PORT_STEP = 16;

/** Колір групи для картки вузла. */
export const GROUP_COLORS: Record<CanvasNodeGroup, string> = {
  trigger: '#2e7d32',
  logic: '#ef6c00',
  transform: '#00838f',
  integration: '#1565c0',
  ai: '#6a1b9a',
  unknown: '#757575',
};

/** Id порту ngx-vflow за індексом формату: `in-0`, `out-1`. Індекси, а не назви, — щоб ребра не залежали від підписів. */
export function inputHandle(index: number): string {
  return `in-${index}`;
}

export function outputHandle(index: number): string {
  return `out-${index}`;
}

/** Індекс порту з id `in-N`/`out-N`; інший id — `-1`. */
export function handleIndex(handle: string | undefined, prefix: 'in' | 'out'): number {
  const match = handle ? new RegExp(`^${prefix}-(\\d+)$`).exec(handle) : null;
  return match ? Number(match[1]) : -1;
}

/**
 * Рендер {@link CanvasModel} на ngx-vflow (ADR-003): картка вузла через `ng-template[nodeHtml]`, порти через
 * `<handle>`, drag/zoom/pan з коробки. Знає лише про бібліотеку; формат workflow — у `CanvasComponent`.
 */
@Component({
  selector: 'app-vflow-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Vflow],
  template: `
    <vflow
      view="auto"
      [nodes]="nodes()"
      [edges]="edges()"
      [optimization]="{ virtualization: virtualization() }"
      background="#f4f6f8"
      [snapGrid]="[10, 10]"
      [connection]="connectionSettings"
      (nodeDragEnd)="dragEnded()"
      (connect)="connected($event)"
      (nodesChanges.select)="selectionChanged()"
      (edgesChanges.select)="selectionChanged()"
    >
      <ng-template nodeHtml let-ctx>
        <div
          class="node"
          selectable
          [class.selected]="ctx.selected()"
          [class.disabled]="ctx.data().node.disabled"
          [class.invalid]="ctx.data().errors.length > 0"
          [attr.data-invalid]="ctx.data().errors.length > 0 ? true : null"
          [attr.data-run-status]="ctx.data().runStatus"
          [attr.data-pinned]="ctx.data().pinned ? true : null"
          [class.run-success]="ctx.data().runStatus === 'success'"
          [class.run-error]="ctx.data().runStatus === 'error'"
          [class.run-skipped]="ctx.data().runStatus === 'skipped' || ctx.data().runStatus === 'cancelled'"
          [style.--node-color]="ctx.data().color"
          [style.height.px]="ctx.data().height"
          [attr.data-node-id]="ctx.node.id"
          [attr.data-node-type]="ctx.data().node.type"
        >
          @for (port of ctx.data().node.inputs; track $index; let i = $index) {
            <handle
              position="left"
              type="target"
              [id]="inputId(i)"
              [offsetY]="handleOffset(i, ctx.data().node.inputs.length, ctx.data().height)"
            />
            @if (ctx.data().node.inputs.length > 1) {
              <span
                class="node__port node__port--in"
                [style.top.px]="portTop(i, ctx.data().node.inputs.length, ctx.data().height)"
                >{{ port }}</span
              >
            }
          }
          <div class="node__head">
            <span class="node__group">{{ ctx.data().groupLabel }}</span>
            @if (ctx.data().pinned) {
              <span class="node__pin" data-testid="node-pinned" role="img" title="Output закріплено" i18n-title="@@canvas.pinned">📌</span>
            }
            @if (ctx.data().runStatus === 'success') {
              <span class="node__run node__run--success" data-testid="node-run-badge" role="img" title="Виконано" i18n-title="@@canvas.run.success">✓</span>
            } @else if (ctx.data().runStatus === 'error') {
              <span class="node__run node__run--error" data-testid="node-run-badge" role="img" title="Помилка" i18n-title="@@canvas.run.error">✕</span>
            }
            @if (ctx.data().errors.length > 0) {
              <span class="node__errors" data-testid="node-errors" role="img" [title]="ctx.data().errors.join('\n')"
                >! {{ ctx.data().errors.length }}</span
              >
            }
          </div>
          <div class="node__name" [title]="ctx.data().node.name">{{ ctx.data().node.name }}</div>
          <div class="node__type" [title]="ctx.data().node.subtitle">{{ ctx.data().node.subtitle }}</div>
          @for (port of ctx.data().node.outputs; track $index; let i = $index) {
            <handle
              position="right"
              type="source"
              [id]="outputId(i)"
              [offsetY]="handleOffset(i, ctx.data().node.outputs.length, ctx.data().height)"
            />
            @if (ctx.data().node.outputs.length > 1) {
              <span
                class="node__port node__port--out"
                [class.node__port--error]="port === 'error'"
                [style.top.px]="portTop(i, ctx.data().node.outputs.length, ctx.data().height)"
                >{{ port }}</span
              >
            }
          }
        </div>
      </ng-template>
    </vflow>
  `,
  styles: `
    :host {
      display: block;
      width: 100%;
      height: 100%;
    }

    .node {
      position: relative;
      box-sizing: border-box;
      width: ${NODE_WIDTH}px;
      padding: 8px 12px;
      border: 1.5px solid var(--node-color);
      border-left-width: 6px;
      border-radius: 8px;
      background: #fff;
      box-shadow: 0 1px 3px rgb(0 0 0 / 0.15);
      font: 13px/1.3 Roboto, sans-serif;
      user-select: none;
    }

    .node.selected {
      box-shadow: 0 0 0 2px var(--node-color);
    }

    .node.invalid {
      border-color: #c62828;
      box-shadow: 0 0 0 2px rgb(198 40 40 / 0.35);
    }

    .node.run-success {
      border-color: #2e7d32;
      background: #f1f8f1;
    }

    .node.run-error {
      border-color: #c62828;
      background: #fdf1f1;
    }

    .node.run-skipped {
      opacity: 0.7;
    }

    .node__pin {
      margin-left: auto;
      font-size: 11px;
    }

    .node__run {
      margin-left: auto;
      margin-right: 4px;
      font-weight: 700;
    }

    .node__pin + .node__run {
      margin-left: 4px;
    }

    .node__run--success {
      color: #2e7d32;
    }

    .node__run--error {
      color: #c62828;
    }

    .node.disabled {
      opacity: 0.55;
      border-style: dashed;
    }

    .node__head {
      display: flex;
      justify-content: space-between;
      font-size: 11px;
    }

    .node__errors {
      padding: 0 6px;
      border-radius: 8px;
      background: #c62828;
      color: #fff;
      font-weight: 600;
      cursor: help;
    }

    .node__group {
      color: var(--node-color);
      font-weight: 600;
      text-transform: uppercase;
    }

    .node__name,
    .node__type {
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      max-width: 150px;
    }

    .node__name {
      margin-top: 4px;
      font-weight: 500;
    }

    .node__type {
      font-size: 11px;
      color: #666;
    }

    .node__port {
      position: absolute;
      transform: translateY(-50%);
      font-size: 10px;
      color: #888;
    }

    .node__port--out {
      right: 10px;
    }

    .node__port--in {
      left: 10px;
    }

    .node__port--error {
      color: #c62828;
    }
  `,
})
export class VflowCanvasComponent {
  /** Модель для відображення. */
  readonly model = input.required<CanvasModel>();

  /** Віртуалізація viewport'у: ngx-vflow рендерить лише видимі вузли (ADR-003: увімкнено за замовчуванням). */
  readonly virtualization = input(true);

  /** Помилки валідації за id вузла — вузол підсвічується, текст у підказці. */
  readonly nodeErrors = input<NodeErrors>(NO_ERRORS);

  /** Статуси вузлів останнього тестового запуску — вузол зеленіє/червоніє. */
  readonly runStatuses = input<NodeRunStatuses>(NO_RUN);

  /** Вузли із закріпленим output (`pinData`). */
  readonly pinnedNodes = input<ReadonlySet<string>>(NO_PINNED);

  /** Виділення (вузли за id, ребра за індексом у `connections`); змінюється і ззовні (вставка, видалення). */
  readonly selection = input<CanvasSelection>(EMPTY_SELECTION);

  /** Вузли, які користувач перетягнув у нову позицію (після відпускання миші). */
  readonly nodesMoved = output<NodePosition[]>();

  /** Користувач з'єднав вихід одного вузла із входом іншого. */
  readonly connectionCreated = output<NewConnection>();

  /** Користувач змінив виділення (клік, рамка, Ctrl+клік). */
  readonly selectionChange = output<CanvasSelection>();

  /** Лише вихід → вхід різних вузлів (цикл на себе формат забороняє). */
  protected readonly connectionSettings: ConnectionSettings = {
    curve: 'bezier',
    marker: { type: 'arrow-closed' },
    validator: (c) =>
      c.source !== c.target && handleIndex(c.sourceHandle, 'out') >= 0 && handleIndex(c.targetHandle, 'in') >= 0,
  };

  protected readonly nodes = computed<Node<NodeData>[]>(() => {
    const errors = this.nodeErrors();
    const run = this.runStatuses();
    const pinned = this.pinnedNodes();
    // Виділення лише при перебудові; далі його синхронізує ефект нижче, без перебудови всіх вузлів.
    const selected = new Set(untracked(this.selection).nodes);
    return createNodes<NodeData>(
      this.model().nodes.map((n) => {
        const height = nodeHeight(n);
        return {
          id: n.id,
          type: 'html-template',
          point: { x: n.x, y: n.y },
          width: NODE_WIDTH,
          height,
          selected: selected.has(n.id),
          data: { node: n, color: GROUP_COLORS[n.group], groupLabel: groupLabel(n.group), height, errors: errors.get(n.id) ?? [], runStatus: run.get(n.id) ?? null, pinned: pinned.has(n.id) },
        };
      }),
    );
  });

  protected readonly edges = computed<Edge[]>(() => {
    const ports = new Map(this.model().nodes.map((n) => [n.id, n]));
    const selected = new Set(untracked(this.selection).edges);
    return createEdges(
      this.model()
        .edges.map((e, index) => ({ e, index }))
        .filter(({ e }) => {
          // Ребро на неіснуючий вузол чи порт не малюється, але лишається в моделі (round-trip без втрат).
          const from = ports.get(e.source);
          const to = ports.get(e.target);
          return !!from && !!to && e.sourceOutput >= 0 && e.sourceOutput < from.outputs.length && e.targetInput >= 0 && e.targetInput < to.inputs.length;
        })
        .map(({ e, index }) => ({
          id: e.id,
          source: e.source,
          sourceHandle: outputHandle(e.sourceOutput),
          target: e.target,
          targetHandle: inputHandle(e.targetInput),
          curve: 'bezier' as const,
          markers: { end: { type: 'arrow-closed' as const } },
          selected: selected.has(index),
          data: { index },
        })),
    );
  });

  constructor() {
    // Виділення ззовні (вставка, видалення, undo) → сигнали `selected` наявних вузлів і ребер.
    effect(() => {
      const selection = this.selection();
      const nodeIds = new Set(selection.nodes);
      const edgeIndexes = new Set(selection.edges);
      untracked(() => {
        for (const node of this.nodes()) node.selected?.set(nodeIds.has(node.id));
        for (const edge of this.edges()) edge.selected?.set(edgeIndexes.has(edgeIndex(edge)));
      });
    });
  }

  protected connected(c: Connection): void {
    const sourceOutput = handleIndex(c.sourceHandle, 'out');
    const targetInput = handleIndex(c.targetHandle, 'in');
    if (sourceOutput < 0 || targetInput < 0 || c.source === c.target) return;
    this.connectionCreated.emit({ source: c.source, sourceOutput, target: c.target, targetInput });
  }

  /** Виділення змінила бібліотека — читаємо його з сигналів `selected` і віддаємо назовні. */
  protected selectionChanged(): void {
    const nodes = this.nodes()
      .filter((n) => n.selected?.())
      .map((n) => n.id);
    const edges = this.edges()
      .filter((e) => e.selected?.())
      .map(edgeIndex);
    const current = this.selection();
    if (sameItems(current.nodes, nodes) && sameItems(current.edges, edges)) return;
    this.selectionChange.emit({ nodes, edges });
  }

  protected dragEnded(): void {
    const model = new Map(this.model().nodes.map((n) => [n.id, n]));
    const moved: NodePosition[] = [];
    for (const node of this.nodes()) {
      const point = node.point();
      const original = model.get(node.id);
      if (original && (original.x !== point.x || original.y !== point.y)) {
        moved.push({ id: node.id, x: point.x, y: point.y });
      }
    }
    if (moved.length > 0) this.nodesMoved.emit(moved);
  }

  protected inputId(index: number): string {
    return inputHandle(index);
  }

  protected outputId(index: number): string {
    return outputHandle(index);
  }

  protected handleOffset(index: number, total: number, height: number): number {
    return this.portTop(index, total, height) - height / 2;
  }

  protected portTop(index: number, total: number, height: number): number {
    const step = height / (total + 1);
    return step * (index + 1);
  }
}

function edgeIndex(edge: Edge): number {
  return (edge.data?.() as { index: number } | undefined)?.index ?? -1;
}

function sameItems<T>(a: readonly T[], b: readonly T[]): boolean {
  return a.length === b.length && a.every((v, i) => v === b[i]);
}

/** Висота картки: мінімум 72 px, більше — якщо портів багато (`core.switch`). */
export function nodeHeight(node: Pick<CanvasNode, 'inputs' | 'outputs'>): number {
  const ports = Math.max(node.inputs.length, node.outputs.length);
  return Math.max(MIN_NODE_HEIGHT, (ports + 1) * PORT_STEP);
}
