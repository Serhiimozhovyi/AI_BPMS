import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, booleanAttribute, computed, input, linkedSignal, output, signal } from '@angular/core';
import { ExecutionItem } from '../executions/executions-api.service';
import { BpTab, BpTabsComponent } from '../shared/tabs/tabs.component';
import { PathSegment, SchemaField, accessorPath, buildSchema, itemsTable, sampleText } from './data-schema';

/** Порт вузла з items для показу. */
export interface DataPort {
  label: string;
  items: ExecutionItem[];
}

export type DataView = 'schema' | 'table' | 'json';

const VIEWS: readonly DataView[] = ['schema', 'table', 'json'];
const VIEW_KEY = 'openbp.nodeData.view.';

/** Поле, яке користувач обрав (клік) чи тягне в поле форми. */
export interface DataFieldPick {
  path: PathSegment[];
}

/**
 * Items вузла у трьох видах (F3-52): **схема** (дерево полів з типом і прикладом), **таблиця** (items рядками, вкладене
 * згорнуто) і **JSON**; вид запам'ятовується в `localStorage` окремо для входу й виходу. З `mappable` поля схеми й
 * колонки таблиці клікаються (`pick`) і перетягуються (текст виразу з `expression`) у поле форми.
 * Порожній стан — проєкція `[dataEmpty]`.
 */
