import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { labelsText, parseLabels } from '../../../projects/page-builder-options';
import { PageCalendar, PageCalendarColumn } from '../../../projects/project-pages-api.service';

type View = 'week' | 'day' | 'month';
const VIEWS: readonly View[] = ['week', 'day', 'month'];

/**
 * Налаштування календаря (F8-19): звідки початок і кінець події, що показати в картці, які режими доступні,
 * яка сторінка відкривається за кліком (з бічною панеллю чи переходом) і години сітки.
 */
@Component({
  selector: 'app-builder-calendar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3 class="mb-1 mt-4 font-medium" i18n="@@bc.title">Календар</h3>
    <div class="mb-2 grid grid-cols-[minmax(9rem,auto)_1fr] items-center gap-x-4 gap-y-2" data-testid="b-calendar">
      <label for="bc-start" i18n="@@bc.start">Початок події</label>
      <select id="bc-start" class="border p-1" data-testid="bc-start" [value]="calendar().start" (change)="patch({ start: $any($event.target).value })">
        <option value="" [selected]="!calendar().start">—</option>
        @for (c of columns(); track c) {
          <option [value]="c" [selected]="c === calendar().start">{{ c }}</option>
        }
      </select>

      <label for="bc-end" i18n="@@bc.end">Тривалість або кінець</label>
      <span class="flex flex-wrap items-center gap-2">
        <select id="bc-end" class="border p-1" data-testid="bc-end-mode" [value]="endMode()" (change)="setEndMode($any($event.target).value)">
          <option value="default" [selected]="endMode() === 'default'" i18n="@@bc.end.default">однакова, хвилин</option>
          <option value="duration" [selected]="endMode() === 'duration'" i18n="@@bc.end.duration">колонка тривалості (хв)</option>
          <option value="end" [selected]="endMode() === 'end'" i18n="@@bc.end.end">колонка кінця</option>
        </select>
        @if (endMode() === 'default') {
          <input class="w-20 border p-1" type="number" min="5" aria-label="Хвилин" i18n-aria-label="@@bc.minutes" [value]="calendar().defaultMinutes ?? 60" (input)="patch({ defaultMinutes: +$any($event.target).value || undefined })" />
        } @else {
          <select class="border p-1" aria-label="Колонка" i18n-aria-label="@@bc.column" [value]="(endMode() === 'end' ? calendar().end : calendar().duration) ?? ''" (change)="setEndColumn($any($event.target).value)">
            <option value="" [selected]="true">—</option>
            @for (c of columns(); track c) {
              <option [value]="c" [selected]="c === (endMode() === 'end' ? calendar().end : calendar().duration)">{{ c }}</option>
            }
          </select>
        }
      </span>

      @for (slot of slots; track slot.key) {
        <span>{{ slot.label }}</span>
        <span class="flex flex-wrap items-start gap-2">
          <select class="border p-1" [attr.data-testid]="'bc-' + slot.key" [attr.aria-label]="slot.label" [value]="calendar()[slot.key]?.column ?? ''" (change)="setSlot(slot.key, { column: $any($event.target).value })">
            <option value="" [selected]="!calendar()[slot.key]">—</option>
            @for (c of columns(); track c) {
              <option [value]="c" [selected]="c === calendar()[slot.key]?.column">{{ c }}</option>
            }
          </select>
          @if (calendar()[slot.key]; as col) {
            <select class="border p-1" aria-label="Формат" i18n-aria-label="@@bc.format" [value]="col.format ?? ''" (change)="setSlot(slot.key, { format: $any($event.target).value || undefined })">
              @for (f of ['', 'text', 'time', 'datetime', 'badge', 'phone', 'enum']; track f) {
                <option [value]="f" [selected]="f === (col.format ?? '')">{{ f || '—' }}</option>
              }
            </select>
            <textarea
              class="h-14 w-56 border p-1 text-sm"
              placeholder="значення=Підпис|колір"
              i18n-placeholder="@@bc.labels"
              [value]="labels(col)"
              (change)="setLabels(slot.key, $any($event.target).value)"
            ></textarea>
          }
        </span>
      }

      <span i18n="@@bc.views">Режими</span>
      <span class="flex flex-wrap items-center gap-3">
        @for (v of views; track v) {
          <label class="inline-flex items-center gap-1">
            <input type="checkbox" [attr.data-view]="v" [checked]="viewsOf().includes(v)" (change)="toggleView(v, $any($event.target).checked)" />
            {{ viewName(v) }}
          </label>
        }
        <select class="border p-1" aria-label="Режим за замовчуванням" i18n-aria-label="@@bc.default" [value]="calendar().default ?? ''" (change)="patch({ default: $any($event.target).value || undefined })">
          <option value="" [selected]="!calendar().default" i18n="@@bc.defaultNone">за замовчуванням — перший</option>
          @for (v of viewsOf(); track v) {
            <option [value]="v" [selected]="v === calendar().default">{{ viewName(v) }}</option>
          }
        </select>
      </span>

      <span i18n="@@bc.hours">Години сітки</span>
      <span class="flex items-center gap-2">
        <input class="w-16 border p-1" type="number" min="0" max="23" aria-label="З години" i18n-aria-label="@@bc.fromHour" [value]="calendar().fromHour ?? ''" (input)="patch({ fromHour: num($any($event.target).value) })" />
        —
        <input class="w-16 border p-1" type="number" min="1" max="24" aria-label="До години" i18n-aria-label="@@bc.toHour" [value]="calendar().toHour ?? ''" (input)="patch({ toHour: num($any($event.target).value) })" />
      </span>

      <label for="bc-open" i18n="@@bc.open">Клік по події відкриває</label>
      <span class="flex flex-wrap items-center gap-2">
        <select id="bc-open" class="border p-1" data-testid="bc-open" [value]="calendar().open?.page ?? ''" (change)="setOpen($any($event.target).value, calendar().open?.mode)">
          <option value="" [selected]="!calendar().open" i18n="@@bc.openNone">нічого</option>
          @for (p of pageKeys(); track p) {
            <option [value]="p" [selected]="p === calendar().open?.page">{{ p }}</option>
          }
        </select>
        @if (calendar().open) {
          <select class="border p-1" aria-label="Як відкрити" i18n-aria-label="@@bc.mode" [value]="calendar().open?.mode ?? 'navigate'" (change)="setOpen(calendar().open?.page ?? '', $any($event.target).value)">
            <option value="navigate" [selected]="calendar().open?.mode !== 'drawer'" i18n="@@bc.mode.navigate">переходом</option>
            <option value="drawer" [selected]="calendar().open?.mode === 'drawer'" i18n="@@bc.mode.drawer">бічною панеллю</option>
          </select>
        }
      </span>
    </div>
  `,
})
export class BuilderCalendarComponent {
  readonly calendar = input.required<PageCalendar>();
  readonly columns = input.required<string[]>();
  readonly pageKeys = input.required<string[]>();
  readonly calendarChange = output<PageCalendar>();

  protected readonly views = VIEWS;
  protected readonly slots: { key: 'title' | 'subtitle' | 'color'; label: string }[] = [
    { key: 'title', label: $localize`:@@bc.slot.title:Заголовок події` },
    { key: 'subtitle', label: $localize`:@@bc.slot.subtitle:Підзаголовок` },
    { key: 'color', label: $localize`:@@bc.slot.color:Колір (за значенням)` },
  ];

  protected endMode(): 'default' | 'duration' | 'end' {
    const c = this.calendar();
    return c.end ? 'end' : c.duration ? 'duration' : 'default';
  }

  protected viewsOf(): View[] {
    return this.calendar().views?.length ? this.calendar().views! : [...VIEWS];
  }

  protected viewName(v: View): string {
    return v === 'week' ? $localize`:@@bc.view.week:Тиждень` : v === 'day' ? $localize`:@@bc.view.day:День` : $localize`:@@bc.view.month:Місяць`;
  }

  protected num(value: string): number | undefined {
    return value === '' ? undefined : Number(value);
  }

  protected labels(col: PageCalendarColumn): string {
    return labelsText(col.labels, col.colors);
  }

  protected patch(patch: Partial<PageCalendar>): void {
    const next: PageCalendar = { ...this.calendar(), ...patch };
    for (const key of Object.keys(next) as (keyof PageCalendar)[]) if (next[key] === undefined) delete next[key];
    this.calendarChange.emit(next);
  }

  protected setEndMode(mode: string): void {
    this.patch({ end: undefined, duration: undefined, defaultMinutes: mode === 'default' ? (this.calendar().defaultMinutes ?? 60) : undefined });
  }

  protected setEndColumn(column: string): void {
    this.patch(this.endMode() === 'end' ? { end: column || undefined } : { duration: column || undefined });
  }

  protected setSlot(key: 'title' | 'subtitle' | 'color', patch: Partial<PageCalendarColumn>): void {
    const current = this.calendar()[key];
    const next = { ...(current ?? { column: '' }), ...patch };
    this.patch({ [key]: next.column ? next : undefined });
  }

  protected setLabels(key: 'title' | 'subtitle' | 'color', text: string): void {
    const { labels, colors } = parseLabels(text);
    this.setSlot(key, { labels, colors });
  }

  protected toggleView(view: View, on: boolean): void {
    const list = VIEWS.filter((v) => (v === view ? on : this.viewsOf().includes(v)));
    const keep = list.length ? list : this.viewsOf();
    this.patch({ views: keep.length === VIEWS.length ? undefined : keep, default: this.calendar().default && keep.includes(this.calendar().default!) ? this.calendar().default : undefined });
  }

  protected setOpen(page: string, mode: string | undefined): void {
    this.patch({ open: page ? { page, mode: mode === 'drawer' ? 'drawer' : undefined } : undefined });
  }
}
