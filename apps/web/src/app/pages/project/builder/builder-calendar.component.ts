import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { labelsText, parseLabels } from '../../../projects/page-builder-options';
import { PageCalendar, PageCalendarColumn } from '../../../projects/project-pages-api.service';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../../shared/select-search/select-search.component';

type View = 'week' | 'day' | 'month';
const VIEWS: readonly View[] = ['week', 'day', 'month'];

/**
 * Налаштування календаря (F8-19): звідки початок і кінець події, що показати в картці, які режими доступні,
 * яка сторінка відкривається за кліком (з бічною панеллю чи переходом) і години сітки.
 */
@Component({
  selector: 'app-builder-calendar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    <h3 class="mb-1 mt-4 font-medium" i18n="@@bc.title">Календар</h3>
    <div class="mb-2 grid grid-cols-[minmax(9rem,auto)_1fr] items-center gap-x-4 gap-y-2" data-testid="b-calendar">
      <label id="bc-start-label" for="bc-start" i18n="@@bc.start">Початок події</label>
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select id="bc-start" aria-labelledby="bc-start-label" data-testid="bc-start" [value]="calendar().start" (selectionChange)="patch({ start: $event.value })">
          <bp-select-search />
          <mat-option value="">—</mat-option>
          @for (c of columns(); track c) {
            <mat-option [value]="c">{{ c }}</mat-option>
          }
        </mat-select>
      </mat-form-field>

      <label id="bc-end-label" for="bc-end" i18n="@@bc.end">Тривалість або кінець</label>
      <span class="flex flex-wrap items-center gap-2">
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select id="bc-end" aria-labelledby="bc-end-label" data-testid="bc-end-mode" [value]="endMode()" (selectionChange)="setEndMode($event.value)">
            <bp-select-search />
            <mat-option value="default" i18n="@@bc.end.default">однакова, хвилин</mat-option>
            <mat-option value="duration" i18n="@@bc.end.duration">колонка тривалості (хв)</mat-option>
            <mat-option value="end" i18n="@@bc.end.end">колонка кінця</mat-option>
          </mat-select>
        </mat-form-field>
        @if (endMode() === 'default') {
          <input class="w-20 border p-1" type="number" min="5" aria-label="Хвилин" i18n-aria-label="@@bc.minutes" [value]="calendar().defaultMinutes ?? 60" (input)="patch({ defaultMinutes: +$any($event.target).value || undefined })" />
        } @else {
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select aria-label="Колонка" i18n-aria-label="@@bc.column" [value]="(endMode() === 'end' ? calendar().end : calendar().duration) ?? ''" (selectionChange)="setEndColumn($event.value)">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              @for (c of columns(); track c) {
                <mat-option [value]="c">{{ c }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
      </span>

      @for (slot of slots; track slot.key) {
        <span>{{ slot.label }}</span>
        <span class="flex flex-wrap items-start gap-2">
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select [attr.data-testid]="'bc-' + slot.key" [aria-label]="slot.label" [value]="calendar()[slot.key]?.column ?? ''" (selectionChange)="setSlot(slot.key, { column: $event.value })">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              @for (c of columns(); track c) {
                <mat-option [value]="c">{{ c }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          @if (calendar()[slot.key]; as col) {
            <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
              <mat-select aria-label="Формат" i18n-aria-label="@@bc.format" [value]="col.format ?? ''" (selectionChange)="setSlot(slot.key, { format: $event.value || undefined })">
                <bp-select-search />
                @for (f of ['', 'text', 'time', 'datetime', 'badge', 'phone', 'enum']; track f) {
                  <mat-option [value]="f">{{ f || '—' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
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
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Режим за замовчуванням" i18n-aria-label="@@bc.default" [value]="calendar().default ?? ''" (selectionChange)="patch({ default: $event.value || undefined })">
            <bp-select-search />
            <mat-option value="" i18n="@@bc.defaultNone">за замовчуванням — перший</mat-option>
            @for (v of viewsOf(); track v) {
              <mat-option [value]="v">{{ viewName(v) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </span>

      <span i18n="@@bc.hours">Години сітки</span>
      <span class="flex items-center gap-2">
        <input class="w-16 border p-1" type="number" min="0" max="23" aria-label="З години" i18n-aria-label="@@bc.fromHour" [value]="calendar().fromHour ?? ''" (input)="patch({ fromHour: num($any($event.target).value) })" />
        —
        <input class="w-16 border p-1" type="number" min="1" max="24" aria-label="До години" i18n-aria-label="@@bc.toHour" [value]="calendar().toHour ?? ''" (input)="patch({ toHour: num($any($event.target).value) })" />
      </span>

      <label id="bc-open-label" for="bc-open" i18n="@@bc.open">Клік по події відкриває</label>
      <span class="flex flex-wrap items-center gap-2">
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select id="bc-open" aria-labelledby="bc-open-label" data-testid="bc-open" [value]="calendar().open?.page ?? ''" (selectionChange)="setOpen($event.value, calendar().open?.mode)">
            <bp-select-search />
            <mat-option value="" i18n="@@bc.openNone">нічого</mat-option>
            @for (p of pageKeys(); track p) {
              <mat-option [value]="p">{{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @if (calendar().open) {
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select aria-label="Як відкрити" i18n-aria-label="@@bc.mode" [value]="calendar().open?.mode ?? 'navigate'" (selectionChange)="setOpen(calendar().open?.page ?? '', $event.value)">
              <bp-select-search />
              <mat-option value="navigate" i18n="@@bc.mode.navigate">переходом</mat-option>
              <mat-option value="drawer" i18n="@@bc.mode.drawer">бічною панеллю</mat-option>
            </mat-select>
          </mat-form-field>
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
