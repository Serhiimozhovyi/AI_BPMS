import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, booleanAttribute, computed, effect, input, output, signal, untracked, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { toCanvasModel } from '../canvas/canvas-model';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessConnector, BusinessEdgeKind, BusinessStepKind, buildBusinessView } from './business-model';
import {
  addExternalStep,
  addLane,
  linkExternalStep,
  moveLane,
  moveStepToLane,
  removeExternalStep,
  renameLane,
  setEdgeLabel,
  setLaneDescription,
  setStepHidden,
  setStepSubtitle,
  setStepTitle,
  toggleLinkDirection,
} from './business-edit';
import { BusinessLayout, DIAMOND_OVERHANG, LANE_GAP, LANE_LABEL_WIDTH, PlacedEdge, PlacedLane, PlacedStep, layoutBusinessView } from './business-layout';
import { exportPng, exportSvg } from './business-export';
import { BusinessRun, BusinessRunStatus, businessRunStatuses } from './business-run';

/** Позначки кроку в лівому верхньому куті: тригер, очікування, цикл. */
const KIND_MARKS: Partial<Record<BusinessStepKind, string>> = { trigger: '⚡', wait: '⏱', loop: '↻' };

/** Скільки символів у рядку назви кроку й максимум рядків — решта обрізається «…». */
const LINE_CHARS = 22;
const MAX_LINES = 3;

const MIN_ZOOM = 0.3;
const MAX_ZOOM = 2.5;
const PAD = 16;

/** Подвійний клік до цього часу після кліку — не відкриваємо вузол, а редагуємо назву. */
const DBLCLICK_MS = 250;

interface LegendEntry {
  id: string;
  label: string;
}

type EditField = 'title' | 'subtitle' | 'edge' | 'lane-name' | 'lane-desc';

/** Поле редагування поверх схеми: координати — у пікселях вікна схеми. */
interface InlineEdit {
  field: EditField;
  id: string;
  value: string;
  x: number;
  y: number;
  width: number;
}

type MenuPage = 'main' | 'lanes' | 'human' | 'link' | 'links';

interface MenuState {
  target: 'step' | 'lane';
  id: string;
  page: MenuPage;
  x: number;
  y: number;
}

interface MenuItem {
  id: string;
  label: string;
  run: () => void;
}

/**
 * Бізнес-вигляд сценарію (F3-66, docs/business-view.md): доріжки учасників, кроки людською мовою, рішення,
 * очікування й стрілки. Клік на крок вузла повідомляє `stepPick`, редактор відкриває вузол у технічному вигляді.
 * З `editable` (F3-68) — правки бізнес-атрибутів: подвійний клік змінює назву кроку, підпис стрілки чи доріжки,
 * правий клік відкриває меню кроку чи доріжки, крок перетягується в іншу доріжку. Кожна правка — нове визначення
 * в `definitionChange`; вузли й ребра не змінюються. Власний SVG без ngx-vflow — той самий файл іде в експорт.
 */
