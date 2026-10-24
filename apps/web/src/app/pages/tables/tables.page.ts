import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { AuthService } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import {
  DATA_COLUMN_NAME_PATTERN,
  DATA_COLUMN_TYPES,
  DataColumn,
  DataColumnType,
  DataRow,
  DataTableSummary,
  DataTablesApiService,
  cellText,
  inputValue,
} from '../../tables/data-tables-api.service';

/** Рядків на сторінці перегляду. */
export const TABLE_ROWS_PAGE_SIZE = 50;

/**
 * `/tables` (F6-11): таблиці даних, які сценарії читають і пишуть вузлом «Таблиця даних». Список і перегляд — усім ролям;
 * створення, зміна колонок, рядки й імпорт CSV — editor і admin.
 */
@Component({
  selector: 'app-tables-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <section class="p-6">
      <div class="mb-2 flex items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@tables.title">Таблиці даних</h1>
        @if (canEdit() && !designing()) {
          <button mat-flat-button type="button" data-testid="table-add" (click)="startCreate()" i18n="@@tables.add">Нова таблиця</button>
        }
      </div>
      <p class="mb-4 max-w-3xl opacity-70" i18n="@@tables.hint">
        Довідники, черги й стан між запусками: сценарії працюють з ними вузлом «Таблиця даних» (core.dataTable), агенти — через MCP.
      </p>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (designing()) {
        <div class="mb-6 max-w-3xl rounded-lg border border-ac-line p-4" data-testid="table-form">
          <h2 class="mb-2 font-semibold">
            @if (designId()) {
              <ng-container i18n="@@tables.form.editTitle">Структура таблиці</ng-container>
            } @else {
              <ng-container i18n="@@tables.form.newTitle">Нова таблиця</ng-container>
            }
          </h2>
          <mat-form-field appearance="outline" class="w-80">
            <mat-label i18n="@@tables.form.name">Назва</mat-label>
            <input matInput data-testid="table-name" [value]="designName()" (input)="designName.set(text($event))" />
          </mat-form-field>
          <div class="flex flex-col gap-1">
            @for (c of designColumns(); track $index; let i = $index) {
              <div class="flex flex-wrap items-center gap-2" data-testid="column-row">
                <mat-form-field appearance="outline" class="w-56">
                  <mat-label i18n="@@tables.form.column">Колонка</mat-label>
                  <input matInput data-testid="column-name" [value]="c.name" (input)="patchColumn(i, { name: text($event) })" />
                </mat-form-field>
                <mat-form-field appearance="outline" class="w-40">
                  <mat-label i18n="@@tables.form.type">Тип</mat-label>
                  <select matNativeControl data-testid="column-type" [value]="c.type" (change)="patchColumn(i, { type: typeOf($event) })">
                    @for (t of types; track t) {
                      <option [value]="t">{{ typeLabel(t) }}</option>
                    }
                  </select>
                </mat-form-field>
                <label class="flex items-center gap-1">
                  <input type="checkbox" data-testid="column-key" [checked]="!!c.key" (change)="setKey(i, checked($event))" />
                  <span i18n="@@tables.form.key">Ключ</span>
                </label>
                <button mat-button type="button" data-testid="column-remove" (click)="removeColumn(i)" i18n="@@tables.form.removeColumn">
                  Прибрати
                </button>
              </div>
            }
          </div>
          @if (designError()) {
            <p role="alert" class="mb-2 text-red-700" data-testid="table-form-error">{{ designError() }}</p>
          }
          <div class="flex flex-wrap gap-2">
            <button mat-button type="button" data-testid="column-add" (click)="addColumn()" i18n="@@tables.form.addColumn">Додати колонку</button>
            <button mat-flat-button type="button" data-testid="table-save" [disabled]="saving()" (click)="saveTable()" i18n="@@tables.form.save">
              Зберегти
            </button>
            <button mat-button type="button" data-testid="table-cancel" (click)="designing.set(false)" i18n="@@tables.form.cancel">Скасувати</button>
          </div>
        </div>
      }

      @if (!loading() && tables().length === 0 && !error()) {
        <p class="opacity-70" data-testid="empty" i18n="@@tables.empty">Таблиць ще немає.</p>
      }

      <div class="flex flex-wrap items-start gap-6">
        @if (tables().length > 0) {
          <ul class="w-64 shrink-0" data-testid="tables-list">
            @for (t of tables(); track t.id) {
              <li>
                <button
                  type="button"
                  class="w-full rounded p-2 text-left"
                  [class.font-semibold]="selected()?.id === t.id"
                  [attr.data-table]="t.name"
                  (click)="select(t)"
                >
                  {{ t.name }} <span class="opacity-60">· {{ t.rowCount }}</span>
                </button>
              </li>
            }
          </ul>
        }

        @if (selected(); as t) {
          <div class="min-w-0 flex-1" data-testid="table-view">
            <div class="mb-2 flex flex-wrap items-center gap-2">
              <h2 class="text-lg font-semibold">{{ t.name }}</h2>
              <span class="opacity-60" i18n="@@tables.view.count">рядків: {{ total() }}:count:</span>
              @if (canEdit()) {
                <button mat-button type="button" data-testid="table-edit" (click)="startEdit(t)" i18n="@@tables.view.edit">Структура</button>
                <button mat-button type="button" data-testid="table-delete" (click)="confirmingDelete.set(true)" i18n="@@tables.view.delete">
                  Видалити таблицю
                </button>
              }
            </div>
            @if (confirmingDelete()) {
              <div class="mb-3 flex flex-wrap items-center gap-2 rounded-lg p-3 state-bad" data-testid="delete-confirm">
                <span i18n="@@tables.view.deleteConfirm">Видалити таблицю «{{ t.name }}:name:» з усіма рядками? Сценарії з нею почнуть падати.</span>
                <button mat-flat-button type="button" data-testid="delete-yes" (click)="removeTable(t)" i18n="@@tables.view.deleteYes">Видалити</button>
                <button mat-button type="button" (click)="confirmingDelete.set(false)" i18n="@@tables.view.deleteNo">Ні</button>
              </div>
            }

            <mat-form-field appearance="outline" class="w-80">
              <mat-label i18n="@@tables.view.search">Пошук у значеннях</mat-label>
              <input matInput data-testid="rows-search" [value]="search()" (input)="search.set(text($event))" (keydown.enter)="loadRows(1)" />
            </mat-form-field>

            @if (canEdit()) {
              <div class="mb-3 flex flex-wrap items-start gap-2" data-testid="row-form">
                @for (c of t.columns; track c.name) {
                  <mat-form-field appearance="outline" class="w-48">
                    <mat-label>{{ c.name }}{{ c.key ? ' 🔑' : '' }}</mat-label>
                    <input
                      matInput
                      [attr.data-input]="c.name"
                      [value]="draft()[c.name] ?? ''"
                      [readonly]="!!c.key && editingKey() !== null"
                      [placeholder]="typeLabel(c.type)"
                      (input)="setDraft(c.name, text($event))"
                    />
                  </mat-form-field>
                }
                <button mat-flat-button type="button" data-testid="row-save" [disabled]="saving()" (click)="saveRow(t)">
                  @if (editingKey() === null) {
                    <ng-container i18n="@@tables.row.add">Додати рядок</ng-container>
                  } @else {
                    <ng-container i18n="@@tables.row.save">Зберегти рядок</ng-container>
                  }
                </button>
                @if (editingKey() !== null) {
                  <button mat-button type="button" data-testid="row-cancel" (click)="resetDraft()" i18n="@@tables.row.cancel">Скасувати</button>
                }
              </div>
            }

            @if (rows().length > 0) {
              <div class="overflow-x-auto">
                <table class="w-full text-left" data-testid="rows-table">
                  <thead>
                    <tr>
                      @for (c of t.columns; track c.name) {
                        <th class="p-2 font-mono">{{ c.name }}</th>
                      }
                      @if (canEdit()) {
                        <th class="p-2"></th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (r of rows(); track r.key) {
                      <tr [attr.data-row]="r.key">
                        @for (c of t.columns; track c.name) {
                          <td class="max-w-xs truncate p-2" [title]="cell(r, c)">{{ cell(r, c) }}</td>
                        }
                        @if (canEdit()) {
                          <td class="whitespace-nowrap p-2">
                            <button mat-button type="button" data-testid="row-edit" (click)="editRow(t, r)" i18n="@@tables.row.edit">Змінити</button>
                            <button mat-button type="button" data-testid="row-delete" (click)="removeRow(t, r)" i18n="@@tables.row.delete">Видалити</button>
                          </td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
              @if (pages() > 1) {
                <div class="mt-2 flex items-center gap-2">
                  <button mat-button type="button" data-testid="rows-prev" [disabled]="page() <= 1" (click)="loadRows(page() - 1)" i18n="@@tables.view.prev">
                    Назад
                  </button>
                  <span>{{ page() }} / {{ pages() }}</span>
                  <button mat-button type="button" data-testid="rows-next" [disabled]="page() >= pages()" (click)="loadRows(page() + 1)" i18n="@@tables.view.next">
                    Далі
                  </button>
                </div>
              }
            } @else if (!rowsLoading()) {
              <p class="opacity-70" data-testid="rows-empty" i18n="@@tables.view.empty">Рядків немає.</p>
            }

            @if (canEdit()) {
              <div class="mt-6 max-w-3xl" data-testid="import-form">
                <h3 class="mb-1 font-semibold" i18n="@@tables.import.title">Імпорт CSV</h3>
                <p class="mb-2 opacity-70" i18n="@@tables.import.hint">
                  Перший рядок — назви колонок, роздільник «,» або «;». Рядки з наявним ключем оновлюються, решта додаються.
                </p>
                <input type="file" accept=".csv,text/csv" class="mb-2 block" data-testid="import-file" (change)="readFile($event)" />
                <mat-form-field appearance="outline" class="w-full">
                  <mat-label i18n="@@tables.import.text">CSV</mat-label>
                  <textarea matInput rows="4" class="font-mono" data-testid="import-text" [value]="csv()" (input)="csv.set(text($event))"></textarea>
                </mat-form-field>
                <button mat-flat-button type="button" data-testid="import-run" [disabled]="saving() || !csv().trim()" (click)="importCsv(t)" i18n="@@tables.import.run">
                  Імпортувати
                </button>
                @if (imported(); as r) {
                  <p role="status" class="mt-2 state-ok rounded-lg p-2" data-testid="import-result" i18n="@@tables.import.result">
                    Додано: {{ r.inserted }}:inserted:, оновлено: {{ r.updated }}:updated:
                  </p>
                }
              </div>
            }
          </div>
        }
      </div>
    </section>
  `,
})
export class TablesPage implements OnInit {
  private readonly api = inject(DataTablesApiService);
  private readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly types = DATA_COLUMN_TYPES;
  readonly tables = signal<DataTableSummary[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly canEdit = computed(() => {
    const role = this.auth.user()?.role;
    return role === 'admin' || role === 'editor';
  });

  // Форма структури: створення (designId = null) або зміна колонок.
  readonly designing = signal(false);
  readonly designId = signal<string | null>(null);
  readonly designName = signal('');
  readonly designColumns = signal<DataColumn[]>([]);
  readonly designError = signal<string | null>(null);

  // Вибрана таблиця і її рядки.
  readonly selected = signal<DataTableSummary | null>(null);
  readonly rows = signal<DataRow[]>([]);
  readonly rowsLoading = signal(false);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly pages = computed(() => Math.max(1, Math.ceil(this.total() / TABLE_ROWS_PAGE_SIZE)));
  readonly search = signal('');
  readonly confirmingDelete = signal(false);

  // Форма рядка: текст полів за назвою колонки; editingKey — ключ рядка, що редагується.
  readonly draft = signal<Record<string, string>>({});
  readonly editingKey = signal<string | null>(null);

  readonly csv = signal('');
  readonly imported = signal<{ inserted: number; updated: number } | null>(null);

  ngOnInit(): void {
    this.loading.set(true);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.tables.set(list);
          this.loading.set(false);
          if (list.length > 0) this.select(list[0]);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLTextAreaElement).value;
  }

  checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  typeOf(event: Event): DataColumnType {
    return (event.target as HTMLSelectElement).value as DataColumnType;
  }

  typeLabel(type: DataColumnType): string {
    switch (type) {
      case 'string':
        return $localize`:@@tables.type.string:Текст`;
      case 'number':
        return $localize`:@@tables.type.number:Число`;
      case 'boolean':
        return $localize`:@@tables.type.boolean:Так/ні`;
      case 'date':
        return $localize`:@@tables.type.date:Дата`;
      default:
        return $localize`:@@tables.type.json:JSON`;
    }
  }

  cell(row: DataRow, column: DataColumn): string {
    return cellText(row.data[column.name]);
  }

  // --- Структура -----------------------------------------------------------------------------------------------------

  startCreate(): void {
    this.designId.set(null);
    this.designName.set('');
    this.designColumns.set([{ name: 'id', type: 'string', key: true }]);
    this.designError.set(null);
    this.designing.set(true);
  }

  startEdit(table: DataTableSummary): void {
    this.designId.set(table.id);
    this.designName.set(table.name);
    this.designColumns.set(table.columns.map((c) => ({ ...c })));
    this.designError.set(null);
    this.designing.set(true);
  }

  addColumn(): void {
    this.designColumns.update((list) => [...list, { name: '', type: 'string' }]);
  }

  removeColumn(index: number): void {
    this.designColumns.update((list) => list.filter((_, i) => i !== index));
  }

  patchColumn(index: number, patch: Partial<DataColumn>): void {
    this.designColumns.update((list) => list.map((c, i) => (i === index ? { ...c, ...patch } : c)));
  }

  /** Ключ — лише одна колонка: позначка знімає її з інших. */
  setKey(index: number, on: boolean): void {
    this.designColumns.update((list) => list.map((c, i) => ({ ...c, key: i === index ? on : on ? false : c.key })));
  }

  /** Помилка структури до запиту; решту (зайнята назва, зміна ключа) повертає Api. */
  designProblem(): string | null {
    const columns = this.designColumns();
    if (!this.designName().trim()) return $localize`:@@tables.form.nameRequired:Вкажіть назву таблиці`;
    if (columns.length === 0) return $localize`:@@tables.form.noColumns:Потрібна хоча б одна колонка`;
    const bad = columns.find((c) => !DATA_COLUMN_NAME_PATTERN.test(c.name.trim()));
    if (bad) return $localize`:@@tables.form.columnInvalid:Назва колонки «${bad.name}:name:»: латиниця, цифри й «_», не з цифри`;
    const names = columns.map((c) => c.name.trim().toLowerCase());
    if (new Set(names).size !== names.length) return $localize`:@@tables.form.columnDuplicate:Назви колонок повторюються`;
    const key = columns.find((c) => c.key);
    if (key && key.type !== 'string' && key.type !== 'number') return $localize`:@@tables.form.keyType:Ключ може бути лише текстом або числом`;
    return null;
  }

  saveTable(): void {
    const problem = this.designProblem();
    this.designError.set(problem);
    if (problem) return;
    const columns: DataColumn[] = this.designColumns().map((c) => ({ name: c.name.trim(), type: c.type, ...(c.key ? { key: true } : {}) }));
    const id = this.designId();
    const request = id ? this.api.update(id, this.designName().trim(), columns) : this.api.create(this.designName().trim(), columns);
    this.saving.set(true);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.designing.set(false);
        this.tables.update((list) => [...list.filter((t) => t.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)));
        this.select(saved);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.designError.set(apiErrorMessage(e));
      },
    });
  }

  removeTable(table: DataTableSummary): void {
    this.api
      .remove(table.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.confirmingDelete.set(false);
          this.tables.update((list) => list.filter((t) => t.id !== table.id));
          this.selected.set(null);
          this.rows.set([]);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  // --- Рядки ---------------------------------------------------------------------------------------------------------

  select(table: DataTableSummary): void {
    this.selected.set(table);
    this.search.set('');
    this.confirmingDelete.set(false);
    this.imported.set(null);
    this.resetDraft();
    this.loadRows(1);
  }

  loadRows(page: number): void {
    const table = this.selected();
    if (!table) return;
    this.rowsLoading.set(true);
    this.api
      .rows(table.id, this.search(), page, TABLE_ROWS_PAGE_SIZE)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.rows.set(result.items);
          this.total.set(result.total);
          this.page.set(result.page);
          this.rowsLoading.set(false);
          if (!this.search().trim()) this.setRowCount(table.id, result.total);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.rowsLoading.set(false);
        },
      });
  }

  setDraft(column: string, value: string): void {
    this.draft.update((d) => ({ ...d, [column]: value }));
  }

  resetDraft(): void {
    this.draft.set({});
    this.editingKey.set(null);
  }

  editRow(table: DataTableSummary, row: DataRow): void {
    this.editingKey.set(row.key);
    this.draft.set(Object.fromEntries(table.columns.map((c) => [c.name, cellText(row.data[c.name])])));
  }

  saveRow(table: DataTableSummary): void {
    const key = this.editingKey();
    const draft = this.draft();
    const values: Record<string, unknown> = {};
    try {
      for (const column of table.columns) {
        if (key !== null && column.key) continue;
        const value = inputValue(column, draft[column.name] ?? '');
        // Новий рядок — лише заповнені поля; зміна — усі, порожнє очищає значення.
        if (value !== null || key !== null) values[column.name] = value;
      }
    } catch (e) {
      this.error.set((e as Error).message);
      return;
    }
    this.error.set(null);
    this.saving.set(true);
    const request = key === null ? this.api.insertRow(table.id, values) : this.api.updateRow(table.id, key, values);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.saving.set(false);
        this.resetDraft();
        this.loadRows(key === null ? this.pages() : this.page());
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  removeRow(table: DataTableSummary, row: DataRow): void {
    this.api
      .removeRow(table.id, row.key)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          if (this.editingKey() === row.key) this.resetDraft();
          this.loadRows(this.page());
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  // --- Імпорт --------------------------------------------------------------------------------------------------------

  readFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    void file.text().then((content) => this.csv.set(content));
  }

  importCsv(table: DataTableSummary): void {
    this.error.set(null);
    this.imported.set(null);
    this.saving.set(true);
    this.api
      .importCsv(table.id, this.csv())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => {
          this.saving.set(false);
          this.imported.set(result);
          this.csv.set('');
          this.loadRows(1);
        },
        error: (e: unknown) => {
          this.saving.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  private setRowCount(id: string, rowCount: number): void {
    this.tables.update((list) => list.map((t) => (t.id === id ? { ...t, rowCount } : t)));
  }
}
