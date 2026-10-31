import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatOption } from '@angular/material/core';
import { MatSelect } from '@angular/material/select';

/** Нормалізація для пошуку: без регістру й зайвих пробілів (кирилиця й латиниця однаково). */
export function normalizeSearch(text: string | null | undefined): string {
  return (text ?? '').toLocaleLowerCase('uk').replace(/\s+/g, ' ').trim();
}

/** Клавіші, які лишаємо списку (перехід між пунктами, вибір, закриття); решта — для поля пошуку. */
const LIST_KEYS = new Set(['ArrowUp', 'ArrowDown', 'Enter', 'Escape', 'Tab', 'PageUp', 'PageDown']);

/**
 * Пошук усередині випадного списку (F9-14, AC-Kit «Dropdown+icon» і «Drop window / Search»): ставиться першим
 * дочірнім елементом кожного `<mat-select>` і ховає пункти, чий текст не містить запиту. Поле з'являється лише
 * тоді, коли пунктів більше за `threshold` (типово 7), — короткі списки лишаються простими, як «Dropdown» у Кіт.
 * Стрілки й Enter працюють зі списком (приховані пункти пропускаються), інші клавіші — з полем; запит скидається
 * при закритті.
 *
 * ```html
 * <mat-select …>
 *   <bp-select-search />
 *   @for (t of tables(); track t.id) { <mat-option [value]="t.name">{{ t.name }}</mat-option> }
 * </mat-select>
 * ```
 */
@Component({
  selector: 'bp-select-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'bp-select-search',
    '[class.bp-select-search--off]': '!active()',
    '(keydown)': 'onKeydown($event)',
    '(click)': '$event.stopPropagation()',
  },
  template: `
    <div class="bp-select-search__box">
      <span class="bp-select-search__icon material-symbols-outlined" aria-hidden="true">search</span>
      <input
        #search
        type="text"
        class="bp-select-search__input"
        data-testid="select-search"
        autocomplete="off"
        [placeholder]="placeholder()"
        [attr.aria-label]="placeholder()"
        [value]="query()"
        (input)="setQuery(search.value)"
      />
    </div>
    @if (nothingFound()) {
      <div class="bp-option-empty" role="status" data-testid="select-search-empty">{{ emptyText() }}</div>
    }
  `,
})
export class SelectSearchComponent {
  /** Підказка в полі пошуку. */
  readonly placeholder = input($localize`:@@select.search:Пошук…`);
  /** Текст, коли жоден пункт не підходить. */
  readonly emptyText = input($localize`:@@select.nothingFound:Нічого не знайдено`);
  /** Від скількох пунктів показувати пошук (більше за це число). */
  readonly threshold = input(7);

  /** Чи показано поле пошуку (пунктів більше за поріг). */
  readonly active = signal(false);

  readonly query = signal('');
  readonly nothingFound = signal(false);

  private readonly select = inject(MatSelect);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly searchInput = viewChild<ElementRef<HTMLInputElement>>('search');

  constructor() {
    const destroyRef = inject(DestroyRef);
    this.select.openedChange.pipe(takeUntilDestroyed(destroyRef)).subscribe((open) => {
      if (open) {
        this.refreshActive();
        if (this.active()) {
          this.installSkip();
          // Панель — popover: фокус можливий лише після показу, тому наступною макрозадачею.
          setTimeout(() => {
            this.searchInput()?.nativeElement.focus();
            this.revealActive();
          });
        }
      } else if (this.query()) {
        this.setQuery('');
      }
    });
    afterNextRender(() => {
      this.select._keyManager?.change.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => this.revealActive());
      this.refreshActive();
      this.select.options.changes.pipe(takeUntilDestroyed(destroyRef)).subscribe(() => {
        this.refreshActive();
        this.apply();
      });
    });
  }

  /** Змінює запит і фільтрує пункти. */
  setQuery(value: string): void {
    this.query.set(value);
    this.apply();
  }

  onKeydown(event: KeyboardEvent): void {
    // Пробіл, літери, Home/End тощо — редагування запиту, не вибір пункту чи type-ahead списку.
    if (!LIST_KEYS.has(event.key)) {
      event.stopPropagation();
    }
  }

  /**
   * Material прокручує активний пункт до верхнього краю панелі — тобто під липке поле пошуку. Зсуваємо прокрутку
   * на висоту поля, щоб пункт було видно.
   */
  private revealActive(): void {
    const option = this.select._keyManager?.activeItem;
    const panel = this.select.panel?.nativeElement as HTMLElement | undefined;
    if (!this.active() || !option || !panel) return;
    const header = this.host.nativeElement.offsetHeight;
    const top = option._getHostElement().offsetTop;
    if (top - header < panel.scrollTop) {
      panel.scrollTop = Math.max(0, top - header);
    }
  }

  private refreshActive(): void {
    this.active.set((this.select.options?.length ?? 0) > this.threshold());
  }

  private apply(): void {
    const needle = normalizeSearch(this.query());
    let visible = 0;
    this.select.options.forEach((option) => {
      const show = !needle || normalizeSearch(option.viewValue).includes(needle);
      option._getHostElement().hidden = !show;
      if (show) visible++;
    });
    this.nothingFound.set(needle !== '' && visible === 0);
    if (needle && this.select.panelOpen) {
      this.select._keyManager?.setFirstItemActive();
    }
  }

  /** Стрілки пропускають приховані пункти; вимкнені — як у Material (доступні лише з відкритою панеллю). */
  private installSkip(): void {
    this.select._keyManager?.skipPredicate(
      (option: MatOption) => option._getHostElement().hidden === true || (!this.select.panelOpen && option.disabled),
    );
  }
}
