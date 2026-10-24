import { Signal, computed, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { WorkflowValidationError, validationErrorsOf } from '../workflows/workflows-api.service';

/** Пауза після останньої зміни перед збереженням, мс. */
export const AUTOSAVE_DEBOUNCE_MS = 800;

/**
 * Стан автозбереження: `idle` — змін не було; `pending` — чекає паузи; `saving` — запит у дорозі;
 * `saved` — останні зміни збережено; `error` — останнє збереження відхилено (зміни лишаються незбереженими).
 */
export type AutosaveStatus = 'idle' | 'pending' | 'saving' | 'saved' | 'error';

/**
 * Автозбереження з debounce: кожна зміна перезапускає таймер; по паузі зберігається **останнє** значення.
 * Одночасно в дорозі не більше одного запиту — зміни, що прийшли під час збереження, підуть наступним запитом
 * (порядок версій на сервері збігається з порядком змін). Відхилене збереження не повторюється само —
 * чекає наступної зміни або {@link flush}; помилки валідації (422) доступні в {@link validationErrors}.
 */
export class Autosave<T, R = unknown> {
  private readonly state = signal<AutosaveStatus>('idle');
  private readonly errors = signal<WorkflowValidationError[]>([]);
  private readonly message = signal<string | null>(null);

  private timer: ReturnType<typeof setTimeout> | null = null;
  /** Запит у дорозі. */
  private saving = false;
  /** Покоління стану: `reset` його збільшує, і результат старого запиту стан уже не змінює. */
  private generation = 0;
  /** Значення, ще не відправлене на сервер. */
  private queued: { value: T } | null = null;

  readonly status: Signal<AutosaveStatus> = this.state.asReadonly();
  /** Помилки валідації останнього відхиленого збереження (422). */
  readonly validationErrors: Signal<WorkflowValidationError[]> = this.errors.asReadonly();
  /** Текст помилки останнього відхиленого збереження. */
  readonly errorMessage: Signal<string | null> = this.message.asReadonly();
  /** Є зміни, яких ще немає на сервері. */
  readonly dirty: Signal<boolean> = computed(() => {
    const s = this.state();
    return s === 'pending' || s === 'saving' || s === 'error';
  });

  constructor(
    private readonly save: (value: T) => Observable<R>,
    private readonly onSaved: (result: R, value: T) => void = () => undefined,
    private readonly debounceMs = AUTOSAVE_DEBOUNCE_MS,
    private readonly onError: (error: unknown, value: T) => void = () => undefined,
  ) {}

  /** Зміна від користувача: зберегти після паузи. */
  schedule(value: T): void {
    this.queued = { value };
    this.clearTimer();
    if (!this.saving || this.state() !== 'saving') this.state.set('pending');
    this.timer = setTimeout(() => {
      this.timer = null;
      this.send();
    }, this.debounceMs);
  }

  /** Зберегти відкладене значення негайно (без паузи). */
  flush(): void {
    this.clearTimer();
    this.send();
  }

  /**
   * Почати з нуля (перехід на інший сценарій): забуває відкладене значення і стан. Запит у дорозі не скасовується —
   * вже відправлені зміни мають дійти до сервера, — але його результат більше не змінює стан.
   */
  reset(): void {
    this.clearTimer();
    this.queued = null;
    this.generation++;
    this.state.set('idle');
    this.errors.set([]);
    this.message.set(null);
  }

  private send(): void {
    if (this.saving || !this.queued) return;
    const { value } = this.queued;
    const generation = this.generation;
    this.queued = null;
    this.saving = true;
    this.state.set('saving');
    this.save(value).subscribe({
      next: (result) => {
        this.onSaved(result, value);
        if (generation !== this.generation) return;
        this.errors.set([]);
        this.message.set(null);
      },
      error: (e: unknown) => {
        this.saving = false;
        if (generation === this.generation) {
          this.errors.set(validationErrorsOf(e));
          this.message.set(apiErrorMessage(e));
        }
        this.onError(e, value);
        // Нова зміна під час запиту — пробуємо зберегти її: вона могла виправити помилку.
        if (this.queued) this.continueQueued();
        else if (generation === this.generation) this.state.set('error');
      },
      complete: () => {
        this.saving = false;
        if (this.queued) this.continueQueued();
        else if (generation === this.generation) this.state.set('saved');
      },
    });
  }

  /** Під час запиту прийшли зміни: якщо таймер ще йде — чекаємо його, інакше відправляємо одразу. */
  private continueQueued(): void {
    if (this.timer) this.state.set('pending');
    else this.send();
  }

  private clearTimer(): void {
    if (this.timer) {
      clearTimeout(this.timer);
      this.timer = null;
    }
  }
}
