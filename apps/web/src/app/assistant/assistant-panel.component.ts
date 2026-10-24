import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { Subscription } from 'rxjs';
import { apiErrorMessage } from '../connectors/connectors-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { AssistantApiService, AssistantTurn } from './assistant-api.service';
import { ExplanationCardComponent } from './explanation-card.component';

/** Запит до помічника ззовні панелі (швидкі дії вузла, «Описати процес»). */
export interface AssistantPrompt {
  text: string;
  /** Надіслати одразу; інакше — лише підставити в поле. */
  send: boolean;
  /** Новий об'єкт на кожен запит — щоб однаковий текст спрацьовував знову. */
  seq: number;
}

/** Рядок розмови: репліка, крок інструмента або помилка. */
interface ChatLine {
  kind: 'user' | 'assistant' | 'step' | 'error';
  text: string;
  ok?: boolean;
}

/** Підписи інструментів для людини. */
const TOOL_LABELS: Record<string, string> = {
  describe_nodes: $localize`:@@assistant.tool.nodes:Переглянув типи вузлів`,
  describe_connectors: $localize`:@@assistant.tool.connectors:Переглянув конектори`,
  get_workflow: $localize`:@@assistant.tool.get:Прочитав сценарій`,
  update_workflow: $localize`:@@assistant.tool.update:Змінив сценарій`,
  test_node: $localize`:@@assistant.tool.test:Протестував вузол`,
};

/**
 * Панель «Помічник» (F3-60): пояснення всього процесу, яке готується після збереження (F3-72), і розмова з ШІ над
 * поточним визначенням. Відповідь помічника приходить подіями; нове
 * визначення віддається назовні один раз, коли відповідь завершилась, — тож одна відповідь = одна правка в історії.
 */
@Component({
  selector: 'app-assistant-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, ExplanationCardComponent],
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
      <app-explanation-card [workflowId]="workflowId()" [version]="version()" />
      <ol class="assistant__log" #log data-testid="assistant-log">
        @for (line of lines(); track $index) {
          <li
            class="assistant__line assistant__line--{{ line.kind }}"
            [attr.data-kind]="line.kind"
            [class.assistant__line--failed]="line.ok === false"
          >
            {{ line.text }}
          </li>
        } @empty {
          <li class="assistant__hint" i18n="@@assistant.empty">
            Опишіть процес або що змінити: «коли приходить заявка з Telegram, створи замовлення в 1С
            і відповідай клієнту». Відповідь помічника можна скасувати (Ctrl+Z).
          </li>
        }
        @if (busy()) {
          <li class="assistant__hint" data-testid="assistant-busy" i18n="@@assistant.busy">
            Помічник працює…
          </li>
        }
      </ol>
      <form class="assistant__form" (submit)="$event.preventDefault(); send()">
        <textarea
          #field
          class="assistant__input"
          rows="3"
          data-testid="assistant-input"
          i18n-placeholder="@@assistant.placeholder"
          placeholder="Що зробити зі сценарієм?"
          [value]="draft()"
          [disabled]="busy()"
          (input)="draft.set($any($event.target).value)"
          (keydown.control.enter)="send()"
        ></textarea>
        <button
          mat-flat-button
          type="submit"
          data-testid="assistant-send"
          [disabled]="busy() || !draft().trim()"
          i18n="@@assistant.send"
        >
          Надіслати
        </button>
      </form>
    </section>
  `,
  styles: `
    .assistant {
      display: flex;
      flex-direction: column;
      gap: 8px;
      height: 100%;
      padding: 12px;
      color: var(--ac-vivid);
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

    .assistant__log {
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

    .assistant__line {
      padding: 6px 10px;
      border-radius: var(--ac-radius);
      white-space: pre-wrap;
    }

    .assistant__line--user {
      align-self: flex-end;
      max-width: 85%;
      background: var(--ac-button);
    }

    .assistant__line--assistant {
      background: var(--ac-cool-grey);
    }

    .assistant__line--step,
    .assistant__hint {
      padding: 0 10px;
      color: var(--ac-cool-grey-4);
      font-size: 12px;
    }

    .assistant__line--error,
    .assistant__line--failed {
      color: var(--ac-red);
    }

    .assistant__form {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }

    .assistant__input {
      padding: 8px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius);
      background: var(--ac-white);
      color: inherit;
      font: inherit;
      resize: vertical;
    }
  `,
})
export class AssistantPanelComponent {
  private readonly api = inject(AssistantApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workflowId = input.required<string>();
  /** Поточне визначення з канвасу (можливо, незбережене). */
  readonly definition = input.required<WorkflowDefinition>();
  /** Поточна збережена версія — пояснення процесу перечитується після кожного збереження (F3-72). */
  readonly version = input<number | null>(null);
  /** Запит ззовні. */
  readonly prompt = input<AssistantPrompt | null>(null);

  /** Нове визначення після відповіді помічника. */
  readonly definitionChange = output<WorkflowDefinition>();
  readonly closed = output<void>();

  protected readonly lines = signal<ChatLine[]>([]);
  protected readonly draft = signal('');
  protected readonly busy = signal(false);
  private readonly history: AssistantTurn[] = [];
  private readonly field = viewChild<ElementRef<HTMLTextAreaElement>>('field');
  private readonly log = viewChild<ElementRef<HTMLElement>>('log');
  private request: Subscription | null = null;

  constructor() {
    effect(() => {
      const prompt = this.prompt();
      if (!prompt) return;
      untracked(() => {
        this.draft.set(prompt.text);
        if (prompt.send) this.send();
        else queueMicrotask(() => this.field()?.nativeElement.focus());
      });
    });
    this.destroyRef.onDestroy(() => this.request?.unsubscribe());
  }

  protected send(): void {
    const message = this.draft().trim();
    if (!message || this.busy()) return;
    this.draft.set('');
    this.busy.set(true);
    this.push({ kind: 'user', text: message });
    const history = [...this.history];
    this.history.push({ role: 'user', text: message });
    let latest: WorkflowDefinition | null = null;
    const finish = () => {
      this.busy.set(false);
      if (latest) this.definitionChange.emit(latest);
      latest = null;
    };
    this.request = this.api
      .ask(this.workflowId(), { message, definition: this.definition(), history })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (e) => {
          if (e.type === 'step')
            this.push({
              kind: 'step',
              text: `${TOOL_LABELS[e.tool ?? ''] ?? e.tool}: ${e.text ?? ''}`,
              ok: e.ok ?? true,
            });
          else if (e.type === 'message' && e.text) {
            this.push({ kind: 'assistant', text: e.text });
            this.history.push({ role: 'assistant', text: e.text });
          } else if (e.type === 'definition' && e.definition) latest = e.definition;
          else if (e.type === 'error') this.push({ kind: 'error', text: e.text ?? '' });
        },
        error: (e: unknown) => {
          this.push({ kind: 'error', text: apiErrorMessage(e) });
          finish();
        },
        complete: finish,
      });
  }

  private push(line: ChatLine): void {
    this.lines.update((lines) => [...lines, line]);
    queueMicrotask(() => {
      const el = this.log()?.nativeElement;
      if (el) el.scrollTop = el.scrollHeight;
    });
  }
}
