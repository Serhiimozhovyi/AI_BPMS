import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { COLUMN_FORMATS, labelsText, parseLabels } from '../../../projects/page-builder-options';
import { PageColumn, PageComputed, PageDefinition, PageOpenMode, PageRelated } from '../../../projects/project-pages-api.service';
import { DataTableSummary } from '../../../tables/data-tables-api.service';
import { BuilderUserFiltersComponent } from './builder-user-filters.component';
import { BpButtonDirective } from '../../../shared/button.directive';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../../shared/select-search/select-search.component';

/** Що конструктор списку зберігає поза основною формою: нові опції F8-14. */
export type ListExtras = Pick<PageDefinition, 'filters' | 'computed' | 'layout' | 'card' | 'expand' | 'empty'>;

/**
 * Налаштування списку (F8-19): фільтри над списком, формат і підписи колонок, обчислювані колонки («записано 3 / 8»),
 * картки замість таблиці, підлеглі списки під рядком і текст порожнього списку.
 */
@Component({
  selector: 'app-builder-list-options',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, BuilderUserFiltersComponent, MatFormFieldModule, MatSelectModule, SelectSearchComponent],
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
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select aria-label="Формат" i18n-aria-label="@@bo.list.format" [value]="c.format ?? ''" (selectionChange)="column(i, { format: $event.value || undefined })">
              <bp-select-search />
              @for (f of formats; track f) {
                <mat-option [value]="f">{{ f || '—' }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
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
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Таблиця" i18n-aria-label="@@bo.computed.table" [value]="x.table" (selectionChange)="computed(i, { table: $event.value, by: '' })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (t of tables(); track t.id) {
              <mat-option [value]="t.name">{{ t.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Зв'язок з рядком" i18n-aria-label="@@bo.computed.by" [value]="x.by" (selectionChange)="computed(i, { by: $event.value })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (c of columnsOf(x.table); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Агрегат" i18n-aria-label="@@bo.computed.aggregate" [value]="x.aggregate ?? 'count'" (selectionChange)="computed(i, { aggregate: $event.value })">
            <bp-select-search />
            <mat-option value="count">count</mat-option>
            <mat-option value="sum">sum</mat-option>
          </mat-select>
        </mat-form-field>
        @if (x.aggregate === 'sum') {
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select aria-label="Колонка суми" i18n-aria-label="@@bo.computed.column" [value]="x.column ?? ''" (selectionChange)="computed(i, { column: $event.value || undefined })">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              @for (c of columnsOf(x.table); track c) {
                <mat-option [value]="c">{{ c }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Із ліміту (колонка рядка)" i18n-aria-label="@@bo.computed.of" [value]="x.of ?? ''" (selectionChange)="computed(i, { of: $event.value || undefined })">
            <bp-select-search />
            <mat-option value="" i18n="@@bo.computed.noOf">без ліміту</mat-option>
            @for (c of columnNames(); track c) {
              <mat-option [value]="c">з {{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <button mat-button type="button" (click)="removeComputed(i)" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
      </div>
    }
    <button mat-stroked-button type="button" data-testid="b-add-computed" (click)="addComputed()" i18n="@@bo.computed.add" bpVariant="tonal">Додати обчислювану колонку</button>

    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.layout">Показ</h3>
    <div class="flex flex-wrap items-center gap-2">
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select data-testid="b-layout" aria-label="Показ списку" i18n-aria-label="@@bo.list.layoutLabel" [value]="extras().layout ?? 'table'" (selectionChange)="layout($event.value)">
          <bp-select-search />
          <mat-option value="table" i18n="@@bo.layout.table">Таблиця</mat-option>
          <mat-option value="cards" i18n="@@bo.layout.cards">Картки</mat-option>
        </mat-select>
      </mat-form-field>
      @if (extras().layout === 'cards') {
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Заголовок картки" i18n-aria-label="@@bo.card.title" [value]="extras().card?.title ?? ''" (selectionChange)="card({ title: $event.value })">
            <bp-select-search />
            <mat-option value="" i18n="@@bo.card.titleNone">заголовок — перша колонка</mat-option>
            @for (c of selectedNames(); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Підзаголовок картки" i18n-aria-label="@@bo.card.subtitle" [value]="extras().card?.subtitle ?? ''" (selectionChange)="card({ subtitle: $event.value })">
            <bp-select-search />
            <mat-option value="" i18n="@@bo.card.subtitleNone">без підзаголовка</mat-option>
            @for (c of selectedNames(); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      <input class="w-72 border p-1" placeholder="Текст порожнього списку" i18n-placeholder="@@bo.list.empty" [value]="extras().empty ?? ''" (input)="patch({ empty: $any($event.target).value || undefined })" />
    </div>

    <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.expand">Підлеглі списки під рядком</h3>
    @for (e of extras().expand ?? []; track $index; let i = $index) {
      <div class="mb-2 flex flex-wrap items-center gap-2" [attr.data-expand]="i">
        <input class="w-36 border p-1" placeholder="Підпис" i18n-placeholder="@@bo.expand.label" [value]="e.label ?? ''" (input)="expand(i, { label: $any($event.target).value || undefined })" />
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Таблиця" i18n-aria-label="@@bo.expand.table" [value]="e.table" (selectionChange)="expand(i, { table: $event.value, by: '', columns: undefined })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (t of tables(); track t.id) {
              <mat-option [value]="t.name">{{ t.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Зв'язок з рядком" i18n-aria-label="@@bo.expand.by" [value]="e.by" (selectionChange)="expand(i, { by: $event.value })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (c of columnsOf(e.table); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        @for (c of columnsOf(e.table); track c) {
          <label class="inline-flex items-center gap-1">
            <input type="checkbox" [checked]="expandHas(e, c)" (change)="expandColumn(i, c, $any($event.target).checked)" />
            {{ c }}
          </label>
        }
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Кнопка «Додати»" i18n-aria-label="@@bo.expand.create" [value]="e.create?.page ?? ''" (selectionChange)="expand(i, { create: $event.value ? { page: $event.value, mode: e.create?.mode ?? 'drawer' } : undefined })">
            <bp-select-search />
            <mat-option value="" i18n="@@bo.expand.noCreate">без кнопки «Додати»</mat-option>
            @for (p of pageKeys(); track p) {
              <mat-option [value]="p">«Додати» → {{ p }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <input class="w-56 border p-1" placeholder="Коли порожньо" i18n-placeholder="@@bo.expand.empty" [value]="e.empty ?? ''" (input)="expand(i, { empty: $any($event.target).value || undefined })" />
        <button mat-button type="button" (click)="removeExpand(i)" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
      </div>
    }
    <button mat-stroked-button type="button" data-testid="b-add-expand" (click)="addExpand()" i18n="@@bo.expand.add" bpVariant="tonal">Додати підлеглий список</button>
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
