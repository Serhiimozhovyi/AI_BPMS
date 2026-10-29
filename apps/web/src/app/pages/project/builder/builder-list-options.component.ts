import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { COLUMN_FORMATS, labelsText, parseLabels } from '../../../projects/page-builder-options';
import { PageColumn, PageComputed, PageDefinition, PageOpenMode, PageRelated } from '../../../projects/project-pages-api.service';
import { DataTableSummary } from '../../../tables/data-tables-api.service';
import { BuilderUserFiltersComponent } from './builder-user-filters.component';

/** Що конструктор списку зберігає поза основною формою: нові опції F8-14. */
export type ListExtras = Pick<PageDefinition, 'filters' | 'computed' | 'layout' | 'card' | 'expand' | 'empty'>;

/**
 * Налаштування списку (F8-19): фільтри над списком, формат і підписи колонок, обчислювані колонки («записано 3 / 8»),
 * картки замість таблиці, підлеглі списки під рядком і текст порожнього списку.
 */
@Component({
  selector: 'app-builder-list-options',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, BuilderUserFiltersComponent],
  template: `
    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.filters">Фільтри над списком</h3>
    <app-builder-user-filters [filters]="extras().filters ?? []" [columns]="columnNames()" (changed)="patch({ filters: $event })" />

    @if (selected().length) {
      <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.formats">Вигляд колонок</h3>
      <p class="mb-1 text-sm opacity-70" i18n="@@bo.list.formatsHint">Підписи й кольори значень: по одному в рядку — значення=Підпис|колір (gray, green, yellow, red, blue, purple).</p>
      @for (c of selected(); track c.column; let i = $index) {
        <div class="mb-2 flex flex-wrap items-start gap-2" [attr.data-column-format]="c.column">
          <span class="w-36 pt-1 font-medium">{{ c.column }}</span>
          <input class="w-36 border p-1" placeholder="Підпис колонки" i18n-placeholder="@@bo.list.colLabel" [value]="c.label ?? ''" (input)="column(i, { label: $any($event.target).value || undefined })" />
          <select class="border p-1" aria-label="Формат" i18n-aria-label="@@bo.list.format" [value]="c.format ?? ''" (change)="column(i, { format: $any($event.target).value || undefined })">
            @for (f of formats; track f) {
              <option [value]="f" [selected]="f === (c.format ?? '')">{{ f || '—' }}</option>
            }
          </select>
          <textarea
            class="h-16 w-64 border p-1 text-sm"
            aria-label="Підписи значень"
            i18n-aria-label="@@bo.list.labels"
            [value]="labels(c)"
            (change)="setLabels(i, $any($event.target).value)"
          ></textarea>
        </div>
      }
    }

    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.computed">Обчислювані колонки</h3>
    @for (x of extras().computed ?? []; track $index; let i = $index) {
      <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-computed]="i">
        <input class="w-28 border p-1" placeholder="ключ" i18n-placeholder="@@bo.computed.key" [value]="x.key" (input)="computed(i, { key: $any($event.target).value })" />
        <input class="w-36 border p-1" placeholder="Підпис" i18n-placeholder="@@bo.computed.label" [value]="x.label" (input)="computed(i, { label: $any($event.target).value })" />
        <select class="border p-1" aria-label="Таблиця" i18n-aria-label="@@bo.computed.table" [value]="x.table" (change)="computed(i, { table: $any($event.target).value, by: '' })">
          <option value="" [selected]="!x.table">—</option>
          @for (t of tables(); track t.id) {
            <option [value]="t.name" [selected]="t.name === x.table">{{ t.name }}</option>
          }
        </select>
        <select class="border p-1" aria-label="Зв'язок з рядком" i18n-aria-label="@@bo.computed.by" [value]="x.by" (change)="computed(i, { by: $any($event.target).value })">
          <option value="" [selected]="!x.by">—</option>
          @for (c of columnsOf(x.table); track c) {
            <option [value]="c" [selected]="c === x.by">{{ c }}</option>
          }
        </select>
        <select class="border p-1" aria-label="Агрегат" i18n-aria-label="@@bo.computed.aggregate" [value]="x.aggregate ?? 'count'" (change)="computed(i, { aggregate: $any($event.target).value })">
          <option value="count" [selected]="(x.aggregate ?? 'count') === 'count'">count</option>
          <option value="sum" [selected]="x.aggregate === 'sum'">sum</option>
        </select>
        @if (x.aggregate === 'sum') {
          <select class="border p-1" aria-label="Колонка суми" i18n-aria-label="@@bo.computed.column" [value]="x.column ?? ''" (change)="computed(i, { column: $any($event.target).value || undefined })">
            <option value="" [selected]="!x.column">—</option>
            @for (c of columnsOf(x.table); track c) {
              <option [value]="c" [selected]="c === x.column">{{ c }}</option>
            }
          </select>
        }
        <select class="border p-1" aria-label="Із ліміту (колонка рядка)" i18n-aria-label="@@bo.computed.of" [value]="x.of ?? ''" (change)="computed(i, { of: $any($event.target).value || undefined })">
          <option value="" [selected]="!x.of" i18n="@@bo.computed.noOf">без ліміту</option>
          @for (c of columnNames(); track c) {
            <option [value]="c" [selected]="c === x.of">з {{ c }}</option>
          }
        </select>
        <button mat-button type="button" (click)="removeComputed(i)" i18n="@@builder.remove">Прибрати</button>
      </div>
    }
    <button mat-button type="button" data-testid="b-add-computed" (click)="addComputed()" i18n="@@bo.computed.add">Додати обчислювану колонку</button>

    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.layout">Показ</h3>
    <div class="flex flex-wrap items-center gap-2">
      <select class="border p-1" data-testid="b-layout" aria-label="Показ списку" i18n-aria-label="@@bo.list.layoutLabel" [value]="extras().layout ?? 'table'" (change)="layout($any($event.target).value)">
        <option value="table" [selected]="(extras().layout ?? 'table') === 'table'" i18n="@@bo.layout.table">Таблиця</option>
        <option value="cards" [selected]="extras().layout === 'cards'" i18n="@@bo.layout.cards">Картки</option>
      </select>
      @if (extras().layout === 'cards') {
        <select class="border p-1" aria-label="Заголовок картки" i18n-aria-label="@@bo.card.title" [value]="extras().card?.title ?? ''" (change)="card({ title: $any($event.target).value })">
          <option value="" [selected]="!extras().card?.title" i18n="@@bo.card.titleNone">заголовок — перша колонка</option>
          @for (c of selectedNames(); track c) {
            <option [value]="c" [selected]="c === extras().card?.title">{{ c }}</option>
          }
        </select>
        <select class="border p-1" aria-label="Підзаголовок картки" i18n-aria-label="@@bo.card.subtitle" [value]="extras().card?.subtitle ?? ''" (change)="card({ subtitle: $any($event.target).value })">
          <option value="" [selected]="!extras().card?.subtitle" i18n="@@bo.card.subtitleNone">без підзаголовка</option>
          @for (c of selectedNames(); track c) {
            <option [value]="c" [selected]="c === extras().card?.subtitle">{{ c }}</option>
          }
        </select>
      }
      <input class="w-72 border p-1" placeholder="Текст порожнього списку" i18n-placeholder="@@bo.list.empty" [value]="extras().empty ?? ''" (input)="patch({ empty: $any($event.target).value || undefined })" />
    </div>

    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.expand">Підлеглі списки під рядком</h3>
    @for (e of extras().expand ?? []; track $index; let i = $index) {
      <div class="mb-2 flex flex-wrap items-center gap-2" [attr.data-expand]="i">
        <input class="w-36 border p-1" placeholder="Підпис" i18n-placeholder="@@bo.expand.label" [value]="e.label ?? ''" (input)="expand(i, { label: $any($event.target).value || undefined })" />
        <select class="border p-1" aria-label="Таблиця" i18n-aria-label="@@bo.expand.table" [value]="e.table" (change)="expand(i, { table: $any($event.target).value, by: '', columns: undefined })">
          <option value="" [selected]="!e.table">—</option>
          @for (t of tables(); track t.id) {
            <option [value]="t.name" [selected]="t.name === e.table">{{ t.name }}</option>
          }
        </select>
        <select class="border p-1" aria-label="Зв'язок з рядком" i18n-aria-label="@@bo.expand.by" [value]="e.by" (change)="expand(i, { by: $any($event.target).value })">
          <option value="" [selected]="!e.by">—</option>
          @for (c of columnsOf(e.table); track c) {
            <option [value]="c" [selected]="c === e.by">{{ c }}</option>
          }
        </select>
        @for (c of columnsOf(e.table); track c) {
          <label class="inline-flex items-center gap-1">
            <input type="checkbox" [checked]="expandHas(e, c)" (change)="expandColumn(i, c, $any($event.target).checked)" />
            {{ c }}
          </label>
        }
        <select class="border p-1" aria-label="Кнопка «Додати»" i18n-aria-label="@@bo.expand.create" [value]="e.create?.page ?? ''" (change)="expand(i, { create: $any($event.target).value ? { page: $any($event.target).value, mode: e.create?.mode ?? 'drawer' } : undefined })">
          <option value="" [selected]="!e.create" i18n="@@bo.expand.noCreate">без кнопки «Додати»</option>
          @for (p of pageKeys(); track p) {
            <option [value]="p" [selected]="p === e.create?.page">«Додати» → {{ p }}</option>
          }
        </select>
        <input class="w-56 border p-1" placeholder="Коли порожньо" i18n-placeholder="@@bo.expand.empty" [value]="e.empty ?? ''" (input)="expand(i, { empty: $any($event.target).value || undefined })" />
        <button mat-button type="button" (click)="removeExpand(i)" i18n="@@builder.remove">Прибрати</button>
      </div>
    }
    <button mat-button type="button" data-testid="b-add-expand" (click)="addExpand()" i18n="@@bo.expand.add">Додати підлеглий список</button>
  `,
})
export class BuilderListOptionsComponent {
  readonly extras = input.required<ListExtras>();
  /** Колонки таблиці сторінки. */
  readonly columnNames = input.required<string[]>();
  /** Вибрані колонки списку з їхнім форматом, підписами й кольорами. */
  readonly selected = input.required<PageColumn[]>();
  readonly tables = input.required<DataTableSummary[]>();
  readonly pageKeys = input.required<string[]>();