@Component({
  selector: 'app-business-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  host: { '(keydown.escape)': 'closeMenu()' },
  template: `
    <div class="bv__bar">
      @if (legend().length > 0) {
        <ul class="bv__legend" data-testid="business-legend">
          @for (l of legend(); track l.id) {
            <li><i class="bv__swatch bv__swatch--{{ l.id }}"></i>{{ l.label }}</li>
          }
        </ul>
      }
      @if (editable()) {
        <span class="bv__hint" data-testid="business-edit-hint" i18n-title="@@business.edit.hint" title="Подвійний клік — змінити назву · правий клік — меню · крок можна перетягнути в іншу доріжку" i18n="@@business.edit.hint">Подвійний клік — змінити назву · правий клік — меню · крок можна перетягнути в іншу доріжку</span>
      }
      <span class="bv__actions">
        @if (editable()) {
          <button mat-button type="button" data-testid="business-add-lane" (click)="newLane()" i18n="@@business.lane.add">+ Доріжка</button>
        }
        <button mat-button type="button" data-testid="business-zoom-out" (click)="zoomBy(1 / 1.2)" i18n-title="@@business.zoomOut" title="Зменшити">−</button>
        <button mat-button type="button" data-testid="business-zoom-in" (click)="zoomBy(1.2)" i18n-title="@@business.zoomIn" title="Збільшити">+</button>
        <button mat-button type="button" data-testid="business-fit" (click)="fit()" i18n="@@business.fit">Вмістити</button>
        <button mat-button type="button" data-testid="business-export-svg" (click)="downloadSvg()" i18n="@@business.exportSvg">Експорт SVG</button>
        <button mat-button type="button" data-testid="business-export-png" (click)="downloadPng()" i18n="@@business.exportPng">PNG</button>
      </span>
    </div>
    <div class="bv__viewport" #viewport data-testid="business-view" (wheel)="wheel($event)" (pointerdown)="panStart($event)" (pointermove)="panMove($event)" (pointerup)="panEnd($event)" (pointercancel)="panEnd()" (pointerleave)="panEnd()">
      @if (layout(); as l) {
        @if (l.steps.length === 0) {
          <p class="bv__empty" data-testid="business-empty" i18n="@@business.empty">Додайте тригер і кроки в технічному вигляді — тут з'явиться схема процесу.</p>
        }
        <svg #svg xmlns="http://www.w3.org/2000/svg" class="bv__svg" [attr.width]="svgWidth(l)" [attr.height]="svgHeight(l)" [attr.viewBox]="viewBox(l)" role="img" [attr.aria-label]="ariaLabel()" [style.transform]="transform()">
          <defs>
            @for (k of markerKinds; track k) {
              <marker [attr.id]="markerId(k)" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                <path d="M0 0L10 5L0 10z" class="bv__arrowhead bv__arrowhead--{{ k }}" />
              </marker>
            }
          </defs>
          @for (lane of l.lanes; track lane.id) {
            <g data-testid="business-lane" [attr.data-lane]="lane.id" [class.bv__lane--drop]="dropLane() === lane.id" (contextmenu)="openLaneMenu($event, lane)">
              <rect class="bv__lane" x="0" [attr.y]="lane.y" [attr.width]="l.width" [attr.height]="lane.height" rx="6" />
              <text class="bv__lane-name" data-testid="business-lane-name" (dblclick)="editLane(lane, 'lane-name')" x="14" [attr.y]="lane.y + lane.height / 2 - (lane.description ? 4 : -4)">{{ lane.name }}</text>
              @if (lane.description) {
                <text class="bv__lane-desc" (dblclick)="editLane(lane, 'lane-desc')" x="14" [attr.y]="lane.y + lane.height / 2 + 12">{{ lane.description }}</text>
              }
            </g>
          }
          @for (e of l.edges; track e.id) {
            <g data-testid="business-edge" [attr.data-kind]="e.kind" [attr.data-from]="e.from" [attr.data-to]="e.to" (dblclick)="editEdge(e)">
              @if (editable()) {
                <path class="bv__edge-hit" data-testid="business-edge-hit" [attr.d]="e.path" />
              }
              <path class="bv__edge bv__edge--{{ e.kind }}" [attr.d]="e.path" [attr.marker-end]="'url(#' + markerId(e.kind) + ')'" />
              @if (e.label) {
                <text class="bv__edge-label" [attr.x]="e.labelX" [attr.y]="e.labelY">{{ e.label }}</text>
              }
            </g>
          }
          @for (s of l.steps; track s.id) {
            <g
              class="bv__step"
              [class]="stepClasses(s)"
              [attr.data-status]="runStatus(s.id)"
              data-testid="business-step"
              [attr.data-step]="s.id"
              [attr.data-kind]="s.kind"
              [attr.tabindex]="s.nodeId ? 0 : null"
              [attr.role]="s.nodeId ? 'button' : null"
              [attr.transform]="dragTransform(s.id)"
              (click)="pick(s)"
              (keydown.enter)="open(s)"
              (dblclick)="editStep(s, 'title')"
              (contextmenu)="openStepMenu($event, s)"
              (pointerdown)="stepDown($event, s)"
            >
              @if (s.kind === 'decision') {
                <polygon class="bv__shape" [attr.points]="diamond(s)" />
              } @else {
                <rect class="bv__shape" [attr.x]="s.x" [attr.y]="s.y" [attr.width]="s.width" [attr.height]="s.height" rx="8" />
              }
              @if (mark(s.kind); as m) {
                <text class="bv__mark" [attr.x]="s.x + 8" [attr.y]="s.y + 15">{{ m }}</text>
              }
              @for (line of lines(s); track $index) {
                <text class="bv__title" text-anchor="middle" [attr.x]="s.x + s.width / 2" [attr.y]="titleY(s, $index)">{{ line }}</text>
              }
              @if (s.subtitle || s.badge) {
                <text class="bv__sub" text-anchor="middle" [attr.x]="s.x + s.width / 2" [attr.y]="s.y + s.height - 8">{{ s.subtitle ?? s.badge }}</text>
              }
              <title>{{ tooltip(s) }}</title>
            </g>
          }
        </svg>
      }
      @if (editing(); as ed) {
        <input
          #inline
          class="bv__inline"
          data-testid="business-inline"
          [attr.data-field]="ed.field"
          [style.left.px]="ed.x"
          [style.top.px]="ed.y"
          [style.width.px]="ed.width"
          [value]="ed.value"
          (pointerdown)="$event.stopPropagation()"
          (keydown.enter)="commitEdit($any($event.target).value)"
          (keydown.escape)="cancelEdit($event)"
          (blur)="commitEdit($any($event.target).value)"
        />
      }
      @if (menu(); as m) {
        <div class="bv__menu" role="menu" data-testid="business-menu" [style.left.px]="m.x" [style.top.px]="m.y" (pointerdown)="$event.stopPropagation()">
          @if (menuTitle(); as t) {
            <div class="bv__menu-title">{{ t }}</div>
          }
          @for (item of menuItems(); track item.id) {
            <button type="button" role="menuitem" class="bv__menu-item" [attr.data-testid]="'business-menu-' + item.id" (click)="item.run()">{{ item.label }}</button>
          }
        </div>
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
      background: var(--ac-cool-grey);
    }

    .bv__bar {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 6px 12px;
      border-bottom: 1px solid var(--ac-line);
      background: var(--ac-white);
    }

    .bv__legend {
      display: flex;
      flex-wrap: wrap;
      gap: 14px;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: 12px;
      color: var(--ac-cool-grey-4);
    }

    .bv__legend li {
      display: inline-flex;
      align-items: center;
      gap: 6px;
    }

    .bv__swatch {
      display: inline-block;
      width: 18px;
      height: 12px;
      border: 1.5px solid;
      border-radius: 3px;
    }

    .bv__swatch--node {
      background: var(--ac-cool-grey-2);
      border-color: var(--ac-mid);
    }

    .bv__swatch--external {
      background: var(--ac-button);
      border-color: var(--ac-kit);
    }

    .bv__swatch--decision {
      background: var(--ac-white);
      border-color: var(--ac-mid);
      transform: rotate(45deg) scale(0.7);
    }

    .bv__swatch--read,
    .bv__swatch--alt {
      height: 0;
      border-width: 1.5px 0 0;
      border-style: dashed;
      border-radius: 0;
    }

    .bv__swatch--read {
      border-color: var(--ac-kit);
    }

    .bv__swatch--alt {
      border-color: var(--ac-orange);
    }

    .bv__hint {
      flex: 1;
      min-width: 0;
      overflow: hidden;
      font-size: 11.5px;
      color: var(--ac-cool-grey-4);
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .bv__actions {
      display: flex;
      flex-shrink: 0;
      gap: 2px;
      margin-left: auto;
      white-space: nowrap;
    }

    .bv__viewport {
      position: relative;
      flex: 1;
      min-height: 0;
      overflow: hidden;
      cursor: grab;
      touch-action: none;
    }

    .bv__empty {
      margin: 24px;
      color: var(--ac-cool-grey-4);
    }

    .bv__svg {
      display: block;
      transform-origin: 0 0;
      font-family: Ubuntu, system-ui, sans-serif;
    }

    .bv__lane {
      fill: var(--ac-white);
      stroke: var(--ac-line-2);
    }

    .bv__lane-name {
      font-size: 12px;
      font-weight: 600;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      fill: var(--ac-cool-grey-4);
    }

    .bv__lane-desc {
      font-size: 11px;
      fill: var(--ac-cool-grey-4);
    }

    .bv__shape {
      fill: var(--ac-cool-grey-2);
      stroke: var(--ac-mid);
      stroke-width: 1.5;
    }

    .bv__step--decision .bv__shape {
      fill: var(--ac-white);
    }

    .bv__step--external .bv__shape {
      fill: var(--ac-button);
      stroke: var(--ac-kit);
    }

    .bv__step--link {
      cursor: pointer;
    }

    .bv__step--link:hover .bv__shape,
    .bv__step--link:focus-visible .bv__shape {
      stroke: var(--ac-kit);
      stroke-width: 2.5;
    }

    .bv__step:focus {
      outline: none;
    }

    .bv__title {
      font-size: 12.5px;
      font-weight: 500;
      fill: var(--ac-vivid);
    }

    .bv__sub,
    .bv__mark {
      font-size: 10.5px;
      fill: var(--ac-cool-grey-4);
    }

    .bv__step--st-done .bv__shape {
      fill: var(--ac-green-15);
      stroke: var(--ac-green);
    }

    .bv__step--st-running .bv__shape,
    .bv__step--st-waiting .bv__shape {
      stroke: var(--ac-kit);
      stroke-width: 3;
    }

    .bv__step--st-waiting .bv__shape {
      stroke-dasharray: 6 3;
    }

    .bv__step--st-error .bv__shape {
      fill: var(--ac-red-15);
      stroke: var(--ac-red);
      stroke-width: 2;
    }

    .bv__step--st-pending {
      opacity: 0.5;
    }

    .bv__swatch--st-done {
      background: var(--ac-green-15);
      border-color: var(--ac-green);
    }

    .bv__swatch--st-running {
      background: var(--ac-cool-grey-2);
      border-color: var(--ac-kit);
      border-width: 3px;
    }

    .bv__swatch--st-error {
      background: var(--ac-red-15);
      border-color: var(--ac-red);
    }

    .bv__swatch--st-pending {
      background: var(--ac-cool-grey-2);
      border-color: var(--ac-mid);
      opacity: 0.5;
    }

    .bv__edge {
      fill: none;
      stroke: var(--ac-vivid);
      stroke-width: 1.4;
    }

    .bv__edge--read {
      stroke: var(--ac-kit);
      stroke-dasharray: 4 3;
    }

    .bv__edge--alt {
      stroke: var(--ac-orange);
      stroke-dasharray: 4 3;
    }

    .bv__arrowhead {
      fill: var(--ac-vivid);
    }

    .bv__arrowhead--read {
      fill: var(--ac-kit);
    }

    .bv__arrowhead--alt {
      fill: var(--ac-orange);
    }

    .bv__edge-label {
      font-size: 10.5px;
      fill: var(--ac-cool-grey-4);
    }

    .bv__edge-hit {
      fill: none;
      stroke: transparent;
      stroke-width: 12;
      cursor: text;
    }

    .bv__lane--drop .bv__lane {
      fill: var(--ac-button);
      stroke: var(--ac-kit);
    }

    .bv__lane-name,
    .bv__lane-desc {
      cursor: default;
    }

    .bv__inline {
      position: absolute;
      z-index: 2;
      box-sizing: border-box;
      height: 28px;
      padding: 2px 6px;
      border: 1.5px solid var(--ac-kit);
      border-radius: 4px;
      background: var(--ac-white);
      color: var(--ac-vivid);
      font: 500 13px Ubuntu, system-ui, sans-serif;
      outline: none;
    }

    .bv__menu {
      position: absolute;
      z-index: 3;
      display: flex;
      flex-direction: column;
      min-width: 200px;
      max-height: 320px;
      overflow-y: auto;
      padding: 4px 0;
      border: 1px solid var(--ac-line);
      border-radius: 6px;
      background: var(--ac-white);
      box-shadow: 0 4px 16px rgb(0 0 0 / 12%);
      cursor: default;
    }

    .bv__menu-title {
      padding: 6px 14px 4px;
      font-size: 11px;
      color: var(--ac-cool-grey-4);
    }

    .bv__menu-item {
      padding: 7px 14px;
      border: 0;
      background: none;
      color: var(--ac-vivid);
      font: 13px Ubuntu, system-ui, sans-serif;
      text-align: left;
      cursor: pointer;
    }

    .bv__menu-item:hover,
    .bv__menu-item:focus-visible {
      background: var(--ac-cool-grey-2);
      outline: none;
    }
  `,
})
export class BusinessViewComponent {
  readonly definition = input.required<WorkflowDefinition>();
  readonly descriptors = input<readonly NodeTypeInfo[]>([]);
  readonly connectors = input<readonly BusinessConnector[]>([]);
  /** Назва файлу експорту (без розширення). */
  readonly fileName = input('scenario');
  /** Виконання, яке показати на схемі (F3-69); `null` — схема без виконання. */
  readonly execution = input<BusinessRun | null>(null);
  /** Можна правити бізнес-атрибути (F3-68); у перегляді й журналі виконань — ні. */
  readonly editable = input(false, { transform: booleanAttribute });
  /** Клік на крок вузла: id вузла. */
  readonly stepPick = output<string>();
  /** Визначення після правки бізнес-атрибутів. */
  readonly definitionChange = output<WorkflowDefinition>();

