import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { DraftTodoItem } from './assistant-api.service';
import { DescribeProcessComponent, DraftResult } from './describe-process.component';
import { ExplanationCardComponent, mcpExplainPrompt } from './explanation-card.component';

/**
 * Панель «Помічник» редактора (F3-60): пояснення всього процесу, яке готується після збереження (F3-72), і «Описати
 * процес» — побудова сценарію з опису (F3-104). Розмова з помічником (швидкі дії вузла, питання, зміни сценарію) живе в
 * загальній панелі (F3-117): її зміни приходять карткою з планом (F3-116), а не прямою правкою канвасу. Модель не
 * вибрана — замість «Описати процес» підказка, що питати Claude чи ChatGPT через MCP (F3-74).
 */
@Component({
  selector: 'app-assistant-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, ExplanationCardComponent, DescribeProcessComponent],
  template: `
    <section class="assistant" data-testid="assistant-panel">
      <header class="assistant__head">
        <h2 class="assistant__title" i18n="@@assistant.title">Помічник</h2>
        <button
          mat-button
          type="button"
          data-testid="assistant-close"
          (click)="closed.emit()"
          i18n="@@assistant.close"
        >
          Закрити
        </button>
      </header>
      <app-explanation-card
        [workflowId]="workflowId()"
        [version]="version()"
        [name]="definition().name"
      />
      @if (notConfigured()) {
        <div class="assistant__mcp" data-testid="assistant-mcp">
          <p class="assistant__mcp-text" i18n="@@assistant.viaMcp">
            Вбудованому помічнику не вибрано модель, тож він не відповідає. Питайте Claude чи
            ChatGPT, підключений до OpenBP через MCP: він бачить сценарій, пояснює його, відповідає
            на питання і змінює кроки. Наприклад:
          </p>
          <p class="assistant__mcp-prompt" data-testid="assistant-mcp-prompt">
            {{ mcpPrompt() }}
          </p>
        </div>
      } @else {
        <app-describe-process
          [workflowId]="workflowId()"
          [definition]="definition()"
          [savedTodo]="draftTodo()"
          (drafted)="drafted.emit($event)"
          (todoSelected)="todoSelected.emit($event)"
          (notConfigured)="notConfigured.set(true)"
        />
        <p class="assistant__hint" data-testid="assistant-chat-hint" i18n="@@assistant.chatHint">
          Питання, пояснення вузлів і зміни сценарію — у загальній панелі «Помічник» у шапці застосунку.
        </p>
      }
    </section>
  `,
  styles: `
    .assistant {
      display: flex;
      flex-direction: column;
      gap: 8px;
      height: 100%;
      padding: 12px;
      overflow-y: auto;
      color: var(--ac-vivid);
    }

    .assistant [hidden] {
      display: none;
    }

    .assistant__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .assistant__title {
      margin: 0;
      font:
        500 16px/24px Ubuntu,
        sans-serif;
    }

    .assistant__hint {
      margin: 0;
      color: var(--ac-cool-grey-4);
      font:
        400 12px/18px Ubuntu,
        sans-serif;
    }

    .assistant__mcp {
      padding: 8px 10px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius);
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .assistant__mcp-text {
      margin: 0;
    }

    .assistant__mcp-prompt {
      margin: 6px 0 0;
      padding: 4px 8px;
      border-radius: var(--ac-radius);
      background: var(--ac-cool-grey);
      user-select: all;
    }
  `,
})
export class AssistantPanelComponent {
  readonly workflowId = input.required<string>();
  /** Поточне визначення з канвасу (можливо, незбережене). */
  readonly definition = input.required<WorkflowDefinition>();
  /** Поточна збережена версія — пояснення процесу перечитується після кожного збереження (F3-72). */
  readonly version = input<number | null>(null);
  /** «Що доналаштувати» після останньої побудови (редактор зберігає, поки панель закрита). */
  readonly draftTodo = input<DraftTodoItem[]>([]);

  /** Сценарій, побудований з опису, — одна правка в історії (F3-104). */
  readonly drafted = output<DraftResult>();
  /** Пункт «Що доналаштувати» — редактор відкриває цей вузол. */
  readonly todoSelected = output<string>();
  readonly closed = output<void>();

  /** Api відповів, що модель не вибрана (409 ASSISTANT_NOT_CONFIGURED) — замість опису підказка про MCP (F3-74). */
  protected readonly notConfigured = signal(false);
  protected readonly mcpPrompt = computed(() => mcpExplainPrompt(this.definition().name));
}
