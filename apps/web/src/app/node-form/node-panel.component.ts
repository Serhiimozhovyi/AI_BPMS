import { ChangeDetectionStrategy, Component, booleanAttribute, effect, input, linkedSignal, output } from '@angular/core';
import { FormGroup } from '@angular/forms';
import { FormlyFieldConfig, FormlyForm, FormlyFormOptions } from '@ngx-formly/core';
import { McpToolInfo } from '../connectors/connectors-api.service';
import { CredentialOption } from '../credentials/credentials-api.service';
import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { CommonModel, MCP_TOOL_TYPES, applyCommon, applyParameters, commonFields, commonModel, parameterFields, parametersModel } from './node-form';
import { EMPTY_EXPRESSION_CONTEXT, ExpressionContext } from './expression-suggest';
import { NodeFormState, provideNodeForm } from './node-form-fields';

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
 */
@Component({
  selector: 'app-node-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormlyForm],
  providers: [provideNodeForm()],
  template: `
    @let s = state();
    <section class="panel" data-testid="node-panel" [attr.data-node]="s.node.id">
      <header class="panel__header">
        <h2 class="mat-font-title-sm m-0" data-testid="node-panel-name">{{ s.node.name }}</h2>
        <span class="panel__type">{{ s.node.type }}</span>
        @if (isMcp(s.node)) {
          <span class="panel__type" data-testid="node-panel-tool">{{ s.node.parameters['serverId'] }} · {{ s.node.parameters['toolName'] }}</span>
        }
      </header>

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

    .panel__type {
      font-size: 12px;
      opacity: 0.6;
      font-family: monospace;
    }

    .panel__note {
      font-size: 13px;
      opacity: 0.8;
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

  /** Облікові дані для вибору в параметрах з `credentials` дескриптора (F4-03); `null` — недоступні, поле текстове. */
  readonly credentials = input<readonly CredentialOption[] | null>(null);

  /** Що підказувати в полях виразів `{{ }}` (F2-19): інші вузли, поля останнього тестового output. */
  readonly expressionContext = input<ExpressionContext>(EMPTY_EXPRESSION_CONTEXT);

  /** Лише перегляд: форми недоступні для введення (`inert`), змін немає. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Змінений вузол після кожної зміни у формі. */
  readonly nodeChange = output<WorkflowNode>();

  /** Спільний `formState` форм параметрів; поля читають контекст у момент підказки — форму не перебудовуємо. */
  private readonly formState: NodeFormState = {};

  constructor() {
    effect(() => {
      this.formState.expressionContext = this.expressionContext();
    });
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
