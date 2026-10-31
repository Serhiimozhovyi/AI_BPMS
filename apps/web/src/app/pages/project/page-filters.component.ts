import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { PageFilterValue, PageOption, PageUserFilter } from '../../projects/project-pages-api.service';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';

/** Періоди фільтра `period`. */
export const PERIODS = ['today', 'week', 'month'] as const;

/** Значення вибору «від–до» у випадному списку періоду. */
const CUSTOM = 'custom';

/** Усі фільтри панелі з порожніми значеннями. */
export function emptyFilters(filters: PageUserFilter[]): Record<string, PageFilterValue> {
  return Object.fromEntries(filters.map((f) => [f.column, { values: [] } as PageFilterValue]));
}

/**
 * Панель фільтрів над списком (F8-16): вибір значення (`select`), стани-чипи (`chips`), період (`period`: сьогодні, тиждень,
 * місяць чи від–до) і пошук підрядком (`search`). Компонент лише показує значення й повідомляє про зміну; відбір робить
 * Api поверх фільтра сторінки й прав.
 */
@Component({
  selector: 'app-page-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatInputModule, MatSelectModule, SelectSearchComponent],
  template: `
    <div class="mb-3 flex flex-wrap items-end gap-3" role="group" aria-label="Фільтри" i18n-aria-label="@@app.page.filters" data-testid="app-filters">
      @for (f of filters(); track f.column) {
        @switch (f.kind) {
          @case ('select') {
            @if (optionsOf(f).length) {
              <mat-form-field appearance="outline" class="w-56" subscriptSizing="dynamic">
                <mat-label>{{ title(f) }}</mat-label>
                <mat-select [attr.data-filter]="f.column" [value]="single(f)" (selectionChange)="pick(f, $event.value)">
                  <bp-select-search />
                  <mat-option value="" i18n="@@app.page.filterAll">Усі</mat-option>
                  @for (o of optionsOf(f); track o.value) {
                    <mat-option [value]="o.value">{{ o.label }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
            } @else {
              <mat-form-field appearance="outline" class="w-56" subscriptSizing="dynamic">
                <mat-label>{{ title(f) }}</mat-label>
                <input matInput [attr.data-filter]="f.column" [value]="single(f)" (keydown.enter)="pick(f, $any($event.target).value)" (blur)="pick(f, $any($event.target).value)" />
              </mat-form-field>
            }
          }
          @case ('chips') {
            <div role="group" [attr.aria-label]="title(f)" class="flex flex-wrap items-center gap-1" [attr.data-filter]="f.column">
              <span class="mr-1 opacity-70">{{ title(f) }}</span>
              @for (o of optionsOf(f); track o.value) {
                <button
                  type="button"
                  class="rounded-full border px-3 py-1 text-sm"
                  [class.border-bp-primary]="selected(f, o.value)"
                  [class.bg-bp-tonal]="selected(f, o.value)"
                  [class.border-bp-line]="!selected(f, o.value)"
                  [attr.aria-pressed]="selected(f, o.value)"
                  [attr.data-chip]="o.value"
                  (click)="toggle(f, o.value)"
                >
                  {{ o.label }}
                </button>
              }
            </div>
          }
          @case ('period') {
            <mat-form-field appearance="outline" class="w-56" subscriptSizing="dynamic">
              <mat-label>{{ title(f) }}</mat-label>
              <mat-select [attr.data-filter]="f.column" [value]="periodValue(f)" (selectionChange)="period(f, $event.value)">
                <bp-select-search />
                <mat-option value="" i18n="@@app.page.periodAll">Увесь час</mat-option>
                <mat-option value="today" i18n="@@app.page.periodToday">Сьогодні</mat-option>
                <mat-option value="week" i18n="@@app.page.periodWeek">Цей тиждень</mat-option>
                <mat-option value="month" i18n="@@app.page.periodMonth">Цей місяць</mat-option>
                <mat-option value="custom" i18n="@@app.page.periodCustom">Від – до</mat-option>
              </mat-select>
            </mat-form-field>
            @if (periodValue(f) === 'custom') {
              <mat-form-field appearance="outline" class="w-40" subscriptSizing="dynamic">
                <mat-label i18n="@@app.page.periodFrom">Від</mat-label>
                <input matInput type="date" [attr.data-filter-from]="f.column" [value]="value(f).from ?? ''" (change)="range(f, 'from', $any($event.target).value)" />
              </mat-form-field>
              <mat-form-field appearance="outline" class="w-40" subscriptSizing="dynamic">
                <mat-label i18n="@@app.page.periodTo">До</mat-label>
                <input matInput type="date" [attr.data-filter-to]="f.column" [value]="value(f).to ?? ''" (change)="range(f, 'to', $any($event.target).value)" />
              </mat-form-field>
            }
          }
          @default {
            <mat-form-field appearance="outline" class="w-56" subscriptSizing="dynamic">
              <mat-label>{{ title(f) }}</mat-label>
              <input matInput [attr.data-filter]="f.column" [value]="single(f)" (keydown.enter)="pick(f, $any($event.target).value)" (blur)="pick(f, $any($event.target).value)" />
            </mat-form-field>
          }
        }
      }
    </div>
  `,
})
export class PageFiltersComponent {
  readonly filters = input.required<PageUserFilter[]>();
  /** Варіанти `select`/`chips` за колонкою (`ProjectPageView.filterOptions`). */
  readonly options = input<Record<string, PageOption[]> | null | undefined>(null);
  /** Поточні значення за колонкою. */
  readonly values = input<Record<string, PageFilterValue>>({});

  /** Нові значення всіх фільтрів після зміни одного. */
  readonly changed = output<Record<string, PageFilterValue>>();

  protected title(f: PageUserFilter): string {
    return f.label ?? f.column;
  }

  protected optionsOf(f: PageUserFilter): PageOption[] {
    return this.options()?.[f.column] ?? [];
  }

  protected value(f: PageUserFilter): PageFilterValue {
    return this.values()[f.column] ?? { values: [] };
  }

  protected single(f: PageUserFilter): string {
    return this.value(f).values[0] ?? '';
  }

  protected selected(f: PageUserFilter, option: string): boolean {
    return this.value(f).values.includes(option);
  }

  protected periodValue(f: PageUserFilter): string {
    const v = this.value(f);
    return v.from != null || v.to != null ? CUSTOM : (v.values[0] ?? '');
  }

  protected pick(f: PageUserFilter, value: string): void {
    const text = value.trim();
    if (text === this.single(f)) return;
    this.emit(f, { values: text ? [text] : [] });
  }

  protected toggle(f: PageUserFilter, option: string): void {
    const current = this.value(f).values;
    this.emit(f, { values: current.includes(option) ? current.filter((x) => x !== option) : [...current, option] });
  }

  protected period(f: PageUserFilter, value: string): void {
    // «Від – до» без дат ще нічого не відбирає: чекаємо межі.
    this.emit(f, value === CUSTOM ? { values: [], from: '', to: '' } : { values: value ? [value] : [] });
  }

  protected range(f: PageUserFilter, edge: 'from' | 'to', value: string): void {
    this.emit(f, { ...this.value(f), values: [], [edge]: value });
  }

  private emit(f: PageUserFilter, value: PageFilterValue): void {
    this.changed.emit({ ...this.values(), [f.column]: value });
  }
}
