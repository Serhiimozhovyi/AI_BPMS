import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Subscription } from 'rxjs';
import { apiErrorCode, apiErrorMessage } from '../connectors/connectors-api.service';
import { WorkflowDefinition } from '../workflows/workflows-api.service';
import { AssistantApiService, DraftEvent, DraftTodoItem } from './assistant-api.service';

/** Побудований сценарій з переліком того, що лишилось доналаштувати. */
export interface DraftResult {
  definition: WorkflowDefinition;
  todo: DraftTodoItem[];
}

/** Рядок прогресу побудови. */
interface ProgressLine {
  text: string;
  ok?: boolean;
}

/** Довжина опису, яку приймає Api. */
export const MAX_DESCRIPTION = 4000;

/** Рядок прогресу з події `step`: «Налаштовую «Лист» (2 з 5)». */
export function progressText(e: DraftEvent): string {
  const text = e.text ?? '';
  return e.stage === 'node' && e.index && e.total && e.ok == null
    ? `${text} (${e.index} з ${e.total})`
    : text;
}

/**
 * «Описати процес» (F3-104): поле опису з прикладом, запуск побудови (`POST /workflows/{id}/draft`), прогрес кроками
 * і «Зупинити». Готовий сценарій віддається назовні один раз, коли побудова завершилась, — тож це одна правка в історії.
 * Після побудови — клікабельний перелік «Що доналаштувати».
 */