  protected readonly markerKinds: BusinessEdgeKind[] = ['flow', 'alt', 'read', 'write'];
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly svg = viewChild<ElementRef<SVGSVGElement>>('svg');
  private readonly inline = viewChild<ElementRef<HTMLInputElement>>('inline');

  private readonly model = computed(() => toCanvasModel(this.definition(), this.descriptors()));
  protected readonly view = computed(() => buildBusinessView(this.definition(), this.model(), this.connectors()));
  private readonly run = computed(() => {
    const execution = this.execution();
    return execution ? businessRunStatuses(this.view(), this.model(), execution) : null;
  });
  protected readonly layout = computed(() => layoutBusinessView(this.view()));

  protected readonly legend = computed((): LegendEntry[] => {
    const v = this.view();
    const entries: LegendEntry[] = [];
    if (v.steps.some((s) => s.kind !== 'external')) entries.push({ id: 'node', label: $localize`:@@business.legend.node:крок сценарію` });
    if (v.steps.some((s) => s.kind === 'external')) entries.push({ id: 'external', label: $localize`:@@business.legend.external:дія людини в іншій системі` });
    if (v.steps.some((s) => s.kind === 'decision')) entries.push({ id: 'decision', label: $localize`:@@business.legend.decision:рішення` });
    if (v.edges.some((e) => e.kind === 'read')) entries.push({ id: 'read', label: $localize`:@@business.legend.read:сценарій читає зі системи` });
    if (v.edges.some((e) => e.kind === 'alt')) entries.push({ id: 'alt', label: $localize`:@@business.legend.alt:таймаут або збій` });
    const run = this.run();
    if (run) {
      const present = new Set([...run.values()].map((i) => i.status));
      for (const status of ['done', 'running', 'waiting', 'error', 'pending'] as const) {
        if (present.has(status)) entries.push({ id: `st-${status === 'waiting' ? 'running' : status}`, label: STATUS_LABELS[status]() });
      }
    }
    return entries;
  });

