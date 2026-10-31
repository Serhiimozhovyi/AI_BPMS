import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AssistantProposalView } from './global-assistant-api.service';
import {
  columnText,
  connectionText,
  failureText,
  nodeChangeText,
  nodeText,
  otherChangesText,
  proposalLink,
  proposalTitle,
  scalarArguments,
  settingText,
  tablePlan,
  workflowPlan,
} from './proposal-view';

/**
 * Картка підтвердження змін (F3-111): що саме помічник пропонує створити чи змінити й кнопки «Застосувати» / «Скасувати».
 * Для таблиць (F3-109) показує план — нові, змінені й видалені колонки та попередження; для сценарію (F3-116) — вузли,
 * зв'язки й налаштування, що зміняться, і помилки (з ними «Застосувати» недоступне); для решти — аргументи виклику.
 * Нічого не виконується, поки людина не натисне «Застосувати».
 */
@Component({
  selector: 'app-proposal-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink],
  template: `
    <section class="pc" role="group" [attr.aria-label]="title()" [attr.data-proposal]="proposal().id" [attr.data-status]="proposal().status">
      <h3 class="pc__title">{{ title() }}</h3>
      @if (plan(); as plan) {
        @if (plan.renamedFrom) {
          <p class="pc__row" data-testid="proposal-renamed" i18n="@@proposal.renamed">Нова назва замість «{{ plan.renamedFrom }}»</p>
        }
        @if (plan.addedColumns?.length) {
          <p class="pc__row"><b i18n="@@proposal.added">Колонки:</b> {{ columns(plan.addedColumns) }}</p>
        }
        @if (plan.removedColumns?.length) {
          <p class="pc__row"><b i18n="@@proposal.removed">Видаляються:</b> {{ columns(plan.removedColumns) }}</p>
        }
        @for (change of plan.changedColumns ?? []; track change.after.name) {
          <p class="pc__row"><b i18n="@@proposal.changed">Змінюється:</b> {{ columnText(change.before) }} → {{ columnText(change.after) }}</p>
        }
        @if (plan.constraintsChanged) {
          <p class="pc__row" i18n="@@proposal.constraints">Змінюються обмеження цілісності.</p>
        }
        @if (plan.action === 'alter' && plan.rowCount !== undefined) {
          <p class="pc__row" i18n="@@proposal.rows">Рядків у таблиці зараз: {{ plan.rowCount }}</p>
        }
        @for (warning of plan.warnings ?? []; track $index) {
          <p class="pc__warning" data-testid="proposal-warning">{{ warning }}</p>
        }
      } @else if (workflow(); as plan) {
        @if (plan.hasChanges === false) {
          <p class="pc__row" data-testid="proposal-no-changes" i18n="@@proposal.noChanges">Змін немає — визначення таке саме, як у версії {{ plan.baseVersion }}.</p>
        }
        @if (plan.addedNodes?.length) {
          <p class="pc__row" data-testid="proposal-nodes-added"><b i18n="@@proposal.nodesAdded">Нові кроки:</b> {{ nodes(plan.addedNodes) }}</p>
        }
        @if (plan.removedNodes?.length) {
          <p class="pc__row" data-testid="proposal-nodes-removed"><b i18n="@@proposal.nodesRemoved">Видаляються кроки:</b> {{ nodes(plan.removedNodes) }}</p>
        }
        @for (change of plan.changedNodes ?? []; track change.id) {
          <p class="pc__row" data-testid="proposal-node-changed"><b i18n="@@proposal.nodeChanged">Змінюється:</b> {{ nodeChangeText(change) }}</p>
        }
        @if (plan.addedConnections?.length) {
          <p class="pc__row" data-testid="proposal-links-added"><b i18n="@@proposal.linksAdded">Нові зв'язки:</b> {{ links(plan.addedConnections) }}</p>
        }
        @if (plan.removedConnections?.length) {
          <p class="pc__row" data-testid="proposal-links-removed"><b i18n="@@proposal.linksRemoved">Прибираються зв'язки:</b> {{ links(plan.removedConnections) }}</p>
        }
        @for (change of plan.settingsChanges ?? []; track change.field) {
          <p class="pc__row" data-testid="proposal-setting"><b i18n="@@proposal.setting">Налаштування:</b> {{ settingText(change) }}</p>
        }
        @if (plan.otherChanges?.length) {
          <p class="pc__row"><b i18n="@@proposal.otherChanges">Також змінюються:</b> {{ other(plan) }}</p>
        }
        @if (plan.hasChanges !== false) {
          <p class="pc__note" i18n="@@proposal.draftNote">Зміни стануть чернеткою версії {{ plan.baseVersion + 1 }}; запуски йдуть на опублікованій версії, доки її не опублікують.</p>
        }
        @for (error of plan.errors ?? []; track $index) {
          <p class="pc__warning" data-testid="proposal-error">{{ error.message }}</p>
        }
        @for (warning of plan.warnings ?? []; track $index) {
          <p class="pc__warning" data-testid="proposal-warning">{{ warning.message }}</p>
        }
      } @else {
        <dl class="pc__args">
          @for (arg of args(); track arg.key) {
            <dt>{{ arg.key }}</dt>
            <dd>{{ arg.value }}</dd>
          }
        </dl>
        <details class="pc__details">
          <summary i18n="@@proposal.params">Параметри</summary>
          <pre class="pc__json">{{ json() }}</pre>
        </details>
      }
      @switch (proposal().status) {
        @case ('pending') {
          <div class="pc__actions">
            <button type="button" class="pc__apply" data-testid="proposal-apply" [disabled]="busy() || invalid()" (click)="approved.emit()" i18n="@@proposal.apply">Застосувати</button>
            <button type="button" class="pc__cancel" data-testid="proposal-cancel" [disabled]="busy()" (click)="rejected.emit()" i18n="@@proposal.cancel">Скасувати</button>
          </div>
          @if (invalid()) {
            <p class="pc__note" data-testid="proposal-invalid" i18n="@@proposal.invalid">Застосувати не можна, доки є помилки: попросіть помічника їх виправити.</p>
          }
        }
        @case ('applied') {
          <p class="pc__status pc__status--ok" data-testid="proposal-status">
            <span i18n="@@proposal.applied">Застосовано.</span>
            @if (link(); as link) {
              <a [routerLink]="link.href" data-testid="proposal-link">{{ link.label }}</a>
            }
          </p>
        }
        @case ('rejected') {
          <p class="pc__status" data-testid="proposal-status" i18n="@@proposal.rejected">Скасовано — нічого не змінено.</p>
        }
        @case ('failed') {
          <p class="pc__status pc__status--failed" data-testid="proposal-status"><span i18n="@@proposal.failed">Не вдалося:</span> {{ failure() }}</p>
        }
      }
    </section>
  `,
  styles: `
    .pc {
      padding: 10px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius);
      background: var(--bp-white);
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .pc__title {
      margin: 0 0 6px;
      font:
        500 14px/20px Ubuntu,
        sans-serif;
    }

    .pc__row,
    .pc__warning,
    .pc__status {
      margin: 0 0 4px;
      overflow-wrap: anywhere;
    }

    .pc__warning {
      padding: 4px 8px;
      border-radius: var(--bp-radius);
      background: var(--bp-bg);
      color: var(--bp-danger);
    }

    .pc__note {
      margin: 0 0 4px;
      color: var(--bp-text-muted);
      font-size: 12px;
    }

    .pc__args {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 2px 8px;
      margin: 0 0 6px;
    }

    .pc__args dt {
      color: var(--bp-text-muted);
    }

    .pc__args dd {
      margin: 0;
      overflow-wrap: anywhere;
    }

    .pc__json {
      max-height: 160px;
      margin: 4px 0 0;
      overflow: auto;
      font-size: 12px;
      white-space: pre-wrap;
    }

    .pc__actions {
      display: flex;
      gap: 8px;
      margin-top: 8px;
    }

    .pc__apply,
    .pc__cancel {
      padding: 6px 14px;
      border-radius: var(--bp-radius);
      font: inherit;
      cursor: pointer;
    }

    .pc__apply {
      border: 0;
      background: var(--bp-text);
      color: var(--bp-white);
    }

    .pc__cancel {
      border: 1px solid var(--bp-line);
      background: transparent;
      color: inherit;
    }

    .pc__apply:disabled,
    .pc__cancel:disabled {
      opacity: 0.5;
      cursor: default;
    }

    .pc__status--ok {
      color: var(--bp-text);
    }

    .pc__status--failed {
      color: var(--bp-danger);
    }
  `,
})
export class ProposalCardComponent {
  readonly proposal = input.required<AssistantProposalView>();
  /** Рішення вже надіслано — кнопки заблоковано. */
  readonly busy = input(false);
  /** «Застосувати». */
  readonly approved = output<void>();
  /** «Скасувати». */
  readonly rejected = output<void>();

