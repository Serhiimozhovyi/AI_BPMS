import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { EMPTY, Subject, catchError, interval, startWith, switchMap, take, takeWhile } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { AssistantApiService, WorkflowExplanation } from './assistant-api.service';

/** Як часто перепитувати пояснення, що готується, мс. */
export const EXPLANATION_POLL_MS = 5000;

/** Скільки разів перепитувати (5 хв), далі — лише за «Оновити» чи наступним збереженням. */
const MAX_POLLS = 60;

/**
 * Пояснення всього сценарію простими словами (F3-72): воркер готує його після збереження, картка показує останнє
 * і, поки воно готується, перепитує Api. Нова версія сценарію (`version`) — перечитати.
 */
@Component({
  selector: 'app-explanation-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <details
      class="explanation"
      data-testid="explanation"
      [open]="open()"
      (toggle)="open.set($any($event.target).open)"
    >
      <summary class="explanation__head">
        <span class="explanation__title" i18n="@@explanation.title">Пояснення процесу</span>
        @if (explanation()?.status === 'pending') {
          <span
            class="explanation__badge"
            data-testid="explanation-pending"
            i18n="@@explanation.pending"
            >готується…</span
          >
        }
      </summary>
      @if (explanation(); as e) {
        @switch (e.status) {
          @case ('not_configured') {
            <p
              class="explanation__note"
              data-testid="explanation-status"
              i18n="@@explanation.notConfigured"
            >
              Пояснення пише Claude чи ChatGPT, підключений до OpenBP через MCP (інструмент
              save_workflow_explanation), або модель із «Налаштувань → Помічник».
            </p>
          }
          @case ('failed') {
            <p
              class="explanation__note explanation__note--error"
              data-testid="explanation-status"
              i18n="@@explanation.failed"
            >
              Не вдалося пояснити: {{ e.error }}
            </p>
          }
          @case ('none') {
            <p class="explanation__note" data-testid="explanation-status" i18n="@@explanation.none">
              Пояснення з'явиться після збереження сценарію.
            </p>
          }
        }
        @if (e.text) {
          @if (e.stale) {
            <p class="explanation__note" data-testid="explanation-stale" i18n="@@explanation.stale">
              Пояснення до версії {{ e.version }}; сценарій уже змінено.
            </p>
          }
          <div class="explanation__text" data-testid="explanation-text">{{ e.text }}</div>
        } @else if (e.status === 'pending') {
          <p
            class="explanation__note"
            data-testid="explanation-status"
            i18n="@@explanation.preparing"
          >
            Готую пояснення — це займає до хвилини після збереження.
          </p>
        }
      }
      @if (error(); as message) {
        <p class="explanation__note explanation__note--error" data-testid="explanation-error">
          {{ message }}
        </p>
      }
      @if (canRefresh()) {
        <button
          mat-button
          type="button"
          data-testid="explanation-refresh"
          [disabled]="
            explanation()?.status === 'pending' || explanation()?.status === 'not_configured'
          "
          (click)="refresh()"
          i18n="@@explanation.refresh"
        >
          Пояснити заново
        </button>
      }
    </details>
  `,
  styles: `
    .explanation {
      display: flex;
      flex-direction: column;
      max-height: 45%;
      overflow-y: auto;
      padding: 8px 10px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius);
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .explanation__head {
      display: flex;
      align-items: center;
      gap: 8px;
      cursor: pointer;
    }

    .explanation__title {
      font-weight: 500;
    }

    .explanation__badge,
    .explanation__note {
      color: var(--ac-cool-grey-4);
      font-size: 12px;
    }

    .explanation__note {
      margin: 6px 0 0;
    }

    .explanation__note--error {
      color: var(--ac-red);
    }

    .explanation__text {
      margin-top: 6px;
      white-space: pre-wrap;
    }
  `,
})
export class ExplanationCardComponent {
  private readonly api = inject(AssistantApiService);

  readonly workflowId = input.required<string>();
  /** Поточна версія сценарію: зміна — перечитати пояснення. */
  readonly version = input<number | null>(null);
  /** Editor може замовити пояснення заново. */
  readonly canRefresh = input(true);

  protected readonly explanation = signal<WorkflowExplanation | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly open = signal(true);
  private readonly reload = new Subject<void>();

  constructor() {
    this.reload
      .pipe(
        switchMap(() =>
          interval(EXPLANATION_POLL_MS).pipe(
            startWith(0),
            switchMap(() =>
              this.api
                .explanation(this.workflowId())
                .pipe(catchError((e: unknown) => this.fail(e))),
            ),
            takeWhile((e) => e.status === 'pending', true),
            take(MAX_POLLS),
          ),
        ),
        takeUntilDestroyed(inject(DestroyRef)),
      )
      .subscribe((e) => {
        this.error.set(null);
        this.explanation.set(e);
      });
    effect(() => {
      this.workflowId();
      this.version();
      untracked(() => this.reload.next());
    });
  }

  protected refresh(): void {
    this.api
      .requestExplanation(this.workflowId())
      .pipe(catchError((e: unknown) => this.fail(e)))
      .subscribe((e) => {
        this.explanation.set(e);
        this.reload.next();
      });
  }

  private fail(e: unknown) {
    this.error.set(apiErrorMessage(e));
    return EMPTY;
  }
}
