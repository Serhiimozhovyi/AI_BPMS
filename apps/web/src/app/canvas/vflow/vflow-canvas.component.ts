import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, booleanAttribute, computed, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { ConnectEndEvent, Connection, ConnectionSettings, Edge, Node, StaticNode, Vflow, VflowComponent, createEdges, createNodes } from 'ngx-vflow';
import { CanvasSelection, EMPTY_SELECTION, NewConnection } from '../canvas-edit';
import { CanvasModel, CanvasNode, CanvasNodeGroup, NodePosition, parsePosition } from '../canvas-model';
import { DEFAULT_NOTE_SIZE, MIN_NOTE_SIZE, NOTE_COLORS, noteColorToken, noteSize, renderNoteMarkdown } from '../canvas-notes';
import { groupLabel } from '../palette';
import { QuickAddRequest, ScreenPoint } from '../quick-add';
import { NodeRunStatus } from '../test-run';
import { nodeIcon } from '../node-icon';
import { Box, MAX_ZOOM, MIN_ZOOM, Viewport, contentBounds, fitViewport, loadViewport, openViewport, revealBox, saveViewport, viewportShowsContent } from '../viewport';
import { debounceTime, map } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NoteColor, WorkflowNote } from '../../workflows/workflows-api.service';

/** Дані картки вузла в ngx-vflow. */
interface CardData {
  kind: 'node';
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
  /** Час завершення кроку в запуску (F3-76), напр. `18:10:42`; `null` — немає. */
  runTime: string | null;
}

/** Дані нотатки на канвасі (F3-62): та сама `nodeHtml`-картка ngx-vflow, інший вигляд. */
interface NoteData {
  kind: 'note';
  note: WorkflowNote;
  /** Текст нотатки як HTML (Markdown-підмножина, екранований). */
  html: string;
  color: NoteColor;
  /** `var(--ac-…)` кольору — рамка й 15 %-тло. */
  colorVar: string;
  width: number;
  height: number;
}

type NodeData = CardData | NoteData;

/** Id вузла ngx-vflow для нотатки — з префіксом, щоб не збігся з id вузла сценарію. */
const NOTE_PREFIX = 'note:';

function noteNodeId(id: string): string {
  return NOTE_PREFIX + id;
}

/** Id нотатки з id вузла ngx-vflow; не нотатка — `null`. */
function noteIdOf(nodeId: string): string | null {
  return nodeId.startsWith(NOTE_PREFIX) ? nodeId.slice(NOTE_PREFIX.length) : null;
}

/** Зміна нотатки з канвасу (F3-62): колір чи розмір; текст іде через {@link NoteEditRequest}. */
export interface NoteChangeRequest {
  id: string;
  color?: NoteColor;
  size?: [number, number];
}

/** Редагування тексту нотатки завершено; `text: null` — скасовано (Escape). */
export interface NoteEditRequest {
  id: string;
  text: string | null;
}

/** Дія з тулбару над карткою вузла (F3-58). */
export type NodeToolbarAction = 'run' | 'toggle' | 'duplicate' | 'delete' | 'rename' | 'open';

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

/** ПКМ на канвасі (F3-58): що під вказівником і де відкрити меню; нотатка (F3-62) — окрема ціль. */
export type CanvasContextTarget =
  | { kind: 'node'; nodeId: string }
  | { kind: 'edge'; edgeIndex: number }
  | { kind: 'note'; noteId: string }
  | { kind: 'canvas' };

