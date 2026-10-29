import { AssistantProposalView } from './global-assistant-api.service';
import { toolLabel } from './global-assistant.service';

/** Колонка таблиці в плані (`DataColumn`): лише те, що показує картка. */
export interface PlanColumn {
  name: string;
  type?: string;
  key?: boolean;
  sensitive?: boolean;
}

/** План зміни таблиці (`DataTablePlan`, F3-109), як його віддає Api. */
export interface TablePlan {
  planId?: string;
  action?: 'create' | 'alter';
  definition?: { name?: string; columns?: PlanColumn[] };
  renamedFrom?: string | null;
  addedColumns?: PlanColumn[];
  removedColumns?: PlanColumn[];
  changedColumns?: { before: PlanColumn; after: PlanColumn }[];
  constraintsChanged?: boolean;
  rowCount?: number;
  warnings?: string[];
}

/** Вузол у плані сценарію. */
export interface PlanNode {
  id: string;
  name: string;
  type: string;
}

/** Змінений вузол: що саме змінилося (`name`, `position`, `parameters.<ключ>`…). */
export interface PlanNodeChange extends PlanNode {
  fields: string[];
  renamedFrom?: string | null;
  typeChangedFrom?: string | null;
}

/** Зв'язок у плані сценарію. */
export interface PlanConnection {
  from: string;
  fromName: string;
  output: number;
  to: string;
  toName: string;
  input: number;
}

/** Помилка чи попередження валідації. */
export interface PlanIssue {
  code: string;
  message: string;
  nodeId?: string | null;
  path?: string | null;
}

/** План зміни сценарію (`WorkflowPlan`, F3-116), як його віддає Api. */
export interface WorkflowPlan {
  planId?: string;
  workflowId?: string;
  name?: string;
  baseVersion: number;
  addedNodes?: PlanNode[];
  removedNodes?: PlanNode[];
  changedNodes?: PlanNodeChange[];
  addedConnections?: PlanConnection[];
  removedConnections?: PlanConnection[];
  settingsChanges?: { field: string; before?: unknown; after?: unknown }[];
  otherChanges?: string[];
  errors?: PlanIssue[];
  warnings?: PlanIssue[];
  valid?: boolean;
  hasChanges?: boolean;
}

/** Посилання на створене чи змінене після застосування. */
export interface ProposalLink {
  href: string;
  label: string;
}

const ID = /"id"\s*:\s*"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})"/;
const UUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;

/** План таблиці, якщо пропозиція його має. */
export function tablePlan(proposal: AssistantProposalView): TablePlan | null {
  const plan = proposal.plan;
  return plan && typeof plan === 'object' && 'action' in plan ? (plan as TablePlan) : null;
}

/** План сценарію, якщо пропозиція його має (`update_workflow`). */
export function workflowPlan(proposal: AssistantProposalView): WorkflowPlan | null {
  const plan = proposal.plan;
  return plan && typeof plan === 'object' && 'baseVersion' in plan ? (plan as WorkflowPlan) : null;
}

const NODE_FIELDS: Record<string, string> = {
  position: $localize`:@@proposal.field.position:місце на полотні`,
  notes: $localize`:@@proposal.field.notes:нотатка`,
  retry: $localize`:@@proposal.field.retry:повтори`,
  timeoutSec: $localize`:@@proposal.field.timeout:тайм-аут`,
  continueOnFail: $localize`:@@proposal.field.continueOnFail:продовжувати при помилці`,
  disabled: $localize`:@@proposal.field.disabled:вимкнення`,
  business: $localize`:@@proposal.field.business:бізнес-вигляд`,
  typeVersion: $localize`:@@proposal.field.typeVersion:версія типу`,
};

const SETTINGS: Record<string, string> = {
  timezone: $localize`:@@proposal.setting.timezone:Часовий пояс`,
  executionTimeoutSec: $localize`:@@proposal.setting.timeout:Тайм-аут виконання, с`,
  errorWorkflowId: $localize`:@@proposal.setting.errorWorkflow:Сценарій помилок`,
  saveSuccessfulData: $localize`:@@proposal.setting.saveData:Зберігати дані успішних виконань`,
  concurrency: $localize`:@@proposal.setting.concurrency:Одночасні виконання`,
  throttle: $localize`:@@proposal.setting.throttle:Обмеження частоти`,
  disableAfterErrors: $localize`:@@proposal.setting.disableAfterErrors:Вимикати після помилок поспіль`,
  correlation: $localize`:@@proposal.setting.correlation:Кореляція`,
};

const OTHER: Record<string, string> = {
  notes: $localize`:@@proposal.other.notes:нотатки на полотні`,
  stages: $localize`:@@proposal.other.stages:етапи`,
  business: $localize`:@@proposal.other.business:бізнес-вигляд`,
  pinData: $localize`:@@proposal.other.pinData:закріплені дані`,
};

/** Вузол текстом: `«Назва» (тип)`. */
export function nodeText(node: PlanNode): string {
  return `«${node.name || node.id}» (${node.type})`;
}

/** Що змінилося у вузлі, людською мовою. */
export function nodeChangeText(change: PlanNodeChange): string {
  const parts: string[] = [];
  if (change.renamedFrom) parts.push($localize`:@@proposal.node.renamed:назва (була «${change.renamedFrom}:old:»)`);
  if (change.typeChangedFrom) parts.push($localize`:@@proposal.node.retyped:тип (був ${change.typeChangedFrom}:old:)`);
  const parameters = change.fields.filter((f) => f.startsWith('parameters.')).map((f) => f.slice('parameters.'.length));
  const list = parameters.join(', ');
  if (parameters.length) parts.push($localize`:@@proposal.node.parameters:параметри ${list}:list:`);
  for (const field of change.fields) {
    if (field === 'name' || field === 'type' || field.startsWith('parameters.')) continue;
    parts.push(NODE_FIELDS[field] ?? field);
  }
  return `«${change.name || change.id}»: ${parts.join(', ')}`;
}

