import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

/**
 * Порожній стан сторінки (F3-82): іконка AC-Kit (Material Symbols), заголовок, пояснення одним реченням і основна дія.
 * Дію не показуємо без `actionLabel`; вона віддає `action` — сторінка сама вирішує, що відкрити.
 */
@Component({
  selector: 'app-empty-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="empty-state" data-testid="empty">
      <span class="material-symbols-outlined empty-state__icon" aria-hidden="true">{{ icon() }}</span>
      <h2 class="empty-state__title" data-testid="empty-title">{{ title() }}</h2>
      <p class="empty-state__text">{{ description() }}</p>
      @if (actionLabel(); as label) {
        <button mat-flat-button type="button" data-testid="empty-action" (click)="action.emit()">{{ label }}</button>
      }
    </div>
  `,
  styles: `
    .empty-state {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 8px;
      max-width: 32rem;
      margin: 48px auto;
      padding: 32px 24px;
      border: 1px dashed var(--bp-line);
      border-radius: var(--bp-radius-lg);
      background: var(--bp-white);
      text-align: center;
    }
    .empty-state__icon {
      font-size: 40px;
      color: var(--bp-primary);
    }
    .empty-state__title {
      margin: 0;
      font-size: 1.125rem;
      font-weight: 600;
      color: var(--bp-text);
    }
    .empty-state__text {
      margin: 0 0 8px;
      color: var(--bp-text-muted);
    }
  `,
})
export class EmptyStateComponent {
  readonly icon = input('inbox');
  readonly title = input.required<string>();
  readonly description = input.required<string>();
  readonly actionLabel = input<string | null>(null);
  readonly action = output<void>();
}