  readonly extrasChange = output<ListExtras>();
  readonly columnsChange = output<PageColumn[]>();

  protected readonly formats = COLUMN_FORMATS;
  protected readonly selectedNames = computed(() => this.selected().map((c) => c.column));

  protected columnsOf(table: string): string[] {
    return this.tables().find((t) => t.name.toLowerCase() === table.toLowerCase())?.columns.map((c) => c.name) ?? [];
  }

  protected patch(patch: Partial<ListExtras>): void {
    const next: ListExtras = { ...this.extras(), ...patch };
    for (const key of Object.keys(next) as (keyof ListExtras)[]) {
      const value = next[key];
      if (value === undefined || (Array.isArray(value) && value.length === 0)) delete next[key];
    }
    this.extrasChange.emit(next);
  }

  protected labels(c: PageColumn): string {
    return labelsText(c.labels, c.colors);
  }

  protected column(index: number, patch: Partial<PageColumn>): void {
    this.columnsChange.emit(this.selected().map((c, i) => (i === index ? { ...c, ...patch } : c)));
  }

  protected setLabels(index: number, text: string): void {
    const { labels, colors } = parseLabels(text);
    this.column(index, { labels, colors });
  }

  protected computed(index: number, patch: Partial<PageComputed>): void {
    this.patch({ computed: (this.extras().computed ?? []).map((x, i) => (i === index ? { ...x, ...patch } : x)) });
  }