@Component({
  selector: 'app-node-data-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, BpTabsComponent],
  template: `
    <section class="data" [attr.data-side]="side()">
      <header class="data__header">
        <h3 class="data__title">{{ title() }}</h3>
        <ng-content select="[dataHeader]" />
        @if (ports().length > 0) {
          <bp-tabs class="bp-tabs--sm" variant="pills" [tabs]="viewTabs()" [value]="view()" (valueChange)="setView($any($event))" label="Вид даних" i18n-label="@@nodedata.views" />
        }
      </header>
      <ng-content select="[dataNote]" />
      @if (ports().length === 0) {
        <ng-content select="[dataEmpty]" />
      } @else {
        @if (ports().length > 1) {
          <bp-tabs class="data__ports bp-tabs--sm" variant="pills" [tabs]="portTabs()" [value]="'' + port()" (valueChange)="port.set(+$any($event))" label="Виходи" i18n-label="@@nodedata.ports" />
        } @else {
          <div class="data__port-label" [attr.data-testid]="side() + '-port'">{{ current().label }} · {{ itemsLabel(current().items.length) }}</div>
        }
        <div class="data__body" [attr.data-testid]="testId()" [attr.data-view]="view()">
          @switch (view()) {
            @case ('schema') {
              @if (schema().length === 0) {
                <p class="data__empty" i18n="@@nodedata.noFields">Items без полів.</p>
              }
              <ul class="tree">
                <ng-container *ngTemplateOutlet="fieldsTpl; context: { $implicit: schema() }" />
              </ul>
            }
            @case ('table') {
              @let t = table();
              <div class="table-wrap">
                <table class="table">
                  <thead>
                    <tr>
                      <th class="table__index">#</th>
                      @for (c of t.columns; track c) {
                        <th>
                          @if (mappable()) {
                            <button type="button" class="field-chip" draggable="true" data-testid="table-column" [attr.data-path]="pathAttr([c])" [title]="expressionOf([c])" (click)="pickPath([c])" (dragstart)="dragStart($event, [c])">{{ c }}</button>
                          } @else {
                            {{ c }}
                          }
                        </th>
                      }
                    </tr>
                  </thead>
                  <tbody>
                    @for (r of t.rows; track $index) {
                      <tr>
                        <td class="table__index">{{ $index }}</td>
                        @for (cell of r.cells; track $index) {
                          <td [class.table__nested]="cell.nested" [title]="cell.title">{{ cell.text }}</td>
                        }
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @case ('json') {
              <pre class="data__json">{{ json() }}</pre>
            }
          }
        </div>
      }
    </section>

    <ng-template #fieldsTpl let-fields>
      @for (f of fields; track f.key) {
        <li class="tree__item">
          <div class="tree__row">
            @if (f.children.length > 0) {
              <button type="button" class="tree__toggle" [attr.aria-expanded]="!collapsed().has(pathAttr(f.path))" (click)="toggle(f)">
                {{ collapsed().has(pathAttr(f.path)) ? '▸' : '▾' }}
              </button>
            } @else {
              <span class="tree__toggle" aria-hidden="true"></span>
            }
            @if (mappable()) {
              <button
                type="button"
                class="field-chip"
                draggable="true"
                data-testid="schema-field"
                [attr.data-path]="pathAttr(f.path)"
                [title]="expressionOf(f.path)"
                (click)="pickPath(f.path)"
                (dragstart)="dragStart($event, f.path)"
              >{{ f.key }}</button>
            } @else {
              <span class="field-name" data-testid="schema-field" [attr.data-path]="pathAttr(f.path)">{{ f.key }}</span>
            }
            <span class="tree__type">{{ f.types.join(' | ') }}</span>
            @if (f.sample !== undefined) {
              <span class="tree__sample" [title]="sample(f.sample, 1000)">{{ sample(f.sample) }}</span>
            }
          </div>
          @if (f.children.length > 0 && !collapsed().has(pathAttr(f.path))) {
            <ul class="tree">
              <ng-container *ngTemplateOutlet="fieldsTpl; context: { $implicit: f.children }" />
            </ul>
          }
        </li>
      }
    </ng-template>
  `,
  styles: `
    :host {
      display: block;
    }

    .data {
      padding: 12px 16px;
    }

    .data__header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }

    .data__title {
      flex: 1;
      margin: 0;
      font-size: 14px;
    }

    .data__ports {
      margin-bottom: 6px;
    }

    .data__port-label {
      margin-bottom: 6px;
      font-size: 12px;
      color: var(--bp-text-muted);
    }

    .data__body {
      max-height: 60vh;
      overflow: auto;
    }

    .data__json {
      margin: 0;
      padding: 6px 8px;
      border-radius: 4px;
      background: var(--bp-bg);
      font-size: 12px;
      white-space: pre-wrap;
      word-break: break-word;
    }

    .data__empty {
      font-size: 12px;
      opacity: 0.6;
    }

    .tree {
      margin: 0;
      padding: 0 0 0 14px;
      list-style: none;
    }

    .data__body > .tree {
      padding-left: 0;
    }

    .tree__row {
      display: flex;
      align-items: center;
      gap: 6px;
      min-height: 24px;
    }

    .tree__toggle {
      flex-shrink: 0;
      width: 16px;
      padding: 0;
      border: 0;
      background: none;
      color: var(--bp-text-muted);
      font-size: 11px;
      cursor: pointer;
    }

    .field-chip,
    .field-name {
      padding: 0 6px;
      border: 1px solid var(--bp-line);
      border-radius: 4px;
      background: var(--bp-white);
      color: var(--bp-text);
      font: 400 12px/18px monospace;
    }

    .field-chip {
      cursor: grab;
    }

    .field-chip:hover,
    .field-chip:focus-visible {
      border-color: var(--bp-primary);
      background: var(--bp-tonal);
      color: var(--bp-primary);
      outline: none;
    }

    .tree__type {
      font-size: 11px;
      color: var(--bp-text-muted);
    }

    .tree__sample {
      min-width: 0;
      overflow: hidden;
      font: 400 12px/18px monospace;
      text-overflow: ellipsis;
      white-space: nowrap;
      color: var(--ac-green);
    }

    .table-wrap {
      overflow-x: auto;
    }

    .table {
      border-collapse: collapse;
      font-size: 12px;
    }

    .table th,
    .table td {
      padding: 2px 8px;
      border: 1px solid var(--bp-line);
      text-align: left;
      white-space: nowrap;
      max-width: 240px;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .table__index {
      color: var(--bp-text-muted);
    }

    .table__nested {
      color: var(--bp-text-muted);
      font-family: monospace;
    }
  `,
})
export class NodeDataViewComponent {
  /** Бік панелі: окремий запам'ятований вид і префікс test id. */
  readonly side = input.required<'input' | 'output'>();

