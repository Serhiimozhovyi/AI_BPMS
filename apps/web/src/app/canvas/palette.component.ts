import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { PaletteItem, PaletteSection, connectorPageSections, filterPalette, itemHint, itemTitle } from './palette';

/** Ключ `localStorage` зі станом згортання секцій: `{ [sectionId]: true — згорнута, false — розгорнута }`. */
export const PALETTE_COLLAPSE_KEY = 'openbp.palette.collapsed';

/**
 * Палітра канвасу: пошук, секції `core.*` за групами і tools за серверами; клік додає вузол.
 * Секції згортаються до заголовка: конектори за замовчуванням згорнуті, групи `core.*` — розгорнуті;
 * вибір користувача пам'ятається в `localStorage`. Під час пошуку всі знайдені секції розгорнуті.
 */
@Component({
  selector: 'app-palette',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatFormFieldModule, MatInputModule],
  template: `
    <mat-form-field appearance="outline" class="w-full" subscriptSizing="dynamic">
      <mat-label>{{ page() ? searchActionLabel : searchLabel }}</mat-label>
      <input matInput type="search" data-testid="palette-search" [value]="query()" (input)="search($event)" />
    </mat-form-field>
    @if (page(); as p) {
      <div class="palette__page" data-testid="palette-page" [attr.data-connector]="p.connectorId">
        <button type="button" class="palette__back" data-testid="palette-back" (click)="back()">
          <span class="material-symbols-outlined" aria-hidden="true">arrow_back</span>
          <span i18n="@@canvas.palette.back">Назад</span>
        </button>
        <h2 class="palette__page-title">
          @if (p.connector?.icon) {
            <span aria-hidden="true">{{ p.connector?.icon }}</span>
          }
          <span>{{ p.label }}</span>
        </h2>
        <p class="palette__page-note" data-testid="palette-page-state">{{ hint(p) }}</p>
        @if (!p.connector?.connected) {
          <p class="palette__page-note" data-testid="palette-page-connect" i18n="@@canvas.palette.connectOnAdd">Підключення створиться, коли ви додасте дію чи подію.</p>
        }
      </div>
    }
    @for (section of visible(); track section.id) {
      <section class="palette__section" [attr.data-section]="section.id">
        @let open = !!page() || query().trim() !== '' || !isCollapsed(section);
        <h2 class="palette__heading">
          <button
            type="button"
            class="palette__title"
            data-testid="palette-toggle"
            [attr.aria-expanded]="open"
            [attr.aria-controls]="'palette-items-' + $index"
            (click)="toggle(section)"
          >
            <span class="material-symbols-outlined palette__chevron" [class.palette__chevron--open]="open" aria-hidden="true">chevron_right</span>
            <span class="palette__title-text">{{ section.label }}</span>
            <span class="palette__count">{{ section.items.length }}</span>
          </button>
        </h2>
        <div class="palette__items" [id]="'palette-items-' + $index" [hidden]="!open">
        @for (item of section.items; track item.key) {
          <button
            type="button"
            class="palette__item"
            [attr.data-item]="item.key"
            [title]="tip(item)"
            (click)="choose(item)"
          >
            <span class="palette__label">{{ page() ? (item.nodeName ?? item.label) : item.label }}</span>
            <span class="palette__type">{{ hint(item) }}</span>
          </button>
        }
        </div>
      </section>
    } @empty {
      <p class="palette__empty" data-testid="palette-empty">{{ page() && query().trim() === '' ? noActions : nothingFound }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      padding: 12px;
      overflow-y: auto;
    }

    .palette__section {
      margin-top: 12px;
    }

    .palette__heading {
      margin: 0 0 4px;
    }

    .palette__title {
      display: flex;
      align-items: center;
      gap: 4px;
      width: 100%;
      padding: 4px 4px 4px 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: none;
      color: var(--ac-vivid);
      font: 700 12px/16px var(--bp-font-family);
      text-align: left;
      text-transform: uppercase;
      cursor: pointer;
    }

    .palette__title:hover,
    .palette__title:focus-visible {
      background: var(--ac-cool-grey-2);
    }

    .palette__chevron {
      font-size: 18px;
      color: var(--ac-cool-grey-4);
      transition: transform 0.15s ease;
    }

    .palette__chevron--open {
      transform: rotate(90deg);
    }

    .palette__title-text {
      flex: 1;
      min-width: 0;
    }

    .palette__count {
      padding: 0 6px;
      border-radius: var(--ac-radius);
      background: var(--ac-button);
      color: var(--ac-kit);
      font-weight: 500;
    }

    .palette__item {
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

    .palette__item:hover,
    .palette__item:focus-visible {
      background: var(--mat-sys-surface-container-high);
    }

    .palette__label {
      font-size: 13px;
    }

    .palette__type {
      font-size: 11px;
      opacity: 0.6;
    }

    .palette__page {
      margin-top: 8px;
    }

    .palette__back {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 2px 6px 2px 0;
      border: 0;
      background: none;
      color: var(--ac-kit);
      font: 500 13px/20px Ubuntu, sans-serif;
      cursor: pointer;
    }

    .palette__back .material-symbols-outlined {
      font-size: 18px;
    }

    .palette__page-title {
      display: flex;
      align-items: center;
      gap: 6px;
      margin: 4px 0 0;
      font: var(--bp-font-h6);
    }

    .palette__page-note {
      margin: 2px 0 0;
      font-size: 12px;
      opacity: 0.7;
    }

    .palette__empty {
      margin-top: 12px;
      opacity: 0.7;
    }
  `,
})
export class PaletteComponent {
  /** Секції палітри (`buildPalette`). */
  readonly sections = input.required<readonly PaletteSection[]>();

