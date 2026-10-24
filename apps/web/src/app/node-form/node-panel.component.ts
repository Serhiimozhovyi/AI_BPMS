import { ChangeDetectionStrategy, Component, booleanAttribute, computed, effect, input, linkedSignal, output, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { FormlyFieldConfig, FormlyForm, FormlyFormOptions } from '@ngx-formly/core';
import { actionLabel, filterActions, groupActions } from '../canvas/connector-actions';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { CredentialOption } from '../credentials/credentials-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { CommonModel, MCP_TOOL_TYPES, applyCommon, applyParameters, commonFields, commonModel, parameterFields, parametersModel } from './node-form';
import { EMPTY_EXPRESSION_CONTEXT, ExpressionContext } from './expression-suggest';
import { NodeFormState, provideNodeForm } from './node-form-fields';
import { OptionsConnector, ToolOptionsService } from './tool-options.service';

/** Стан форм для одного вузла: моделі (копії) і поля formly. Новий стан — нові екземпляри форм. */
interface PanelState {
  /** Ключ екземпляра форм: змінюється лише при перебудові. */
  id: number;
  node: WorkflowNode;
  descriptor: NodeTypeInfo | undefined;
  tool: McpToolInfo | null;
  credentials: readonly CredentialOption[] | null;
  paramsForm: FormGroup;
  paramsModel: Record<string, unknown>;
  paramsFields: FormlyFieldConfig[];
  /** Опції форми параметрів: `formState` спільний для всіх станів панелі (контекст підказок виразів). */
  paramsOptions: FormlyFormOptions;
  commonForm: FormGroup;
  commonModel: CommonModel;
  commonFields: FormlyFieldConfig[];
}

/**
 * Панель вузла (F2-18): форма параметрів з `ParametersSchema` дескриптора (`core.*`) або з `inputSchema` tool'а
 * (`mcp.tool`) і загальні поля вузла. Кожна зміна — новий `WorkflowNode` у `nodeChange`; невідомі поля вузла й
 * параметрів зберігаються. Зовнішня зміна вузла (undo/redo, інший вузол) перебудовує форму, власна — ні.
 * Дії редактора над вузлом (тест кроку, pin) проєктуються в заголовок поруч із назвою: `[panelActions]`,
 * редактор закріпленого output — одразу під заголовком: `[panelPinned]` (F3-51).
 */
@Component({
  selector: 'app-node-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormlyForm, ReactiveFormsModule, MatAutocompleteModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  // Кеш списків з конекторів (F3-54) — у межах панелі.
  providers: [provideNodeForm(), ToolOptionsService],
  template: `
    @let s = state();
    <section class="panel" data-testid="node-panel" [attr.data-node]="s.node.id">
      <header class="panel__header">
        <div class="panel__title-row">
          <h2 class="mat-font-title-sm m-0 panel__name" data-testid="node-panel-name">{{ s.node.name }}</h2>
          <ng-content select="[panelActions]" />
        </div>
        <span class="panel__type">{{ s.node.type }}</span>
        @if (isMcp(s.node)) {
          <span class="panel__type" data-testid="node-panel-tool">{{ s.node.parameters['serverId'] }} · {{ s.node.parameters['toolName'] }}</span>
        }
      </header>
      <ng-content select="[panelPinned]" />

      @if (isMcp(s.node) && actionGroups().length > 0) {
        <div class="panel__action" data-testid="node-action" [attr.inert]="readonly() ? '' : null">
          <mat-form-field appearance="outline" class="panel__action-field" subscriptSizing="dynamic">
            <mat-label i18n="@@nodeform.action">Дія</mat-label>
            <input
              matInput
              data-testid="action-select"
              [formControl]="actionSearch"
              [matAutocomplete]="actions"
              (focus)="openActions($any($event.target))"
              (input)="actionQuery.set($any($event.target).value)"
              (blur)="restoreAction()"
            />
            <mat-autocomplete #actions="matAutocomplete" class="panel__actions" (optionSelected)="actionPicked($event)">
              @for (g of visibleActions(); track g.resource) {
                <mat-optgroup [label]="g.label">
                  @for (a of g.actions; track a.tool.name) {
                    <mat-option [value]="a.tool.name" data-testid="action-option" [attr.data-tool]="a.tool.name">
                      <span class="panel__action-label">{{ a.label }}</span>
                      <span class="panel__action-name">{{ a.tool.name }}</span>
                      @if (a.tool.description) {
                        <span class="panel__action-desc">{{ a.tool.description }}</span>
                      }
                    </mat-option>
                  }
                </mat-optgroup>
              }
            </mat-autocomplete>
            @if (s.tool?.description) {
              <mat-hint>{{ s.tool?.description }}</mat-hint>
            }
          </mat-form-field>
          @if (pendingTool(); as pending) {
            <div class="panel__warning" role="alert" data-testid="action-reset-warning">
              <span i18n="@@nodeform.action.reset">Аргументи поточної дії буде скинуто. Змінити дію на «{{ label(pending) }}»?</span>
              <button mat-flat-button type="button" data-testid="action-confirm" (click)="confirmTool()" i18n="@@nodeform.action.confirm">Змінити</button>
              <button mat-button type="button" data-testid="action-cancel" (click)="cancelTool()" i18n="@@nodeform.action.cancel">Скасувати</button>
            </div>
          }
        </div>
      }

      @if (!s.descriptor) {
        <p class="panel__note" role="status" data-testid="node-panel-unknown" i18n="@@nodeform.unknownType">
          Тип вузла не зареєстровано — параметри можна змінити лише в JSON сценарію.
        </p>
      } @else {
        @if (isMcp(s.node) && !s.tool) {
          <p class="panel__note" role="status" data-testid="node-panel-no-tool" i18n="@@nodeform.noTool">
            Схема tool'а невідома (конектор не знайдено або tools не завантажено) — аргументи редагуються як JSON.
          </p>
        }
        @if (s.paramsFields.length === 0) {
          <p class="panel__note" i18n="@@nodeform.noParameters">Параметрів немає.</p>
        }
        @for (k of [s]; track k.id) {
          <div class="panel__form" data-testid="node-parameters" [attr.inert]="readonly() ? '' : null">
            <formly-form [form]="k.paramsForm" [fields]="k.paramsFields" [model]="k.paramsModel" [options]="k.paramsOptions" (modelChange)="parametersChanged($event)" />
          </div>
        }
      }

      <h3 class="panel__subtitle" i18n="@@nodeform.common">Налаштування вузла</h3>
      @for (k of [s]; track k.id) {
        <div class="panel__form" data-testid="node-common" [attr.inert]="readonly() ? '' : null">
          <formly-form [form]="k.commonForm" [fields]="k.commonFields" [model]="k.commonModel" (modelChange)="commonChanged($event)" />
        </div>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }

    .panel {
      padding: 12px 16px;
    }

    .panel__header {
      display: flex;
      flex-direction: column;
      gap: 2px;
      margin-bottom: 12px;
    }

    .panel__title-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
    }

    .panel__name {
      flex: 1;
      min-width: 0;
      overflow-wrap: anywhere;
    }

    .panel__type {
      font-size: 12px;
      opacity: 0.6;
      font-family: monospace;
    }

    .panel__note {
      font-size: 13px;
      opacity: 0.8;
    }

    .panel__action {
      margin-bottom: 12px;
    }

    .panel__action-field {
      width: 100%;
    }

    .panel__action-label {
      font-weight: 500;
    }

    .panel__action-name {
      margin-left: 8px;
      font-family: monospace;
      font-size: 12px;
      opacity: 0.6;
    }

    .panel__action-desc {
      display: block;
      font-size: 12px;
      line-height: 16px;
      opacity: 0.7;
    }

    .panel__warning {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      margin-top: 8px;
      padding: 8px 12px;
      border-radius: 8px;
      background: var(--mat-sys-error-container);
      color: var(--mat-sys-on-error-container);
      font-size: 13px;
    }

    .panel__subtitle {
      margin: 16px 0 8px;
      font-size: 14px;
    }
  `,
})
export class NodePanelComponent {
  /** Вузол сценарію. */
  readonly node = input.required<WorkflowNode>();

  /** Дескриптор типу вузла; немає — тип невідомий, форма параметрів недоступна. */
  readonly descriptor = input<NodeTypeInfo | undefined>(undefined);

  /** Для `mcp.tool`: tool із `tools_cache` (його `inputSchema` описує `arguments`). */
  readonly tool = input<McpToolInfo | null>(null);

  /** Для `mcp.tool`/`mcp.poll`: усі tools конектора вузла — списки `x-openbp-options` (F3-54); `null` — без списків. */
  readonly serverTools = input<readonly McpToolInfo[] | null>(null);

  /** Облікові дані для вибору в параметрах з `credentials` дескриптора (F4-03); `null` — недоступні, поле текстове. */
  readonly credentials = input<readonly CredentialOption[] | null>(null);

  /** Що підказувати в полях виразів `{{ }}` (F2-19): інші вузли, поля останнього тестового output. */
  readonly expressionContext = input<ExpressionContext>(EMPTY_EXPRESSION_CONTEXT);

  /** Лише перегляд: форми недоступні для введення (`inert`), змін немає. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Змінений вузол після кожної зміни у формі. */
  readonly nodeChange = output<WorkflowNode>();

  /** Обрано іншу дію конектора (F3-55): редактор змінює `toolName` і скидає `arguments` (`changeNodeTool`). */
  readonly toolChange = output<McpToolInfo>();

  /** Дії конектора вузла, згруповані «Ресурс → Операція». */
  protected readonly actionGroups = computed(() => groupActions(this.serverTools() ?? []));
  protected readonly actionQuery = signal('');
  protected readonly visibleActions = computed(() => filterActions(this.actionGroups(), this.actionQuery()));
  /** Текст поля «Дія»: назва поточної дії або пошук; вибір — ім'я tool'а. */
  protected readonly actionSearch = new FormControl('', { nonNullable: true });
  /** Дія, що чекає підтвердження: зміна скине непорожні `arguments`. */
  protected readonly pendingTool = signal<McpToolInfo | null>(null);
  protected readonly label = actionLabel;

  /** Спільний `formState` форм параметрів; поля читають контекст у момент підказки — форму не перебудовуємо. */
  private readonly formState: NodeFormState = {};

  constructor() {
    effect(() => {
      this.formState.expressionContext = this.expressionContext();
    });
    // Поле «Дія» показує назву поточного tool'а вузла.
    effect(() => {
      this.state();
      this.pendingTool.set(null);
      this.restoreAction(0);
    });
  }

  private currentTool(): McpToolInfo | null {
    const name = this.state().node.parameters?.['toolName'];
    return (this.serverTools() ?? []).find((t) => t.name === name) ?? null;
  }

  /** Фокус — усі дії, текст виділено: введення одразу шукає. */
  protected openActions(el: HTMLInputElement): void {
    this.actionQuery.set('');
    el.select();
  }

  /** Без вибору поле повертає назву поточної дії (після кліку по варіанту blur приходить раніше за вибір). */
  protected restoreAction(delay = 200): void {
    const restore = () => {
      const tool = this.currentTool();
      const name = this.state().node.parameters?.['toolName'];
      this.actionSearch.setValue(tool ? actionLabel(tool) : typeof name === 'string' ? name : '', { emitEvent: false });
      this.actionQuery.set('');
    };
    if (delay === 0) restore();
    else setTimeout(restore, delay);
  }

  protected actionPicked(event: MatAutocompleteSelectedEvent): void {
    const tool = (this.serverTools() ?? []).find((t) => t.name === event.option.value);
    if (!tool || this.readonly()) return;
    if (tool.name === this.state().node.parameters?.['toolName']) {
      this.restoreAction(0);
      return;
    }
    const args = this.state().node.parameters?.['arguments'];
    if (args && typeof args === 'object' && Object.keys(args).length > 0) {
      this.pendingTool.set(tool);
      this.actionSearch.setValue(actionLabel(tool), { emitEvent: false });
      return;
    }
    this.toolChange.emit(tool);
  }

  protected confirmTool(): void {
    const tool = this.pendingTool();
    this.pendingTool.set(null);
    if (tool) this.toolChange.emit(tool);
  }

  protected cancelTool(): void {
    this.pendingTool.set(null);
    this.restoreAction(0);
  }

  /** Останній вузол, відправлений з панелі: коли він повертається через `node`, форму не перебудовуємо. */
  private lastEmitted: WorkflowNode | null = null;

  protected readonly state = linkedSignal<PanelSource, PanelState>({
    source: () => ({ node: this.node(), descriptor: this.descriptor(), tool: this.tool(), credentials: this.credentials() }),
    computation: (src, previous) => {
      const prev = previous?.value;
      if (prev && src.node === this.lastEmitted && src.descriptor === prev.descriptor && src.tool === prev.tool && src.credentials === prev.credentials) {
        return { ...prev, node: src.node };
      }
      this.formState.connector = connectorOf(src.node, this.serverTools());
      this.formState.nodeId = src.node.id;
      return createState(src, this.formState);
    },
  });

  protected isMcp(node: WorkflowNode): boolean {
    return MCP_TOOL_TYPES.includes(node.type);
  }

  protected parametersChanged(model: Record<string, unknown>): void {
    this.emit(applyParameters(this.state().node, model));
  }

  protected commonChanged(model: CommonModel): void {
    this.emit(applyCommon(this.state().node, model));
  }

  private emit(node: WorkflowNode): void {
    if (this.readonly()) return;
    this.lastEmitted = node;
    // Стан лишається тим самим (ті самі форми) — оновлюється лише поточний вузол.
    this.state.update((s) => ({ ...s, node }));
    this.nodeChange.emit(node);
  }
}

let nextStateId = 0;

/** Конектор вузла для списків з `x-openbp-options`: лише `mcp.*` з відомим `serverId` і tools. */
function connectorOf(node: WorkflowNode, tools: readonly McpToolInfo[] | null): OptionsConnector | null {
  const serverId = node.parameters?.['serverId'];
  return MCP_TOOL_TYPES.includes(node.type) && typeof serverId === 'string' && serverId && tools ? { id: serverId, tools } : null;
}

interface PanelSource {
  node: WorkflowNode;
  descriptor: NodeTypeInfo | undefined;
  tool: McpToolInfo | null;
  credentials: readonly CredentialOption[] | null;
}

function createState({ node, descriptor, tool, credentials }: PanelSource, formState: NodeFormState): PanelState {
  const paramsModel = parametersModel(node);
  return {
    id: ++nextStateId,
    node,
    descriptor,
    tool,
    credentials,
    paramsForm: new FormGroup({}),
    paramsModel,
    paramsFields: parameterFields(node.type, descriptor, tool, credentials, paramsModel),
    paramsOptions: { formState },
    commonForm: new FormGroup({}),
    commonModel: commonModel(node),
    commonFields: commonFields(),
  };
}
