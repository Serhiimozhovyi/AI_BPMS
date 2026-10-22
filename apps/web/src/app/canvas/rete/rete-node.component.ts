import { ChangeDetectorRef, Component, Input, OnChanges, inject } from '@angular/core';
import { ClassicPreset } from 'rete';
import { RefDirective } from 'rete-angular-plugin/22';
import { GROUP_COLORS, GROUP_LABELS, SpikeNode } from '../spike-graph';

/** Вузол Rete з даними спайку; сокети іменовані за портами формату workflow. */
export class SpikeReteNode extends ClassicPreset.Node {
  width = 220;
  height = 72;

  constructor(
    public readonly spike: SpikeNode,
    socket: ClassicPreset.Socket,
  ) {
    super(spike.name);
    this.id = spike.id;
    // multipleConnections: у форматі workflow один вихід → кілька ребер і один вхід ← кілька ребер;
    // без цього Rete при новому з'єднанні видаляє попереднє на тому ж порту.
    for (const name of spike.inputs) {
      this.addInput(name, new ClassicPreset.Input(socket, name, true));
    }
    spike.outputs.forEach((name, index) => {
      const output = new ClassicPreset.Output(socket, name, true);
      output.index = index;
      this.addOutput(name, output);
    });
  }

  get color(): string {
    return GROUP_COLORS[this.spike.group];
  }

  get groupLabel(): string {
    return GROUP_LABELS[this.spike.group];
  }

  get outputKeys(): string[] {
    return this.spike.outputs;
  }

  get inputKeys(): string[] {
    return this.spike.inputs;
  }
}

/**
 * Кастомна картка вузла для Rete.js: той самий вигляд, що й у прототипі ngx-vflow.
 * Контракт плагіна: входи `data`/`emit`/`rendered`, ручний `detectChanges` (як у класичному NodeComponent).
 */
@Component({
  selector: 'app-rete-node',
  imports: [RefDirective],
  template: `
    <div class="node" [class.selected]="data.selected" [style.--node-color]="data.color" [attr.data-node-id]="data.id">
      @for (key of data.inputKeys; track key) {
        <div
          class="socket socket--in"
          refComponent
          [data]="{ type: 'socket', side: 'input', key, nodeId: data.id, payload: data.inputs[key]?.socket, seed }"
          [emit]="emit"
        ></div>
      }
      <div class="node__head">
        <span class="node__group">{{ data.groupLabel }}</span>
        <span class="node__type">{{ data.spike.type }}</span>
      </div>
      <div class="node__name">{{ data.label }}</div>
      @for (key of data.outputKeys; track key; let i = $index) {
        <div
          class="socket socket--out"
          [style.top.px]="portTop(i, data.outputKeys.length) - 18"
          refComponent
          [data]="{ type: 'socket', side: 'output', key, nodeId: data.id, payload: data.outputs[key]?.socket, seed }"
          [emit]="emit"
        ></div>
        <span class="node__port" [style.top.px]="portTop(i, data.outputKeys.length)">{{ key }}</span>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }

    .node {
      position: relative;
      box-sizing: border-box;
      width: 220px;
      height: 72px;
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

    /* Без transform: rete-render-utils рахує центр сокета через offsetLeft/offsetTop, трансформи не бачить. */
    .socket {
      position: absolute;
      width: 36px;
      height: 36px;
      line-height: 0;
    }

    .socket--in {
      left: -18px;
      top: 18px;
    }

    .socket--out {
      left: calc(100% - 18px);
    }
  `,
})
export class ReteNodeComponent implements OnChanges {
  @Input() data!: SpikeReteNode;
  @Input() emit!: (data: unknown) => void;
  @Input() rendered!: () => void;

  protected seed = 0;

  private readonly cdr = inject(ChangeDetectorRef);

  constructor() {
    this.cdr.detach();
  }

  ngOnChanges(): void {
    this.cdr.detectChanges();
    requestAnimationFrame(() => this.rendered());
    this.seed++;
  }

  protected portTop(index: number, total: number): number {
    return (72 / (total + 1)) * (index + 1);
  }
}
