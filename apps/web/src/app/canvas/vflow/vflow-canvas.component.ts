import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, booleanAttribute, computed, effect, inject, input, output, untracked, viewChild } from '@angular/core';
import { ConnectEndEvent, Connection, ConnectionSettings, Edge, Node, Vflow, VflowComponent, createEdges, createNodes } from 'ngx-vflow';
import { CanvasSelection, EMPTY_SELECTION, NewConnection } from '../canvas-edit';
import { CanvasModel, CanvasNode, CanvasNodeGroup, NodePosition } from '../canvas-model';
import { groupLabel } from '../palette';
import { QuickAddRequest, ScreenPoint } from '../quick-add';
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
  /** Повідомлення помилки вузла в останньому запуску (F3-57); `null` — не падав. */
  runError: string | null;
  /** Незаповнені обов'язкові параметри (F3-57) — ключі полів у порядку форми. */
  missing: readonly string[];
}

/** Дія з тулбару над карткою вузла (F3-58). */
export type NodeToolbarAction = 'run' | 'toggle' | 'duplicate' | 'delete' | 'rename';

/** Дія над вузлом з тулбару. */
export interface NodeActionRequest {
  nodeId: string;
  action: NodeToolbarAction;
}

/** Нова назва з inline-поля картки; `null` — скасовано (Escape). */
export interface NodeRenameRequest {
  nodeId: string;
  name: string | null;
}

/** ПКМ на канвасі (F3-58): що під вказівником і де відкрити меню. */
export type CanvasContextTarget = { kind: 'node'; nodeId: string } | { kind: 'edge'; edgeIndex: number } | { kind: 'canvas' };

export interface CanvasContextMenuRequest {
  target: CanvasContextTarget;
  anchor: ScreenPoint;
}

/** Клік по «не заповнено: N» — відкрити вузол і поставити фокус на перше незаповнене поле. */
export interface MissingFieldRequest {
  nodeId: string;
  field: string;
}

/** Помилки валідації за id вузла. */
export type NodeErrors = ReadonlyMap<string, readonly string[]>;

const NO_ERRORS: NodeErrors = new Map();

/** Статуси вузлів тестового запуску за id вузла. */
export type NodeRunStatuses = ReadonlyMap<string, NodeRunStatus>;

const NO_RUN: NodeRunStatuses = new Map();

const NO_RUN_ERRORS: ReadonlyMap<string, string> = new Map();

const NO_MISSING: ReadonlyMap<string, readonly string[]> = new Map();

const NO_PINNED: ReadonlySet<string> = new Set();
const NO_COUNTS: ReadonlyMap<string, number> = new Map();

export const NODE_WIDTH = 220;

/** Крок кнопок «+»/«−» і межі масштабу. */
const ZOOM_STEP = 1.25;
const MIN_ZOOM = 0.25;
const MAX_ZOOM = 2;

const MIN_NODE_HEIGHT = 72;
/** Вертикальний крок між портами: стільки, щоб підписи портів не накладались. */
const PORT_STEP = 16;

