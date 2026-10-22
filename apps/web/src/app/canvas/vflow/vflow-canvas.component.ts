import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import {
  Connection,
  Edge,
  Node,
  NodeChange,
  Vflow,
  createEdge,
  createEdges,
  createNodes,
} from 'ngx-vflow';
import { GROUP_COLORS, GROUP_LABELS, SpikeGraph, SpikeNode } from '../spike-graph';

interface NodeData {
  node: SpikeNode;
  color: string;
  groupLabel: string;
}

const NODE_WIDTH = 220;
const NODE_HEIGHT = 72;

/**
 * Прототип канвасу на ngx-vflow (спайк F1-11): кастомна картка вузла через `ng-template[nodeHtml]`,
 * порти через `<handle>`, створення з'єднань мишею, drag/zoom/pan з коробки.
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
      (connect)="connect($event)"
      (nodesChanges)="nodesChanged($event)"
    >
      <ng-template nodeHtml let-ctx>
        <div
          class="node"
          [class.selected]="ctx.selected()"
          [style.--node-color]="ctx.data().color"
          [attr.data-node-id]="ctx.node.id"
        >
          @if (ctx.data().node.inputs.length) {
            <handle position="left" type="target" id="main" />
          }
          <div class="node__head">
            <span class="node__group">{{ ctx.data().groupLabel }}</span>
            <span class="node__type">{{ ctx.data().node.type }}</span>
          </div>
          <div class="node__name">{{ ctx.data().node.name }}</div>
          @for (output of ctx.data().node.outputs; track output; let i = $index) {
            <handle
              position="right"
              type="source"
              [id]="output"
              [offsetY]="handleOffset(i, ctx.data().node.outputs.length)"
            />
            <span class="node__port" [style.top.px]="portTop(i, ctx.data().node.outputs.length)">{{ output }}</span>
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
      height: ${NODE_HEIGHT}px;
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

    .node__head {
      display: flex;
      justify-content: space-between;
      font-size: 11px;
      color: #666;
    }

    .node__group {
      color: var(--node-color);
      font-weight: 600;
      text-transform: uppercase;
    }

    .node__name {
      margin-top: 6px;
      font-weight: 500;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .node__port {
      position: absolute;
      right: 10px;
      transform: translateY(-50%);
      font-size: 10px;
      color: #888;
    }
  `,
})
export class VflowCanvasComponent {
  /** Граф для відображення. */
  readonly graph = input.required<SpikeGraph>();

  /** Віртуалізація viewport'у (ngx-vflow рендерить лише видимі вузли). */
  readonly virtualization = input(false);

  protected readonly nodes = computed<Node<NodeData>[]>(() =>
    createNodes<NodeData>(
      this.graph().nodes.map((n) => ({
        id: n.id,
        type: 'html-template',
        point: { x: n.x, y: n.y },
        width: NODE_WIDTH,
        height: NODE_HEIGHT,
        data: { node: n, color: GROUP_COLORS[n.group], groupLabel: GROUP_LABELS[n.group] },
      })),
    ),
  );

  private readonly initialEdges = computed<Edge[]>(() =>
    createEdges(
      this.graph().edges.map((e) => {
        const source = this.graph().nodes.find((n) => n.id === e.source);
        return {
          id: e.id,
          source: e.source,
          sourceHandle: source?.outputs[e.sourceOutput] ?? 'main',
          target: e.target,
          targetHandle: 'main',
          curve: 'bezier' as const,
          markers: { end: { type: 'arrow-closed' as const } },
        };
      }),
    ),
  );

  private readonly addedEdges = signal<Edge[]>([]);

  protected readonly edges = computed(() => [...this.initialEdges(), ...this.addedEdges()]);

  /** Останні зміни позицій — для перевірки у спайку, що події приходять. */
  readonly lastChange = signal<NodeChange | null>(null);

  protected connect(connection: Connection): void {
    this.addedEdges.update((edges) => [
      ...edges,
      createEdge({
        id: `${connection.source}:${connection.sourceHandle}->${connection.target}`,
        ...connection,
        curve: 'bezier',
        markers: { end: { type: 'arrow-closed' } },
      }),
    ]);
  }

  protected nodesChanged(changes: NodeChange[]): void {
    this.lastChange.set(changes.at(-1) ?? null);
  }

  protected handleOffset(index: number, total: number): number {
    return this.portTop(index, total) - NODE_HEIGHT / 2;
  }

  protected portTop(index: number, total: number): number {
    const step = NODE_HEIGHT / (total + 1);
    return step * (index + 1);
  }
}