  protected readonly ariaLabel = computed(() =>
    $localize`:@@business.aria:Схема процесу: доріжки ${this.view()
      .lanes.map((l) => l.name)
      .join(', ')}:lanes:`,
  );

  private readonly zoom = signal(1);
  private readonly pan = signal({ x: 0, y: 0 });
  private drag: { x: number; y: number; panX: number; panY: number; moved: boolean } | null = null;
  protected readonly transform = computed(() => `translate(${this.pan().x}px, ${this.pan().y}px) scale(${this.zoom()})`);

  protected readonly editing = signal<InlineEdit | null>(null);
  protected readonly menu = signal<MenuState | null>(null);
  /** Крок, що перетягується, і зсув у координатах схеми. */
  private readonly stepOffset = signal<{ id: string; dx: number; dy: number } | null>(null);
  protected readonly dropLane = signal<string | null>(null);
  private stepDrag: { step: PlacedStep; x: number; y: number; moved: boolean } | null = null;
  /** Після перетягування кроку click не відкриває вузол. */
  private suppressClick = false;
  private clickTimer: ReturnType<typeof setTimeout> | undefined;
  /** Новий крок чи доріжка, назву яких відкрити на редагування, щойно вони з'являться на схемі. */
  private pendingEdit: { field: EditField; id: string } | null = null;

