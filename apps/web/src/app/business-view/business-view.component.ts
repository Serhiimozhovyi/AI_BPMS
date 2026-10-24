import { ChangeDetectionStrategy, Component, ElementRef, afterNextRender, computed, input, output, signal, viewChild } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { toCanvasModel } from '../canvas/canvas-model';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { BusinessConnector, BusinessEdgeKind, BusinessStepKind, buildBusinessView } from './business-model';
import { BusinessLayout, DIAMOND_OVERHANG, LANE_LABEL_WIDTH, PlacedStep, layoutBusinessView } from './business-layout';
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

interface LegendEntry {
  id: string;
  label: string;
}

/**
 * Бізнес-вигляд сценарію (F3-66, docs/business-view.md): доріжки учасників, кроки людською мовою, рішення,
 * очікування й стрілки. Лише читання: клік на крок вузла повідомляє `stepPick`, редактор відкриває вузол у
 * технічному вигляді. Власний SVG без ngx-vflow — той самий файл іде в експорт SVG/PNG.
 */
@Component({
  selector: 'app-business-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="bv__bar">
      @if (legend().length > 0) {
        <ul class="bv__legend" data-testid="business-legend">
          @for (l of legend(); track l.id) {
            <li><i class="bv__swatch bv__swatch--{{ l.id }}"></i>{{ l.label }}</li>
          }
        </ul>
      }
      <span class="bv__actions">
        <button mat-button type="button" data-testid="business-zoom-out" (click)="zoomBy(1 / 1.2)" i18n-title="@@business.zoomOut" title="Зменшити">−</button>
        <button mat-button type="button" data-testid="business-zoom-in" (click)="zoomBy(1.2)" i18n-title="@@business.zoomIn" title="Збільшити">+</button>
        <button mat-button type="button" data-testid="business-fit" (click)="fit()" i18n="@@business.fit">Вмістити</button>
        <button mat-button type="button" data-testid="business-export-svg" (click)="downloadSvg()" i18n="@@business.exportSvg">Експорт SVG</button>
        <button mat-button type="button" data-testid="business-export-png" (click)="downloadPng()" i18n="@@business.exportPng">PNG</button>
      </span>
    </div>
    <div class="bv__viewport" #viewport data-testid="business-view" (wheel)="wheel($event)" (pointerdown)="panStart($event)" (pointermove)="panMove($event)" (pointerup)="panEnd()" (pointercancel)="panEnd()">
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
            <g data-testid="business-lane" [attr.data-lane]="lane.id">
              <rect class="bv__lane" x="0" [attr.y]="lane.y" [attr.width]="l.width" [attr.height]="lane.height" rx="6" />
              <text class="bv__lane-name" x="14" [attr.y]="lane.y + lane.height / 2 - (lane.description ? 4 : -4)">{{ lane.name }}</text>
              @if (lane.description) {
                <text class="bv__lane-desc" x="14" [attr.y]="lane.y + lane.height / 2 + 12">{{ lane.description }}</text>
              }
            </g>
          }
          @for (e of l.edges; track e.id) {
            <g data-testid="business-edge" [attr.data-kind]="e.kind" [attr.data-from]="e.from" [attr.data-to]="e.to">
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
              (click)="pick(s)"
              (keydown.enter)="pick(s)"
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

    .bv__actions {
      display: flex;
      gap: 2px;
      margin-left: auto;
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
  /** Клік на крок вузла: id вузла. */
  readonly stepPick = output<string>();

  protected readonly markerKinds: BusinessEdgeKind[] = ['flow', 'alt', 'read', 'write'];
  private readonly viewport = viewChild<ElementRef<HTMLElement>>('viewport');
  private readonly svg = viewChild<ElementRef<SVGSVGElement>>('svg');

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

  constructor() {
    // Велика схема одразу вміщується у вікно; маленька лишається в масштабі 1.
    afterNextRender(() => this.fit());
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
    if (this.drag?.moved) return;
    if (s.nodeId) this.stepPick.emit(s.nodeId);
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
    if (event.button !== 0) return;
    this.drag = { x: event.clientX, y: event.clientY, panX: this.pan().x, panY: this.pan().y, moved: false };
  }

  protected panMove(event: PointerEvent): void {
    if (!this.drag) return;
    const dx = event.clientX - this.drag.x;
    const dy = event.clientY - this.drag.y;
    if (!this.drag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
    this.drag.moved = true;
    this.pan.set({ x: this.drag.panX + dx, y: this.drag.panY + dy });
  }

  protected panEnd(): void {
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
