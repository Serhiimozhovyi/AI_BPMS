import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { Edge, Node, Vflow, createEdges, createNodes } from 'ngx-vflow';
import { CanvasModel, CanvasNode, CanvasNodeGroup, NodePosition } from '../canvas-model';
import { groupLabel } from '../palette';

interface NodeData {
  node: CanvasNode;
  color: string;
  groupLabel: string;
  height: number;
}

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
      (nodeDragEnd)="dragEnded()"
    >
      <ng-template nodeHtml let-ctx>
        <div
          class="node"
          [class.selected]="ctx.selected()"
          [class.disabled]="ctx.data().node.disabled"
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

    .node.disabled {
      opacity: 0.55;
      border-style: dashed;
    }

    .node__head {
      font-size: 11px;
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

  /** Вузли, які користувач перетягнув у нову позицію (після відпускання миші). */
  readonly nodesMoved = output<NodePosition[]>();

  protected readonly nodes = computed<Node<NodeData>[]>(() =>
    createNodes<NodeData>(
      this.model().nodes.map((n) => {
        const height = nodeHeight(n);
        return {
          id: n.id,
          type: 'html-template',
          point: { x: n.x, y: n.y },
          width: NODE_WIDTH,
          height,
          data: { node: n, color: GROUP_COLORS[n.group], groupLabel: groupLabel(n.group), height },
        };
      }),
    ),
  );

  protected readonly edges = computed<Edge[]>(() => {
    const ports = new Map(this.model().nodes.map((n) => [n.id, n]));
    return createEdges(
      this.model()
        .edges.filter((e) => {
          // Ребро на неіснуючий вузол чи порт не малюється, але лишається в моделі (round-trip без втрат).
          const from = ports.get(e.source);
          const to = ports.get(e.target);
          return !!from && !!to && e.sourceOutput >= 0 && e.sourceOutput < from.outputs.length && e.targetInput >= 0 && e.targetInput < to.inputs.length;
        })
        .map((e) => ({
          id: e.id,
          source: e.source,
          sourceHandle: outputHandle(e.sourceOutput),
          target: e.target,
          targetHandle: inputHandle(e.targetInput),
          curve: 'bezier' as const,
          markers: { end: { type: 'arrow-closed' as const } },
        })),
    );
  });

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

/** Висота картки: мінімум 72 px, більше — якщо портів багато (`core.switch`). */
export function nodeHeight(node: Pick<CanvasNode, 'inputs' | 'outputs'>): number {
  const ports = Math.max(node.inputs.length, node.outputs.length);
  return Math.max(MIN_NODE_HEIGHT, (ports + 1) * PORT_STEP);
}
