import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { ChipComponent, ChipTone } from '../../shared/chip/chip.component';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Subscription, of } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { eventLine } from '../../projects/calendar-line';
import { zonedParts } from '../../projects/calendar-time';
import { BADGE_CLASSES, BADGE_TONES, CellView, DisplayColumn, cellView } from '../../projects/page-format';
import {
  PageAction,
  PageCalendarEvent,
  PageActionResult,
  PageDashboard,
  PageDashboardBlock,
  PageFilterValue,
  PageRow,
  ProjectPageView,
  ProjectPagesApiService,
} from '../../projects/project-pages-api.service';
import { ExpandOpen } from './page-expand.component';
import { PageFiltersComponent, emptyFilters } from './page-filters.component';
import { PageFormComponent } from './page-form.component';
import { PageMetricsComponent } from './page-metrics.component';

/** Ширина блоку на екрані від `md`: класи записані повністю, щоб Tailwind їх бачив. */
const SPAN: Record<number, string> = {
  1: 'md:col-span-1',
  2: 'md:col-span-2',
  3: 'md:col-span-3',
  4: 'md:col-span-4',
  5: 'md:col-span-5',
  6: 'md:col-span-6',
  7: 'md:col-span-7',
  8: 'md:col-span-8',
  9: 'md:col-span-9',
  10: 'md:col-span-10',
  11: 'md:col-span-11',
  12: 'md:col-span-12',
};

interface AgendaItem {
  event: PageCalendarEvent;
  key: string;
  day: string;
  title: CellView;
  subtitle: string;
  color: keyof typeof BADGE_CLASSES;
  time: string;
}

/**
 * Головна сторінка застосунку (F8-18, `dashboard`): блоки в сітці з 12 колонок — текст, показники, короткі списки,
 * події дня чи тижня, форми та кнопки дій — під спільними фільтрами. Дані всіх блоків віддає один запит; права, `when`
 * блоків і спільні фільтри застосовує Api.
 */