  constructor() {
    // Велика схема одразу вміщується у вікно; маленька лишається в масштабі 1.
    afterNextRender(() => this.fit());
    effect(() => {
      // Нове поле редагування може прийти в той самий <input> — фокус ставимо на кожне.
      this.editing();
      const el = this.inline()?.nativeElement;
      if (!el) return;
      el.focus();
      el.select();
    });
    effect(() => {
      const l = this.layout();
      untracked(() => {
        const pending = this.pendingEdit;
        if (!pending) return;
        const lane = pending.field === 'lane-name' ? l.lanes.find((x) => x.id === pending.id) : undefined;
        const step = pending.field === 'title' ? l.steps.find((x) => x.id === pending.id) : undefined;
        if (!lane && !step) return;
        this.pendingEdit = null;
        if (lane) this.editLane(lane, 'lane-name');
        if (step) this.editStep(step, 'title');
      });
    });
  }

  protected svgWidth(l: BusinessLayout): number {
    return Math.max(l.width, LANE_LABEL_WIDTH) + PAD * 2;
  }

  protected svgHeight(l: BusinessLayout): number {
    return l.height + PAD * 2;
  }

  protected viewBox(l: BusinessLayout): string {
    return `${-PAD} ${-PAD} ${this.svgWidth(l)} ${this.svgHeight(l)}`;
  }

  protected markerId(kind: BusinessEdgeKind): string {
    return `bv-arrow-${kind}`;
  }

  protected mark(kind: BusinessStepKind): string | undefined {
    return KIND_MARKS[kind];
  }

  protected diamond(s: PlacedStep): string {
    const cx = s.x + s.width / 2;
    const cy = s.y + s.height / 2;
    return `${cx},${s.y - DIAMOND_OVERHANG} ${s.x + s.width + 4},${cy} ${cx},${s.y + s.height + DIAMOND_OVERHANG} ${s.x - 4},${cy}`;
  }

  protected lines(s: PlacedStep): string[] {
    return wrapTitle(s.title, s.kind === 'decision' ? LINE_CHARS - 6 : LINE_CHARS, s.subtitle || s.badge ? MAX_LINES - 1 : MAX_LINES);
  }

  protected titleY(s: PlacedStep, index: number): number {
    const count = this.lines(s).length;
    const block = count * 15;
    const area = s.subtitle || s.badge ? s.height - 14 : s.height;
    return s.y + (area - block) / 2 + 12 + index * 15;
  }

  protected runStatus(id: string): BusinessRunStatus | null {
    return this.run()?.get(id)?.status ?? null;
  }

  protected stepClasses(s: PlacedStep): string {
    const status = this.runStatus(s.id);
    return [`bv__step--${s.kind}`, s.nodeId ? 'bv__step--link' : '', status ? `bv__step--st-${status}` : ''].filter(Boolean).join(' ');
  }

  protected tooltip(s: PlacedStep): string {
    const info = this.run()?.get(s.id);
    if (!info) return s.title;
    const parts = [s.title, STATUS_LABELS[info.status]()];
    if (info.startedAt) parts.push(new Date(info.startedAt).toLocaleString());
    if (info.durationMs !== undefined) parts.push(formatDuration(info.durationMs));
    return parts.join(' · ');
  }

  protected pick(s: PlacedStep): void {
    if (this.drag?.moved || this.suppressClick || !s.nodeId) return;
    if (!this.editable()) {
      this.stepPick.emit(s.nodeId);
      return;
    }
    // Подвійний клік редагує назву — вузол відкриваємо, лише якщо другого кліку не було.
    clearTimeout(this.clickTimer);
    const nodeId = s.nodeId;
    this.clickTimer = setTimeout(() => this.stepPick.emit(nodeId), DBLCLICK_MS);
  }

  protected open(s: PlacedStep): void {
    if (s.nodeId) this.stepPick.emit(s.nodeId);
  }

  // --- Правки (F3-68)

  protected editStep(s: PlacedStep, field: 'title' | 'subtitle'): void {
    if (!this.editable()) return;
    clearTimeout(this.clickTimer);
    this.menu.set(null);
    const y = field === 'title' ? s.y + s.height / 2 - 14 : s.y + s.height - 24;
    const at = this.toScreen(s.x, y);
    const value = field === 'title' ? s.title : (s.subtitle ?? '');
    this.editing.set({ field, id: s.id, value, x: at.x, y: at.y, width: Math.max(s.width * this.zoom(), 160) });
  }

