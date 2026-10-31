import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, linkedSignal, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ExecutionItem } from '../executions/executions-api.service';
import { formatPinnedItems, parsePinnedItems } from './pinned-data';

/**
 * Редактор закріпленого output вузла (F3-51): items як JSON у полі вводу. «Застосувати» перевіряє формат і віддає
 * items у `save`; некоректний текст не застосовується, помилка — під полем. Нові `items` ззовні (undo, інший вузол)
 * замінюють текст.
 */
@Component({
  selector: 'app-pinned-data-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <section class="pinned" data-testid="pinned-editor">
      <h3 class="pinned__title">
        <span i18n="@@pinned.title">Закріплений output</span>
        <span class="pinned__count">{{ items().length }}</span>
      </h3>
      <p class="pinned__hint" i18n="@@pinned.hint">Тестові запуски віддають ці items, не виконуючи вузол.</p>
      <textarea
        class="pinned__json"
        data-testid="pinned-json"
        spellcheck="false"
        rows="10"
        i18n-aria-label="@@pinned.label"
        aria-label="Закріплені items у JSON"
        [readOnly]="readonly()"
        [value]="text()"
        [attr.aria-invalid]="error() ? true : null"
        (input)="edited($event)"
      ></textarea>
      @if (error(); as message) {
        <p class="pinned__error" role="alert" data-testid="pinned-error">{{ message }}</p>
      }
      @if (!readonly()) {
        <div class="pinned__actions">
          <button mat-flat-button type="button" data-testid="pinned-apply" [disabled]="!dirty()" (click)="apply()" i18n="@@pinned.apply">Застосувати</button>
          <button mat-button type="button" data-testid="pinned-reset" [disabled]="!dirty()" (click)="reset()" i18n="@@pinned.reset">Скасувати зміни</button>
        </div>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }

    .pinned {
      display: flex;
      flex-direction: column;
      gap: 8px;
      padding: 12px 16px;
      border-top: 1px solid var(--mat-sys-outline-variant);
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }

    .pinned__title {
      display: flex;
      align-items: center;
      gap: 8px;
      margin: 0;
      font-size: 14px;
    }

    .pinned__count {
      padding: 0 8px;
      border-radius: 10px;
      background: var(--bp-tonal);
      font-size: 12px;
    }

    .pinned__hint {
      margin: 0;
      font-size: 12px;
      opacity: 0.7;
    }

    .pinned__json {
      box-sizing: border-box;
      width: 100%;
      min-height: 160px;
      padding: 8px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius-sm);
      font: 12px/1.4 monospace;
      resize: vertical;
    }

    .pinned__json[aria-invalid='true'] {
      border-color: var(--bp-danger);
    }

    .pinned__error {
      margin: 0;
      font-size: 12px;
      color: var(--bp-danger);
    }

    .pinned__actions {
      display: flex;
      gap: 8px;
    }
  `,
})
export class PinnedDataEditorComponent {
  /** Закріплені items вузла. */
  readonly items = input.required<readonly unknown[]>();

  /** Лише перегляд: текст не редагується. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Нові items після «Застосувати». */
  readonly save = output<ExecutionItem[]>();

  private readonly original = computed(() => formatPinnedItems(this.items()));
  protected readonly text = linkedSignal(() => this.original());
  protected readonly error = linkedSignal<string, string | null>({ source: this.original, computation: () => null });
  protected readonly dirty = computed(() => this.text() !== this.original());

  protected edited(event: Event): void {
    this.text.set((event.target as HTMLTextAreaElement).value);
    this.error.set(null);
  }

  protected apply(): void {
    const result = parsePinnedItems(this.text());
    if ('error' in result) {
      this.error.set(result.error);
      return;
    }
    this.save.emit(result.items);
  }

  protected reset(): void {
    this.text.set(this.original());
    this.error.set(null);
  }
}