  /** Обраний елемент — редактор додає вузол. */
  readonly pick = output<PaletteItem>();

  protected readonly query = signal('');

  /** Відкрита сторінка конектора (F3-96); `null` — корінь палітри. */
  protected readonly page = computed(() => {
    const key = this.pageKey();
    return key ? (this.sections().flatMap((s) => s.items).find((i) => i.key === key && i.connector) ?? null) : null;
  });
  private readonly pageKey = signal<string | null>(null);

  protected readonly visible = computed(() => {
    const page = this.page();
    return page ? filterPalette(connectorPageSections(page), this.query()) : filterPalette(this.sections(), this.query());
  });
  protected readonly searchLabel = $localize`:@@canvas.palette.search:Пошук вузла`;
  protected readonly searchActionLabel = $localize`:@@canvas.palette.searchAction:Пошук дії`;
  protected readonly nothingFound = $localize`:@@canvas.palette.empty:Нічого не знайдено`;
  protected readonly noActions = $localize`:@@canvas.palette.noActions:Дій і подій поки немає: вони з'являться після підключення конектора.`;
  protected readonly hint = itemHint;
  protected readonly tip = itemTitle;

  /** Явний вибір користувача; секції без запису — за замовчуванням (`kind`). */
  private readonly collapsed = signal<Record<string, boolean>>(readCollapsed());

  protected isCollapsed(section: PaletteSection): boolean {
    return this.collapsed()[section.id] ?? section.kind === 'server';
  }

  protected toggle(section: PaletteSection): void {
    if (this.query().trim() !== '') return;
    const next = { ...this.collapsed(), [section.id]: !this.isCollapsed(section) };
    this.collapsed.set(next);
    try {
      localStorage.setItem(PALETTE_COLLAPSE_KEY, JSON.stringify(next));
    } catch {
      // Сховище недоступне (приватний режим) — стан живе до перезавантаження.
    }
  }

  /** Конектор відкриває свою сторінку, дія чи подія додається вузлом. */
  protected choose(item: PaletteItem): void {
    if (item.connector) {
      this.pageKey.set(item.key);
      this.query.set('');
      return;
    }
    this.pick.emit(item);
  }

  protected back(): void {
    this.pageKey.set(null);
    this.query.set('');
  }

  protected search(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }
}

function readCollapsed(): Record<string, boolean> {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(PALETTE_COLLAPSE_KEY) ?? '{}');
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, boolean>) : {};
  } catch {
    return {};
  }
}