  protected editEdge(e: PlacedEdge): void {
    if (!this.editable()) return;
    this.menu.set(null);
    const at = this.toScreen(e.labelX, e.labelY - 18);
    this.editing.set({ field: 'edge', id: e.id, value: e.label ?? '', x: at.x, y: at.y, width: 160 });
  }

  protected editLane(lane: PlacedLane, field: 'lane-name' | 'lane-desc'): void {
    if (!this.editable()) return;
    this.menu.set(null);
    const at = this.toScreen(10, lane.y + lane.height / 2 + (field === 'lane-name' ? -20 : 0));
    const value = field === 'lane-name' ? lane.name : (lane.description ?? '');
    this.editing.set({ field, id: lane.id, value, x: at.x, y: at.y, width: Math.max((LANE_LABEL_WIDTH - 20) * this.zoom(), 160) });
  }

  protected commitEdit(value: string): void {
    const ed = this.editing();
    if (!ed) return;
    this.editing.set(null);
    const def = this.definition();
    const view = this.view();
    switch (ed.field) {
      case 'title':
        this.emit(setStepTitle(def, ed.id, value));
        break;
      case 'subtitle':
        this.emit(setStepSubtitle(def, ed.id, value));
        break;
      case 'edge': {
        const edge = view.edges.find((e) => e.id === ed.id);
        if (edge && value.trim() !== (edge.label ?? '')) this.emit(setEdgeLabel(def, edge, value));
        break;
      }
      case 'lane-name': {
        const lane = view.lanes.find((l) => l.id === ed.id);
        if (lane && value.trim() !== lane.name) this.emit(renameLane(def, view, ed.id, value));
        break;
      }
      case 'lane-desc':
        this.emit(setLaneDescription(def, view, ed.id, value));
        break;
    }
  }

  protected cancelEdit(event: Event): void {
    event.stopPropagation();
    this.editing.set(null);
  }

  protected openStepMenu(event: MouseEvent, s: PlacedStep): void {
    if (!this.editable()) return;
    event.preventDefault();
    event.stopPropagation();
    clearTimeout(this.clickTimer);
    this.openMenu(event, 'step', s.id);
  }

  protected openLaneMenu(event: MouseEvent, lane: PlacedLane): void {
    if (!this.editable()) return;
    event.preventDefault();
    this.openMenu(event, 'lane', lane.id);
  }

  protected closeMenu(): void {
    this.menu.set(null);
  }

  private openMenu(event: MouseEvent, target: 'step' | 'lane', id: string): void {
    const rect = this.viewport()?.nativeElement.getBoundingClientRect();
    this.editing.set(null);
    this.menu.set({ target, id, page: 'main', x: event.clientX - (rect?.left ?? 0), y: event.clientY - (rect?.top ?? 0) });
  }

  protected readonly menuTitle = computed((): string | null => {
    const m = this.menu();
    if (!m) return null;
    switch (m.page) {
      case 'lanes':
        return $localize`:@@business.menu.toLane:Перенести в доріжку`;
      case 'human':
        return $localize`:@@business.menu.humanLane:Дія людини — у доріжці`;
      case 'link':
        return $localize`:@@business.menu.linkTo:Пов'язати з кроком`;
      case 'links':
        return $localize`:@@business.menu.direction:Напрям зв'язку`;
      default:
        return null;
    }
  });

