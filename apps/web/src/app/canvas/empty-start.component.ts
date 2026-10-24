import { ChangeDetectionStrategy, Component, DestroyRef, inject, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { WorkflowTemplate, WorkflowsApiService } from '../workflows/workflows-api.service';
import { ScreenPoint } from './quick-add';

/**
 * Порожній сценарій (F3-59): картка по центру канвасу — «Додати тригер» (спливаюча палітра лише з тригерами) або
 * «Почати з прикладу» (шаблони з `GET /workflows/templates`) або
 * «Описати процес» (помічник, F3-60). Що робити з вибором — вирішує редактор.
 */
@Component({
  selector: 'app-empty-start',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <section class="start" data-testid="empty-start" aria-labelledby="empty-start-title">
      @if (templates(); as list) {
        <header class="start__head">
          <h2 id="empty-start-title" class="start__title" i18n="@@emptystart.templates.title">Почати з прикладу</h2>
          <button mat-button type="button" data-testid="templates-back" (click)="templates.set(null)" i18n="@@emptystart.back">Назад</button>
        </header>
        <ul class="start__list">
          @for (t of list; track t.id) {
            <li>
              <button type="button" class="start__template" data-testid="template" [attr.data-template]="t.id" (click)="pick.emit(t)">
                <span class="start__name">{{ t.name }}</span>
                <span class="start__description">{{ t.description }}</span>
              </button>
            </li>
          } @empty {
            <li class="start__description" i18n="@@emptystart.templates.empty">Прикладів немає.</li>
          }
        </ul>
      } @else {
        <h2 id="empty-start-title" class="start__title" i18n="@@emptystart.title">З чого почнеться процес?</h2>
        <p class="start__description" i18n="@@emptystart.hint">Сценарій починається з тригера: ручний запуск, webhook, розклад або подія в системі.</p>
        <div class="start__actions">
          <button mat-flat-button type="button" data-testid="start-trigger" (click)="addTrigger.emit(anchorOf($event))" i18n="@@emptystart.trigger">Додати тригер</button>
          <button mat-stroked-button type="button" data-testid="start-template" [disabled]="loading()" (click)="loadTemplates()" i18n="@@emptystart.template">
            Почати з прикладу
          </button>
          <button mat-stroked-button type="button" data-testid="start-describe" (click)="describe.emit()" i18n="@@emptystart.describe">Описати процес</button>
        </div>
        @if (error()) {
          <p role="alert" class="start__error" data-testid="templates-error">{{ error() }}</p>
        }
      }
    </section>
  `,
  styles: `
    .start {
      display: flex;
      flex-direction: column;
      gap: 12px;
      width: min(440px, calc(100vw - 32px));
      max-height: 70vh;
      padding: 24px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-lg);
      background: var(--ac-white);
      box-shadow: var(--ac-shadow);
      color: var(--ac-vivid);
    }

    .start__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .start__title {
      margin: 0;
      font: 500 18px/24px Ubuntu, sans-serif;
    }

    .start__description {
      margin: 0;
      color: var(--ac-cool-grey-4);
      font: 400 13px/18px Ubuntu, sans-serif;
    }

    .start__actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }

    .start__error {
      margin: 0;
      color: var(--ac-red);
      font-size: 13px;
    }

    .start__list {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin: 0;
      padding: 0;
      overflow-y: auto;
      list-style: none;
    }

    .start__template {
      display: flex;
      flex-direction: column;
      gap: 2px;
      width: 100%;
      padding: 8px 12px;
      border: 1px solid transparent;
      border-radius: var(--ac-radius);
      background: none;
      color: inherit;
      text-align: left;
      cursor: pointer;
    }

    .start__template:hover,
    .start__template:focus-visible {
      border-color: var(--ac-kit);
      background: var(--ac-cool-grey);
    }

    .start__name {
      font: 500 14px/20px Ubuntu, sans-serif;
    }
  `,
})
export class EmptyStartComponent {
  private readonly workflows = inject(WorkflowsApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** «Додати тригер»: точка, біля якої відкрити спливаючу палітру. */
  readonly addTrigger = output<ScreenPoint>();
  /** «Описати процес»: відкрити помічника (F3-60). */
  readonly describe = output<void>();
  /** Вибраний шаблон. */
  readonly pick = output<WorkflowTemplate>();

  /** Список шаблонів після «Почати з прикладу»; `null` — головна картка. */
  protected readonly templates = signal<WorkflowTemplate[] | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected loadTemplates(): void {
    this.loading.set(true);
    this.error.set(null);
    this.workflows
      .templates()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (list) => {
          this.loading.set(false);
          this.templates.set(list);
        },
        error: () => {
          this.loading.set(false);
          this.error.set($localize`:@@emptystart.templates.error:Не вдалося завантажити приклади. Спробуйте ще раз.`);
        },
      });
  }

  protected anchorOf(event: MouseEvent): ScreenPoint {
    const box = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return { x: box.left, y: box.bottom + 8 };
  }
}
