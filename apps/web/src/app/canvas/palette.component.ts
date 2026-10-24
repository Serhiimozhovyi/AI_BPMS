import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { PaletteItem, PaletteSection, filterPalette, itemHint } from './palette';

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
      <mat-label i18n="@@canvas.palette.search">Пошук вузла</mat-label>
      <input matInput type="search" data-testid="palette-search" [value]="query()" (input)="search($event)" />
    </mat-form-field>
    @for (section of visible(); track section.id) {
      <section class="palette__section" [attr.data-section]="section.id">
        @let open = query().trim() !== '' || !isCollapsed(section);
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
            [title]="item.description ?? item.type"
            (click)="pick.emit(item)"
          >
            <span class="palette__label">{{ item.label }}</span>
            <span class="palette__type">{{ hint(item) }}</span>
          </button>
        }
        </div>
      </section>
    } @empty {
      <p class="palette__empty" data-testid="palette-empty" i18n="@@canvas.palette.empty">Нічого не знайдено</p>
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
      font: 700 11px/16px Ubuntu, sans-serif;
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

  protected readonly visible = computed(() => filterPalette(this.sections(), this.query()));
  protected readonly hint = itemHint;

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