@Component({
  selector: 'app-describe-process',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <section class="describe" data-testid="describe-process">
      <p class="describe__hint" i18n="@@describe.hint">
        Опишіть процес своїми словами: хто що робить, які системи задіяні, де потрібне погодження.
        Наприклад: «Працівник подає заявку на відпустку, керівник погоджує або відхиляє, кадровик
        отримує повідомлення в Telegram».
      </p>
      @if (definition().nodes.length) {
        <p class="describe__warn" data-testid="describe-replace-warning" i18n="@@describe.replace">
          Побудова замінить поточні кроки сценарію. Це можна скасувати (Ctrl+Z).
        </p>
      }
      <textarea
        class="describe__input"
        rows="6"
        data-testid="describe-input"
        i18n-placeholder="@@describe.placeholder"
        placeholder="Опис процесу"
        [attr.maxlength]="maxLength"
        [value]="description()"
        [disabled]="busy()"
        (input)="description.set($any($event.target).value)"
        (keydown.control.enter)="run()"
      ></textarea>
      <div class="describe__actions">
        @if (busy()) {
          <button
            mat-stroked-button
            type="button"
            data-testid="describe-stop"
            (click)="stop()"
            i18n="@@describe.stop"
          >
            Зупинити
          </button>
        } @else {
          <button
            mat-flat-button
            type="button"
            data-testid="describe-run"
            [disabled]="!description().trim()"
            (click)="run()"
            i18n="@@describe.run"
          >
            Побудувати сценарій
          </button>
        }
      </div>
      @if (lines().length) {
        <ol class="describe__progress" data-testid="describe-progress">
          @for (line of lines(); track $index) {
            <li class="describe__line" [class.describe__line--failed]="line.ok === false">
              {{ line.text }}
            </li>
          }
        </ol>
      }
      @if (error()) {
        <p class="describe__error" data-testid="describe-error">{{ error() }}</p>
      }
      @if (todo().length) {
        <div class="describe__todo" data-testid="describe-todo">
          <h3 class="describe__todo-title" i18n="@@describe.todo">Що доналаштувати</h3>
          <ul class="describe__todo-list">
            @for (item of todo(); track $index) {
              <li>
                <button
                  type="button"
                  class="describe__todo-item"
                  data-testid="describe-todo-item"
                  (click)="todoSelected.emit(item.nodeId)"
                >
                  {{ item.text }}
                </button>
              </li>
            }
          </ul>
        </div>
      }
    </section>
  `,
  styles: `
    .describe {
      display: flex;
      flex-direction: column;
      gap: 8px;
      min-height: 0;
      overflow-y: auto;
      font:
        400 13px/18px Ubuntu,
        sans-serif;
    }

    .describe__hint,
    .describe__warn,
    .describe__error {
      margin: 0;
    }

    .describe__hint,
    .describe__line {
      color: var(--ac-cool-grey-4);
    }

    .describe__warn,
    .describe__error,
    .describe__line--failed {
      color: var(--ac-red);
    }

    .describe__input {
      padding: 8px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius);
      background: var(--ac-white);
      color: inherit;
      font: inherit;
      resize: vertical;
    }

    .describe__progress,
    .describe__todo-list {
      display: flex;
      flex-direction: column;
      gap: 4px;
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .describe__line {
      padding: 0 10px;
      font-size: 12px;
    }

    .describe__todo-title {
      margin: 0 0 4px;
      font:
        500 14px/20px Ubuntu,
        sans-serif;
    }

    .describe__todo-item {
      width: 100%;
      padding: 6px 10px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius);
      background: var(--ac-cool-grey);
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
    }
  `,
})
export class DescribeProcessComponent {
  private readonly api = inject(AssistantApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workflowId = input.required<string>();
  /** Поточне визначення з канвасу — його замінить побудований сценарій. */
  readonly definition = input.required<WorkflowDefinition>();

  /** «Що доналаштувати» від попередньої побудови: панель помічника закривається, коли відкривається вузол. */
  readonly savedTodo = input<DraftTodoItem[]>([]);

  /** Готовий сценарій — один раз, коли побудова завершилась. */
  readonly drafted = output<DraftResult>();
  /** Вибраний пункт «Що доналаштувати». */
  readonly todoSelected = output<string>();
  /** Помічник не налаштований (409) — панель покаже підказку про MCP (F3-74). */
  readonly notConfigured = output<void>();

  protected readonly maxLength = MAX_DESCRIPTION;
  protected readonly description = signal('');
  protected readonly busy = signal(false);
  protected readonly lines = signal<ProgressLine[]>([]);
  protected readonly error = signal('');
  private readonly built = signal<DraftTodoItem[] | null>(null);
  protected readonly todo = computed(() => this.built() ?? this.savedTodo());
  private request: Subscription | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.request?.unsubscribe());
  }

  protected run(): void {
    const description = this.description().trim();
    if (!description || this.busy()) return;
    this.busy.set(true);
    this.lines.set([]);
    this.error.set('');
    this.built.set([]);
    let latest: WorkflowDefinition | null = null;
    let todo: DraftTodoItem[] = [];
    const finish = () => {
      this.busy.set(false);
      this.request = null;
      if (latest) {
        this.built.set(todo);
        this.drafted.emit({ definition: latest, todo });
      }
      latest = null;
    };
    this.request = this.api
      .draft(this.workflowId(), { description, definition: this.definition() })
      .subscribe({
        next: (e) => {
          if (e.type === 'step') this.lines.update((l) => [...l, { text: progressText(e), ok: e.ok ?? undefined }]);
          else if (e.type === 'definition' && e.definition) latest = e.definition;
          else if (e.type === 'todo') todo = e.todo ?? [];
          else if (e.type === 'error') this.error.set(e.text ?? '');
        },
        error: (e: unknown) => {
          latest = null;
          if (apiErrorCode(e) === 'ASSISTANT_NOT_CONFIGURED') this.notConfigured.emit();
          else this.error.set(apiErrorMessage(e));
          finish();
        },
        complete: finish,
      });
  }

  /** Зупинити побудову: запит обривається, недобудований сценарій на канвас не потрапляє. */
  protected stop(): void {
    this.request?.unsubscribe();
    this.request = null;
    this.busy.set(false);
    this.lines.update((l) => [...l, { text: $localize`:@@describe.stopped:Зупинено`, ok: false }]);
  }
}