/** Зв'язок текстом: `«А» → «Б»`, номер виходу чи входу — лише коли він не перший. */
export function connectionText(connection: PlanConnection): string {
  const output = connection.output > 0 ? $localize`:@@proposal.connection.output: (вихід ${connection.output + 1}:n:)` : '';
  const input = connection.input > 0 ? $localize`:@@proposal.connection.input: (вхід ${connection.input + 1}:n:)` : '';
  return `«${connection.fromName}»${output} → «${connection.toName}»${input}`;
}

function valueText(value: unknown): string {
  if (value === null || value === undefined) return '—';
  if (typeof value === 'boolean') return value ? $localize`:@@proposal.yes:так` : $localize`:@@proposal.no:ні`;
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

/** Зміна налаштування текстом: `Часовий пояс: Europe/Kyiv → UTC`. */
export function settingText(change: { field: string; before?: unknown; after?: unknown }): string {
  return `${SETTINGS[change.field] ?? change.field}: ${valueText(change.before)} → ${valueText(change.after)}`;
}

/** Інші змінені частини визначення текстом. */
export function otherChangesText(plan: WorkflowPlan): string {
  return (plan.otherChanges ?? []).map((key) => OTHER[key] ?? key).join(', ');
}

/** Заголовок картки: що буде зроблено. */
export function proposalTitle(proposal: AssistantProposalView): string {
  const name = typeof proposal.arguments['name'] === 'string' ? (proposal.arguments['name'] as string) : '';
  const plan = tablePlan(proposal);
  switch (proposal.tool) {
    case 'create_table':
      return $localize`:@@proposal.createTable:Створити таблицю «${plan?.definition?.name ?? name}:name:»`;
    case 'alter_table':
      return $localize`:@@proposal.alterTable:Змінити таблицю «${plan?.definition?.name ?? name}:name:»`;
    case 'create_workflow':
      return name ? $localize`:@@proposal.createWorkflowNamed:Створити сценарій «${name}:name:»` : $localize`:@@proposal.createWorkflow:Створити сценарій`;
    case 'update_workflow': {
      const workflow = workflowPlan(proposal)?.name;
      return workflow ? $localize`:@@proposal.updateWorkflowNamed:Змінити сценарій «${workflow}:name:»` : $localize`:@@proposal.updateWorkflow:Змінити сценарій`;
    }
    case 'publish_workflow':
      return $localize`:@@proposal.publishWorkflow:Опублікувати сценарій`;
    case 'move_workflow':
      return $localize`:@@proposal.moveWorkflow:Перенести сценарій у групу`;
    case 'create_workflow_group':
      return $localize`:@@proposal.createGroup:Створити проєкт «${name}:name:»`;
    case 'run_workflow':
      return $localize`:@@proposal.runWorkflow:Запустити сценарій`;
    default:
      return toolLabel(proposal.tool) || proposal.tool;
  }
}

/** Скалярні аргументи виклику для показу (великі й вкладені — лише в «Параметри»). */
export function scalarArguments(proposal: AssistantProposalView): { key: string; value: string }[] {
  return Object.entries(proposal.arguments)
    .filter(([, v]) => v !== null && (typeof v === 'string' || typeof v === 'number' || typeof v === 'boolean'))
    .map(([key, v]) => ({ key, value: String(v).length > 120 ? `${String(v).slice(0, 117)}…` : String(v) }));
}

/** Колонка текстом: `name (string)` з позначками. */
export function columnText(column: PlanColumn): string {
  const flags = [column.key ? $localize`:@@proposal.key:ключ` : '', column.sensitive ? $localize`:@@proposal.sensitive:чутлива` : ''].filter(Boolean);
  return `${column.name} (${column.type ?? '?'}${flags.length ? `, ${flags.join(', ')}` : ''})`;
}

/** Помилка застосування з результату пропозиції: `message`, інакше сирий текст. */
export function failureText(proposal: AssistantProposalView): string {
  const result = proposal.result ?? '';
  try {
    const parsed = JSON.parse(result) as { message?: string; detail?: string };
    return parsed.message ?? parsed.detail ?? result;
  } catch {
    return result;
  }
}

/** Куди перейти після застосування: створене чи змінене. */
export function proposalLink(proposal: AssistantProposalView): ProposalLink | null {
  if (proposal.status !== 'applied') return null;
  const created = ID.exec(proposal.result ?? '')?.[1];
  const argument = (key: string) => (typeof proposal.arguments[key] === 'string' && UUID.test(proposal.arguments[key] as string) ? (proposal.arguments[key] as string) : null);
  switch (proposal.tool) {
    case 'create_table':
    case 'alter_table':
      return { href: '/tables', label: $localize`:@@proposal.openTables:Відкрити таблиці` };
    case 'create_workflow': {
      return created ? { href: `/workflows/${created}`, label: $localize`:@@proposal.openWorkflow:Відкрити сценарій` } : null;
    }
    case 'update_workflow':
    case 'publish_workflow':
    case 'move_workflow': {
      const id = argument('id') ?? argument('workflowId');
      return id ? { href: `/workflows/${id}`, label: $localize`:@@proposal.openWorkflow:Відкрити сценарій` } : null;
    }
    case 'create_workflow_group':
      return created ? { href: `/projects/${created}`, label: $localize`:@@proposal.openProject:Відкрити проєкт` } : null;
    case 'run_workflow':
      return created ? { href: `/executions/${created}`, label: $localize`:@@proposal.openExecution:Відкрити виконання` } : null;
    default:
      return null;
  }
}
