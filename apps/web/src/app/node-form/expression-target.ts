import { Injectable, signal } from '@angular/core';

/** Поле форми, у яке можна вставити вираз: останнє з фокусом (F3-52). */
export interface ExpressionTarget {
  /** Назва поля — для підказки «вставлено в …». */
  label(): string;
  /** Вставляє вираз у позицію курсора поля (або замість виділення). */
  insert(expression: string): void;
}

/**
 * Куди вставляється вираз з панелі входу (F3-52): поле параметра, що останнім отримало фокус. Надається редактором;
 * поля форми вузла реєструються самі, панель входу вставляє клік по полю схеми.
 */
@Injectable()
export class ExpressionTargetService {
  private target: ExpressionTarget | null = null;

  /** Назва активного поля; `null` — жодне поле ще не мало фокусу. */
  readonly activeLabel = signal<string | null>(null);

  activate(target: ExpressionTarget): void {
    this.target = target;
    this.activeLabel.set(target.label());
  }

  /** Поле зникло (інший вузол, перебудова форми) — більше не ціль. */
  release(target: ExpressionTarget): void {
    if (this.target !== target) return;
    this.target = null;
    this.activeLabel.set(null);
  }

  /** `false` — немає активного поля, нічого не вставлено. */
  insert(expression: string): boolean {
    if (!this.target) return false;
    this.target.insert(expression);
    return true;
  }
}
