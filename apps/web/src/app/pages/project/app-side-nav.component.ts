import { AfterViewInit, ChangeDetectionStrategy, Component, DestroyRef, ElementRef, inject, input, signal } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';
import { PAGE_KIND_ICONS } from '../../projects/app-menu';
import { PageKind } from '../../projects/project-pages-api.service';

/** Група бокового меню застосунку: підпис (може бути порожнім) і пункти-сторінки. */
export interface SideNavGroup {
  label: string;
  pages: { key: string; name: string; kind: PageKind }[];
}

/** Ключ `localStorage`: бокове меню застосунку згорнуте до значків. */
export const SIDE_NAV_COLLAPSED_KEY = 'openbp.appNav.collapsed';

/**
 * Бокове меню застосунку проєкту (F8-18, вигляд фази 9): компактна шапка зі значком і назвою, групи розділів, пункти зі
 * значком за типом сторінки й підсвіченим активним, згортання до значків (пам'ятається). Інструменти редактора
 * («Конструктор», «Вигляд застосунку») — окремим блоком унизу, не серед розділів.
 */
@Component({
  selector: 'app-side-nav',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  host: {
    class: 'side-nav',
    '[class.side-nav--collapsed]': 'collapsed()',
    '[style.max-height.px]': 'maxHeight()',
    '(window:resize)': 'fit()',
    '(window:scroll)': 'fit()',
  },
  template: `
    <div class="side-nav__head">
      @if (iconName(); as icon) {
        <span class="side-nav__sign material-symbols-outlined" aria-hidden="true" data-testid="shell-icon">{{ icon }}</span>
      } @else if (emoji(); as e) {
        <span class="side-nav__sign" aria-hidden="true" data-testid="shell-icon">{{ e }}</span>
      }
      <h2 class="side-nav__title" data-testid="shell-title" [title]="title()">{{ title() }}</h2>
      <button
        type="button"
        class="side-nav__toggle"
        data-testid="shell-collapse"
        [attr.aria-expanded]="!collapsed()"
        [title]="collapsed() ? expandLabel : collapseLabel"
        [attr.aria-label]="collapsed() ? expandLabel : collapseLabel"
        (click)="toggle()"
      >
        <span class="material-symbols-outlined" aria-hidden="true">{{ collapsed() ? 'left_panel_open' : 'left_panel_close' }}</span>
      </button>
    </div>
    <nav class="side-nav__nav" aria-label="Розділи застосунку" i18n-aria-label="@@project.app.sections" data-testid="shell-nav">
      @for (group of groups(); track $index) {
        @if (group.label) {
          <div class="side-nav__group" data-testid="shell-group">{{ group.label }}</div>
        } @else if (!$first) {
          <div class="side-nav__divider" aria-hidden="true"></div>
        }
        @for (p of group.pages; track p.key) {
          <a class="side-nav__link" [routerLink]="[p.key]" routerLinkActive="active" ariaCurrentWhenActive="page" [attr.data-page]="p.key" [title]="p.name">
            <span class="material-symbols-outlined" aria-hidden="true">{{ icon(p.kind) }}</span>
            <span class="side-nav__label">{{ p.name }}</span>
          </a>
        }
      }
    </nav>
    @if (canEdit()) {
      <div class="side-nav__tools" role="group" aria-label="Налаштування застосунку" i18n-aria-label="@@project.app.tools">
        <a class="side-nav__link side-nav__link--tool" data-testid="shell-builder" routerLink="_builder" routerLinkActive="active" title="Конструктор сторінок" i18n-title="@@project.app.builder.hint">
          <span class="material-symbols-outlined" aria-hidden="true">construction</span>
          <span class="side-nav__label" i18n="@@project.app.builder">Конструктор</span>
        </a>
        <a class="side-nav__link side-nav__link--tool" data-testid="shell-settings" routerLink="_app" routerLinkActive="active" title="Назва, значок і меню застосунку" i18n-title="@@project.app.settings.hint">
          <span class="material-symbols-outlined" aria-hidden="true">tune</span>
          <span class="side-nav__label" i18n="@@project.app.settings">Вигляд застосунку</span>
        </a>
      </div>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 12px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius-lg);
      background: var(--bp-white);
    }

    .side-nav__head {
      display: flex;
      align-items: center;
      gap: 10px;
      min-height: 36px;
      padding: 0 0 8px 4px;
      border-bottom: 1px solid var(--bp-line);
    }

    .side-nav__sign {
      display: grid;
      flex-shrink: 0;
      place-items: center;
      width: 32px;
      height: 32px;
      border-radius: var(--bp-radius);
      background: var(--bp-tonal);
      color: var(--bp-primary);
      font-size: 18px;

      &.material-symbols-outlined {
        font-size: 20px;
      }
    }

    .side-nav__title {
      display: -webkit-box;
      flex: 1;
      min-width: 0;
      margin: 0;
      overflow: hidden;
      color: var(--bp-text);
      font: var(--bp-font-h7);
      -webkit-line-clamp: 3;
      -webkit-box-orient: vertical;
    }

    .side-nav__toggle {
      display: grid;
      flex-shrink: 0;
      place-items: center;
      width: 32px;
      height: 32px;
      padding: 0;
      border: 0;
      border-radius: var(--bp-radius);
      background: transparent;
      color: var(--bp-text-muted);
      cursor: pointer;

      .material-symbols-outlined {
        font-size: 20px;
      }

      &:hover {
        background: var(--bp-bg-2);
        color: var(--bp-text);
      }

      &:focus-visible {
        outline: 2px solid var(--bp-primary);
        outline-offset: 1px;
      }
    }

    .side-nav__nav {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }

    .side-nav__group {
      padding: 8px 10px 2px;
      color: var(--bp-text-muted);
      font: 500 11px/16px var(--bp-font-family);
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }

    .side-nav__divider {
      margin: 6px 10px;
      border-top: 1px solid var(--bp-line);
    }

    .side-nav__link {
      position: relative;
      display: flex;
      align-items: center;
      gap: 10px;
      min-height: 32px;
      padding: 5px 10px;
      border-radius: var(--bp-radius);
      color: var(--bp-text);
      font: var(--bp-font-p3);
      text-decoration: none;

      .material-symbols-outlined {
        flex-shrink: 0;
        color: var(--bp-text-muted);
        font-size: 20px;
      }

      &:hover {
        background: var(--bp-bg-2);
      }

      &:focus-visible {
        outline: 2px solid var(--bp-primary);
        outline-offset: -2px;
      }

      &.active {
        background: var(--bp-tonal);
        color: var(--bp-primary);
        font-weight: 500;

        .material-symbols-outlined {
          color: var(--bp-primary);
        }

        &::before {
          position: absolute;
          top: 8px;
          bottom: 8px;
          left: 0;
          width: 3px;
          border-radius: 0 3px 3px 0;
          background: var(--bp-primary);
          content: '';
        }
      }
    }

    .side-nav__label {
      min-width: 0;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .side-nav__tools {
      display: flex;
      flex-direction: column;
      gap: 2px;
      padding-top: 8px;
      border-top: 1px solid var(--bp-line);
    }

    .side-nav__link--tool {
      color: var(--bp-text-muted);
    }

    /* Панель липка й не нижча за край вікна (max-height рахує fit()): розділи прокручуються всередині, а інструменти
       редактора лишаються видимими. */
    @media (min-width: 768px) {
      :host {
        width: 240px;
      }

      .side-nav__nav {
        min-height: 0;
        overflow-y: auto;
        scrollbar-width: thin;
      }

      :host(.side-nav--collapsed) {
        width: 60px;
      }
    }

    :host(.side-nav--collapsed) {
      align-items: center;
      padding: 12px 8px;

      .side-nav__head {
        flex-direction: column;
        padding: 0 0 8px;
      }

      .side-nav__title,
      .side-nav__label {
        display: none;
      }

      /* Групи без підписів — лише розділювачі між значками. */
      .side-nav__group {
        height: 0;
        margin: 6px 6px;
        padding: 0;
        overflow: hidden;
        font-size: 0;
        border-top: 1px solid var(--bp-line);
      }

      .side-nav__nav,
      .side-nav__tools {
        align-self: stretch;
      }

      .side-nav__link {
        justify-content: center;
        padding: 6px;
      }
    }

    /* Телефон: розділи — рядок, що прокручується; згортання не потрібне. */
    @media (max-width: 767.98px) {
      :host {
        padding: 8px;
      }

      .side-nav__toggle,
      .side-nav__group,
      .side-nav__divider {
        display: none;
      }

      .side-nav__nav,
      .side-nav__tools {
        flex-direction: row;
        overflow-x: auto;
        scrollbar-width: none;
      }

      .side-nav__tools {
        padding-top: 4px;
      }

      .side-nav__link {
        flex-shrink: 0;
      }

      :host(.side-nav--collapsed) {
        align-items: stretch;

        .side-nav__head {
          flex-direction: row;
        }

        .side-nav__title,
        .side-nav__label {
          display: block;
        }
      }
    }
  `,
})
export class AppSideNavComponent implements AfterViewInit {
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  readonly title = input.required<string>();
  /** Назва значка Material Symbols або, якщо значок оболонки — emoji, `emoji`. */
  readonly iconName = input<string | null>(null);
  readonly emoji = input<string | null>(null);
  readonly groups = input.required<readonly SideNavGroup[]>();
  /** Editor чи admin проєкту бачить блок «Конструктор» і «Вигляд застосунку». */
  readonly canEdit = input(false);

