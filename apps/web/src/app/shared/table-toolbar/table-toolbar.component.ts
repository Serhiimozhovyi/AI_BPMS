import { ChangeDetectionStrategy, Component, input, model, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

export type TableView = 'table' | 'cards';

/**
 * Панель над таблицею (F9-07, Figma «Реєстр продажів» 3499:77865): зліва перемикач таблиця/картки (`view`), пошук
 * (`search`), кнопки дій (проєкція контенту); праворуч іконки фільтра, вивантаження й налаштувань — кожна показується,
 * коли задано `showFilter` / `showExport` / `showSettings`, і шле подію `filter` / `exportData` / `settings`.
 * Стилі — `theme/_tables.scss`.
 */
@Component({
  selector: 'bp-table-toolbar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule],
  template: `
    <div class="bp-toolbar" role="toolbar" [attr.aria-label]="ariaLabel()">
      @if (view() !== null) {
        <div
          class="bp-toolbar__view"
          role="group"
          i18n-aria-label="@@toolbar.view"
          aria-label="Вигляд списку"
        >
          <button
            type="button"
            data-testid="view-table"
            [attr.aria-pressed]="view() === 'table'"
            i18n-title="@@toolbar.view.table"
            title="Таблиця"
            i18n-aria-label="@@toolbar.view.table"
            aria-label="Таблиця"
            (click)="view.set('table')"
          >
            <mat-icon>table_rows</mat-icon>
          </button>
          <button
            type="button"
            data-testid="view-cards"
            [attr.aria-pressed]="view() === 'cards'"
            i18n-title="@@toolbar.view.cards"
            title="Картки"
            i18n-aria-label="@@toolbar.view.cards"
            aria-label="Картки"
            (click)="view.set('cards')"
          >
            <mat-icon>grid_view</mat-icon>
          </button>
        </div>
      }
      @if (searchPlaceholder()) {
        <input
          type="search"
          class="bp-toolbar__search"
          [attr.data-testid]="searchTestId()"
          [placeholder]="searchPlaceholder()!"
          [attr.aria-label]="searchPlaceholder()"
          [value]="search()"
          (input)="search.set($any($event.target).value)"
        />
      }
      <div class="bp-toolbar__actions"><ng-content /></div>
      <div class="bp-toolbar__tools">
        @if (showFilter()) {
          <button
            mat-icon-button
            type="button"
            data-testid="toolbar-filter"
            i18n-title="@@toolbar.filter"
            title="Фільтр"
            i18n-aria-label="@@toolbar.filter"
            aria-label="Фільтр"
            (click)="filter.emit()"
          >
            <mat-icon>filter_list</mat-icon>
          </button>
        }
        @if (showExport()) {
          <button
            mat-icon-button
            type="button"
            data-testid="toolbar-export"
            i18n-title="@@toolbar.export"
            title="Вивантажити"
            i18n-aria-label="@@toolbar.export"
            aria-label="Вивантажити"
            (click)="exportData.emit()"
          >
            <mat-icon>download</mat-icon>
          </button>
        }
        @if (showSettings()) {
          <button
            mat-icon-button
            type="button"
            data-testid="toolbar-settings"
            i18n-title="@@toolbar.settings"
            title="Налаштування таблиці"
            i18n-aria-label="@@toolbar.settings"
            aria-label="Налаштування таблиці"
            (click)="settings.emit()"
          >
            <mat-icon>settings</mat-icon>
          </button>
        }
      </div>
    </div>
  `,
})
export class TableToolbarComponent {
  /** Поточний вигляд; `null` — перемикача немає. */
  readonly view = model<TableView | null>(null);
  /** Текст пошуку; без `searchPlaceholder` поле пошуку не показується. */
  readonly search = model('');
  readonly searchPlaceholder = input<string | null>(null);
  readonly searchTestId = input<string | null>(null);
  readonly ariaLabel = input<string | null>(null);
  readonly showFilter = input(false);
  readonly showExport = input(false);
  readonly showSettings = input(false);
  readonly filter = output<void>();
  readonly exportData = output<void>();
  readonly settings = output<void>();
}
