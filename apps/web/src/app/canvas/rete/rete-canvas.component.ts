import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ClassicPreset, GetSchemes, NodeEditor } from 'rete';
import { AngularArea2D, AngularPlugin, Presets } from 'rete-angular-plugin/22';
import { AreaExtensions, AreaPlugin } from 'rete-area-plugin';
import { ConnectionPlugin, Presets as ConnectionPresets } from 'rete-connection-plugin';
import { SpikeGraph } from '../spike-graph';
import { ReteNodeComponent, SpikeReteNode } from './rete-node.component';

/** Окремий клас потрібен TypeScript: з ClassicPreset.Connection напряму варіантність generic'ів ламає ClassicScheme. */
class SpikeConnection<N extends SpikeReteNode> extends ClassicPreset.Connection<N, N> {}

type Schemes = GetSchemes<SpikeReteNode, SpikeConnection<SpikeReteNode>>;
type AreaExtra = AngularArea2D<Schemes>;

/**
 * Прототип канвасу на Rete.js v2 (спайк F1-11): NodeEditor + AreaPlugin + ConnectionPlugin,
 * рендер через rete-angular-plugin (Angular Elements) з кастомним компонентом вузла.
 */
@Component({
  selector: 'app-rete-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div #container class="rete-container"></div>`,
  styles: `
    :host {
      display: block;
      width: 100%;
      height: 100%;
    }

    .rete-container {
      width: 100%;
      height: 100%;
      background: #f4f6f8;
    }
  `,
})
export class ReteCanvasComponent {
  /** Граф для відображення. */
  readonly graph = input.required<SpikeGraph>();

  /** Кількість ребер, створених користувачем мишею — для перевірки у спайку. */
  readonly connectionsAdded = signal(0);

  private readonly container = viewChild.required<ElementRef<HTMLDivElement>>('container');
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    afterNextRender(() => void this.build());
  }

  private async build(): Promise<void> {
    const socket = new ClassicPreset.Socket('items');
    const editor = new NodeEditor<Schemes>();
    const area = new AreaPlugin<Schemes, AreaExtra>(this.container().nativeElement);
    const connection = new ConnectionPlugin<Schemes, AreaExtra>();
    const render = new AngularPlugin<Schemes, AreaExtra>({ injector: this.injector });

    render.addPreset(Presets.classic.setup({ customize: { node: () => ReteNodeComponent } }));
    connection.addPreset(ConnectionPresets.classic.setup());

    editor.use(area);
    area.use(connection);
    area.use(render);

    AreaExtensions.selectableNodes(area, AreaExtensions.selector(), {
      accumulating: AreaExtensions.accumulateOnCtrl(),
    });
    AreaExtensions.simpleNodesOrder(area);

    editor.addPipe((context) => {
      if (context.type === 'connectioncreated') {
        this.connectionsAdded.update((n) => n + 1);
      }
      return context;
    });

    const graph = this.graph();
    const nodes = new Map<string, SpikeReteNode>();
    for (const spike of graph.nodes) {
      const node = new SpikeReteNode(spike, socket);
      nodes.set(spike.id, node);
      await editor.addNode(node);
      await area.translate(node.id, { x: spike.x, y: spike.y });
    }

    for (const edge of graph.edges) {
      const source = nodes.get(edge.source);
      const target = nodes.get(edge.target);
      if (!source || !target) {
        continue;
      }
      const sourceKey = source.spike.outputs[edge.sourceOutput];
      const targetKey = target.spike.inputs[edge.targetInput];
      await editor.addConnection(new SpikeConnection(source, sourceKey, target, targetKey));
    }

    this.destroyRef.onDestroy(() => area.destroy());
  }
}