  protected readonly menuItems = computed((): MenuItem[] => {
    const m = this.menu();
    if (!m) return [];
    const view = this.view();
    if (m.target === 'lane') return this.laneItems(m.id);
    const step = view.steps.find((s) => s.id === m.id);
    if (!step) return [];
    const placed = this.layout().steps.find((s) => s.id === m.id)!;
    const page = (p: MenuPage) => () => this.menu.update((x) => (x ? { ...x, page: p } : x));
    const act = (next: () => WorkflowDefinition) => () => {
      this.menu.set(null);
      this.emit(next());
    };
    switch (m.page) {
      case 'lanes':
        return view.lanes
          .filter((l) => l.id !== step.lane)
          .map((l) => ({ id: `lane-${l.id}`, label: l.name, run: act(() => moveStepToLane(this.definition(), this.view(), step.id, l.id)) }));
      case 'human':
        return view.lanes.map((l) => ({
          id: `lane-${l.id}`,
          label: l.name,
          run: () => {
            this.menu.set(null);
            const added = addExternalStep(this.definition(), this.view(), l.id, step.nodeId!, $localize`:@@business.human.new:Нова дія людини`);
            this.pendingEdit = { field: 'title', id: added.stepId };
            this.emit(added.definition);
          },
        }));
      case 'link': {
        const linked = new Set(view.edges.filter((e) => e.from === step.id || e.to === step.id).map((e) => (e.from === step.id ? e.to : e.from)));
        return view.steps
          .filter((s) => s.nodeId && !linked.has(s.id))
          .map((s) => ({ id: `node-${s.id}`, label: s.title, run: act(() => linkExternalStep(this.definition(), step.id, s.id, 'in')) }));
      }
      case 'links':
        return view.edges
          .filter((e) => 'step' in e.origin && e.origin.step === step.id)
          .map((e) => {
            const origin = e.origin as { step: string; link: number };
            const other = view.steps.find((s) => s.id === (e.from === step.id ? e.to : e.from));
            const label =
              e.kind === 'read'
                ? $localize`:@@business.link.read:сценарій читає → ${other?.title ?? ''}:step:`
                : $localize`:@@business.link.write:сценарій ставить ← ${other?.title ?? ''}:step:`;
            return { id: `link-${origin.link}`, label, run: act(() => toggleLinkDirection(this.definition(), step.id, origin.link)) };
          });
    }
    const items: MenuItem[] = [
      { id: 'rename', label: $localize`:@@business.menu.rename:Перейменувати`, run: () => this.editStep(placed, 'title') },
      { id: 'subtitle', label: $localize`:@@business.menu.subtitle:Підрядок…`, run: () => this.editStep(placed, 'subtitle') },
      { id: 'move', label: $localize`:@@business.menu.move:Перенести в доріжку…`, run: page('lanes') },
    ];
    if (step.nodeId) {
      const nodeId = step.nodeId;
      items.push(
        { id: 'human', label: $localize`:@@business.menu.human:Додати дію людини…`, run: page('human') },
        { id: 'hide', label: $localize`:@@business.menu.hide:Сховати з бізнес-вигляду`, run: act(() => setStepHidden(this.definition(), nodeId, true)) },
        {
          id: 'open',
          label: $localize`:@@business.menu.open:Відкрити в технічному вигляді`,
          run: () => {
            this.menu.set(null);
            this.stepPick.emit(nodeId);
          },
        },
      );
    } else {
      items.push(
        { id: 'link', label: $localize`:@@business.menu.link:Пов'язати з кроком…`, run: page('link') },
        { id: 'direction', label: $localize`:@@business.menu.toggle:Змінити напрям зв'язку…`, run: page('links') },
        { id: 'remove', label: $localize`:@@business.menu.remove:Видалити дію`, run: act(() => removeExternalStep(this.definition(), step.id)) },
      );
    }
    return items;
  });

  private laneItems(laneId: string): MenuItem[] {
    const lanes = this.layout().lanes;
    const index = lanes.findIndex((l) => l.id === laneId);
    const lane = lanes[index];
    if (!lane) return [];
    const act = (next: () => WorkflowDefinition) => () => {
      this.menu.set(null);
      this.emit(next());
    };
    const items: MenuItem[] = [
      { id: 'rename', label: $localize`:@@business.menu.rename:Перейменувати`, run: () => this.editLane(lane, 'lane-name') },
      { id: 'description', label: $localize`:@@business.menu.laneDesc:Підпис доріжки…`, run: () => this.editLane(lane, 'lane-desc') },
    ];
    if (index > 0) items.push({ id: 'up', label: $localize`:@@business.menu.up:Вище`, run: act(() => moveLane(this.definition(), this.view(), laneId, -1)) });
    if (index < lanes.length - 1)
      items.push({ id: 'down', label: $localize`:@@business.menu.down:Нижче`, run: act(() => moveLane(this.definition(), this.view(), laneId, 1)) });
    items.push({ id: 'new-lane', label: $localize`:@@business.menu.newLane:Нова доріжка`, run: () => this.newLane() });
    return items;
  }

  protected newLane(): void {
    if (!this.editable()) return;
    this.menu.set(null);
    const added = addLane(this.definition(), this.view(), $localize`:@@business.lane.new:Нова доріжка`);
    this.pendingEdit = { field: 'lane-name', id: added.laneId };
    this.emit(added.definition);
  }

  protected dragTransform(id: string): string | null {
    const o = this.stepOffset();
    return o && o.id === id ? `translate(${o.dx} ${o.dy})` : null;
  }

  protected stepDown(event: PointerEvent, s: PlacedStep): void {
    if (!this.editable() || event.button !== 0) return;
    event.stopPropagation();
    this.menu.set(null);
    this.stepDrag = { step: s, x: event.clientX, y: event.clientY, moved: false };
  }

  private emit(next: WorkflowDefinition): void {
    if (this.editable() && next !== this.definition()) this.definitionChange.emit(next);
  }

  /** Точка схеми → пікселі у вікні схеми (з урахуванням зсуву й масштабу). */
  private toScreen(x: number, y: number): { x: number; y: number } {
    const k = this.zoom();
    const p = this.pan();
    return { x: p.x + (x + PAD) * k, y: p.y + (y + PAD) * k };
  }

  /** Доріжка під точкою вікна (clientY). */
  private laneAt(clientY: number): PlacedLane | undefined {
    const rect = this.viewport()?.nativeElement.getBoundingClientRect();
    const y = (clientY - (rect?.top ?? 0) - this.pan().y) / this.zoom() - PAD;
    return this.layout().lanes.find((l) => y >= l.y - LANE_GAP / 2 && y < l.y + l.height + LANE_GAP / 2);
  }