/** Колір групи для картки вузла. */
export const GROUP_COLORS: Record<CanvasNodeGroup, string> = {
  trigger: '#47cc8a',
  logic: '#ff840a',
  transform: '#27c7be',
  integration: '#20aaea',
  ai: '#b36bd4',
  unknown: '#8d86a3',
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
      background="#f4f3f8"
      [snapGrid]="[10, 10]"
      [connection]="connectionSettings"
      (nodeDragEnd)="dragEnded()"
      (connect)="connected($event)"
      (connectEnd)="connectEnded($event)"
      (nodesChanges.select)="selectionChanged()"
      (edgesChanges.select)="selectionChanged()"
      (contextmenu)="openContextMenu($event)"
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
          @if (!readonly()) {
            <div class="node__toolbar" data-testid="node-toolbar" role="toolbar" (mousedown)="$event.stopPropagation()" (pointerdown)="$event.stopPropagation()">
              <button type="button" class="node__tool" data-testid="node-tool-run" i18n-title="@@canvas.tool.run" title="Виконати вузол" (click)="act($event, ctx.node.id, 'run')">▶</button>
              <button
                type="button"
                class="node__tool"
                data-testid="node-tool-toggle"
                [title]="ctx.data().node.disabled ? enableLabel : disableLabel"
                (click)="act($event, ctx.node.id, 'toggle')"
              >
                ⏻
              </button>
              <button type="button" class="node__tool" data-testid="node-tool-duplicate" i18n-title="@@canvas.tool.duplicate" title="Дублювати (Ctrl+D)" (click)="act($event, ctx.node.id, 'duplicate')">⧉</button>
              <button type="button" class="node__tool" data-testid="node-tool-rename" i18n-title="@@canvas.tool.rename" title="Перейменувати (F2)" (click)="act($event, ctx.node.id, 'rename')">✎</button>
              <button type="button" class="node__tool node__tool--danger" data-testid="node-tool-delete" i18n-title="@@canvas.tool.delete" title="Видалити (Delete)" (click)="act($event, ctx.node.id, 'delete')">🗑</button>
            </div>
          }
          @if (renaming() === ctx.node.id) {
            <input
              class="node__rename"
              data-testid="node-rename"
              i18n-aria-label="@@canvas.rename.label"
              aria-label="Нова назва вузла"
              [value]="ctx.data().node.name"
              (mousedown)="$event.stopPropagation()"
              (pointerdown)="$event.stopPropagation()"
              (keydown.enter)="commitRename(ctx.node.id, $any($event.target).value)"
              (keydown.escape)="cancelRename(ctx.node.id)"
              (blur)="commitRename(ctx.node.id, $any($event.target).value)"
            />
          } @else {
            <div class="node__name" [title]="ctx.data().node.name" (dblclick)="act($event, ctx.node.id, 'rename')">{{ ctx.data().node.name }}</div>
          }
          @if (ctx.data().runError; as runError) {
            <div class="node__run-error" data-testid="node-run-error" [title]="runError">{{ firstLine(runError) }}</div>
          } @else {
            <div class="node__type" [title]="ctx.data().node.subtitle">{{ ctx.data().node.subtitle }}</div>
          }
          @if (ctx.data().missing.length > 0) {
            <button
              type="button"
              class="node__missing"
              data-testid="node-missing"
              [title]="missingTitle(ctx.data().missing)"
              (mousedown)="$event.stopPropagation()"
              (pointerdown)="$event.stopPropagation()"
              (click)="openMissing($event, ctx.node.id, ctx.data().missing[0])"
              i18n="@@canvas.missing"
            >
              не заповнено: {{ ctx.data().missing.length }}
            </button>
          }
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
          @if (ctx.selected() && !readonly()) {
            @for (port of ctx.data().node.outputs; track $index; let i = $index) {
              <button
                type="button"
                class="node__add"
                data-testid="port-add"
                [attr.data-output]="i"
                [style.top.px]="portTop(i, ctx.data().node.outputs.length, ctx.data().height)"
                i18n-title="@@canvas.quickAdd.portHint"
                title="Додати наступний крок"
                (mousedown)="$event.stopPropagation()"
                (pointerdown)="$event.stopPropagation()"
                (click)="addFromPort($event, ctx.node.id, i)"
              >
                +
              </button>
            }
          }
        </div>
      </ng-template>
      <ng-template edgeLabelHtml let-ctx>
        <span class="flex flex-col items-center gap-0.5">
          @if (ctx.label.data.count !== undefined) {
            <span class="edge__count pointer-events-none block whitespace-nowrap rounded-md bg-[var(--ac-cool-grey)] px-[3px] text-[10px] leading-[14px] text-[var(--ac-cool-grey-4)]" data-testid="edge-count" [attr.data-edge]="ctx.label.data.index">{{ itemsLabel(ctx.label.data.count) }}</span>
          }
          @if (ctx.label.data.add) {
            <button
              type="button"
              class="edge__add"
              data-testid="edge-add"
              i18n-title="@@canvas.quickAdd.edgeHint"
              title="Вставити крок між вузлами"
              (mousedown)="$event.stopPropagation()"
              (pointerdown)="$event.stopPropagation()"
              (click)="addOnEdge($event, ctx.label.data.index)"
            >
              +
            </button>
          }
        </span>
      </ng-template>
      <mini-map position="bottom-left" maskColor="rgb(244 243 248 / 0.7)" strokeColor="#b9b4cc" />
    </vflow>
    <div class="controls" role="toolbar" i18n-aria-label="@@canvas.controls" aria-label="Керування канвасом" data-testid="canvas-controls">
      @if (!readonly()) {
        <button type="button" class="controls__btn controls__btn--wide" data-testid="canvas-arrange" i18n-title="@@canvas.arrange.hint" title="Розставити вузли зліва направо за з'єднаннями" (click)="arrangeClicked()" i18n="@@canvas.arrange">Вирівняти</button>
      }
      <button type="button" class="controls__btn" data-testid="canvas-fit" i18n-title="@@canvas.fit" title="Показати весь сценарій" (click)="fit()">⤢</button>
      <button type="button" class="controls__btn" data-testid="canvas-zoom-in" i18n-title="@@canvas.zoomIn" title="Збільшити" (click)="zoomBy(ZOOM_STEP)">+</button>
      <button type="button" class="controls__btn" data-testid="canvas-zoom-out" i18n-title="@@canvas.zoomOut" title="Зменшити" (click)="zoomBy(1 / ZOOM_STEP)">−</button>
    </div>
  `,
  styles: `
    :host {
      position: relative;
      display: block;
      width: 100%;
      height: 100%;
    }

    /* Мінікарта (F3-58) — лише показує: кліки проходять до вузлів під нею. */
    :host ::ng-deep svg.root-svg > svg,
    :host ::ng-deep svg.root-svg > rect {
      pointer-events: none;
    }

    /* Кнопки канвасу (F3-58) — внизу праворуч, над полотном. */
    .controls {
      position: absolute;
      right: 12px;
      bottom: 12px;
      display: flex;
      gap: 4px;
    }

    .controls__btn {
      min-width: 30px;
      height: 30px;
      padding: 0 6px;
      border: 1px solid var(--ac-cool-grey);
      border-radius: 6px;
      background: var(--ac-white);
      color: var(--ac-cool-grey-4);
      font: 500 14px/1 Ubuntu, sans-serif;
      cursor: pointer;
      box-shadow: 0 1px 3px rgb(38 31 75 / 0.1);
    }

    .controls__btn--wide {
      padding: 0 10px;
      font-size: 13px;
    }

    .controls__btn:hover {
      border-color: var(--ac-kit);
      color: var(--ac-kit);
    }

    /* Картка вузла — у foreignObject ngx-vflow, який обрізає все за межами вузла; «+» біля порту стоїть за краєм. */
    :host ::ng-deep foreignObject.selectable {
      overflow: visible;
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
      box-shadow: 0 1px 4px rgb(38 31 75 / 0.12);
      font: 13px/1.3 Ubuntu, sans-serif;
      user-select: none;
    }

    .node.selected {
      box-shadow: 0 0 0 2px var(--node-color);
    }

    .node.invalid {
      border-color: var(--ac-red);
      box-shadow: 0 0 0 2px rgb(255 96 96 / 0.35);
    }

    .node.run-success {
      border-color: var(--ac-green);
      background: var(--ac-green-15);
    }

    .node.run-error {
      border-color: var(--ac-red);
      background: var(--ac-red-15);
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
      color: var(--ac-green);
    }

    .node__run--error {
      color: var(--ac-red);
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
      background: var(--ac-red);
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
      color: var(--ac-cool-grey-4);
    }

    /* Тулбар над карткою (F3-58): при наведенні і на виділеному вузлі; за межею картки, як «+» біля порту. */
    .node__toolbar {
      position: absolute;
      bottom: calc(100% + 4px);
      right: 0;
      display: none;
      gap: 2px;
      padding: 2px;
      border: 1px solid var(--ac-cool-grey);
      border-radius: 6px;
      background: var(--ac-white);
      box-shadow: 0 1px 4px rgb(38 31 75 / 0.12);
    }

    .node:hover .node__toolbar,
    .node.selected .node__toolbar,
    .node__toolbar:focus-within {
      display: flex;
    }

    .node__tool {
      width: 24px;
      height: 22px;
      padding: 0;
      border: 0;
      border-radius: 4px;
      background: none;
      color: var(--ac-cool-grey-4);
      font: 13px/1 Ubuntu, sans-serif;
      cursor: pointer;
    }

    .node__tool:hover,
    .node__tool:focus-visible {
      background: var(--ac-cool-grey);
      color: var(--ac-kit);
    }

    .node__tool--danger:hover {
      color: var(--ac-red);
    }

    .node__rename {
      box-sizing: border-box;
      width: 100%;
      margin-top: 2px;
      padding: 1px 4px;
      border: 1px solid var(--ac-kit);
      border-radius: 4px;
      font: 500 13px/1.3 Ubuntu, sans-serif;
    }

    /* Перший рядок помилки запуску (F3-57) замість підзаголовка; повний текст — у підказці й панелі вузла. */
    .node__run-error {
      overflow: hidden;
      font-size: 11px;
      color: var(--ac-red);
      white-space: nowrap;
      text-overflow: ellipsis;
    }

    /* «не заповнено: N» (F3-57) — під карткою, щоб не міняти її висоту й положення портів. */
    .node__missing {
      position: absolute;
      top: calc(100% + 4px);
      left: 0;
      padding: 0 6px;
      border: 1px solid var(--ac-orange);
      border-radius: 8px;
      background: var(--ac-white);
      color: var(--ac-orange);
      font: 600 11px/16px Ubuntu, sans-serif;
      white-space: nowrap;
      cursor: pointer;
    }

    .node__port {
      position: absolute;
      transform: translateY(-50%);
      font-size: 10px;
      color: var(--ac-cool-grey-4);
    }

    .node__port--out {
      right: 10px;
    }

    .node__port--in {
      left: 10px;
    }

    .node__port--error {
      color: var(--ac-red);
    }

    /* «+» біля вихідного порту виділеного вузла (F3-50): за межею картки, щоб не заважати тягнути з'єднання з порту. */
    .node__add,
    .edge__add {
      display: flex;
      align-items: center;
      justify-content: center;
      width: 20px;
      height: 20px;
      padding: 0;
      border: 1.5px solid var(--ac-kit);
      border-radius: 50%;
      background: var(--ac-white);
      color: var(--ac-kit);
      font: 700 14px/1 Ubuntu, sans-serif;
      cursor: pointer;
    }

    /* Лічильник items після запуску (F3-52) — лише підпис: кліки проходять до ребра. */
    :host ::ng-deep foreignObject:has(.edge__count):not(:has(.edge__add)) {
      pointer-events: none;
    }

    .node__add {
      position: absolute;
      right: -40px;
      transform: translateY(-50%);
    }

    .node__add:hover,
    .edge__add:hover {
      background: var(--ac-kit);
      color: var(--ac-white);
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

  /** Кількість items на виході після запуску за `вузол:порт` — підпис на ребрах (F3-52). */
  readonly itemCounts = input<ReadonlyMap<string, number>>(NO_COUNTS);

  /** Повідомлення помилок останнього запуску за id вузла — перший рядок на картці (F3-57). */
  readonly runErrors = input<ReadonlyMap<string, string>>(NO_RUN_ERRORS);

  /** Незаповнені обов'язкові параметри за id вузла — «не заповнено: N» на картці (F3-57). */
  readonly missingFields = input<ReadonlyMap<string, readonly string[]>>(NO_MISSING);

  /** Виділення (вузли за id, ребра за індексом у `connections`); змінюється і ззовні (вставка, видалення). */
  readonly selection = input<CanvasSelection>(EMPTY_SELECTION);

  /** Лише перегляд: вузли не перетягуються, нові ребра не приймаються. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Вузли, які користувач перетягнув у нову позицію (після відпускання миші). */
  readonly nodesMoved = output<NodePosition[]>();

  /** Користувач з'єднав вихід одного вузла із входом іншого. */
  readonly connectionCreated = output<NewConnection>();

  /** Користувач змінив виділення (клік, рамка, Ctrl+клік). */
  readonly selectionChange = output<CanvasSelection>();

  /** Вузол з відкритим inline-полем назви (F3-58); `null` — немає. */
  readonly renaming = input<string | null>(null);

  /** Дія з тулбару над карткою (F3-58). */
  readonly nodeAction = output<NodeActionRequest>();

  /** Inline-перейменування завершено або скасовано. */
  readonly renamed = output<NodeRenameRequest>();

  /** ПКМ на вузлі, ребрі чи порожньому канвасі (F3-58); у режимі перегляду не надходить. */
  readonly contextMenu = output<CanvasContextMenuRequest>();

  /** «Вирівняти» (F3-58): розставити вузли — рахує й застосовує `CanvasComponent`, тут лише кнопка і fit після. */
  readonly arrange = output<void>();

  /** Клік по «не заповнено: N»: відкрити вузол із фокусом на першому незаповненому полі (F3-57). */
  readonly missingClick = output<MissingFieldRequest>();

  /** Відкрити спливаючу палітру (F3-50): «+» біля порту чи на ребрі, з'єднання відпущено в порожньому місці. */
  readonly quickAdd = output<QuickAddRequest>();

  private readonly flow = viewChild(VflowComponent);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  /** Вузол, для якого inline-перейменування вже віддано (Enter + blur не дублюються). */
  private renameDone: string | null = null;

  /** Останнє положення вказівника: `connectEnd` не несе координат, а палітра відкривається там, де відпустили. */
  private pointer: ScreenPoint | null = null;

  /** Лише вихід → вхід різних вузлів (цикл на себе формат забороняє). */
  protected readonly connectionSettings: ConnectionSettings = {
    curve: 'bezier',
    marker: { type: 'arrow-closed' },
    validator: (c) =>
      !this.readonly() &&
      c.source !== c.target && handleIndex(c.sourceHandle, 'out') >= 0 && handleIndex(c.targetHandle, 'in') >= 0,
  };

  protected readonly nodes = computed<Node<NodeData>[]>(() => {
    const errors = this.nodeErrors();
    const run = this.runStatuses();
    const pinned = this.pinnedNodes();
    const runErrors = this.runErrors();
    const missing = this.missingFields();
    const draggable = !this.readonly();
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
          draggable,
          selected: selected.has(n.id),
          data: { node: n, color: GROUP_COLORS[n.group], groupLabel: groupLabel(n.group), height, errors: errors.get(n.id) ?? [], runStatus: run.get(n.id) ?? null, pinned: pinned.has(n.id), runError: runErrors.get(n.id) ?? null, missing: missing.get(n.id) ?? [] },
        };
      }),
    );
  });

  protected readonly edges = computed<Edge[]>(() => {
    const ports = new Map(this.model().nodes.map((n) => [n.id, n]));
    const selected = new Set(untracked(this.selection).edges);
    const counts = this.itemCounts();
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
          edgeLabels: this.edgeLabels(index, selected.has(index), counts.get(`${e.source}:${e.sourceOutput}`)),
          selected: selected.has(index),
          data: { index },
        })),
    );
  });

  constructor() {
    // Слухач без зміни стану компонента: лише запам'ятовує координати, тож change detection не потрібен.
    const track = (e: PointerEvent) => (this.pointer = { x: e.clientX, y: e.clientY });
    document.addEventListener('pointermove', track, { capture: true, passive: true });
    document.addEventListener('pointerup', track, { capture: true, passive: true });
    inject(DestroyRef).onDestroy(() => {
      document.removeEventListener('pointermove', track, { capture: true });
      document.removeEventListener('pointerup', track, { capture: true });
    });

    // Inline-поле назви: фокус і виділення тексту, щойно воно з'явилося.
    effect(() => {
      const id = this.renaming();
      untracked(() => {
        this.renameDone = null;
        if (!id) return;
        let tries = 0;
        const focus = () => {
          const el = this.host.nativeElement.querySelector<HTMLInputElement>(`[data-node-id="${CSS.escape(id)}"] [data-testid="node-rename"]`);
          if (el) {
            el.focus();
            el.select();
          } else if (++tries < 10) {
            requestAnimationFrame(focus);
          }
        };
        requestAnimationFrame(focus);
      });
    });

    // Виділення ззовні (вставка, видалення, undo) → сигнали `selected` наявних вузлів і ребер.
    effect(() => {
      const selection = this.selection();
      const nodeIds = new Set(selection.nodes);
      const edgeIndexes = new Set(selection.edges);
      untracked(() => {
        for (const node of this.nodes()) node.selected?.set(nodeIds.has(node.id));
        for (const edge of this.edges()) edge.selected?.set(edgeIndexes.has(edgeIndex(edge)));
        this.syncEdgeLabels();
      });
    });
  }

  protected connected(c: Connection): void {
    if (this.readonly()) return;
    const sourceOutput = handleIndex(c.sourceHandle, 'out');
    const targetInput = handleIndex(c.targetHandle, 'in');
    if (sourceOutput < 0 || targetInput < 0 || c.source === c.target) return;
    this.connectionCreated.emit({ source: c.source, sourceOutput, target: c.target, targetInput });
  }

  /** З'єднання з вихідного порту відпущено не на вузлі — палітра там, де відпустили, новий вузол з'єднається з портом. */
  protected connectEnded(event: ConnectEndEvent): void {
    const output = handleIndex(event.from.handle.id, 'out');
    const flow = this.flow();
    if (this.readonly() || event.to.node || output < 0 || !this.pointer || !flow) return;
    const point = flow.documentPointToFlowPoint(this.pointer);
    // Точка відпускання — на рівні входу нової картки (середина лівого краю), з кроком сітки.
    const at: [number, number] = [snap(point.x), snap(point.y - MIN_NODE_HEIGHT / 2)];
    this.quickAdd.emit({ target: { kind: 'port', from: { node: event.from.node.id, output }, at }, anchor: this.pointer });
  }

  protected addFromPort(event: MouseEvent, node: string, output: number): void {
    event.stopPropagation();
    this.quickAdd.emit({ target: { kind: 'port', from: { node, output } }, anchor: anchorOf(event) });
  }

  protected readonly ZOOM_STEP = ZOOM_STEP;

  protected arrangeClicked(): void {
    if (this.readonly()) return;
    this.arrange.emit();
    // Нові позиції приходять через `model` — fit, коли вузли вже перебудовано.
    requestAnimationFrame(() => requestAnimationFrame(() => this.fit()));
  }

  protected fit(): void {
    this.flow()?.fitView({ padding: 0.15, duration: 200 });
  }

  protected zoomBy(factor: number): void {
    const flow = this.flow();
    if (!flow) return;
    flow.zoomTo(Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, flow.viewport().zoom * factor)));
  }

  protected readonly disableLabel = $localize`:@@canvas.tool.disable:Вимкнути (D)`;
  protected readonly enableLabel = $localize`:@@canvas.tool.enable:Увімкнути (D)`;

  protected act(event: MouseEvent, nodeId: string, action: NodeToolbarAction): void {
    event.stopPropagation();
    if (this.readonly()) return;
    this.nodeAction.emit({ nodeId, action });
  }

  protected commitRename(nodeId: string, name: string): void {
    // Enter і наступний blur приходять обидва — віддаємо лише перший.
    if (this.renaming() !== nodeId || this.renameDone === nodeId) return;
    this.renameDone = nodeId;
    this.renamed.emit({ nodeId, name });
  }

  protected cancelRename(nodeId: string): void {
    if (this.renaming() !== nodeId || this.renameDone === nodeId) return;
    this.renameDone = nodeId;
    this.renamed.emit({ nodeId, name: null });
  }

  /** ПКМ: вузол — за `data-node-id`, ребро — за порядком `g[edge]` (той самий, що в `edges()`), інакше — канвас. */
  protected openContextMenu(event: MouseEvent): void {
    if (this.readonly()) return;
    const target = event.target instanceof Element ? event.target : null;
    if (target?.closest('input, textarea')) return;
    event.preventDefault();
    const anchor: ScreenPoint = { x: event.clientX, y: event.clientY };
    const nodeId = target?.closest('[data-node-id]')?.getAttribute('data-node-id');
    if (nodeId) {
      this.contextMenu.emit({ target: { kind: 'node', nodeId }, anchor });
      return;
    }
    const edgeEl = target?.closest('g[edge]');
    if (edgeEl) {
      const index = [...this.host.nativeElement.querySelectorAll('g[edge]')].indexOf(edgeEl);
      const edge = this.edges()[index];
      if (edge) {
        this.contextMenu.emit({ target: { kind: 'edge', edgeIndex: edgeIndex(edge) }, anchor });
        return;
      }
    }
    this.contextMenu.emit({ target: { kind: 'canvas' }, anchor });
  }

  protected openMissing(event: MouseEvent, nodeId: string, field: string): void {
    event.stopPropagation();
    this.missingClick.emit({ nodeId, field });
  }

  /** Перший рядок повідомлення — для картки (повний текст — у підказці). */
  protected firstLine(message: string): string {
    return message.split('\n', 1)[0];
  }

  protected missingTitle(fields: readonly string[]): string {
    return $localize`:@@canvas.missing.title:Не заповнено обов'язкові поля: ${fields.join(', ')}:fields:. Клацніть, щоб заповнити.`;
  }

  protected addOnEdge(event: MouseEvent, edgeIndex: number): void {
    event.stopPropagation();
    this.quickAdd.emit({ target: { kind: 'edge', edgeIndex }, anchor: anchorOf(event) });
  }

  /** Виділення змінила бібліотека — читаємо його з сигналів `selected` і віддаємо назовні. */
  protected selectionChanged(): void {
    const nodes = this.nodes()
      .filter((n) => n.selected?.())
      .map((n) => n.id);
    const edges = this.edges()
      .filter((e) => e.selected?.())
      .map(edgeIndex);
    this.syncEdgeLabels();
    const current = this.selection();
    if (sameItems(current.nodes, nodes) && sameItems(current.edges, edges)) return;
    this.selectionChange.emit({ nodes, edges });
  }

  /**
   * Мітка в центрі ребра: «+» лише на виділеному ребрі (мітка-foreignObject перехоплює кліки, тож на невиділеному
   * ребрі кнопки немає і клік по середині ребра виділяє його) і лічильник items після запуску (F3-52) — кліки крізь нього
   * проходять до ребра.
   */
  private edgeLabels(index: number, selected: boolean, count: number | undefined): NonNullable<StaticEdgeLabels> {
    const add = selected && !this.readonly();
    return add || count !== undefined ? { center: { type: 'html-template', data: { index, add, count } satisfies EdgeLabelData } } : {};
  }

  private syncEdgeLabels(): void {
    for (const edge of this.edges()) {
      const current = edge.edgeLabels?.().center;
      const data = current?.type === 'html-template' ? (current.data as EdgeLabelData | undefined) : undefined;
      const add = !!edge.selected?.() && !this.readonly();
      if (!!data?.add !== add) edge.edgeLabels?.set(this.edgeLabels(edgeIndex(edge), !!edge.selected?.(), data?.count));
    }
  }

  protected itemsLabel(count: number): string {
    return count === 1 ? $localize`:@@canvas.edge.item:1 item` : $localize`:@@canvas.edge.items:${count}:count: items`;
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

/** Палітра — праворуч від кнопки «+», на рівні її верху. */
function anchorOf(event: MouseEvent): ScreenPoint {
  const rect = (event.currentTarget as HTMLElement | null)?.getBoundingClientRect();
  return rect ? { x: rect.right + 8, y: rect.top } : { x: event.clientX, y: event.clientY };
}

function snap(value: number): number {
  return Math.round(value / 10) * 10;
}

/** Дані мітки ребра: індекс у `connections`, чи показувати «+», кількість items після запуску. */
interface EdgeLabelData {
  index: number;
  add: boolean;
  count?: number;
}

type StaticEdgeLabels = ReturnType<NonNullable<Edge['edgeLabels']>>;

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
