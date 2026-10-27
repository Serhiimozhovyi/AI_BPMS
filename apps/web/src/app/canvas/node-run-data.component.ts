import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ExecutionItem, ExecutionStep, SubStep } from '../executions/executions-api.service';

/** Порт з items для показу. */
interface PortView {
  label: string;
  items: ExecutionItem[];
  json: string;
}

/**
 * Input/output вузла з останнього тестового запуску (F2-20): по блоку на порт, items як JSON.
 * Назви портів — з дескриптора; порт без назви (напр. додатковий `error`) — за індексом.
 */
@Component({
  selector: 'app-node-run-data',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let s = step();
    <section class="run" data-testid="node-run" [attr.data-status]="s.status">
      <h3 class="run__title">
        <span i18n="@@noderun.title">Останній запуск</span>
        <span class="run__status run__status--{{ s.status }}" data-testid="node-run-status">{{ statusLabel() }}</span>
      </h3>
      @if (s.error; as error) {
        <div class="run__error" role="alert" data-testid="node-run-error">
          <p class="m-0" data-testid="node-run-error-message">{{ error.message }}</p>
          @if (error.itemIndex !== null && error.itemIndex !== undefined) {
            <p class="run__error-note m-0" data-testid="node-run-error-item" i18n="@@noderun.error.item">Вузол зупинився на item {{ error.itemIndex }} вхідних даних.</p>
          }
          @if (error.details) {
            <details class="run__error-note" data-testid="node-run-error-details">
              <summary i18n="@@noderun.error.details">Технічні подробиці</summary>
              <pre class="run__json">{{ error.details }}</pre>
            </details>
          }
          <button type="button" class="run__error-input" data-testid="node-run-error-input" (click)="inputTitle.scrollIntoView({ block: 'nearest' })" i18n="@@noderun.error.showInput">Показати вхідні дані</button>
        </div>
      }
      @if (subSteps().length > 0) {
        <h4 class="run__subtitle" i18n="@@noderun.subSteps">Кроки агента · {{ subSteps().length }}</h4>
        <ol class="run__subs" data-testid="node-run-substeps">
          @for (sub of subSteps(); track $index) {
            <li class="run__sub" [attr.data-kind]="sub.kind" [class.run__sub--error]="!!sub.error">
              <details>
                <summary>
                  <span class="run__sub-kind">{{ kindLabel(sub) }}</span>
                  {{ sub.name }}
                  <span class="run__sub-meta">#{{ sub.iteration ?? 1 }} · {{ duration(sub) }}@if (tokens(sub); as t) { · {{ t }} }</span>
                </summary>
                @if (sub.error) {
                  <p class="run__sub-error m-0" data-testid="node-run-substep-error">{{ sub.error }}</p>
                }
                @if (sub.input !== undefined && sub.input !== null) {
                  <div class="run__port-label" i18n="@@noderun.subSteps.input">Аргументи</div>
                  <pre class="run__json">{{ json(sub.input) }}</pre>
                }
                @if (sub.output !== undefined && sub.output !== null) {
                  <div class="run__port-label" i18n="@@noderun.subSteps.output">Результат</div>
                  <pre class="run__json">{{ json(sub.output) }}</pre>
                }
              </details>
            </li>
          }
        </ol>
      }
      <h4 #inputTitle class="run__subtitle" i18n="@@noderun.input">Вхід</h4>
      @for (port of inputs(); track $index) {
        <div class="run__port" data-testid="node-run-input">
          <div class="run__port-label">{{ port.label }} · {{ port.items.length }}</div>
          <pre class="run__json">{{ port.json }}</pre>
        </div>
      } @empty {
        <p class="run__empty" i18n="@@noderun.noInput">Немає вхідних items.</p>
      }
      <h4 class="run__subtitle" i18n="@@noderun.output">Вихід</h4>
      @for (port of outputs(); track $index) {
        <div class="run__port" data-testid="node-run-output">
          <div class="run__port-label">{{ port.label }} · {{ port.items.length }}</div>
          <pre class="run__json">{{ port.json }}</pre>
        </div>
      } @empty {
        <p class="run__empty" i18n="@@noderun.noOutput">Немає вихідних items.</p>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }

    .run {
      padding: 12px 16px;
      border-top: 1px solid var(--mat-sys-outline-variant);
    }

    .run__title {
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin: 0 0 8px;
      font-size: 14px;
    }

    .run__status {
      padding: 0 8px;
      border-radius: 8px;
      font-size: 12px;
      color: #fff;
      background: var(--ac-cool-grey-4);
    }

    .run__status--success {
      background: var(--ac-green);
    }

    .run__status--error {
      background: var(--ac-red);
    }

    .run__subtitle {
      margin: 12px 0 4px;
      font-size: 13px;
    }

    .run__port-label {
      font-size: 12px;
      opacity: 0.7;
    }

    .run__json {
      max-height: 240px;
      margin: 2px 0 8px;
      padding: 6px 8px;
      overflow: auto;
      border-radius: 4px;
      background: var(--ac-cool-grey);
      font-size: 12px;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .run__error {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 4px;
      font-size: 13px;
      color: var(--mat-sys-error);
    }

    .run__error-note {
      max-width: 100%;
      color: var(--ac-cool-grey-4);
    }

    .run__error-input {
      padding: 0;
      border: 0;
      background: none;
      color: var(--ac-kit);
      font: inherit;
      text-decoration: underline;
      cursor: pointer;
    }

    .run__empty {
      font-size: 12px;
      opacity: 0.6;
    }

    .run__subs {
      margin: 0 0 8px;
      padding: 0;
      list-style: none;
      font-size: 12px;
    }

    .run__sub summary {
      cursor: pointer;
    }

    .run__sub-kind {
      padding: 0 6px;
      border-radius: 6px;
      background: var(--ac-cool-grey);
    }

    .run__sub-meta {
      opacity: 0.6;
    }

    .run__sub--error .run__sub-kind,
    .run__sub-error {
      color: var(--mat-sys-error);
    }
  `,
})
export class NodeRunDataComponent {
  /** Крок вузла з тестового запуску. */
  readonly step = input.required<ExecutionStep>();

  /** Назви вхідних портів з дескриптора. */
  readonly inputNames = input<readonly string[]>([]);

  /** Назви вихідних портів з дескриптора. */
  readonly outputNames = input<readonly string[]>([]);

  protected readonly inputs = computed(() => ports(this.step().input, this.inputNames(), false));
  protected readonly outputs = computed(() => ports(this.step().output, this.outputNames(), true));

  protected readonly subSteps = computed<SubStep[]>(() => this.step().subSteps ?? []);

  protected kindLabel(sub: SubStep): string {
    return sub.kind === 'llm' ? $localize`:@@noderun.subSteps.llm:модель` : sub.kind === 'tool' ? $localize`:@@noderun.subSteps.tool:дія` : sub.kind;
  }

  protected duration(sub: SubStep): string {
    const ms = Math.max(0, Date.parse(sub.finishedAt) - Date.parse(sub.startedAt));
    return ms < 1000 ? `${ms} мс` : `${(ms / 1000).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} с`;
  }

  protected tokens(sub: SubStep): string | null {
    const total = (sub.usage?.input ?? 0) + (sub.usage?.output ?? 0);
    return total > 0 ? $localize`:@@noderun.subSteps.tokens:${total}:total: токенів` : null;
  }

  protected json(value: unknown): string {
    return typeof value === 'string' ? value : JSON.stringify(value, null, 2);
  }

  protected readonly statusLabel = computed(() => {
    switch (this.step().status) {
      case 'success':
        return $localize`:@@noderun.status.success:успішно`;
      case 'error':
        return $localize`:@@noderun.status.error:помилка`;
      case 'skipped':
        return $localize`:@@noderun.status.skipped:пропущено`;
      case 'cancelled':
        return $localize`:@@noderun.status.cancelled:скасовано`;
      case 'waiting':
        return $localize`:@@noderun.status.waiting:очікує`;
    }
  });
}

function ports(items: ExecutionItem[][], names: readonly string[], withErrorPort: boolean): PortView[] {
  // Вихід одразу за описаними — `error` вузла з `continueOnFail` (docs/workflow-format.md). Без назв (журнал виконань) — номери.
  const label = (i: number) => names[i] ?? (withErrorPort && names.length > 0 && i === names.length ? 'error' : `#${i}`);
  return items.map((port, i) => ({ label: label(i), items: port, json: JSON.stringify(port, null, 2) }));
}
