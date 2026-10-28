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
    case 'update_workflow':
      return $localize`:@@proposal.updateWorkflow:Змінити сценарій`;
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
