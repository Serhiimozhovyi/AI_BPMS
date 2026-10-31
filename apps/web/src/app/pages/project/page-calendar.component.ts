import { ViewAsService } from '../../auth/view-as.service';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Subscription, of } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import {
  CalendarView,
  Civil,
  dayKey,
  localInput,
  sameDay,
  shift,
  todayIn,
  viewDays,
  viewRange,
  zonedParts,
  zonedToInstant,
} from '../../projects/calendar-time';
import { EventLine, eventLine } from '../../projects/calendar-line';
import { BADGE_CLASSES } from '../../projects/page-format';
import {
  PageAction,
  PageCalendarEvent,
  PageCalendarEvents,
  PageFilterValue,
  PageRow,
  ProjectPageView,
  ProjectPagesApiService,
} from '../../projects/project-pages-api.service';
import { PageFiltersComponent, emptyFilters } from './page-filters.component';
import { ExpandOpen } from './page-expand.component';

/** Висота години у вигляді тижня й дня, px. */
const HOUR_PX = 48;
const DEFAULT_FROM_HOUR = 7;
const DEFAULT_TO_HOUR = 21;
const MAX_MONTH_CHIPS = 3;

/** Подія з підготовленим показом: день, положення в сітці, смуга при перетині. */
interface Shown extends EventLine {
  event: PageCalendarEvent;
  id: string;
  top: number;
  height: number;
  lane: number;
  lanes: number;
}

interface DayColumn {
  day: Civil;
  key: string;
  today: boolean;
  label: string;
  events: Shown[];
}

/**
 * Календар (F8-17, `calendar`): тиждень, день чи місяць у часовому поясі застосунку. Подія — рядок таблиці; колір — за
 * міткою `color`, заголовок і підзаголовок — за колонками з визначення. Клік по події показує її (і відкриває `open`, якщо дій немає),
 * клік по вільній клітинці відкриває `create` з підставленим часом початку. Усе доступне з клавіатури: стрілки
 * переходять між клітинками, Enter створює запис.
 */
