import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { ExecutionItem, ExecutionStep } from '../executions/executions-api.service';

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
        <p class="run__error" role="alert" data-testid="node-run-error">{{ error.message }}</p>
      }
      <h4 class="run__subtitle" i18n="@@noderun.input">Вхід</h4>
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
      font-size: 13px;
      color: var(--mat-sys-error);
    }

    .run__empty {
      font-size: 12px;
      opacity: 0.6;
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

  protected readonly inputs = computed(() => ports(this.step().input, this.inputNames()));
  protected readonly outputs = computed(() => ports(this.step().output, this.outputNames()));

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
    }
  });
}

function ports(items: ExecutionItem[][], names: readonly string[]): PortView[] {
  // Порт одразу за описаними — `error` вузла з `continueOnFail` (docs/workflow-format.md).
  return items.map((port, i) => ({ label: names[i] ?? (i === names.length ? 'error' : `#${i}`), items: port, json: JSON.stringify(port, null, 2) }));
}
