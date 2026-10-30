import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, afterNextRender, computed, inject, input, model, signal, viewChild } from '@angular/core';
import { Params, RouterLink, RouterLinkActive } from '@angular/router';

/** Вид вкладок AC-Kit («Atoms / Tabs»): пігулки для фільтрів, підкреслені для розділів сторінки, з іконкою для карток. */
export type BpTabsVariant = 'pills' | 'underline' | 'icon';

export interface BpTab {
  /** Значення вкладки (режим значення) або ключ (режим посилань); також стає `data-tab`. */
  value: string;
  label: string;
  /** Значок перед підписом (символ або emoji, як у решті інтерфейсу). */
  icon?: string;
  /** `routerLink` — вкладка стає посиланням, активну визначає `routerLinkActive`. */
  link?: string | readonly unknown[];
  queryParams?: Params;
  testId?: string;
  disabled?: boolean;
}

/**
 * `bp-tabs` (F9-03): один компонент для трьох видів вкладок AC-Kit. Режим посилань — вкладки з `link`
 * (активна — за адресою), режим значення — `[(value)]`. ARIA `tablist`/`tab` з `aria-selected`, «блукаючий» `tabindex`:
 * ←/→ (і ↑/↓) переходять між вкладками, Home/End — до першої/останньої; у режимі значення перехід одразу вибирає
 * вкладку, у режимі посилань — лише фокусує (Enter відкриває). Коли вкладки не вміщаються — прокрутка й кнопки ‹ ›.
 * Стилі — `theme/_tabs.scss`.
 */
@Component({
  // Префікс bp — спільні компоненти дизайн-системи (класи .bp-tabs у theme/_tabs.scss).
  // eslint-disable-next-line @angular-eslint/component-selector
  selector: 'bp-tabs',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive],
  host: {
    class: 'bp-tabs',
    '[class.bp-tabs--pills]': 'variant() === "pills"',
    '[class.bp-tabs--underline]': 'variant() === "underline"',
    '[class.bp-tabs--icon]': 'variant() === "icon"',
    '[class.bp-tabs--overflow]': 'overflow()',
  },
  template: `
    @if (overflow()) {
      <button type="button" class="bp-tabs__scroll" tabindex="-1" aria-hidden="true" data-testid="tabs-scroll-start" [disabled]="atStart()" (click)="scrollBy(-1)">‹</button>
    }
    <div #list class="bp-tabs__list" role="tablist" [attr.aria-label]="label() || null" [attr.data-testid]="testId() || null" (scroll)="measure()">
      @for (t of tabs(); track t.value; let i = $index) {
        @if (t.link !== undefined) {
          <a
            #rla="routerLinkActive"
            class="bp-tab"
            role="tab"
            [routerLink]="t.link"
            [queryParams]="t.queryParams"
            routerLinkActive="bp-tab--on"
            (isActiveChange)="linkActive($index, $event)"
            [attr.aria-selected]="rla.isActive"
            [attr.aria-disabled]="t.disabled || null"
            [attr.tabindex]="focusIndex() === i ? 0 : -1"
            [attr.data-tab]="t.value"
            [attr.data-testid]="t.testId || null"
            (focus)="focused.set(i)"
            (keydown)="onKeydown($event)"
          >
            @if (t.icon) {
              <span class="bp-tab__icon" aria-hidden="true">{{ t.icon }}</span>
            }
            <span class="bp-tab__label">{{ t.label }}</span>
          </a>
        } @else {
          <button
            type="button"
            class="bp-tab"
            role="tab"
            [class.bp-tab--on]="t.value === value()"
            [attr.aria-selected]="t.value === value()"
            [disabled]="t.disabled"
            [attr.tabindex]="focusIndex() === i ? 0 : -1"
            [attr.data-tab]="t.value"
            [attr.data-testid]="t.testId || null"
            (focus)="focused.set(i)"
            (keydown)="onKeydown($event)"
            (click)="select(t)"
          >
            @if (t.icon) {
              <span class="bp-tab__icon" aria-hidden="true">{{ t.icon }}</span>
            }
            <span class="bp-tab__label">{{ t.label }}</span>
          </button>
        }
      }
    </div>
    @if (overflow()) {
      <button type="button" class="bp-tabs__scroll" tabindex="-1" aria-hidden="true" data-testid="tabs-scroll-end" [disabled]="atEnd()" (click)="scrollBy(1)">›</button>
    }
  `,
})
export class BpTabsComponent {
  readonly tabs = input.required<readonly BpTab[]>();
  readonly variant = input<BpTabsVariant>('underline');
  readonly label = input('');
  readonly testId = input('');
  /** Вибрана вкладка в режимі значення. */
  readonly value = model<string | null>(null);

  private readonly list = viewChild.required<ElementRef<HTMLElement>>('list');
  /** Активна вкладка-посилання (за `routerLinkActive`). */
  private readonly activeLink = signal(-1);
  /** Вкладка, на якій зараз фокус (після стрілок/кліку). */
  readonly focused = signal(-1);
  readonly overflow = signal(false);
  readonly atStart = signal(true);
  readonly atEnd = signal(false);

  private readonly selectedIndex = computed(() => {
    const tabs = this.tabs();
    const byValue = tabs.findIndex((t) => t.link === undefined && t.value === this.value());
    return byValue >= 0 ? byValue : this.activeLink();
  });

  /** Єдина вкладка з `tabindex=0`: сфокусована, інакше вибрана, інакше перша. */
  readonly focusIndex = computed(() => {
    const count = this.tabs().length;
    const focused = this.focused();
    if (focused >= 0 && focused < count) return focused;
    const selected = this.selectedIndex();
    return selected >= 0 && selected < count ? selected : 0;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      this.measure();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => this.measure());
      observer.observe(this.list().nativeElement);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  select(tab: BpTab): void {
    if (!tab.disabled) this.value.set(tab.value);
  }

  linkActive(index: number, active: boolean): void {
    if (active) this.activeLink.set(index);
    else if (this.activeLink() === index) this.activeLink.set(-1);
  }

  onKeydown(event: KeyboardEvent): void {
    const tabs = this.tabs();
    const enabled = tabs.map((t, i) => (t.disabled ? -1 : i)).filter((i) => i >= 0);
    if (enabled.length === 0) return;
    const current = enabled.indexOf(this.focusIndex());
    let next: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        next = enabled[(current + 1) % enabled.length];
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        next = enabled[(current - 1 + enabled.length) % enabled.length];
        break;
      case 'Home':
        next = enabled[0];
        break;
      case 'End':
        next = enabled[enabled.length - 1];
        break;
      default:
        return;
    }
    event.preventDefault();
    this.focused.set(next);
    const el = this.list().nativeElement.querySelectorAll<HTMLElement>('.bp-tab')[next];
    el?.focus();
    el?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    if (tabs[next].link === undefined) this.select(tabs[next]);
  }

  scrollBy(direction: 1 | -1): void {
    const el = this.list().nativeElement;
    el.scrollBy?.({ left: direction * Math.max(el.clientWidth * 0.8, 80), behavior: 'smooth' });
  }

  /** Перераховує переповнення й положення прокрутки (кнопки ‹ ›). */
  measure(): void {
    const el = this.list().nativeElement;
    this.overflow.set(el.scrollWidth > el.clientWidth + 1);
    this.atStart.set(el.scrollLeft <= 0);
    this.atEnd.set(el.scrollLeft + el.clientWidth >= el.scrollWidth - 1);
  }
}
