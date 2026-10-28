import { Injectable, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { Subscription, filter, map } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { apiErrorCode, apiErrorMessage } from '../connectors/connectors-api.service';
import {
  AssistantConversation,
  AssistantConversationSummary,
  AssistantMessageView,
  AssistantPageContext,
  AssistantProposalView,
  GlobalAssistantApiService,
} from './global-assistant-api.service';

/** Рядок розмови: репліка, крок інструмента, картка пропозиції змін або помилка. */
export interface GlobalChatLine {
  kind: 'user' | 'assistant' | 'step' | 'proposal' | 'error';
  text: string;
  ok?: boolean;
  /** Для `proposal` — пропозиція змін, яку показує картка (`GlobalAssistantService.proposals`). */
  proposalId?: string;
}

/** Запит до панелі ззовні (швидкі дії вузла): підставити в поле чи одразу надіслати. */
export interface GlobalAssistantPrompt {
  text: string;
  send: boolean;
  /** Вибраний вузол сценарію — іде моделі в контексті сторінки. */
  nodeId?: string;
}

const UUID = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';

/** Контекст сторінки за адресою: сценарій, виконання, проєкт; сторінка — без параметрів запиту. */
export function contextFromUrl(url: string): AssistantPageContext {
  const path = url.split(/[?#]/)[0] || '/';
  const context: AssistantPageContext = { page: path };
  const workflow = new RegExp(`^/workflows/(${UUID})(?:/|$)`).exec(path);
  if (workflow) context.workflowId = workflow[1];
  const execution = new RegExp(`^/executions/(${UUID})(?:/|$)`).exec(path);
  if (execution) context.executionId = execution[1];
  const project = new RegExp(`^/projects/(${UUID})(?:/|$)`).exec(path);
  if (project) context.projectId = project[1];
  return context;
}

/**
 * Загальний помічник (F3-110): стан панелі на кожній сторінці — відкрита, список розмов, поточна розмова й її рядки.
 * Живе в корені, тож розмова не губиться при переході між сторінками. Повідомлення йдуть у `/assistant/conversations`
 * з контекстом сторінки; пропозиції змін зберігаються для карток підтвердження (F3-111).
 */
@Injectable({ providedIn: 'root' })
export class GlobalAssistantService {
  private readonly api = inject(GlobalAssistantApiService);
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);

  /** Помічник — для editor і admin; viewer кнопки не бачить (F3-108). */
  readonly available = computed(() => {
    const role = this.auth.user()?.role;
    return role === 'editor' || role === 'admin';
  });
  readonly open = signal(false);
  readonly historyOpen = signal(false);
  readonly conversations = signal<AssistantConversationSummary[]>([]);
  readonly currentId = signal<string | null>(null);
  readonly lines = signal<GlobalChatLine[]>([]);
  readonly proposals = signal<AssistantProposalView[]>([]);
  readonly busy = signal(false);
  /** Пропозиції, рішення щодо яких зараз надсилається. */
  readonly deciding = signal<string[]>([]);
  /** Api відповів, що модель не вибрана (409 `ASSISTANT_NOT_CONFIGURED`). */
  readonly notConfigured = signal(false);
  readonly draft = signal('');
  /** Лічильник запитів на фокус поля вводу. */
  readonly focusRequest = signal(0);

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );
  private readonly nodeId = signal<string | null>(null);
  /** Вузол із запиту ззовні («Розібрати з помічником»): лише для найближчого повідомлення. */
  private readonly promptNode = signal<string | null>(null);
  private request: Subscription | null = null;

  /** Де користувач: сторінка й ідентифікатори з адреси та вибраний вузол. */
  readonly context = computed<AssistantPageContext>(() => {
    const context = contextFromUrl(this.url());
    const nodeId = this.promptNode() ?? this.nodeId();
    return nodeId ? { ...context, nodeId } : context;
  });

  /** Вибраний на сторінці вузол сценарію (редактор повідомляє; `null` — вибір знято). */
  setNode(nodeId: string | null): void {
    this.nodeId.set(nodeId);
  }

  toggle(): void {
    if (this.open()) this.close();
    else this.show();
  }

  close(): void {
    this.open.set(false);
    this.promptNode.set(null);
  }

  /** Відкриває панель; з `prompt` — підставляє текст у поле або одразу надсилає. */
  show(prompt?: GlobalAssistantPrompt): void {
    this.open.set(true);
    this.historyOpen.set(false);
    if (!prompt) return;
    this.promptNode.set(prompt.nodeId ?? null);
    this.draft.set(prompt.text);
    if (prompt.send) this.send();
    else this.focusRequest.update((n) => n + 1);
  }

  /** Новий порожній чат; розмова створюється з першим повідомленням. */
  newConversation(): void {
    this.stop();
    this.promptNode.set(null);
    this.currentId.set(null);
    this.lines.set([]);
    this.proposals.set([]);
    this.notConfigured.set(false);
    this.historyOpen.set(false);
  }

  showHistory(): void {
    this.historyOpen.set(true);
    this.refresh();
  }

  refresh(): void {
    this.api.list().subscribe({
      next: (list) => this.conversations.set(list),
      error: (e: unknown) => this.pushError(apiErrorMessage(e)),
    });
  }

  select(id: string): void {
    this.stop();
    this.api.get(id).subscribe({
      next: (conversation) => this.load(conversation),
      error: (e: unknown) => this.pushError(apiErrorMessage(e)),
    });
  }

  remove(id: string): void {
    this.api.remove(id).subscribe({
      next: () => {
        if (this.currentId() === id) this.newConversation();
        this.historyOpen.set(true);
        this.refresh();
      },
      error: (e: unknown) => this.pushError(apiErrorMessage(e)),
    });
  }

  send(): void {
    const message = this.draft().trim();
    if (!message || this.busy()) return;
    this.draft.set('');
    this.busy.set(true);
    this.notConfigured.set(false);
    this.lines.update((lines) => [...lines, { kind: 'user', text: message }]);
    const context = this.context();
    this.promptNode.set(null);
    const existing = this.currentId();
    if (existing) {
      this.ask(existing, message, context);
      return;
    }
    this.request = this.api.create().subscribe({
      next: (conversation) => {
        this.currentId.set(conversation.id);
        this.ask(conversation.id, message, context);
      },
      error: (e: unknown) => this.failed(e),
    });
  }

  /** Рішення людини щодо картки: застосувати чи скасувати. Стан картки оновлюється відповіддю Api. */
  decide(proposalId: string, apply: boolean): void {
    const id = this.currentId();
    if (!id || this.deciding().includes(proposalId)) return;
    this.deciding.update((list) => [...list, proposalId]);
    this.api.decide(id, proposalId, apply).subscribe({
      next: (proposal) => {
        this.proposals.update((list) => list.map((p) => (p.id === proposal.id ? proposal : p)));
        this.deciding.update((list) => list.filter((p) => p !== proposalId));
      },
      error: (e: unknown) => {
        this.deciding.update((list) => list.filter((p) => p !== proposalId));
        // Уже вирішено (в іншій вкладці): підтягуємо актуальний стан замість помилки.
        if (apiErrorCode(e) === 'ASSISTANT_PROPOSAL_DECIDED') this.select(id);
        else this.pushError(apiErrorMessage(e));
      },
    });
  }

  private ask(id: string, message: string, context: AssistantPageContext): void {
    this.request = this.api.send(id, message, context).subscribe({
      next: (e) => {
        if (e.type === 'step')
          this.lines.update((lines) => [
            ...lines,
            { kind: 'step', text: `${toolLabel(e.tool)}: ${e.text ?? ''}`, ok: e.ok ?? true },
          ]);
        else if (e.type === 'proposal' && e.proposal) {
          const proposal = e.proposal;
          this.proposals.update((list) => [...list.filter((p) => p.id !== proposal.id), proposal]);
          this.lines.update((lines) => [...lines, { kind: 'proposal', text: proposal.tool, proposalId: proposal.id }]);
        }
        else if (e.type === 'message' && e.text) this.lines.update((lines) => [...lines, { kind: 'assistant', text: e.text! }]);
        else if (e.type === 'error') this.pushError(e.text ?? '');
      },
      error: (e: unknown) => this.failed(e),
      complete: () => this.finish(),
    });
  }

  private failed(e: unknown): void {
    if (apiErrorCode(e) === 'ASSISTANT_NOT_CONFIGURED') this.notConfigured.set(true);
    else this.pushError(apiErrorMessage(e));
    this.finish();
  }

  private finish(): void {
    this.busy.set(false);
    this.request = null;
    this.api.list().subscribe({ next: (list) => this.conversations.set(list) });
  }

  private stop(): void {
    this.request?.unsubscribe();
    this.request = null;
    this.busy.set(false);
  }

  private load(conversation: AssistantConversation): void {
    this.currentId.set(conversation.id);
    this.lines.set(conversation.messages.flatMap(toLines));
    this.proposals.set(conversation.proposals);
    this.notConfigured.set(false);
    this.historyOpen.set(false);
  }

  private pushError(text: string): void {
    this.lines.update((lines) => [...lines, { kind: 'error', text }]);
  }
}

/** Підписи інструментів для людини; решта — назва інструмента як є. */
const TOOL_LABELS: Record<string, string> = {
  list_tables: $localize`:@@globalassistant.tool.listTables:Переглянув таблиці`,
  query_table: $localize`:@@globalassistant.tool.queryTable:Прочитав рядки таблиці`,
  create_table: $localize`:@@globalassistant.tool.createTable:Створення таблиці`,
  alter_table: $localize`:@@globalassistant.tool.alterTable:Зміна таблиці`,
  list_workflows: $localize`:@@globalassistant.tool.listWorkflows:Переглянув сценарії`,
  get_workflow: $localize`:@@globalassistant.tool.getWorkflow:Прочитав сценарій`,
  create_workflow: $localize`:@@globalassistant.tool.createWorkflow:Створення сценарію`,
  update_workflow: $localize`:@@globalassistant.tool.updateWorkflow:Зміна сценарію`,
  validate_workflow: $localize`:@@globalassistant.tool.validateWorkflow:Перевірив сценарій`,
  describe_nodes: $localize`:@@globalassistant.tool.describeNodes:Переглянув типи вузлів`,
  describe_connectors: $localize`:@@globalassistant.tool.describeConnectors:Переглянув конектори`,
  get_execution: $localize`:@@globalassistant.tool.getExecution:Прочитав виконання`,
  list_executions: $localize`:@@globalassistant.tool.listExecutions:Переглянув виконання`,
  run_workflow: $localize`:@@globalassistant.tool.runWorkflow:Запуск сценарію`,
};

export function toolLabel(tool: string | null | undefined): string {
  return (tool && TOOL_LABELS[tool]) || tool || '';
}

/** Репліки для показу: рішення щодо пропозицій (`note`) призначені моделі — людина бачить стан на картці. */
function toLines(message: AssistantMessageView): GlobalChatLine[] {
  switch (message.role) {
    case 'tool': {
      const step: GlobalChatLine = { kind: 'step', text: `${toolLabel(message.tool)}: ${message.text ?? ''}`, ok: message.ok ?? true };
      return message.proposalId ? [step, { kind: 'proposal', text: message.tool ?? '', proposalId: message.proposalId }] : [step];
    }
    case 'assistant':
    case 'user':
      return [{ kind: message.role, text: message.text ?? '' }];
    default:
      return [];
  }
}
