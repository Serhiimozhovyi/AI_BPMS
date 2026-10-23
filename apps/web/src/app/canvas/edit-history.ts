import { Signal, computed, signal } from '@angular/core';

/** Скільки кроків назад пам'ятає історія за замовчуванням. */
export const DEFAULT_HISTORY_LIMIT = 100;

/**
 * Лінійна історія змін для undo/redo: знімки незмінних значень (визначення сценарію).
 * `push` після `undo` відкидає гілку redo; найстаріші знімки понад `limit` забуваються.
 */
export class EditHistory<T> {
  private readonly past = signal<readonly T[]>([]);
  private readonly future = signal<readonly T[]>([]);
  private readonly present = signal<T>(undefined as T);

  readonly canUndo: Signal<boolean> = computed(() => this.past().length > 0);
  readonly canRedo: Signal<boolean> = computed(() => this.future().length > 0);
  readonly current: Signal<T> = this.present.asReadonly();

  constructor(
    initial: T,
    private readonly limit = DEFAULT_HISTORY_LIMIT,
  ) {
    this.present.set(initial);
  }

  /** Починає історію заново (інший сценарій, перезавантаження). */
  reset(value: T): void {
    this.past.set([]);
    this.future.set([]);
    this.present.set(value);
  }

  /** Новий стан після дії користувача. Той самий об'єкт — не крок історії. */
  push(value: T): void {
    if (Object.is(value, this.present())) return;
    const past = [...this.past(), this.present()];
    this.past.set(past.length > this.limit ? past.slice(past.length - this.limit) : past);
    this.future.set([]);
    this.present.set(value);
  }

  /** Крок назад; `undefined`, якщо нікуди. */
  undo(): T | undefined {
    const past = this.past();
    if (past.length === 0) return undefined;
    const previous = past[past.length - 1];
    this.past.set(past.slice(0, -1));
    this.future.set([this.present(), ...this.future()]);
    this.present.set(previous);
    return previous;
  }

  /** Крок уперед після `undo`; `undefined`, якщо нікуди. */
  redo(): T | undefined {
    const [next, ...rest] = this.future();
    if (this.future().length === 0) return undefined;
    this.past.set([...this.past(), this.present()]);
    this.future.set(rest);
    this.present.set(next);
    return next;
  }
}
