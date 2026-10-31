import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, computed, input, output, signal, viewChild } from '@angular/core';
import { PaletteItem, PaletteSection, connectorPageSections, filterPalette, itemHint, itemTitle } from './palette';

/**
 * Спливаюча палітра біля вузла чи ребра (F3-50): пошук з фокусом одразу, секції без згортання, клавіші
 * `↑`/`↓` — вибір, `Enter` — вставити вибраний (за замовчуванням перший результат), `Esc` — закрити.
 * Секції готує редактор (`quickAddSections`): лише тригери чи лише кроки, «Останні» першими.
 */
@Component({
  selector: 'app-quick-add',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { role: 'dialog', '[attr.aria-label]': 'title()', '(keydown)': 'keydown($event)' },
  template: `
    <div class="quick__title">{{ title() }}</div>
    <input
      #searchInput
      type="search"
      class="quick__search"
      data-testid="quick-add-search"
      autocomplete="off"
      i18n-placeholder="@@canvas.quickAdd.search"
      placeholder="Пошук вузла або дії"
      [value]="query()"
      [attr.aria-activedescendant]="activeKey() ? 'quick-' + activeIndex() : null"
      (input)="search($event)"
    />
    @if (page(); as p) {
      <div class="quick__page" data-testid="quick-add-page" [attr.data-connector]="p.connectorId">
        <button type="button" class="quick__back" data-testid="quick-add-back" tabindex="-1" (click)="back()">
          <span class="material-symbols-outlined" aria-hidden="true">arrow_back</span>
          <span i18n="@@canvas.palette.back">Назад</span>
        </button>
        <span class="quick__page-title">{{ p.label }}</span>
      </div>
    }
    <div class="quick__list" role="listbox">
      @for (section of visible(); track section.id) {
        <div class="quick__heading">{{ section.label }}</div>
        @for (item of section.items; track item.key) {
          @let index = indexOf(section, item);
          <button
            type="button"
            role="option"
            class="quick__item"
            tabindex="-1"
            [id]="'quick-' + index"
            [class.quick__item--active]="index === activeIndex()"
            [attr.aria-selected]="index === activeIndex()"
            [attr.data-item]="item.key"
            [title]="tip(item)"
            (mouseenter)="activeIndex.set(index)"
            (click)="choose(item)"
          >
            <span class="quick__label">{{ page() ? (item.nodeName ?? item.label) : item.label }}</span>
            <span class="quick__type">{{ hint(item) }}</span>
          </button>
        }
      } @empty {
        <p class="quick__empty" data-testid="quick-add-empty" i18n="@@canvas.palette.empty">Нічого не знайдено</p>
      }
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      width: 300px;
      max-height: 380px;
      padding: 8px;
      border: 1px solid var(--bp-line);
      border-radius: var(--ac-radius);
      background: var(--bp-white);
      box-shadow: var(--ac-shadow);
      font: 13px/1.3 Ubuntu, sans-serif;
    }

    .quick__title {
      padding: 2px 4px 6px;
      font-size: 11px;
      font-weight: 700;
      color: var(--bp-text-muted);
      text-transform: uppercase;
    }

    .quick__search {
      box-sizing: border-box;
      width: 100%;
      padding: 8px 10px;
      border: 1px solid var(--bp-line);
      border-radius: var(--ac-radius-sm);
      font: inherit;
      outline: none;
    }

    .quick__search:focus {
      border-color: var(--bp-primary);
    }

    .quick__list {
      margin-top: 6px;
      overflow-y: auto;
    }

    .quick__heading {
      padding: 8px 4px 2px;
      font-size: 11px;
      font-weight: 700;
      color: var(--bp-text);
      text-transform: uppercase;
    }

    .quick__item {
      display: flex;
      flex-direction: column;
      width: 100%;
      padding: 6px 8px;
      border: 0;
      border-radius: 6px;
      background: none;
      text-align: left;
      cursor: pointer;
      font: inherit;
    }

    .quick__item--active {
      background: var(--bp-tonal);
    }

    .quick__type {
      font-size: 11px;
      opacity: 0.6;
    }

    .quick__page {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 6px;
    }

    .quick__back {
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 0;
      border: 0;
      background: none;
      color: var(--bp-primary);
      font: inherit;
      cursor: pointer;
    }

    .quick__back .material-symbols-outlined {
      font-size: 16px;
    }

    .quick__page-title {
      font-weight: 500;
    }

    .quick__empty {
      margin: 8px 4px;
      opacity: 0.7;
    }
  `,
})
export class QuickAddComponent implements AfterViewInit {
  /** Секції (уже відфільтровані за режимом, «Останні» першими). */
  readonly sections = input.required<readonly PaletteSection[]>();

