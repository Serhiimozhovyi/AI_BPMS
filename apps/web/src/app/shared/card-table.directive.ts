import { AfterViewInit, Directive, ElementRef, OnDestroy, inject } from '@angular/core';

/**
 * Таблиця, що на телефоні (< 768 px) стає списком карток (F3-85, стилі — `table[appCardTable]` у `styles.scss`):
 * підписи полів беруться із заголовків `th` (тож перекладаються разом із ними) і кладуться в `data-label` клітинок.
 * Заголовок із `data-no-label` — клітинка без підпису (назва картки, дії).
 */
@Directive({ selector: 'table[appCardTable]' })
export class CardTableDirective implements AfterViewInit, OnDestroy {
  private readonly table = inject<ElementRef<HTMLTableElement>>(ElementRef).nativeElement;
  private observer?: MutationObserver;

  ngAfterViewInit(): void {
    this.label();
    // Рядки з'являються й зникають разом зі списком; зміни атрибутів не спостерігаємо, тож циклу немає.
    this.observer = new MutationObserver(() => this.label());
    this.observer.observe(this.table, { childList: true, subtree: true });
  }

  ngOnDestroy(): void {
    this.observer?.disconnect();
  }

  private label(): void {
    const head = this.table.tHead?.rows[0];
    if (!head) return;
    const labels = Array.from(head.cells).map((th) => (th.hasAttribute('data-no-label') ? '' : (th.textContent ?? '').trim()));
    for (const body of Array.from(this.table.tBodies)) {
      for (const row of Array.from(body.rows)) {
        Array.from(row.cells).forEach((cell, i) => {
          const label = labels[i] ?? '';
          if (label && cell.getAttribute('data-label') !== label) cell.setAttribute('data-label', label);
        });
      }
    }
  }
}