  readonly collapsed = signal(readCollapsed());
  /** Висота до нижнього краю вікна (від 768 px); `null` — без обмеження (телефон чи не в браузері). */
  readonly maxHeight = signal<number | null>(null);
  readonly collapseLabel = $localize`:@@project.app.collapse:Згорнути меню`;
  readonly expandLabel = $localize`:@@project.app.expand:Розгорнути меню`;

  ngAfterViewInit(): void {
    this.fit();
    // Шапка проєкту над панеллю довантажується (назва, опис) і зсуває її вниз — перераховуємо за зміною розмітки.
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => this.fit());
    observer.observe(document.body);
    this.destroyRef.onDestroy(() => observer.disconnect());
  }

  /** Панель закінчується над нижнім краєм вікна: на 900 px «Вигляд застосунку» видно без прокрутки сторінки. */
  fit(): void {
    if (typeof window === 'undefined' || window.innerWidth < 768) {
      this.maxHeight.set(null);
      return;
    }
    const top = Math.max(16, this.host.nativeElement.getBoundingClientRect().top);
    this.maxHeight.set(Math.max(240, Math.floor(window.innerHeight - top - 16)));
  }

  icon(kind: PageKind): string {
    return PAGE_KIND_ICONS[kind] ?? 'article';
  }

  toggle(): void {
    const next = !this.collapsed();
    this.collapsed.set(next);
    try {
      if (next) localStorage.setItem(SIDE_NAV_COLLAPSED_KEY, '1');
      else localStorage.removeItem(SIDE_NAV_COLLAPSED_KEY);
    } catch {
      // Сховище недоступне — стан живе до перезавантаження.
    }
  }
}

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(SIDE_NAV_COLLAPSED_KEY) === '1';
  } catch {
    return false;
  }
}