  protected zoomBy(factor: number): void {
    this.zoom.update((z) => clamp(z * factor, MIN_ZOOM, MAX_ZOOM));
  }

  protected fit(): void {
    const el = this.viewport()?.nativeElement;
    const l = this.layout();
    if (!el || el.clientWidth === 0) return;
    const k = clamp(Math.min(el.clientWidth / this.svgWidth(l), el.clientHeight / this.svgHeight(l)), MIN_ZOOM, 1);
    this.zoom.set(k);
    this.pan.set({ x: 0, y: 0 });
  }

  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    const el = this.viewport()?.nativeElement;
    const rect = el?.getBoundingClientRect();
    const px = rect ? event.clientX - rect.left : 0;
    const py = rect ? event.clientY - rect.top : 0;
    const before = this.zoom();
    const after = clamp(before * (event.deltaY < 0 ? 1.1 : 1 / 1.1), MIN_ZOOM, MAX_ZOOM);
    // Точка під курсором лишається на місці.
    this.pan.update((p) => ({ x: px - ((px - p.x) * after) / before, y: py - ((py - p.y) * after) / before }));
    this.zoom.set(after);
  }

  protected panStart(event: PointerEvent): void {
    this.menu.set(null);
    if (event.button !== 0) return;
    this.drag = { x: event.clientX, y: event.clientY, panX: this.pan().x, panY: this.pan().y, moved: false };
  }

  protected panMove(event: PointerEvent): void {
    const sd = this.stepDrag;
    if (sd) {
      const dx = event.clientX - sd.x;
      const dy = event.clientY - sd.y;
      if (!sd.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      sd.moved = true;
      this.stepOffset.set({ id: sd.step.id, dx: dx / this.zoom(), dy: dy / this.zoom() });
      this.dropLane.set(this.laneAt(event.clientY)?.id ?? null);
      return;
    }
    if (!this.drag) return;
    const dx = event.clientX - this.drag.x;
    const dy = event.clientY - this.drag.y;
    if (!this.drag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
    this.drag.moved = true;
    this.pan.set({ x: this.drag.panX + dx, y: this.drag.panY + dy });
  }

  protected panEnd(event?: PointerEvent): void {
    const sd = this.stepDrag;
    if (sd) {
      this.stepDrag = null;
      this.stepOffset.set(null);
      this.dropLane.set(null);
      if (!sd.moved) return;
      // Click після перетягування кроку не відкриває вузол.
      this.suppressClick = true;
      setTimeout(() => (this.suppressClick = false));
      const lane = event ? this.laneAt(event.clientY) : undefined;
      if (lane && lane.id !== sd.step.lane) this.emit(moveStepToLane(this.definition(), this.view(), sd.step.id, lane.id));
      return;
    }
    // Клік на крок після перетягування не відкриває вузол: скидаємо після обробки click.
    const drag = this.drag;
    if (drag?.moved) setTimeout(() => (this.drag = null));
    else this.drag = null;
  }

  protected downloadSvg(): void {
    const svg = this.svg()?.nativeElement;
    if (svg) exportSvg(svg, this.fileName());
  }

  protected downloadPng(): void {
    const svg = this.svg()?.nativeElement;
    if (svg) void exportPng(svg, this.fileName());
  }
}

/** Розбиває назву на рядки по словах; що не влізло в `maxLines` — обрізається «…». */
export function wrapTitle(title: string, lineChars: number, maxLines: number): string[] {
  const words = title.split(/\s+/).filter(Boolean);
  const lines: string[] = [];
  let current = '';
  for (const word of words) {
    const candidate = current ? `${current} ${word}` : word;
    if (candidate.length <= lineChars || !current) {
      current = candidate;
    } else {
      lines.push(current);
      current = word;
    }
  }
  if (current) lines.push(current);
  const cut = lines.map((l) => (l.length > lineChars ? `${l.slice(0, lineChars - 1)}…` : l));
  if (cut.length <= maxLines) return cut;
  const kept = cut.slice(0, maxLines);
  const last = kept[maxLines - 1];
  kept[maxLines - 1] = `${last.length >= lineChars ? last.slice(0, lineChars - 1) : last}…`;
  return kept;
}

const STATUS_LABELS: Record<BusinessRunStatus, () => string> = {
  done: () => $localize`:@@business.status.done:пройдено`,
  running: () => $localize`:@@business.status.running:виконується`,
  waiting: () => $localize`:@@business.status.waiting:чекає`,
  error: () => $localize`:@@business.status.error:збій`,
  pending: () => $localize`:@@business.status.pending:не дійшло`,
};

function formatDuration(ms: number): string {
  if (ms < 1000) return $localize`:@@business.duration.ms:${ms}:ms: мс`;
  if (ms < 60_000) return $localize`:@@business.duration.s:${(ms / 1000).toFixed(1)}:s: с`;
  return $localize`:@@business.duration.min:${Math.round(ms / 60_000)}:min: хв`;
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(value, max));
}
