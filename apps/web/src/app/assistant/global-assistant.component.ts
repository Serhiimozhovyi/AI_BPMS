import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, untracked, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { GlobalAssistantService } from './global-assistant.service';
import { ProposalCardComponent } from './proposal-card.component';

/**
 * Загальний помічник (F3-110): бічна панель справа на кожній сторінці — розмова, історія, контекст сторінки. Відкриває її
 * кнопка в шапці застосунку (лише editor і admin). Картки підтвердження змін — F3-111.
 */
@Component({
  selector: 'app-global-assistant',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, ProposalCardComponent],
  template: `
    @if (assistant.available()) {
      @if (assistant.open()) {
        <aside class="ga" data-testid="global-assistant" aria-label="Помічник" i18n-aria-label="@@globalassistant.panel">
          <header class="ga__head">
            <h2 class="ga__title" i18n="@@globalassistant.title">Помічник</h2>
            <button type="button" class="ga__icon" data-testid="global-assistant-new" title="Нова розмова" i18n-title="@@globalassistant.new" aria-label="Нова розмова" i18n-aria-label="@@globalassistant.newLabel" (click)="assistant.newConversation()">
              <span class="material-symbols-outlined" aria-hidden="true">add_comment</span>
            </button>
            <button type="button" class="ga__icon" data-testid="global-assistant-history" title="Історія розмов" i18n-title="@@globalassistant.history" aria-label="Історія розмов" i18n-aria-label="@@globalassistant.historyLabel" (click)="assistant.historyOpen() ? assistant.historyOpen.set(false) : assistant.showHistory()">
              <span class="material-symbols-outlined" aria-hidden="true">history</span>
            </button>
            <button type="button" class="ga__icon" data-testid="global-assistant-close" title="Закрити" i18n-title="@@globalassistant.close" aria-label="Закрити помічника" i18n-aria-label="@@globalassistant.closeLabel" (click)="assistant.close()">
              <span class="material-symbols-outlined" aria-hidden="true">close</span>
            </button>
          </header>
          <p class="ga__context" data-testid="global-assistant-context">{{ contextLabel() }}</p>
          @if (assistant.historyOpen()) {
            <ul class="ga__history" data-testid="global-assistant-list">
              @for (c of assistant.conversations(); track c.id) {
                <li class="ga__item" [class.ga__item--current]="c.id === assistant.currentId()">
                  <button type="button" class="ga__open" [attr.data-conversation]="c.id" (click)="assistant.select(c.id)">
                    <span class="ga__item-title">{{ c.title }}</span>
                    <span class="ga__item-date">{{ c.updatedAt | date: 'dd.MM.yyyy HH:mm' }}</span>
                  </button>
                  <button type="button" class="ga__icon" [attr.data-delete]="c.id" title="Видалити розмову" i18n-title="@@globalassistant.delete" aria-label="Видалити розмову" i18n-aria-label="@@globalassistant.deleteLabel" (click)="assistant.remove(c.id)">
                    <span class="material-symbols-outlined" aria-hidden="true">delete</span>
                  </button>
                </li>
              } @empty {
                <li class="ga__hint" data-testid="global-assistant-list-empty" i18n="@@globalassistant.noHistory">Розмов ще немає.</li>
              }
            </ul>
          } @else {
            <ol class="ga__log" #log data-testid="global-assistant-log">
              @for (line of assistant.lines(); track $index) {
                @if (line.kind === 'proposal') {
                  @if (proposalOf(line.proposalId); as proposal) {
                    <li class="ga__card" data-kind="proposal">
                      <app-proposal-card
                        [proposal]="proposal"
                        [busy]="assistant.deciding().includes(proposal.id)"
                        (approved)="assistant.decide(proposal.id, true)"
                        (rejected)="assistant.decide(proposal.id, false)"
                      />
                    </li>
                  }
                } @else {
                  <li class="ga__line ga__line--{{ line.kind }}" [attr.data-kind]="line.kind" [class.ga__line--failed]="line.ok === false">{{ line.text }}</li>
                }
              } @empty {
                <li class="ga__hint" i18n="@@globalassistant.empty">
                  Спитайте про процес, попросіть створити таблицю чи сценарій або розібрати помилку виконання. Зміни помічник
                  лише пропонує — застосовуєте їх ви.
                </li>
              }
              @if (assistant.busy()) {
                <li class="ga__hint" data-testid="global-assistant-busy" i18n="@@globalassistant.busy">Помічник працює…</li>
              }
            </ol>
            @if (assistant.notConfigured()) {
              <div class="ga__mcp" data-testid="global-assistant-not-configured">
                <p class="ga__mcp-text" i18n="@@globalassistant.notConfigured">
                  Вбудованому помічнику не вибрано модель, тож він не відповідає. Адміністратор обирає її в налаштуваннях.
                </p>
                <a routerLink="/settings" i18n="@@globalassistant.toSettings">Налаштування → Помічник</a>
              </div>
            }
            <form class="ga__form" (submit)="$event.preventDefault(); assistant.send()">
              <textarea
                #field
                class="ga__input"
                rows="3"
                data-testid="global-assistant-input"
                aria-label="Повідомлення помічнику"
                i18n-aria-label="@@globalassistant.inputLabel"
                placeholder="Що зробити?"
                i18n-placeholder="@@globalassistant.placeholder"
                [value]="assistant.draft()"
                [disabled]="assistant.busy()"
                (input)="assistant.draft.set($any($event.target).value)"
                (keydown.control.enter)="assistant.send()"
              ></textarea>
              <button type="submit" class="ga__send" data-testid="global-assistant-send" [disabled]="assistant.busy() || !assistant.draft().trim()" i18n="@@globalassistant.send">Надіслати</button>
            </form>
          }
        </aside>
      }
    }
  `,
  styles: `
    .ga {
      position: fixed;
      top: 52px;
      right: 0;
      bottom: 0;
      z-index: 60;
      display: flex;
      flex-direction: column;
      gap: 8px;
      box-sizing: border-box;
      width: min(400px, 100vw);
      padding: 12px;
      background: var(--bp-white);
      color: var(--bp-text);
      box-shadow: -2px 0 12px rgb(0 0 0 / 0.2);
    }

    .ga__head {
      display: flex;
      align-items: center;
      gap: 4px;
    }

    .ga__title {
      flex: 1;
      margin: 0;
      font:
        500 16px/24px Ubuntu,
        sans-serif;
    }

    .ga__icon {
      display: grid;
      place-items: center;
      width: 36px;
      height: 36px;
      padding: 0;
      border: 0;
      border-radius: var(--bp-radius);
      background: transparent;
      color: inherit;
      cursor: pointer;
    }

    .ga__icon:hover {
      background: var(--bp-bg);
    }

    .ga__context {
      margin: 0;
      color: var(--bp-text-muted);
      font:
        400 12px/16px Ubuntu,
        sans-serif;
      overflow-wrap: anywhere;
    }

    .ga__log,
    .ga__history {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: 6px;
      min-height: 0;
      margin: 0;
      padding: 0;
      overflow-y: auto;
      list-style: none;
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .ga__line {
      padding: 6px 10px;
      border-radius: var(--bp-radius);
      white-space: pre-wrap;
    }

    .ga__line--user {
      align-self: flex-end;
      max-width: 85%;
      background: var(--bp-tonal);
    }

    .ga__line--assistant {
      background: var(--bp-bg);
    }

    .ga__line--step,
    .ga__hint {
      padding: 0 10px;
      color: var(--bp-text-muted);
      font-size: 12px;
    }

    .ga__line--error,
    .ga__line--failed {
      color: var(--bp-danger);
    }

    .ga__card {
      list-style: none;
    }

    .ga__item {
      display: flex;
      align-items: center;
      gap: 4px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius);
    }

    .ga__item--current {
      background: var(--bp-bg);
    }

    .ga__open {
      display: flex;
      flex: 1;
      flex-direction: column;
      gap: 2px;
      padding: 8px 10px;
      border: 0;
      background: transparent;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }

    .ga__item-date {
      color: var(--bp-text-muted);
      font-size: 12px;
    }

    .ga__mcp {
      padding: 8px 10px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius);
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .ga__mcp-text {
      margin: 0 0 4px;
    }

    .ga__form {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }

    .ga__input {
      padding: 8px;
      border: 1px solid var(--bp-line);
      border-radius: var(--bp-radius);
      background: var(--bp-white);
      color: inherit;
      font: inherit;
      resize: vertical;
    }

    .ga__send {
      align-self: flex-end;
      padding: 8px 16px;
      border: 0;
      border-radius: var(--bp-radius);
      background: var(--bp-text);
      color: var(--bp-white);
      font: inherit;
      cursor: pointer;
    }

    .ga__send:disabled {
      opacity: 0.5;
      cursor: default;
    }
  `,
})
export class GlobalAssistantComponent {
  protected readonly assistant = inject(GlobalAssistantService);
  private readonly field = viewChild<ElementRef<HTMLTextAreaElement>>('field');
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');

  /** Де користувач — те, що помічник отримує разом із повідомленням. */
  protected readonly contextLabel = computed(() => {
    const context = this.assistant.context();
    const parts = [context.page ?? '/'];
    if (context.nodeId) parts.push($localize`:@@globalassistant.context.node:вузол ${context.nodeId}:node:`);
    return $localize`:@@globalassistant.context:Контекст: ${parts.join(' · ')}:context:`;
  });

  /** Пропозиція за id — картка живе, поки відповідь Api змінює її стан. */
  protected proposalOf(id: string | undefined) {
    return id ? this.assistant.proposals().find((p) => p.id === id) : undefined;
  }

  constructor() {
    // Запит ззовні (швидка дія вузла без надсилання) — фокус у полі.
    effect(() => {
      this.assistant.focusRequest();
      untracked(() => queueMicrotask(() => this.field()?.nativeElement.focus()));
    });
    // Нові рядки — прокрутка донизу.
    effect(() => {
      this.assistant.lines();
      untracked(() =>
        queueMicrotask(() => {
          const el = this.log()?.nativeElement;
          if (el) el.scrollTop = el.scrollHeight;
        }),
      );
    });
  }
}