  /** Заголовок над пошуком: «Додати тригер», «Наступний крок», «Вставити крок». */
  readonly title = input('');

  /** Обраний елемент. */
  readonly pick = output<PaletteItem>();

  /** Закрити без вибору (`Esc`). */
  readonly closed = output<void>();

  protected readonly query = signal('');
  protected readonly activeIndex = signal(0);

  /** Відкрита сторінка конектора (F3-96); `null` — корінь меню. */
  protected readonly page = computed(() => {
    const key = this.pageKey();
    return key ? (this.sections().flatMap((s) => s.items).find((i) => i.key === key && i.connector) ?? null) : null;
  });
  private readonly pageKey = signal<string | null>(null);

  protected readonly visible = computed(() => {
    const page = this.page();
    return page ? filterPalette(connectorPageSections(page), this.query()) : filterPalette(this.sections(), this.query());
  });
  protected readonly hint = itemHint;
  protected readonly tip = itemTitle;

  /** Плаский список видимих елементів у порядку показу — для клавіш і `Enter`. */
  private readonly flat = computed(() => this.visible().flatMap((s) => s.items.map((item) => ({ section: s.id, item }))));

  protected readonly activeKey = computed(() => this.flat()[this.activeIndex()]?.item.key ?? null);

  private readonly searchEl = viewChild.required<ElementRef<HTMLInputElement>>('searchInput');

  ngAfterViewInit(): void {
    this.searchEl().nativeElement.focus();
  }

  protected indexOf(section: PaletteSection, item: PaletteItem): number {
    return this.flat().findIndex((e) => e.section === section.id && e.item === item);
  }

  /** Конектор відкриває свою сторінку, дія чи подія додається вузлом. */
  protected choose(item: PaletteItem): void {
    if (item.connector) {
      this.pageKey.set(item.key);
      this.query.set('');
      this.activeIndex.set(0);
      return;
    }
    this.pick.emit(item);
  }

  protected back(): void {
    this.pageKey.set(null);
    this.query.set('');
    this.activeIndex.set(0);
    this.searchEl().nativeElement.focus();
  }

  protected search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
    this.activeIndex.set(0);
  }

  protected keydown(event: KeyboardEvent): void {
    const count = this.flat().length;
    switch (event.key) {
      case 'Escape':
        // На сторінці конектора `Esc` повертає до списку, а не закриває меню.
        if (this.page()) this.back();
        else this.closed.emit();
        break;
      case 'Enter': {
        const entry = this.flat()[this.activeIndex()];
        if (entry) this.choose(entry.item);
        break;
      }
      case 'ArrowDown':
        if (count > 0) this.activeIndex.set((this.activeIndex() + 1) % count);
        break;
      case 'ArrowUp':
        if (count > 0) this.activeIndex.set((this.activeIndex() - 1 + count) % count);
        break;
      default:
        return;
    }
    event.preventDefault();
    // Клавіші спливаючої палітри не доходять до гарячих клавіш редактора (Delete, Ctrl+Z…).
    event.stopPropagation();
  }
}