@Component({
  selector: 'app-page-calendar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, PageFiltersComponent],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p role="alert" class="mb-2 text-red-700" data-testid="cal-error">{{ error() }}</p>
    }
    <div class="mb-3 flex flex-wrap items-center gap-2" data-testid="cal-toolbar">
      <button mat-stroked-button type="button" data-testid="cal-prev" [attr.aria-label]="prevLabel" (click)="move(-1)">‹</button>
      <button mat-stroked-button type="button" data-testid="cal-today" (click)="goToday()" i18n="@@cal.today">Сьогодні</button>
      <button mat-stroked-button type="button" data-testid="cal-next" [attr.aria-label]="nextLabel" (click)="move(1)">›</button>
      <h3 class="mx-2 text-lg font-semibold" aria-live="polite" data-testid="cal-range">{{ rangeLabel() }}</h3>
      @if (views().length > 1) {
        <div class="ml-auto flex gap-1" role="group" [attr.aria-label]="viewLabel">
          @for (v of views(); track v) {
            <button
              mat-stroked-button
              type="button"
              [attr.data-cal-view]="v"
              [attr.aria-pressed]="mode() === v"
              [class.bg-bp-tonal]="mode() === v"
              (click)="setMode(v)"
            >
              {{ viewName(v) }}
            </button>
          }
        </div>
      }
    </div>

    @if (definition().filters?.length) {
      <app-page-filters [filters]="definition().filters!" [options]="view().filterOptions" [values]="filterState()" (changed)="filtersChanged($event)" />
    }
    @if (truncated()) {
      <p role="status" class="mb-2 opacity-70" data-testid="cal-truncated" i18n="@@cal.truncated">Подій забагато — показано перші. Звузьте фільтри чи вигляд.</p>
    }

    @if (mode() === 'month') {
      <div class="max-w-6xl overflow-x-auto">
        <div class="grid min-w-[40rem] grid-cols-7 border-l border-t border-bp-line" data-testid="cal-month">
          @for (name of weekdayNames; track name) {
            <div class="border-b border-r border-bp-line bg-bp-surface-2 p-1 text-center text-sm opacity-80">{{ name }}</div>
          }
          @for (col of columns(); track col.key) {
            <div
              class="min-h-24 border-b border-r border-bp-line p-1"
              [class.bg-bp-surface-2]="!inMonth(col.day)"
              [class.opacity-60]="!inMonth(col.day)"
              [attr.data-day]="col.key"
            >
              <button
                type="button"
                class="mb-1 rounded px-1 text-sm"
                [class.font-bold]="col.today"
                [class.bg-bp-tonal]="col.today"
                [attr.data-day-open]="col.key"
                [attr.aria-label]="col.label"
                (click)="openDay(col.day)"
              >
                {{ col.day.d }}
              </button>
              @for (s of col.events.slice(0, maxChips); track s.id) {
                <button
                  type="button"
                  class="mb-0.5 block w-full truncate rounded px-1 text-left text-sm"
                  [class]="badge(s.color)"
                  [attr.data-event]="s.event.row.key"
                  [attr.aria-label]="s.time + ': ' + s.title.text"
                  (click)="select(s)"
                >
                  {{ s.title.text }}
                </button>
              }
              @if (col.events.length > maxChips) {
                <button type="button" class="block text-sm underline" [attr.data-more]="col.key" (click)="openDay(col.day)">
                  {{ moreText(col.events.length - maxChips) }}
                </button>
              }
            </div>
          }
        </div>
      </div>
    } @else {
      <div class="max-w-6xl overflow-x-auto">
        <div class="min-w-[40rem]" data-testid="cal-grid" [attr.data-view]="mode()">
          <div class="grid" [style.grid-template-columns]="gridColumns()">
            <div></div>
            @for (col of columns(); track col.key) {
              <div class="border-b border-bp-line p-1 text-center" [class.font-bold]="col.today" [attr.data-day-head]="col.key">{{ col.label }}</div>
            }
          </div>
          <div class="grid" [style.grid-template-columns]="gridColumns()">
            <div class="relative" [style.height.px]="gridHeight()">
              @for (h of hours(); track h) {
                <div class="absolute right-1 text-xs opacity-70" [style.top.px]="(h - fromHour()) * hourPx" aria-hidden="true">{{ hourText(h) }}</div>
              }
            </div>
            @for (col of columns(); track col.key; let ci = $index) {
              <div class="relative border-l border-bp-line" [style.height.px]="gridHeight()" [attr.data-day]="col.key" [class.bg-bp-surface-2]="col.today">
                @for (h of hours(); track h) {
                  @if (canCreate()) {
                    <button
                      type="button"
                      class="block w-full border-b border-bp-line hover:bg-bp-tonal"
                      [style.height.px]="hourPx"
                      [attr.data-slot]="slotId(col.day, h)"
                      [attr.aria-label]="slotLabel(col.day, h)"
                      [attr.tabindex]="slotId(col.day, h) === activeSlot() ? 0 : -1"
                      (focus)="activeSlot.set(slotId(col.day, h))"
                      (click)="create(col.day, h)"
                      (keydown)="slotKey($event, ci, h)"
                    ></button>
                  } @else {
                    <div class="border-b border-bp-line" [style.height.px]="hourPx"></div>
                  }
                }
                @for (s of col.events; track s.id) {
                  @if (s.height > 0) {
                  <button
                    type="button"
                    class="absolute z-10 overflow-hidden rounded border border-bp-line px-1 text-left text-sm"
                    [class]="badge(s.color)"
                    [class.ring-2]="selected()?.id === s.id"
                    [style.top.px]="s.top"
                    [style.height.px]="s.height"
                    [style.left.%]="(s.lane / s.lanes) * 100"
                    [style.width.%]="100 / s.lanes"
                    [attr.data-event]="s.event.row.key"
                    [attr.aria-label]="s.time + ': ' + s.title.text"
                    (click)="select(s)"
                  >
                    <span class="block truncate font-semibold">{{ s.title.text }}</span>
                    @if (s.subtitle) {
                      <span class="block truncate opacity-80">{{ s.subtitle }}</span>
                    }
                  </button>
                  }
                }
              </div>
            }
          </div>
        </div>
      </div>
      @if (outside().length) {
        <div class="mt-2" data-testid="cal-outside">
          <span class="opacity-70" i18n="@@cal.outside">Поза годинами сітки:</span>
          @for (s of outside(); track s.id) {
            <button type="button" class="ml-2 rounded px-2 text-sm" [class]="badge(s.color)" [attr.data-event]="s.event.row.key" (click)="select(s)">
              {{ s.time }} · {{ s.title.text }}
            </button>
          }
        </div>
      }
    }

    @if (!loading() && !error() && eventCount() === 0) {
      <p class="mt-3 opacity-70" data-testid="cal-empty">{{ definition().empty ?? emptyText }}</p>
    }

    @if (selected(); as s) {
      <section class="mt-4 max-w-xl rounded border border-bp-line p-4" data-testid="cal-selected" aria-live="polite">
        <div class="flex items-start gap-2">
          <div class="grow">
            <div class="font-semibold" data-testid="cal-selected-title">{{ s.title.text }}</div>
            @if (s.subtitle) {
              <div class="opacity-70">{{ s.subtitle }}</div>
            }
            <div class="opacity-70">{{ s.time }}</div>
          </div>
          <button type="button" class="rounded px-2" data-testid="cal-selected-close" [attr.aria-label]="closeLabel" (click)="selected.set(null)">×</button>
        </div>
        <div class="mt-2 flex flex-wrap gap-1">
          @if (definition().calendar?.open; as target) {
            <button mat-flat-button type="button" data-testid="cal-open" (click)="openEvent(s, target.page, target.mode)" i18n="@@cal.open">Відкрити</button>
          }
          @for (i of s.event.row.actions; track i) {
            <span class="inline-flex" [attr.title]="readOnly() && !definition().actions![i].page ? readOnlyTip : null"><button mat-stroked-button type="button" [attr.data-action]="i" [disabled]="readOnly() && !definition().actions![i].page" (click)="runAction(i, s.event.row)">
              {{ definition().actions![i].label }}
            </button></span>
          }
        </div>
      </section>
    }
  `,
})
export class PageCalendarComponent {
  private readonly api = inject(ProjectPagesApiService);
  /** Перегляд «як користувач» (F12-02): дії, що змінюють дані, неактивні. */
  protected readonly readOnly = inject(ViewAsService).active;
  protected readonly readOnlyTip = $localize`:@@viewAs.disabled:У режимі перегляду дані не змінюються`;
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = input.required<string>();
  readonly view = input.required<ProjectPageView>();
  /** Змінюється, коли дані треба перечитати (збережено форму, виконано дію). */
  readonly version = input(0);
  /** Готові події чернетки (конструктор, F8-19): Api не опитується. */
  readonly preview = input<PageCalendarEvents | null>(null);

  /** Відкрити сторінку застосунку: картку чи форму події або створення із часом початку. */
  readonly open = output<ExpandOpen>();
  /** Дія над подією: перехід виконує хост, сценарій — запит до Api. */
  readonly act = output<{ action: PageAction; index: number; row: PageRow }>();

  protected readonly hourPx = HOUR_PX;
  protected readonly maxChips = MAX_MONTH_CHIPS;
  protected readonly weekdayNames = [1, 2, 3, 4, 5, 6, 7].map((n) => new Intl.DateTimeFormat('uk-UA', { weekday: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(2026, 10, 1 + n))));
  protected readonly prevLabel = $localize`:@@cal.prev:Раніше`;
  protected readonly nextLabel = $localize`:@@cal.next:Пізніше`;
  protected readonly viewLabel = $localize`:@@cal.views:Вигляд календаря`;
  protected readonly closeLabel = $localize`:@@cal.close:Закрити`;
  protected readonly emptyText = $localize`:@@cal.empty:Подій немає.`;

  readonly definition = computed(() => this.view().page.definition);
  private readonly timezone = computed(() => this.view().timezone ?? 'UTC');
  readonly views = computed<CalendarView[]>(() => {
    const configured = this.definition().calendar?.views;
    return configured?.length ? configured : ['week', 'day', 'month'];
  });
  readonly mode = signal<CalendarView>('week');
  readonly anchor = signal<Civil>({ y: 1970, m: 1, d: 1 });
  readonly filterState = signal<Record<string, PageFilterValue>>({});
  /** Фільтри, обрані людиною; до першої зміни діють значення за замовчуванням з Api. */
  private readonly chosen = signal<Record<string, PageFilterValue> | undefined>(undefined);
  readonly events = signal<PageCalendarEvent[]>([]);
  readonly truncated = signal(false);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly selected = signal<Shown | null>(null);
  readonly activeSlot = signal('');

  protected readonly days = computed(() => viewDays(this.mode(), this.anchor()));
  protected readonly fromHour = computed(() => this.definition().calendar?.fromHour ?? DEFAULT_FROM_HOUR);
  protected readonly toHour = computed(() => Math.max(this.fromHour() + 1, this.definition().calendar?.toHour ?? DEFAULT_TO_HOUR));
  protected readonly hours = computed(() => Array.from({ length: this.toHour() - this.fromHour() }, (_, i) => this.fromHour() + i));
  protected readonly gridHeight = computed(() => this.hours().length * HOUR_PX);
  protected readonly canCreate = computed(() => !!this.definition().create && !this.readOnly());
  protected readonly gridColumns = computed(() => `3.5rem repeat(${this.days().length}, minmax(0, 1fr))`);
  private readonly shown = computed(() => this.events().map((event, i) => this.present(event, i)));
  protected readonly columns = computed<DayColumn[]>(() => {
    const today = todayIn(this.timezone());
    const grid = this.mode() !== 'month';
    const all = this.shown();
    return this.days().map((day) => {
      const key = dayKey(day);
      const own = all.filter((s) => dayKey(zonedParts(new Date(s.event.start), this.timezone())) === key);
      return {
        day,
        key,
        today: sameDay(day, today),
        label: this.dayLabel(day),
        events: grid ? this.place(own, day) : own,
      };
    });
  });
  /** Події, що не потрапили у видимі години сітки. */
  protected readonly outside = computed(() => (this.mode() === 'month' ? [] : this.columns().flatMap((c) => c.events.filter((s) => s.height <= 0))));
  protected readonly eventCount = computed(() => this.events().length);
  protected readonly rangeLabel = computed(() => {
    const days = this.days();
    const first = days[0];
    const last = days[days.length - 1];
    const utc = (c: Civil) => new Date(Date.UTC(c.y, c.m - 1, c.d));
    if (this.mode() === 'day') return new Intl.DateTimeFormat('uk-UA', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' }).format(utc(first));
    if (this.mode() === 'month') {
      return new Intl.DateTimeFormat('uk-UA', { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(utc(this.anchor()));
    }
    const short = new Intl.DateTimeFormat('uk-UA', { day: 'numeric', month: 'short', timeZone: 'UTC' });
    return `${short.format(utc(first))} – ${short.format(utc(last))} ${last.y}`;
  });

  private loadSub: Subscription | null = null;
  private initialisedFor = '';

  constructor() {
    // Нова сторінка (чи її визначення) — початкові вигляд, дата й фільтри.
    effect(() => {
      const v = this.view();
      const id = v.page.id + v.page.updatedAt + (this.preview() ? JSON.stringify(v.page.definition.calendar) : '');
      untracked(() => {
        if (this.initialisedFor === id) return;
        this.initialisedFor = id;
        const wanted = v.page.definition.calendar?.default;
        const views = this.views();
        this.mode.set(wanted && views.includes(wanted) ? wanted : views[0]);
        this.anchor.set(todayIn(this.timezone()));
        this.filterState.set(emptyFilters(v.page.definition.filters ?? []));
        this.chosen.set(undefined);
        this.selected.set(null);
      });
    });
    // Діапазон, фільтри чи версія змінилися — читаємо події.
    effect(() => {
      const days = this.days();
      this.version();
      const filters = this.chosen();
      this.projectId();
      this.view();
      this.preview();
      untracked(() => {
        if (this.initialisedFor) this.load(days, filters);
      });
    });
    this.destroyRef.onDestroy(() => this.loadSub?.unsubscribe());
  }

  protected viewName(view: CalendarView): string {
    return view === 'week' ? $localize`:@@cal.week:Тиждень` : view === 'day' ? $localize`:@@cal.day:День` : $localize`:@@cal.month:Місяць`;
  }

  protected hourText(h: number): string {
    return `${String(h).padStart(2, '0')}:00`;
  }

  protected badge(color: keyof typeof BADGE_CLASSES): string {
    return BADGE_CLASSES[color];
  }

  protected moreText(count: number): string {
    return $localize`:@@cal.more:ще ${count}:count:`;
  }

  protected inMonth(day: Civil): boolean {
    return day.m === this.anchor().m;
  }

  protected slotId(day: Civil, hour: number): string {
    return localInput(day, hour);
  }

  protected slotLabel(day: Civil, hour: number): string {
    const date = new Intl.DateTimeFormat('uk-UA', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(day.y, day.m - 1, day.d)));
    return $localize`:@@cal.createAt:Створити запис: ${date}:date:, ${this.hourText(hour)}:time:`;
  }

  setMode(mode: CalendarView): void {
    this.mode.set(mode);
    this.selected.set(null);
  }

  move(direction: 1 | -1): void {
    this.anchor.set(shift(this.mode(), this.anchor(), direction));
    this.selected.set(null);
  }

  goToday(): void {
    this.anchor.set(todayIn(this.timezone()));
    this.selected.set(null);
  }

  openDay(day: Civil): void {
    this.anchor.set(day);
    if (this.views().includes('day')) this.mode.set('day');
    this.selected.set(null);
  }

  filtersChanged(state: Record<string, PageFilterValue>): void {
    this.filterState.set(state);
    this.chosen.set(state);
    this.selected.set(null);
  }

  /** Клік по події: сама картка, якщо дій немає, інакше — панель з діями. */
  select(shown: Shown): void {
    const target = this.definition().calendar?.open;
    if (target && shown.event.row.actions.length === 0) {
      this.openEvent(shown, target.page, target.mode);
      return;
    }
    this.selected.set(shown);
  }

  openEvent(shown: Shown, page: string, mode?: ExpandOpen['mode']): void {
    this.open.emit({ page, rowKey: shown.event.row.key, mode });
  }

  runAction(index: number, row: PageRow): void {
    const action = this.definition().actions?.[index];
    if (action) this.act.emit({ action, index, row });
  }

  /** Вільна клітинка: форма створення з часом початку (`datetime-local` у поясі застосунку). */
  create(day: Civil, hour: number): void {
    const create = this.definition().create;
    const start = this.definition().calendar?.start;
    if (!create || !start) return;
    this.open.emit({ page: create.page, mode: create.mode, prefill: { [start]: localInput(day, hour) } });
  }

  /** Стрілки переходять між клітинками сітки: вгору/вниз — години, вліво/вправо — дні. */
  protected slotKey(event: KeyboardEvent, dayIndex: number, hour: number): void {
    const moves: Record<string, [number, number]> = { ArrowUp: [0, -1], ArrowDown: [0, 1], ArrowLeft: [-1, 0], ArrowRight: [1, 0] };
    const move = moves[event.key];
    if (!move) return;
    const day = this.days()[dayIndex + move[0]];
    const nextHour = hour + move[1];
    if (!day || nextHour < this.fromHour() || nextHour >= this.toHour()) return;
    event.preventDefault();
    const id = this.slotId(day, nextHour);
    this.activeSlot.set(id);
    this.host.nativeElement.querySelector<HTMLElement>(`[data-slot="${id}"]`)?.focus();
  }

  private load(days: Civil[], filters: Record<string, PageFilterValue> | undefined): void {
    const { from, to } = viewRange(days, this.timezone());
    this.loadSub?.unsubscribe();
    this.loading.set(true);
    this.error.set(null);
    const preview = this.preview();
    this.loadSub = (preview ? of(preview) : this.api.calendar(this.projectId(), this.view().page.key, from, to, filters)).subscribe({
      next: (result) => {
        this.events.set(result.events);
        this.truncated.set(result.truncated);
        this.loading.set(false);
        if (!this.chosen()) {
          this.filterState.set({ ...emptyFilters(this.definition().filters ?? []), ...(result.filters ?? {}) });
        }
        if (!this.activeSlot()) this.activeSlot.set(this.slotId(this.days()[0], this.fromHour()));
        const keep = this.selected();
        if (keep) this.selected.set(this.shown().find((s) => s.event.row.key === keep.event.row.key) ?? null);
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.events.set([]);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  private dayLabel(day: Civil): string {
    return new Intl.DateTimeFormat('uk-UA', { weekday: 'short', day: 'numeric', month: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(day.y, day.m - 1, day.d)));
  }

  private present(event: PageCalendarEvent, index: number): Shown {
    return { ...eventLine(this.view(), event, this.timezone()), event, id: `${event.row.key}#${index}`, top: 0, height: 0, lane: 0, lanes: 1 };
  }

  /** Положення подій дня в сітці годин; перетини ділять день на смуги. */
  private place(events: Shown[], day: Civil): Shown[] {
    const tz = this.timezone();
    const dayStart = zonedToInstant(day, this.fromHour(), 0, tz).getTime();
    const dayEnd = zonedToInstant(day, this.toHour() >= 24 ? 23 : this.toHour(), this.toHour() >= 24 ? 59 : 0, tz).getTime() + (this.toHour() >= 24 ? 60_000 : 0);
    const placed = events
      .map((s) => {
        const from = Math.max(new Date(s.event.start).getTime(), dayStart);
        const to = Math.min(new Date(s.event.end).getTime(), dayEnd);
        const top = ((from - dayStart) / 3_600_000) * HOUR_PX;
        const height = to > from ? Math.max(18, ((to - from) / 3_600_000) * HOUR_PX - 2) : 0;
        return { ...s, top, height };
      })
      .sort((a, b) => a.top - b.top || b.height - a.height);

    // Смуги: подія йде в першу вільну, кількість смуг — максимум у групі, що перетинається.
    const result: Shown[] = [];
    let group: Shown[] = [];
    let groupEnd = -1;
    const closeGroup = () => {
      const lanes = Math.max(1, ...group.map((s) => s.lane + 1));
      for (const s of group) result.push({ ...s, lanes });
      group = [];
    };
    const laneEnds: number[] = [];
    for (const s of placed) {
      if (s.height <= 0) {
        result.push(s);
        continue;
      }
      if (group.length && s.top >= groupEnd) {
        closeGroup();
        laneEnds.length = 0;
        groupEnd = -1;
      }
      let lane = laneEnds.findIndex((end) => end <= s.top);
      if (lane < 0) lane = laneEnds.length;
      laneEnds[lane] = s.top + s.height;
      groupEnd = Math.max(groupEnd, s.top + s.height);
      group.push({ ...s, lane });
    }
    if (group.length) closeGroup();
    return result;
  }
}