  protected readonly title = computed(() => proposalTitle(this.proposal()));
  protected readonly plan = computed(() => tablePlan(this.proposal()));
  protected readonly workflow = computed(() => workflowPlan(this.proposal()));
  /** План сценарію з помилками валідації застосувати не можна. */
  protected readonly invalid = computed(() => (this.workflow()?.errors?.length ?? 0) > 0);
  protected readonly args = computed(() => scalarArguments(this.proposal()));
  protected readonly json = computed(() => JSON.stringify(this.proposal().arguments, null, 2));
  protected readonly link = computed(() => proposalLink(this.proposal()));
  protected readonly failure = computed(() => failureText(this.proposal()));
  protected readonly columnText = columnText;
  protected readonly nodeChangeText = nodeChangeText;
  protected readonly settingText = settingText;
  protected readonly other = otherChangesText;

  protected nodes(nodes: Parameters<typeof nodeText>[0][] | undefined): string {
    return (nodes ?? []).map(nodeText).join(', ');
  }

  protected links(links: Parameters<typeof connectionText>[0][] | undefined): string {
    return (links ?? []).map(connectionText).join('; ');
  }

  protected columns(columns: Parameters<typeof columnText>[0][] | undefined): string {
    return (columns ?? []).map(columnText).join(', ');
  }
}