@Component({
  selector: 'app-page-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, ChipComponent, RouterLink, PageFiltersComponent, PageFormComponent, PageMetricsComponent],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p role="alert" class="mb-2 text-red-700" data-testid="dash-error">{{ error() }}</p>
    }
    @if (notice(); as n) {
      <p role="status" class="mb-2" [class.text-red-700]="n.state !== 'success'" data-testid="dash-notice" [attr.data-state]="n.state">{{ n.text }}</p>
    }
    @if (definition().filters?.length) {
      <app-page-filters [filters]="definition().filters!" [options]="view().filterOptions" [values]="filterState()" (changed)="filtersChanged($event)" />
    }
    @if (dashboard(); as d) {
      @if (d.blocks.length === 0 && !loading()) {
        <p class="opacity-70" data-testid="dash-empty" i18n="@@dash.empty">Для вашої ролі на цій сторінці нічого немає.</p>
      }
      <div class="grid max-w-6xl grid-cols-12 gap-4" data-testid="dash-grid">
        @for (b of d.blocks; track b.index) {
          <section class="col-span-12 rounded border border-ac-line p-4" [class]="span(b.width)" [attr.data-block]="b.index" [attr.data-kind]="b.kind">
            @if (b.label) {
              <h3 class="mb-2 font-semibold" data-testid="block-label">{{ b.label }}</h3>
            }
            @switch (b.kind) {
              @case ('text') {
                <p class="whitespace-pre-line" data-testid="block-text">{{ b.text }}</p>
              }
              @case ('tiles') {
                @if (b.metrics; as m) {
                  <app-page-metrics [metrics]="m" />
                }
              }
              @case ('list') {
                @if (b.rows; as r) {
                  @if (r.items.length === 0) {
                    <p class="opacity-70" data-testid="block-empty">{{ b.view?.page?.definition?.empty ?? emptyText }}</p>
                  } @else {
                    <div class="overflow-x-auto">
                      <table class="w-full text-left" data-testid="block-table">
                        <thead>
                          <tr>
                            @for (c of listColumns(b); track c.column) {
                              <th class="p-1.5 font-medium opacity-70">{{ c.label }}</th>
                            }
                            <th></th>
                          </tr>
                        </thead>
                        <tbody>
                          @for (row of r.items; track row.key) {
                            <tr [attr.data-row]="row.key" class="border-t border-ac-line">
                              @for (c of listColumns(b); track c.column) {
                                @let cell = cellOf(b, row, c);
                                <td class="p-1.5" [attr.data-field]="c.column">
                                  @if (cell.color) {
                                    <bp-chip [tone]="tone(cell.color)">{{ cell.text }}</bp-chip>
                                  } @else {
                                    {{ cell.text }}
                                  }
                                </td>
                              }
                              <td class="whitespace-nowrap p-1.5 text-right">
                                @for (i of row.actions; track i) {
                                  <button mat-button type="button" [attr.data-action]="i" (click)="rowAction(b, row, i)">{{ rowActionLabel(b, i) }}</button>
                                }
                              </td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                    @if (pageOf(b); as page) {
                      <a class="mt-1 inline-block text-sm underline" data-testid="block-all" [routerLink]="['/projects', projectId(), 'app', page]">
                        <ng-container i18n="@@dash.all">Усі</ng-container> ({{ r.total }})
                      </a>
                    }
                  }
                }
              }
              @case ('calendar') {
                @let agenda = agendaOf(b);
                @if (agenda.length === 0) {
                  <p class="opacity-70" data-testid="block-empty">{{ b.view?.page?.definition?.empty ?? emptyEvents }}</p>
                } @else {
                  <ul data-testid="block-agenda">
                    @for (a of agenda; track a.key) {
                      <li class="flex items-baseline gap-2 py-0.5">
                        @if (a.day) {
                          <span class="w-24 shrink-0 text-sm opacity-70">{{ a.day }}</span>
                        }
                        <button
                          type="button"
                          class="flex min-w-0 grow items-baseline gap-2 rounded px-1 text-left"
                          [attr.data-event]="a.event.row.key"
                          [attr.aria-label]="a.time + ': ' + a.title.text"
                          (click)="openEvent(b, a)"
                        >
                          <span class="shrink-0 tabular-nums">{{ a.time }}</span>
                          <span class="truncate rounded px-1.5" [class]="badge(a.color)">{{ a.title.text }}</span>
                          @if (a.subtitle) {
                            <span class="truncate opacity-70">{{ a.subtitle }}</span>
                          }
                        </button>
                      </li>
                    }
                  </ul>
                }
                @if (pageOf(b); as page) {
                  <a class="mt-1 inline-block text-sm underline" data-testid="block-all" [routerLink]="['/projects', projectId(), 'app', page]" i18n="@@dash.calendar">Відкрити календар</a>
                }
              }
              @case ('form') {
                @if (b.view; as formView) {
                  <app-page-form [projectId]="projectId()" [view]="formView" [pageKey]="b.source ?? null" (submitted)="formDone($event.result ?? null)" />
                }
              }
              @case ('actions') {
                <div class="flex flex-wrap gap-2" data-testid="block-actions">
                  @for (i of b.actions ?? []; track i) {
                    <button mat-stroked-button type="button" [attr.data-block-action]="i" (click)="blockAction(b, i)">{{ blockActionOf(b, i)?.label }}</button>
                  }
                </div>
              }
            }
          </section>
        }
      </div>
    }
  `,
})
export class PageDashboardComponent {
  private readonly api = inject(ProjectPagesApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = input.required<string>();
  readonly view = input.required<ProjectPageView>();
  /** Змінюється, коли дані треба перечитати. */
  readonly version = input(0);
  /** Готові дані чернетки (конструктор, F8-19): Api не опитується. */
  readonly preview = input<PageDashboard | null>(null);

  /** Відкрити сторінку застосунку: картка, форма чи бокова панель. */
  readonly open = output<ExpandOpen>();

  readonly definition = computed(() => this.view().page.definition);
  readonly dashboard = signal<PageDashboard | null>(null);
  readonly filterState = signal<Record<string, PageFilterValue>>({});
  private readonly chosen = signal<Record<string, PageFilterValue> | undefined>(undefined);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly notice = signal<{ state: PageActionResult['state']; text: string } | null>(null);

  protected readonly emptyText = $localize`:@@app.page.empty:Записів немає.`;
  protected readonly emptyEvents = $localize`:@@dash.noEvents:Подій немає.`;
  private readonly timezone = computed(() => this.view().timezone ?? 'UTC');
  private loadSub: Subscription | null = null;
  private initialisedFor = '';

  constructor() {
    effect(() => {
      const v = this.view();
      const id = v.page.id + v.page.updatedAt + (this.preview() ? JSON.stringify(v.page.definition.filters) : '');
      untracked(() => {
        if (this.initialisedFor === id) return;
        this.initialisedFor = id;
        this.chosen.set(undefined);
        this.filterState.set(emptyFilters(v.page.definition.filters ?? []));
        this.notice.set(null);
      });
    });
    effect(() => {
      const filters = this.chosen();
      this.version();
      this.projectId();
      this.view();
      this.preview();
      untracked(() => {
        if (this.initialisedFor) this.load(filters);
      });
    });
    this.destroyRef.onDestroy(() => this.loadSub?.unsubscribe());
  }

  protected span(width: number): string {
    return SPAN[Math.min(12, Math.max(1, width))] ?? SPAN[12];
  }

  protected badge(color: keyof typeof BADGE_CLASSES): string {
    return BADGE_CLASSES[color];
  }

  protected tone(color: keyof typeof BADGE_TONES): ChipTone {
    return BADGE_TONES[color];
  }

  filtersChanged(state: Record<string, PageFilterValue>): void {
    this.filterState.set(state);
    this.chosen.set(state);
  }

  /** Колонки короткого списку блоку: підписи й типи з Api, формат, мітки й кольори — з визначення. */
  protected listColumns(block: PageDashboardBlock): DisplayColumn[] {
    const view = block.view;
    if (!view) return [];
    const def = view.page.definition;
    return (def.columns ?? []).map((dc) => {
      const known = view.columns.find((c) => c.column === dc.column);
      const computedColumn = def.computed?.find((x) => x.key === dc.column);
      if (computedColumn) return { column: computedColumn.key, label: dc.label ?? computedColumn.label, type: 'number', format: dc.format, labels: dc.labels, computed: computedColumn };
      return { column: dc.column, label: known?.label ?? dc.label ?? dc.column, type: known?.type ?? 'string', format: dc.format ?? known?.format, labels: dc.labels, colors: dc.colors };
    });
  }

  protected cellOf(block: PageDashboardBlock, row: PageRow, column: DisplayColumn): CellView {
    return cellView(row, column, block.view?.timezone ?? this.timezone());
  }

  /** Сторінка, на яку веде «Усі» / «Відкрити календар»: лише блок із `page`, а не з власним визначенням. */
  protected pageOf(block: PageDashboardBlock): string | null {
    return this.definition().blocks?.[block.index]?.page ?? null;
  }

  protected rowActionLabel(block: PageDashboardBlock, index: number): string {
    return block.view?.page.definition.actions?.[index]?.label ?? '';
  }

  protected blockActionOf(block: PageDashboardBlock, index: number): PageAction | undefined {
    return this.definition().blocks?.[block.index]?.actions?.[index];
  }

  /** Події блоку `calendar` за часом; для тижня — з назвою дня. */
  protected agendaOf(block: PageDashboardBlock): AgendaItem[] {
    const view = block.view;
    const events = block.events?.events ?? [];
    if (!view) return [];
    const week = this.definition().blocks?.[block.index]?.range === 'week';
    const tz = view.timezone ?? this.timezone();
    return events.map((event, i) => {
      const line = eventLine(view, event, tz);
      const p = zonedParts(new Date(event.start), tz);
      const day = week ? new Intl.DateTimeFormat('uk-UA', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(p.y, p.m - 1, p.d))) : '';
      return { event, key: `${event.row.key}#${i}`, day, title: line.title, subtitle: line.subtitle, color: line.color, time: line.time };
    });
  }

  openEvent(block: PageDashboardBlock, item: AgendaItem): void {
    const target = block.view?.page.definition.calendar?.open;
    if (target) this.open.emit({ page: target.page, rowKey: item.event.row.key, mode: target.mode });
  }

  /** Дія над рядком списку блоку: перехід чи сценарій/видалення через сторінку блоку. */
  rowAction(block: PageDashboardBlock, row: PageRow, index: number): void {
    const action = block.view?.page.definition.actions?.[index];
    if (!action || !block.source) return;
    if (action.page) {
      this.open.emit({ page: action.page, rowKey: row.key, mode: action.mode });
      return;
    }
    if (action.confirm && !window.confirm(action.confirm)) return;
    this.run(this.api.runAction(this.projectId(), block.source, row.key, index), action.workflow ? $localize`:@@app.page.done:Готово.` : '');
  }

  /** Кнопка блоку `actions`: перехід (сторінка чи панель) або сценарій від імені користувача. */
  blockAction(block: PageDashboardBlock, index: number): void {
    const action = this.blockActionOf(block, index);
    if (!action) return;
    if (action.page) {
      this.open.emit({ page: action.page, mode: action.mode });
      return;
    }
    if (action.confirm && !window.confirm(action.confirm)) return;
    this.run(this.api.runBlockAction(this.projectId(), this.view().page.key, block.index, index), $localize`:@@app.page.done:Готово.`);
  }

  formDone(result: PageActionResult | null): void {
    if (result) this.show(result, '');
    this.reload();
  }

  private run(request: ReturnType<ProjectPagesApiService['runAction']>, fallback: string): void {
    this.error.set(null);
    this.notice.set(null);
    request.subscribe({
      next: (result) => {
        this.show(result, fallback);
        this.reload();
      },
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }

  private reload(): void {
    this.load(this.chosen());
  }

  /** Повідомлення сценарію (`message`) або запасний текст; відмова й помилка — червоним. */
  private show(result: PageActionResult, fallback: string): void {
    const text =
      result.message ||
      (result.state === 'running'
        ? $localize`:@@app.page.running:Сценарій ще виконується, результат буде у виконаннях проєкту.`
        : result.state === 'success'
          ? fallback
          : $localize`:@@app.page.failed:Дію не виконано.`);
    this.notice.set(text ? { state: result.state, text } : null);
  }

  private load(filters: Record<string, PageFilterValue> | undefined): void {
    this.loadSub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    const preview = this.preview();
    this.loadSub = (preview ? of(preview) : this.api.dashboard(this.projectId(), this.view().page.key, filters)).subscribe({
      next: (dashboard) => {
        this.dashboard.set(dashboard);
        this.loading.set(false);
        if (!this.chosen()) {
          // Значення за замовчуванням діяли на сервері — панель показує їх, поки людина не змінила фільтри.
          const applied: Record<string, PageFilterValue> = {};
          for (const block of dashboard.blocks) Object.assign(applied, block.rows?.filters ?? {}, block.events?.filters ?? {});
          this.filterState.set({ ...emptyFilters(this.definition().filters ?? []), ...applied });
        }
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.dashboard.set(null);
        this.error.set(apiErrorMessage(e));
      },
    });
  }
}