  /** Заголовок блоку («Вхід», «Вихід»). */
  readonly title = input.required<string>();

  /** Порти з items; порожньо — показується `[dataEmpty]`. */
  readonly ports = input<readonly DataPort[]>([]);

  /** Поля можна клікати й перетягувати у форму. */
  readonly mappable = input(false, { transform: booleanAttribute });

  /** Вираз для поля — підказка і текст перетягування. */
  readonly expression = input<(path: PathSegment[]) => string>((path) => `{{ $json${accessorPath(path)} }}`);

  /** Клік по полю схеми чи колонці таблиці. */
  readonly pick = output<DataFieldPick>();

  protected readonly views = VIEWS;
  /** Запам'ятований вид цього боку; зміна — `setView`. */
  protected readonly view = linkedSignal<DataView>(() => readView(this.side()));
  protected readonly port = linkedSignal({ source: () => this.ports().length, computation: () => 0 });
  protected readonly collapsed = signal<ReadonlySet<string>>(new Set());

  protected readonly current = computed<DataPort>(() => this.ports()[this.port()] ?? this.ports()[0] ?? { label: '', items: [] });
  protected readonly schema = computed(() => buildSchema(this.current().items));
  protected readonly table = computed(() => itemsTable(this.current().items));
  protected readonly json = computed(() => JSON.stringify(this.current().items, null, 2));
  /** Test id тіла — сумісний з попереднім блоком «Останній запуск» (`node-run-input`/`node-run-output`). */
  protected readonly testId = computed(() => `node-run-${this.side()}`);
  protected readonly viewTabs = computed<BpTab[]>(() => this.views.map((v) => ({ value: v, label: this.viewLabel(v), testId: `${this.side()}-view-${v}` })));
  protected readonly portTabs = computed<BpTab[]>(() =>
    this.ports().map((p, i) => ({ value: String(i), label: `${p.label} · ${this.itemsLabel(p.items.length)}`, testId: `${this.side()}-port` })),
  );

  protected setView(view: DataView): void {
    this.view.set(view);
    writeView(this.side(), view);
  }

  protected viewLabel(view: DataView): string {
    switch (view) {
      case 'schema':
        return $localize`:@@nodedata.view.schema:Схема`;
      case 'table':
        return $localize`:@@nodedata.view.table:Таблиця`;
      case 'json':
        return 'JSON';
    }
  }

  protected itemsLabel(count: number): string {
    return count === 1 ? $localize`:@@nodedata.item:1 item` : $localize`:@@nodedata.items:${count}:count: items`;
  }

  protected pathAttr(path: readonly PathSegment[]): string {
    return accessorPath(path);
  }

  protected expressionOf(path: PathSegment[]): string {
    return this.expression()(path);
  }

  protected sample(value: unknown, max?: number): string {
    return sampleText(value, max);
  }

  protected toggle(field: SchemaField): void {
    const key = accessorPath(field.path);
    const next = new Set(this.collapsed());
    if (!next.delete(key)) next.add(key);
    this.collapsed.set(next);
  }

  protected pickPath(path: PathSegment[]): void {
    this.pick.emit({ path });
  }

  /** Перетягування: текст виразу — поле вводу вставляє його в місце відпускання саме (стандартний drop тексту). */
  protected dragStart(event: DragEvent, path: PathSegment[]): void {
    if (!event.dataTransfer) return;
    event.dataTransfer.setData('text/plain', this.expressionOf(path));
    event.dataTransfer.effectAllowed = 'copy';
  }
}

function readView(side: string): DataView {
  try {
    const value = localStorage.getItem(VIEW_KEY + side);
    return VIEWS.includes(value as DataView) ? (value as DataView) : 'schema';
  } catch {
    return 'schema';
  }
}

function writeView(side: string, view: DataView): void {
  try {
    localStorage.setItem(VIEW_KEY + side, view);
  } catch {
    // Сховище недоступне — вид просто не запам'ятається.
  }
}