  protected addComputed(): void {
    this.patch({ computed: [...(this.extras().computed ?? []), { key: '', label: '', table: '', by: '', aggregate: 'count' }] });
  }

  protected removeComputed(index: number): void {
    this.patch({ computed: (this.extras().computed ?? []).filter((_, i) => i !== index) });
  }

  protected layout(value: string): void {
    this.patch({ layout: value === 'cards' ? 'cards' : undefined, card: value === 'cards' ? this.extras().card : undefined });
  }

  protected card(patch: { title?: string; subtitle?: string }): void {
    const next = { title: this.extras().card?.title ?? '', subtitle: this.extras().card?.subtitle, ...patch };
    this.patch({ card: next.title || next.subtitle ? { title: next.title, subtitle: next.subtitle || undefined } : undefined });
  }

  protected expand(index: number, patch: Partial<PageRelated>): void {
    this.patch({ expand: (this.extras().expand ?? []).map((e, i) => (i === index ? { ...e, ...patch } : e)) });
  }

  protected expandHas(e: PageRelated, column: string): boolean {
    return (e.columns ?? []).some((c) => c.column === column);
  }

  protected expandColumn(index: number, column: string, on: boolean): void {
    const e = (this.extras().expand ?? [])[index];
    const order = this.columnsOf(e.table);
    const kept = (e.columns ?? []).filter((c) => c.column !== column);
    const next = on ? [...kept, { column }] : kept;
    this.expand(index, { columns: next.sort((a, b) => order.indexOf(a.column) - order.indexOf(b.column)) });
  }

  protected addExpand(): void {
    this.patch({ expand: [...(this.extras().expand ?? []), { table: '', by: '' }] });
  }

  protected removeExpand(index: number): void {
    this.patch({ expand: (this.extras().expand ?? []).filter((_, i) => i !== index) });
  }
}

export type { PageOpenMode };