export interface CanvasContextMenuRequest {
  target: CanvasContextTarget;
  anchor: ScreenPoint;
  /** Точка меню в координатах канвасу (з кроком сітки) — туди ставиться нова нотатка. */
  at?: [number, number];
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
const NO_RUN_TIMES: ReadonlyMap<string, string> = new Map();

/** Найбільша мінікарта (F3-76), px; ngx-vflow малює її як 20 % полотна. */
const MINIMAP_MAX = { width: 180, height: 110 };
const MINIMAP_RATIO = 0.2;
const MINIMAP_KEY = 'openbp.minimap';
/** Відступ від краю полотна, коли вузол підсувається з-під панелі, px. */
const REVEAL_PADDING = 32;

export const NODE_WIDTH = 220;

function readMinimapOpen(): boolean {
  try {
    return localStorage.getItem(MINIMAP_KEY) === '1';
  } catch {
    return false;
  }
}

/** Крок кнопок «+»/«−» і межі масштабу. */
const ZOOM_STEP = 1.25;

const MIN_NODE_HEIGHT = 72;
/** Вертикальний крок між портами: стільки, щоб підписи портів не накладались. */
const PORT_STEP = 16;

/** Колір групи для картки вузла: highlight-гама AC-Kit; текст — суміш 45% кольору з `--bp-text` (≥ 4,5:1 на 15%-тлі). */
export const GROUP_COLORS: Record<CanvasNodeGroup, string> = {
  trigger: 'var(--ac-green)',
  logic: 'var(--ac-orange)',
  transform: 'var(--bp-accent)',
  integration: 'var(--ac-blue)',
  ai: 'var(--ac-violet)',
  unknown: 'var(--bp-text-muted)',
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
  host: { '[style.--minimap-scale]': 'minimapScale()' },
  template: `
    <vflow
      view="auto"
      [minZoom]="MIN_ZOOM"
      [maxZoom]="MAX_ZOOM"
      [nodes]="nodes()"
      [edges]="edges()"
      [optimization]="{ virtualization: virtualization(), virtualizationZoomThreshold: MIN_ZOOM }"
      background="#f2f6f6"
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
        @let d = ctx.data();
        @if (d.kind === 'note') {
          <div
            class="note"
            selectable
            [class.selected]="ctx.selected()"
            [class.note--editing]="editingNote() === d.note.id"
            [style.--note-color]="d.colorVar"
            [style.width.px]="noteWidth(d)"
            [style.height.px]="noteHeight(d)"
            [attr.data-note-id]="d.note.id"
            [attr.data-note-color]="d.color"
            (dblclick)="startNoteEdit($event, d.note.id)"
          >
            @if (editingNote() === d.note.id) {
              <textarea
                class="note__editor"
                data-testid="note-editor"
                i18n-aria-label="@@canvas.note.editor"
                aria-label="Текст нотатки (Markdown)"
                i18n-placeholder="@@canvas.note.placeholder"
                placeholder="Текст нотатки… Ctrl+Enter — зберегти, Escape — скасувати"
                [value]="d.note.text"
                (mousedown)="$event.stopPropagation()"
                (pointerdown)="$event.stopPropagation()"
                (dblclick)="$event.stopPropagation()"
                (keydown)="noteEditorKey($event, d.note.id)"
                (blur)="commitNoteEdit(d.note.id, $any($event.target).value)"
              ></textarea>
            } @else {
              <div class="note__body" data-testid="note-body" [innerHTML]="d.html"></div>
              @if (!d.note.text && !readonly()) {
                <span class="note__hint" i18n="@@canvas.note.hint">Двічі клацніть, щоб написати</span>
              }
            }
            @if (!readonly() && ctx.selected() && editingNote() !== d.note.id) {
              <div class="note__tools" role="toolbar" data-testid="note-colors" (mousedown)="$event.stopPropagation()" (pointerdown)="$event.stopPropagation()">
                @for (c of noteColors; track c.color) {
                  <button
                    type="button"
                    class="note__swatch"
                    [class.note__swatch--on]="c.color === d.color"
                    [style.--swatch]="'var(' + c.token + ')'"
                    [attr.data-testid]="'note-color-' + c.color"
                    [attr.aria-pressed]="c.color === d.color"
                    [title]="c.label"
                    (click)="pickNoteColor($event, d.note.id, c.color)"
                  ></button>
                }
                <button type="button" class="note__tool" data-testid="note-edit" i18n-title="@@canvas.note.edit" title="Редагувати (подвійний клік)" (click)="startNoteEdit($event, d.note.id)">✎</button>
                <button type="button" class="note__tool note__tool--danger" data-testid="note-delete" i18n-title="@@canvas.note.delete" title="Видалити нотатку (Delete)" (click)="deleteNote($event, d.note.id)">🗑</button>
              </div>
            }
            @if (!readonly()) {
              <span
                class="note__resize"
                data-testid="note-resize"
                i18n-title="@@canvas.note.resize"
                title="Розтягнути"
                (mousedown)="$event.stopPropagation()"
                (pointerdown)="startNoteResize($event, d.note.id)"
              ></span>
            }
          </div>
        } @else {
          <div
            class="node"
            selectable
            (dblclick)="act($event, ctx.node.id, 'open')"
            [class.selected]="ctx.selected()"
            [class.disabled]="d.node.disabled"
            [class.invalid]="d.errors.length > 0"
            [attr.data-invalid]="d.errors.length > 0 ? true : null"
            [attr.data-run-status]="d.runStatus"
            [attr.data-pinned]="d.pinned ? true : null"
            [class.run-success]="d.runStatus === 'success'"
            [class.run-error]="d.runStatus === 'error'"
            [class.run-skipped]="d.runStatus === 'skipped' || d.runStatus === 'cancelled'"
            [style.--node-color]="d.color"
            [style.height.px]="d.height"
            [attr.data-node-id]="ctx.node.id"
            [attr.data-node-type]="d.node.type"
          >
            @for (port of d.node.inputs; track $index; let i = $index) {
              <handle
                position="left"
                type="target"
                [id]="inputId(i)"
                [offsetY]="handleOffset(i, d.node.inputs.length, d.height)"
              />
              @if (d.node.inputs.length > 1) {
                <span
                  class="node__port node__port--in"
                  [style.top.px]="portTop(i, d.node.inputs.length, d.height)"
                  >{{ port }}</span
                >
              }
            }
            <div class="node__head">
              <span class="node__lead">
                <span class="material-symbols-outlined node__icon" aria-hidden="true" data-testid="node-icon">{{ icon(d.node) }}</span>
                <span class="node__group">{{ d.groupLabel }}</span>
              </span>
              @if (d.pinned) {
                <span class="node__pin" data-testid="node-pinned" role="img" title="Output закріплено" i18n-title="@@canvas.pinned">📌</span>
              }
              @if (d.runStatus === 'success') {
                <span class="node__run node__run--success" data-testid="node-run-badge" role="img" title="Виконано" i18n-title="@@canvas.run.success">✓</span>
              } @else if (d.runStatus === 'error') {
                <span class="node__run node__run--error" data-testid="node-run-badge" role="img" title="Помилка" i18n-title="@@canvas.run.error">✕</span>
              } @else if (d.runStatus === 'skipped' || d.runStatus === 'cancelled') {
                <span class="node__run node__run--skipped" data-testid="node-run-badge" role="img" title="Пропущено" i18n-title="@@canvas.run.skipped">–</span>
              }
              @if (d.runStatus && d.runTime) {
                <span class="node__time" data-testid="node-run-time">{{ d.runTime }}</span>
              }
              @if (d.errors.length > 0) {
                <span class="node__errors" data-testid="node-errors" role="img" [title]="d.errors.join('\n')"
                  >! {{ d.errors.length }}</span
                >
              }
            </div>
            @if (!readonly()) {
              <div class="node__toolbar" data-testid="node-toolbar" role="toolbar" (dblclick)="$event.stopPropagation()" (mousedown)="$event.stopPropagation()" (pointerdown)="$event.stopPropagation()">
                <button type="button" class="node__tool" data-testid="node-tool-run" i18n-title="@@canvas.tool.run" title="Виконати вузол" (click)="act($event, ctx.node.id, 'run')">▶</button>
                <button
                  type="button"
                  class="node__tool"
                  data-testid="node-tool-toggle"
                  [title]="d.node.disabled ? enableLabel : disableLabel"
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
                (dblclick)="$event.stopPropagation()"
                i18n-aria-label="@@canvas.rename.label"
                aria-label="Нова назва вузла"
                [value]="d.node.name"
                (mousedown)="$event.stopPropagation()"
                (pointerdown)="$event.stopPropagation()"
                (keydown.enter)="commitRename(ctx.node.id, $any($event.target).value)"
                (keydown.escape)="cancelRename(ctx.node.id)"
                (blur)="commitRename(ctx.node.id, $any($event.target).value)"
              />
            } @else {
              <div class="node__name" [title]="d.node.name">{{ d.node.name }}</div>
            }
            @if (d.runError; as runError) {
              <div class="node__run-error" data-testid="node-run-error" [title]="runError">{{ firstLine(runError) }}</div>
            } @else if (d.node.subtitle) {
              <div class="node__type" [title]="d.node.subtitle">{{ d.node.subtitle }}</div>
            }
            @if (d.missing.length > 0) {
              <button
                type="button"
                class="node__missing"
                data-testid="node-missing"
                [title]="missingTitle(d.missing)"
                (mousedown)="$event.stopPropagation()"
                (pointerdown)="$event.stopPropagation()"
                (click)="openMissing($event, ctx.node.id, d.missing[0])"
                i18n="@@canvas.missing"
              >
                не заповнено: {{ d.missing.length }}
              </button>
            }
            @for (port of d.node.outputs; track $index; let i = $index) {
              <handle
                position="right"
                type="source"
                [id]="outputId(i)"
                [offsetY]="handleOffset(i, d.node.outputs.length, d.height)"
              />
              @if (d.node.outputs.length > 1) {
                <span
                  class="node__port node__port--out"
                  [class.node__port--error]="port === 'error'"
                  [style.top.px]="portTop(i, d.node.outputs.length, d.height)"
                  >{{ port }}</span
                >
              }
            }
            @if ((ctx.selected() || firstStepHint(d.node)) && !readonly()) {
              @if (firstStepHint(d.node)) {
                <span
                  class="pointer-events-none absolute left-[calc(100%+48px)] -translate-y-1/2 whitespace-nowrap text-[12px] text-ac-kit"
                  data-testid="first-step-hint"
                  [style.top.px]="portTop(0, d.node.outputs.length, d.height)"
                  i18n="@@canvas.firstStepHint"
                  >Додайте наступний крок</span
                >
              }
              @for (port of d.node.outputs; track $index; let i = $index) {
                <button
                  type="button"
                  class="node__add"
                  data-testid="port-add"
                  [attr.data-output]="i"
                  [style.top.px]="portTop(i, d.node.outputs.length, d.height)"
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
        }
      </ng-template>
      <ng-template edgeLabelHtml let-ctx>
        <span class="flex flex-col items-center gap-0.5">
          @if (ctx.label.data.count !== undefined) {
            <span class="edge__count pointer-events-none block whitespace-nowrap rounded-md bg-[var(--bp-bg)] px-[3px] text-[10px] leading-[14px] text-[var(--bp-text-muted)]" data-testid="edge-count" [attr.data-edge]="ctx.label.data.index">{{ itemsLabel(ctx.label.data.count) }}</span>
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
      @if (minimapOpen()) {
        <mini-map position="bottom-left" maskColor="rgb(242 246 246 / 0.7)" strokeColor="#b4c4c3" [scaleOnHover]="false" />
      }
    </vflow>
    <div class="controls" role="toolbar" i18n-aria-label="@@canvas.controls" aria-label="Керування канвасом" data-testid="canvas-controls">
      @if (!readonly()) {
        <button type="button" class="controls__btn controls__btn--wide" data-testid="canvas-arrange" i18n-title="@@canvas.arrange.hint" title="Розставити вузли зліва направо за з'єднаннями" (click)="arrangeClicked()" i18n="@@canvas.arrange">Вирівняти</button>
      }
      @if (lastRunToggle() !== null) {
        <button type="button" class="controls__btn controls__btn--wide" data-testid="canvas-last-run" [attr.aria-pressed]="lastRunToggle()" [class.controls__btn--on]="lastRunToggle()" i18n-title="@@canvas.lastRun.hint" title="Показувати на вузлах результат останнього запуску" (click)="lastRunToggled.emit(!lastRunToggle())" i18n="@@canvas.lastRun">Останній запуск</button>
      }
      <button type="button" class="controls__btn" data-testid="canvas-minimap" [attr.aria-pressed]="minimapOpen()" [class.controls__btn--on]="minimapOpen()" i18n-title="@@canvas.minimap" title="Мінікарта" (click)="toggleMinimap()">▣</button>
      <button type="button" class="controls__btn" data-testid="canvas-fit" i18n-title="@@canvas.fit" title="Показати весь сценарій (1)" i18n-aria-label="@@canvas.fit.label" aria-label="Показати весь сценарій" (click)="fit()">⤢</button>
      <button type="button" class="controls__btn controls__btn--zoom" data-testid="canvas-zoom-level" i18n-title="@@canvas.zoomReset" title="Масштаб: клік — 100 % (0)" (click)="zoomReset()">{{ zoomPercent() }}%</button>
      <button type="button" class="controls__btn" data-testid="canvas-zoom-in" i18n-title="@@canvas.zoomIn" title="Збільшити (+)" i18n-aria-label="@@canvas.zoomIn.label" aria-label="Збільшити" (click)="zoomBy(ZOOM_STEP)">+</button>
      <button type="button" class="controls__btn" data-testid="canvas-zoom-out" i18n-title="@@canvas.zoomOut" title="Зменшити (−)" i18n-aria-label="@@canvas.zoomOut.label" aria-label="Зменшити" (click)="zoomBy(1 / ZOOM_STEP)">−</button>
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
      /* Мінікарта ≤ 180×110 px: ngx-vflow дає 20 % полотна, тож стискаємо від лівого нижнього кута. */
      transform-box: view-box;
      transform-origin: 10px calc(100% - 10px);
      transform: scale(var(--minimap-scale, 0.7));
    }

    /* Кнопки канвасу (F3-58) — внизу праворуч, над полотном. */
    .controls {
      position: absolute;
      right: 12px;
      bottom: 12px;
      display: flex;
      gap: 4px;
    }

    .controls__btn--zoom {
      min-width: 52px;
      font-variant-numeric: tabular-nums;
    }

    .controls__btn {
      min-width: 30px;
      height: 30px;
      padding: 0 6px;
      border: 1px solid var(--bp-bg);
      border-radius: 6px;
      background: var(--bp-white);
      color: var(--bp-text-muted);
      font: 500 14px/1 Ubuntu, sans-serif;
      cursor: pointer;
      box-shadow: 0 1px 3px rgb(12 43 48 / 0.1);
    }

    .controls__btn--wide {
      padding: 0 10px;
      font-size: 13px;
    }

    .controls__btn--on {
      border-color: var(--bp-primary);
      background: var(--bp-tonal);
      color: var(--bp-primary);
    }

    .controls__btn:hover {
      border-color: var(--bp-primary);
      color: var(--bp-primary);
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
      background: var(--bp-white);
      box-shadow: 0 1px 4px rgb(12 43 48 / 0.12);
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

    .node__run--skipped {
      color: var(--bp-text-muted);
    }

    .node__time {
      margin-right: 4px;
      font-size: 10px;
      color: var(--bp-text-muted);
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
      color: var(--bp-white);
      font-weight: 600;
      cursor: help;
    }

    .node__group {
      /* Яскравий колір групи на білому не проходить контраст AA — підмішуємо Vivid (F3-86). */
      color: color-mix(in srgb, var(--node-color) 45%, var(--bp-text));
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
      font-size: 14px;
      font-weight: 500;
    }

    .node__lead {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      min-width: 0;
    }

    .node__icon {
      font-size: 16px;
      color: color-mix(in srgb, var(--node-color) 45%, var(--bp-text));
    }

    .node__type {
      font-size: 11px;
      color: var(--bp-text-muted);
    }

    /* Тулбар над карткою (F3-58): при наведенні і на виділеному вузлі; за межею картки, як «+» біля порту. */
    .node__toolbar {
      position: absolute;
      bottom: calc(100% + 4px);
      right: 0;
      display: none;
      gap: 2px;
      padding: 2px;
      border: 1px solid var(--bp-bg);
      border-radius: 6px;
      background: var(--bp-white);
      box-shadow: 0 1px 4px rgb(12 43 48 / 0.12);
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
      color: var(--bp-text-muted);
      font: 13px/1 Ubuntu, sans-serif;
      cursor: pointer;
    }

    .node__tool:hover,
    .node__tool:focus-visible {
      background: var(--bp-bg);
      color: var(--bp-primary);
    }

    .node__tool--danger:hover {
      color: var(--ac-red);
    }

    .node__rename {
      box-sizing: border-box;
      width: 100%;
      margin-top: 2px;
      padding: 1px 4px;
      border: 1px solid var(--bp-primary);
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
      background: var(--bp-white);
      color: var(--ac-orange);
      font: 700 12px/16px var(--bp-font-family);
      white-space: nowrap;
      cursor: pointer;
    }

    .node__port {
      position: absolute;
      transform: translateY(-50%);
      font-size: 10px;
      color: var(--bp-text-muted);
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
      border: 1.5px solid var(--bp-primary);
      border-radius: 50%;
      background: var(--bp-white);
      color: var(--bp-primary);
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
      background: var(--bp-primary);
      color: var(--bp-white);
    }

    /* Нотатка (F3-62): Highlight-колір AC-Kit — рамка і 15 %-тло; ngx-vflow бере розмір вузла з картки, тож він явний. */
    .note {
      position: relative;
      box-sizing: border-box;
      padding: 8px 10px;
      border: 1.5px solid var(--note-color);
      border-radius: 8px;
      background: color-mix(in srgb, var(--note-color) 15%, white);
      color: var(--bp-text);
      font: 13px/1.4 Ubuntu, sans-serif;
      user-select: none;
    }

    .note.selected {
      box-shadow: 0 0 0 2px var(--note-color);
    }

    .note--editing {
      background: var(--bp-white);
      user-select: text;
    }

    .note__body {
      height: 100%;
      overflow: hidden;
      overflow-wrap: anywhere;
    }

    .note__body :is(h1, h2, h3) {
      margin: 0 0 4px;
      font: 600 14px/1.3 Ubuntu, sans-serif;
    }

    .note__body h1 {
      font-size: 16px;
    }

    .note__body p {
      margin: 0 0 6px;
    }

    .note__body :is(ul, ol) {
      margin: 0 0 6px;
      padding-left: 18px;
    }

    .note__body code {
      padding: 0 3px;
      border-radius: 3px;
      background: rgb(12 43 48 / 0.08);
      font-size: 12px;
    }

    .note__body a {
      color: var(--bp-primary);
    }

    .note__hint {
      position: absolute;
      inset: 8px 10px;
      color: var(--bp-text-muted);
      font-size: 12px;
      pointer-events: none;
    }

    .note__editor {
      display: block;
      box-sizing: border-box;
      width: 100%;
      height: 100%;
      padding: 0;
      border: 0;
      background: none;
      color: inherit;
      font: inherit;
      resize: none;
      outline: none;
    }

    /* Палітра над виділеною нотаткою — за межею картки, як тулбар вузла. */
    .note__tools {
      position: absolute;
      bottom: calc(100% + 4px);
      left: 0;
      display: flex;
      align-items: center;
      gap: 4px;
      padding: 3px 4px;
      border: 1px solid var(--bp-bg);
      border-radius: 6px;
      background: var(--bp-white);
      box-shadow: 0 1px 4px rgb(12 43 48 / 0.12);
    }

    .note__swatch {
      width: 16px;
      height: 16px;
      padding: 0;
      border: 2px solid transparent;
      border-radius: 50%;
      background: var(--swatch);
      cursor: pointer;
    }

    .note__swatch--on,
    .note__swatch:hover {
      border-color: var(--bp-text);
    }

    .note__tool {
      width: 22px;
      height: 20px;
      padding: 0;
      border: 0;
      border-radius: 4px;
      background: none;
      color: var(--bp-text-muted);
      font: 13px/1 Ubuntu, sans-serif;
      cursor: pointer;
    }

    .note__tool:hover {
      background: var(--bp-bg);
      color: var(--bp-primary);
    }

    .note__tool--danger:hover {
      color: var(--ac-red);
    }

    .note__resize {
      position: absolute;
      right: 0;
      bottom: 0;
      width: 14px;
      height: 14px;
      border-right: 2px solid var(--note-color);
      border-bottom: 2px solid var(--note-color);
      border-bottom-right-radius: 6px;
      cursor: nwse-resize;
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

  /** Час завершення кроку останнього запуску за id вузла (F3-76) — підпис на картці поруч зі значком. */
  readonly runTimes = input<ReadonlyMap<string, string>>(NO_RUN_TIMES);

  /** Ключ збереження положення полотна (F3-76, id сценарію); `null` — не запам'ятовувати. */
  readonly viewportKey = input<string | null>(null);

  /** Перемикач «Останній запуск» у керуванні канвасом (F3-76): `null` — кнопки немає, інакше — чи увімкнено. */
  readonly lastRunToggle = input<boolean | null>(null);

  readonly lastRunToggled = output<boolean>();

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

  /** Нотатки сценарію (F3-62) — картки без портів поруч із вузлами. */
  readonly notes = input<readonly WorkflowNote[]>([]);

  /** Нотатка з відкритим полем тексту (F3-62); `null` — немає. */
  readonly editingNote = input<string | null>(null);

  /** Нотатки, які користувач перетягнув у нову позицію. */
  readonly notesMoved = output<NodePosition[]>();

  /** Колір чи розмір нотатки змінено з канвасу. */
  readonly noteChange = output<NoteChangeRequest>();

  /** Текст нотатки збережено або редагування скасовано. */
  readonly noteEdit = output<NoteEditRequest>();

  /** Подвійний клік чи «Редагувати» на нотатці — редактор відкриває поле тексту. */
  readonly noteEditStart = output<string>();

  /** «Видалити» з палітри нотатки. */
  readonly noteDelete = output<string>();

  protected readonly noteColors = NOTE_COLORS;
  /** Нотатка, яку зараз розтягують за кут, і її проміжний розмір; `null` — ніяку. */
  protected readonly resizing = signal<{ id: string; width: number; height: number } | null>(null);
  /** Нотатка, для якої завершення редагування вже віддано (Ctrl+Enter + blur не дублюються). */
  private noteEditDone: string | null = null;

  private readonly flow = viewChild(VflowComponent);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  /** Мінікарта згорнута за замовчуванням (F3-76); вибір користувача запам'ятовується. */
  protected readonly minimapOpen = signal(readMinimapOpen());

  /** Розмір полотна: з нього рахуються масштаб мінікарти й вписування сценарію. */
  private readonly size = signal({ width: 0, height: 0 });

  /** Масштаб, до якого стискається мінікарта, щоб не перевищити {@link MINIMAP_MAX}. */
  protected readonly minimapScale = computed(() => {
    const { width, height } = this.size();
    if (!width || !height) return 0.7;
    return Math.min(1, MINIMAP_MAX.width / (width * MINIMAP_RATIO), MINIMAP_MAX.height / (height * MINIMAP_RATIO));
  });

  /** Id сценарію, для якого початкове положення вже застосовано; нові вузли після порожнього сценарію вписуються знову. */
  private viewAppliedFor: string | null | undefined;
  private viewHadNodes = false;
  private viewSubscribed = false;
  private knownNodeIds: ReadonlySet<string> = new Set();
  /** Положення, яке виставили ми (вписування або відновлення), — його не зберігаємо як вибір користувача. */
  private appliedView: Viewport | null = null;
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
    const runTimes = this.runTimes();
    const draggable = !this.readonly();
    // Виділення лише при перебудові; далі його синхронізує ефект нижче, без перебудови всіх вузлів.
    const selection = untracked(this.selection);
    const selected = new Set(selection.nodes);
    const selectedNotes = new Set(selection.notes ?? []);
    const cards = this.model().nodes.map((n): StaticNode<NodeData> => {
      const height = nodeHeight(n);
      return {
        id: n.id,
        type: 'html-template',
        point: { x: n.x, y: n.y },
        width: NODE_WIDTH,
        height,
        draggable,
        selected: selected.has(n.id),
        data: { kind: 'node', node: n, color: GROUP_COLORS[n.group], groupLabel: groupLabel(n.group), height, errors: errors.get(n.id) ?? [], runStatus: run.get(n.id) ?? null, pinned: pinned.has(n.id), runError: runErrors.get(n.id) ?? null, missing: missing.get(n.id) ?? [], runTime: runTimes.get(n.id) ?? null },
      };
    });
    // Нотатки (F3-62) — перед вузлами, щоб картка вузла малювалась поверх нотатки, яка її перекриває.
    const notes = this.notes().map((note): StaticNode<NodeData> => {
      const [width, height] = noteSize(note);
      const { x, y } = parsePosition(note.position);
      const color = (NOTE_COLORS.find((c) => c.color === note.color) ?? NOTE_COLORS[0]).color;
      return {
        id: noteNodeId(note.id),
        type: 'html-template',
        point: { x, y },
        width,
        height,
        draggable,
        selected: selectedNotes.has(note.id),
        data: { kind: 'note', note, html: renderNoteMarkdown(note.text ?? ''), color, colorVar: `var(${noteColorToken(note.color)})`, width, height },
      };
    });
    return createNodes<NodeData>([...notes, ...cards]);
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
    if (typeof ResizeObserver !== 'undefined') {
      const observer = new ResizeObserver((entries) => {
        const rect = entries[0]?.contentRect;
        if (rect) this.size.set({ width: rect.width, height: rect.height });
      });
      observer.observe(this.host.nativeElement);
      inject(DestroyRef).onDestroy(() => observer.disconnect());
    }

    // Початкове положення (F3-76): збережене для цього сценарію або «вписати все» з відступом 48 px, масштаб ≤ 100 %.
    effect(() => {
      const flow = this.flow();
      if (!flow || !flow.initialized()) return;
      const key = this.viewportKey();
      const hasContent = this.model().nodes.length > 0 || this.notes().length > 0;
      untracked(() => this.updateView(flow, key, hasContent));
    });

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

    // Поле тексту нотатки (F3-62): фокус і курсор у кінець, щойно воно з'явилося.
    effect(() => {
      const id = this.editingNote();
      untracked(() => {
        this.noteEditDone = null;
        if (!id) return;
        let tries = 0;
        const focus = () => {
          const el = this.host.nativeElement.querySelector<HTMLTextAreaElement>(`[data-note-id="${CSS.escape(id)}"] [data-testid="note-editor"]`);
          if (el) {
            el.focus();
            el.setSelectionRange(el.value.length, el.value.length);
          } else if (++tries < 10) {
            requestAnimationFrame(focus);
          }
        };
        requestAnimationFrame(focus);
      });
    });

    // Виділення ззовні (вставка, видалення, undo) → сигнали `selected` наявних вузлів, нотаток і ребер.
    effect(() => {
      const selection = this.selection();
      const nodeIds = new Set([...selection.nodes, ...(selection.notes ?? []).map(noteNodeId)]);
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

  /** Підказка «Додайте наступний крок» біля порту тригера (F3-59) — доки в сценарії немає жодного з'єднання. */
  protected firstStepHint(node: CanvasNode): boolean {
    return node.inputs.length === 0 && node.outputs.length > 0 && this.model().edges.length === 0;
  }

  protected addFromPort(event: MouseEvent, node: string, output: number): void {
    event.stopPropagation();
    this.quickAdd.emit({ target: { kind: 'port', from: { node, output } }, anchor: anchorOf(event) });
  }

  protected readonly ZOOM_STEP = ZOOM_STEP;
  /** Нижня межа масштабу: ngx-vflow малює вузли сірими заглушками при масштабі менше за 0.5, а вписаний довгий сценарій має лишатись читабельним. */
  protected readonly icon = (node: CanvasNode): string => nodeIcon(node.type, node.group);
  protected readonly MIN_ZOOM = MIN_ZOOM;
  protected readonly MAX_ZOOM = MAX_ZOOM;

  protected arrangeClicked(): void {
    if (this.readonly()) return;
    this.arrange.emit();
    // Нові позиції приходять через `model` — fit, коли вузли вже перебудовано.
    requestAnimationFrame(() => requestAnimationFrame(() => this.fit()));
  }

  /** Перший вигляд сценарію і повторне вписування, коли порожній сценарій отримав вузли («Почати з прикладу»). */
  private updateView(flow: VflowComponent, key: string | null, hasContent: boolean): void {
    if (!this.viewSubscribed) {
      this.viewSubscribed = true;
      this.zoom.set(flow.viewport().zoom);
      flow.viewportChange$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((v) => this.zoom.set(v.zoom));
      flow.viewportChange$
        .pipe(
          // Положення, яке ми самі щойно виставили (вписування), не вважаємо вибором користувача.
          map((v) => ({ v, own: this.isAppliedView(v) })),
          debounceTime(400),
          takeUntilDestroyed(this.destroyRef),
        )
        .subscribe(({ v, own }) => {
          const current = this.viewportKey();
          if (!current || !this.viewHadNodes || own) return;
          saveViewport(current, { x: v.x, y: v.y, zoom: v.zoom });
        });
    }
    const ids = new Set(this.model().nodes.map((n) => n.id));
    const previousIds = this.knownNodeIds;
    this.knownNodeIds = ids;
    if (!hasContent) {
      this.viewHadNodes = false;
      return;
    }
    if (this.viewHadNodes) {
      this.revealAdded(flow, [...ids].filter((id) => !previousIds.has(id)));
      return;
    }
    const firstOpen = this.viewAppliedFor !== key;
    this.viewHadNodes = true;
    this.viewAppliedFor = key;
    this.placeView(flow, firstOpen && key ? loadViewport(key) : null);
  }

  /** Тригер (вузол без входів, лівіший) або найлівіший вузол — з нього починається показ довгого сценарію. */
  private startBox(): Box | null {
    const nodes = this.model().nodes;
    const triggers = nodes.filter((n) => n.inputs.length === 0);
    const pool = triggers.length > 0 ? triggers : nodes;
    const first = pool.reduce<(typeof nodes)[number] | null>((best, n) => (!best || n.x < best.x ? n : best), null);
    return first ? { x: first.x, y: first.y, width: NODE_WIDTH, height: nodeHeight(first) } : null;
  }

  /** `open` — при відкритті: не дрібніше за `OPEN_MIN_ZOOM`; `all` — вписати все (новий вузол за межами екрана). */
  private placeView(flow: VflowComponent, saved: Viewport | null, mode: 'open' | 'all' = 'open'): void {
    let tries = 0;
    const apply = () => {
      const rect = this.host.nativeElement.getBoundingClientRect();
      // Полотно ще не має розміру (вкладка прихована, перший кадр) — чекаємо.
      if ((rect.width < 50 || rect.height < 50) && ++tries < 30) {
        requestAnimationFrame(apply);
        return;
      }
      const bounds = contentBounds(this.contentBoxes());
      if (!bounds) return;
      const size = { width: rect.width, height: rect.height };
      const start = this.startBox();
      const fitted = mode === 'open' && start ? openViewport(bounds, start, size) : fitViewport(bounds, size);
      const view = saved && viewportShowsContent(saved, bounds, size) ? saved : fitted;
      this.appliedView = view;
      flow.viewportTo(view);
    };
    requestAnimationFrame(() => requestAnimationFrame(apply));
  }

  /** Новий вузол (з палітри, вставка, undo) опинився за межами екрана — вписуємо весь сценарій, щоб його було видно. */
  private revealAdded(flow: VflowComponent, addedIds: string[]): void {
    if (addedIds.length === 0) return;
    const rect = this.host.nativeElement.getBoundingClientRect();
    if (rect.width < 50 || rect.height < 50) return;
    const view = flow.viewport();
    const visible: Box = { x: -view.x / view.zoom, y: -view.y / view.zoom, width: rect.width / view.zoom, height: rect.height / view.zoom };
    const added = new Set(addedIds);
    const boxes = this.model().nodes.filter((n) => added.has(n.id)).map((n): Box => ({ x: n.x, y: n.y, width: NODE_WIDTH, height: nodeHeight(n) }));
    const seen = boxes.some((b) => b.x < visible.x + visible.width && b.x + b.width > visible.x && b.y < visible.y + visible.height && b.y + b.height > visible.y);
    if (!seen) this.placeView(flow, null, 'all');
  }

  /**
   * Вузол (виділений, а біля нього відкрилася панель) не має ховатися за краєм полотна (F3-88): зсуваємо полотно, масштаб лишається.
   * Панель змінює розмір полотна за кадр-два, тому міряємо після них.
   */
  revealNode(nodeId: string): void {
    const flow = this.flow();
    if (!flow) return;
    requestAnimationFrame(() =>
      requestAnimationFrame(() => {
        const node = this.model().nodes.find((n) => n.id === nodeId);
        const rect = this.host.nativeElement.getBoundingClientRect();
        if (!node || rect.width < 50 || rect.height < 50) return;
        const view = revealBox(flow.viewport(), { x: node.x, y: node.y, width: NODE_WIDTH, height: nodeHeight(node) }, { width: rect.width, height: rect.height }, REVEAL_PADDING);
        if (view) flow.viewportTo(view);
      }),
    );
  }

  private isAppliedView(v: Viewport): boolean {
    const a = this.appliedView;
    return !!a && Math.abs(v.x - a.x) < 0.5 && Math.abs(v.y - a.y) < 0.5 && Math.abs(v.zoom - a.zoom) < 0.001;
  }

  /** Прямокутники вузлів і нотаток у координатах сценарію. */
  private contentBoxes(): Box[] {
    const cards = this.model().nodes.map((n): Box => ({ x: n.x, y: n.y, width: NODE_WIDTH, height: nodeHeight(n) }));
    const notes = this.notes().map((note): Box => {
      const [width, height] = noteSize(note);
      const { x, y } = parsePosition(note.position);
      return { x, y, width, height };
    });
    return [...cards, ...notes];
  }

  protected toggleMinimap(): void {
    const open = !this.minimapOpen();
    this.minimapOpen.set(open);
    try {
      localStorage.setItem(MINIMAP_KEY, open ? '1' : '0');
    } catch {
      // Сховище недоступне — вибір діє до перезавантаження.
    }
  }

  /** Поточний масштаб, % — у групі керування (F3-90). */
  protected readonly zoom = signal(1);
  protected readonly zoomPercent = computed(() => Math.round(this.zoom() * 100));

  /** «Показати весь сценарій»; з клавіші `1` теж. */
  fit(): void {
    this.flow()?.fitView({ padding: 0.15, duration: 200 });
  }

  /** Масштаб у `factor` разів (з клавіш `+`/`−` і кнопок), у межах `MIN_ZOOM…MAX_ZOOM`. */
  zoomBy(factor: number): void {
    const flow = this.flow();
    if (!flow) return;
    flow.zoomTo(Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, flow.viewport().zoom * factor)));
  }

  /** Масштаб 100 % (клавіша `0`, клік по відсотку). */
  zoomReset(): void {
    this.flow()?.zoomTo(1);
  }

  /** Крок масштабу для клавіш. */
  zoomStep(direction: 'in' | 'out'): void {
    this.zoomBy(direction === 'in' ? ZOOM_STEP : 1 / ZOOM_STEP);
  }

  protected readonly disableLabel = $localize`:@@canvas.tool.disable:Вимкнути (D)`;
  protected readonly enableLabel = $localize`:@@canvas.tool.enable:Увімкнути (D)`;

  protected act(event: MouseEvent, nodeId: string, action: NodeToolbarAction): void {
    event.stopPropagation();
    // «Відкрити» лише показує панель, тож доступне і в режимі перегляду.
    if (this.readonly() && action !== 'open') return;
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
    const noteId = target?.closest('[data-note-id]')?.getAttribute('data-note-id');
    if (noteId) {
      this.contextMenu.emit({ target: { kind: 'note', noteId }, anchor });
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
    this.contextMenu.emit({ target: { kind: 'canvas' }, anchor, at: this.flowPoint(anchor) });
  }

  /** Точка екрана → координати канвасу з кроком сітки; без vflow чи SVG-геометрії (jsdom у тестах) — як є. */
  private flowPoint(point: ScreenPoint): [number, number] {
    const flow = this.flow();
    if (!flow) return [snap(point.x), snap(point.y)];
    try {
      const p = flow.documentPointToFlowPoint(point);
      return [snap(p.x), snap(p.y)];
    } catch {
      return [snap(point.x), snap(point.y)];
    }
  }

  /** Лівий верхній кут нотатки типового розміру, щоб вона стала в центрі видимої частини канвасу (F3-62, `Shift+S`). */
  viewportCenter(): [number, number] {
    const rect = this.host.nativeElement.getBoundingClientRect();
    const [x, y] = this.flowPoint({ x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 });
    return [snap(x - DEFAULT_NOTE_SIZE[0] / 2), snap(y - DEFAULT_NOTE_SIZE[1] / 2)];
  }

  // ---------- Нотатки (F3-62) ----------

  protected startNoteEdit(event: Event, id: string): void {
    event.stopPropagation();
    if (this.readonly() || this.editingNote() === id) return;
    this.noteEditStart.emit(id);
  }

  protected noteEditorKey(event: KeyboardEvent, id: string): void {
    // Клавіші належать полю: канвас їх не бачить (редактор не перехоплює keydown у textarea), але Escape і Ctrl+Enter — наші.
    event.stopPropagation();
    if (event.key === 'Escape') {
      event.preventDefault();
      this.cancelNoteEdit(id);
    } else if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      this.commitNoteEdit(id, (event.target as HTMLTextAreaElement).value);
    }
  }

  protected commitNoteEdit(id: string, text: string): void {
    // Ctrl+Enter і наступний blur приходять обидва — віддаємо лише перший.
    if (this.editingNote() !== id || this.noteEditDone === id) return;
    this.noteEditDone = id;
    this.noteEdit.emit({ id, text });
  }

  protected cancelNoteEdit(id: string): void {
    if (this.editingNote() !== id || this.noteEditDone === id) return;
    this.noteEditDone = id;
    this.noteEdit.emit({ id, text: null });
  }

  protected pickNoteColor(event: MouseEvent, id: string, color: NoteColor): void {
    event.stopPropagation();
    if (this.readonly()) return;
    this.noteChange.emit({ id, color });
  }

  protected deleteNote(event: MouseEvent, id: string): void {
    event.stopPropagation();
    if (this.readonly()) return;
    this.noteDelete.emit(id);
  }

  protected noteWidth(d: NoteData): number {
    const r = this.resizing();
    return r?.id === d.note.id ? r.width : d.width;
  }

  protected noteHeight(d: NoteData): number {
    const r = this.resizing();
    return r?.id === d.note.id ? r.height : d.height;
  }

  /** Кут нотатки: тягнемо — картка росте наживо (ngx-vflow бере розмір вузла з неї), відпустили — одна зміна визначення. */
  protected startNoteResize(event: PointerEvent, id: string): void {
    event.stopPropagation();
    event.preventDefault();
    if (this.readonly()) return;
    const note = this.notes().find((n) => n.id === id);
    if (!note) return;
    const zoom = this.flow()?.viewport().zoom || 1;
    const [startWidth, startHeight] = noteSize(note);
    const start = { x: event.clientX, y: event.clientY };
    let size: [number, number] = [startWidth, startHeight];
    const move = (e: PointerEvent) => {
      size = [
        Math.max(MIN_NOTE_SIZE[0], startWidth + (e.clientX - start.x) / zoom),
        Math.max(MIN_NOTE_SIZE[1], startHeight + (e.clientY - start.y) / zoom),
      ];
      this.resizing.set({ id, width: size[0], height: size[1] });
    };
    const up = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      document.removeEventListener('pointercancel', up);
      this.resizing.set(null);
      const snapped: [number, number] = [snap(size[0]), snap(size[1])];
      if (snapped[0] !== startWidth || snapped[1] !== startHeight) this.noteChange.emit({ id, size: snapped });
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', up);
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
    const selectedIds = this.nodes()
      .filter((n) => n.selected?.())
      .map((n) => n.id);
    const nodes = selectedIds.filter((id) => noteIdOf(id) === null);
    const notes = selectedIds.map(noteIdOf).filter((id): id is string => id !== null);
    const edges = this.edges()
      .filter((e) => e.selected?.())
      .map(edgeIndex);
    this.syncEdgeLabels();
    const current = this.selection();
    if (sameItems(current.nodes, nodes) && sameItems(current.edges, edges) && sameItems(current.notes ?? [], notes)) return;
    this.selectionChange.emit(notes.length ? { nodes, edges, notes } : { nodes, edges });
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
    const notes = new Map(this.notes().map((n) => [n.id, parsePosition(n.position)]));
    const moved: NodePosition[] = [];
    const movedNotes: NodePosition[] = [];
    for (const node of this.nodes()) {
      const point = node.point();
      const noteId = noteIdOf(node.id);
      if (noteId !== null) {
        const original = notes.get(noteId);
        if (original && (original.x !== point.x || original.y !== point.y)) movedNotes.push({ id: noteId, x: point.x, y: point.y });
        continue;
      }
      const original = model.get(node.id);
      if (original && (original.x !== point.x || original.y !== point.y)) {
        moved.push({ id: node.id, x: point.x, y: point.y });
      }
    }
    if (moved.length > 0) this.nodesMoved.emit(moved);
    if (movedNotes.length > 0) this.notesMoved.emit(movedNotes);
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
