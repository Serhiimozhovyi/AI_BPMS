import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnInit, booleanAttribute, computed, inject, input, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';
import { Observable, Subscription, catchError, filter, forkJoin, map, of, switchMap, take, tap, throwError } from 'rxjs';
import { Autosave } from '../canvas/autosave';
import { CanvasSelection, EMPTY_SELECTION, copySelection, deleteSelection, pasteClipboard } from '../canvas/canvas-edit';
import { DescriptorIndex } from '../canvas/canvas-model';
import { CanvasComponent } from '../canvas/canvas.component';
import { CanvasClipboardService } from '../canvas/clipboard.service';
import { EditHistory } from '../canvas/edit-history';
import { PaletteItem, PaletteServer, addNode, buildPalette, randomNodeId } from '../canvas/palette';
import { PaletteComponent } from '../canvas/palette.component';
import { NodeRunDataComponent } from '../canvas/node-run-data.component';
import { QuickAddMode, QuickAddRequest, QuickAddTarget, ScreenPoint, applyQuickAdd, quickAddSections, readRecent, rememberRecent, writeRecent } from '../canvas/quick-add';
import { QuickAddComponent } from '../canvas/quick-add.component';
import { EMPTY_PINNED_ITEMS } from '../canvas/pinned-data';
import { PinnedDataEditorComponent } from '../canvas/pinned-data-editor.component';
import { TestRun, applyEvent, isRunning, nodeStatuses, runOutputs, singleNodeRequest, startedRun, streamFailed, untilNodeRequest } from '../canvas/test-run';
import { ExecutionItem, ExecutionsApiService, StartExecutionRequest } from '../executions/executions-api.service';
import { ConnectorsApiService, McpToolInfo, apiErrorCode, apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialOption, CredentialsApiService } from '../credentials/credentials-api.service';
import { EMPTY_EXPRESSION_CONTEXT, expressionContext } from '../node-form/expression-suggest';
import { MCP_TOOL_TYPES, updateNode } from '../node-form/node-form';
import { NodePanelComponent } from '../node-form/node-panel.component';
import { NodeTypeInfo, NodesApiService } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowDetail, WorkflowNode, WorkflowsApiService } from '../workflows/workflows-api.service';
import { WorkflowSettingsDialogComponent, WorkflowSettingsDialogData } from '../workflow-settings/workflow-settings-dialog.component';
import { WorkflowSettings, settingsChanged } from '../workflow-settings/workflow-settings';

/** Що зберігає автозбереження: визначення разом з id сценарію, якому воно належить. */
interface SaveRequest {
  id: string;
  definition: WorkflowDefinition;
}

/** Збережено нову версію сценарію. */
export interface EditorSavedEvent {
  workflowId: string;
  version: number;
}

/** Відкрита спливаюча палітра (F3-50): куди додається вузол і де стоїть палітра в межах канвасу. */
interface QuickAddState {
  target: QuickAddTarget;
  mode: QuickAddMode;
  title: string;
  left: number;
  top: number;
}

/** Розмір спливаючої палітри з відступом — щоб вона не виходила за край канвасу. */
const QUICK_ADD_WIDTH = 308;
const QUICK_ADD_HEIGHT = 388;

/** Почато виконання сценарію (тестовий запуск). */
export interface EditorRunEvent {
  executionId: string;
}

/** Помилка редактора: завантаження, збереження або запуску. `code` — код Api (`NOT_FOUND`, `WORKFLOW_INVALID`…). */
export interface EditorErrorEvent {
  code: string;
  message: string;
}

/**
 * Редактор сценарію (сторінка `/workflows/:id` і веб-компонент `<openbp-canvas>`, F4-13): сценарій з Api на канвасі, палітра `core.*` і tools MCP-серверів (F2-16);
 * з'єднання портів, видалення, копіювання/вставка, переміщення, undo/redo і автозбереження кожної зміни в нову
 * версію з помилками валідації на вузлах (F2-17); панель з формою виділеного вузла (F2-18); тестовий запуск
 * «Виконати» з живим статусом вузлів через SSE та input/output вузла в панелі (F2-20); «Виконати вузол» на даних
 * останнього запуску і закріплення output вузла в `pinData` (F2-21); спливаюча палітра з «+» біля порту, на ребрі,
 * з відпущеного в порожнє з'єднання і з `Tab`/`N` (F3-50); тест кроку й pin у заголовку панелі, редагування закріпленого output,
 * «Виконати до цього вузла», клавіша `P` (F3-51).
 */
@Component({
  selector: 'app-workflow-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'keydown($event)' },
  imports: [RouterLink, MatButtonModule, MatProgressBarModule, CanvasComponent, PaletteComponent, QuickAddComponent, NodePanelComponent, NodeRunDataComponent, PinnedDataEditorComponent],
  template: `
    <section class="editor">
      <header class="editor__bar">
        @if (!embedded()) {
          <a mat-button routerLink="/workflows" i18n="@@workfloweditor.back">← Сценарії</a>
        }
        <h1 class="mat-font-title-md m-0" data-testid="workflow-name">
          {{ workflow()?.name ?? defaultTitle }}
        </h1>
        @if (definition(); as def) {
          <span class="editor__count" data-testid="counts" i18n="@@workfloweditor.counts">
            вузлів: {{ def.nodes.length }} · ребер: {{ def.connections.length }}
          </span>
        }
        @if (readonly()) {
          <span class="editor__readonly" data-testid="readonly" i18n="@@workfloweditor.readonly">Лише перегляд</span>
        } @else if (definition(); as def) {
          <span class="editor__tools">
            <button mat-button type="button" data-testid="undo" [disabled]="!canUndo()" (click)="undo()" i18n-title="@@workfloweditor.undo.hint" title="Скасувати (Ctrl+Z)" i18n="@@workfloweditor.undo">Скасувати</button>
            <button mat-button type="button" data-testid="redo" [disabled]="!canRedo()" (click)="redo()" i18n-title="@@workfloweditor.redo.hint" title="Повторити (Ctrl+Shift+Z)" i18n="@@workfloweditor.redo">Повторити</button>
            <button mat-button type="button" data-testid="copy" [disabled]="!hasNodeSelection()" (click)="copy()" i18n-title="@@workfloweditor.copy.hint" title="Копіювати (Ctrl+C)" i18n="@@workfloweditor.copy">Копіювати</button>
            <button mat-button type="button" data-testid="paste" [disabled]="!canPaste()" (click)="paste()" i18n-title="@@workfloweditor.paste.hint" title="Вставити (Ctrl+V)" i18n="@@workfloweditor.paste">Вставити</button>
            <button mat-button type="button" data-testid="delete" [disabled]="!hasSelection()" (click)="deleteSelected()" i18n-title="@@workfloweditor.delete.hint" title="Видалити (Delete)" i18n="@@workfloweditor.delete">Видалити</button>
            <button mat-button type="button" data-testid="settings" (click)="openSettings()" i18n-title="@@workfloweditor.settings.hint" title="Часовий пояс, таймаут, сценарій помилок" i18n="@@workfloweditor.settings">Налаштування</button>
            @if (!embedded()) {
              <a mat-button data-testid="executions-link" [routerLink]="['/executions']" [queryParams]="{ workflowId: workflow()?.id }" i18n="@@workfloweditor.executions">Журнал виконань</a>
            }
            <button mat-flat-button type="button" data-testid="run" [disabled]="running() || def.nodes.length === 0" (click)="runWorkflow()" i18n-title="@@workfloweditor.run.hint" title="Зберегти і виконати з тестовими даними (pinData)">
              @if (running()) {
                <ng-container i18n="@@workfloweditor.running">Виконується…</ng-container>
              } @else {
                <ng-container i18n="@@workfloweditor.run">Виконати</ng-container>
              }
            </button>
          </span>
        }
        @if (testRun(); as r) {
          <span class="editor__run editor__run--{{ r.state }}" data-testid="run-status" [attr.data-state]="r.state">{{ runStateLabel(r) }}</span>
        }
        @switch (saveStatus()) {
          @case ('pending') {
            <span class="editor__dirty" data-testid="dirty" data-status="pending" i18n="@@workfloweditor.dirty">Є незбережені зміни</span>
          }
          @case ('saving') {
            <span class="editor__dirty" data-testid="dirty" data-status="saving" i18n="@@workfloweditor.saving">Збереження…</span>
          }
          @case ('error') {
            <span class="editor__save-error" data-testid="dirty" data-status="error" i18n="@@workfloweditor.saveError">Не збережено</span>
          }
          @case ('saved') {
            <span class="editor__saved" data-testid="saved" i18n="@@workfloweditor.saved">Збережено · версія {{ workflow()?.version }}</span>
          }
        }
      </header>
      @if (runError(); as message) {
        <p role="alert" class="editor__errors" data-testid="run-error">{{ message }}</p>
      }
      @if (saveError(); as message) {
        <div role="alert" class="editor__errors" data-testid="save-error">
          <p class="m-0">{{ message }}</p>
          @if (generalErrors().length > 0) {
            <ul class="m-0">
              @for (e of generalErrors(); track $index) {
                <li>{{ e.message }}</li>
              }
            </ul>
          }
        </div>
      }
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="m-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }
      @if (definition(); as def) {
        <div class="editor__body">
          @if (!readonly()) {
            <aside class="editor__palette">
              @if (toolsError()) {
                <p role="status" class="editor__warning" data-testid="tools-error">{{ toolsError() }}</p>
              }
              <app-palette [sections]="palette()" (pick)="add($event)" />
            </aside>
          }
          <div class="editor__canvas" #canvasArea>
            <app-canvas
              [definition]="def"
              [descriptors]="descriptors()"
              [errors]="validationErrors()"
              [runStatuses]="runStatuses()"
              [selection]="selection()"
              [readonly]="readonly()"
              (definitionChange)="changed($event)"
              (selectionChange)="selection.set($event)"
              (quickAdd)="openQuickAdd($event)"
            />
            @if (quickAdd(); as qa) {
              <div class="editor__backdrop" data-testid="quick-add-backdrop" (pointerdown)="closeQuickAdd()"></div>
              <app-quick-add
                class="editor__quick-add"
                data-testid="quick-add"
                [style.left.px]="qa.left"
                [style.top.px]="qa.top"
                [title]="qa.title"
                [sections]="quickAddSections()"
                (pick)="quickPick($event)"
                (closed)="closeQuickAdd()"
              />
            }
          </div>
          @if (selectedNode(); as node) {
            <aside class="editor__panel">
              <app-node-panel [node]="node" [descriptor]="selectedDescriptor()" [tool]="selectedTool()" [credentials]="credentialOptions()" [expressionContext]="selectedExpressionContext()" [readonly]="readonly()" (nodeChange)="nodeEdited($event)">
                @if (!readonly()) {
                  <div panelActions class="editor__node-actions" data-testid="node-actions">
                    <button mat-stroked-button type="button" data-testid="run-node" [disabled]="running()" [attr.aria-busy]="running() || null" (click)="runNode(node.id)" i18n-title="@@workfloweditor.runNode.hint" title="Виконати лише цей вузол на вхідних даних з останнього запуску">
                      @if (running()) {
                        <ng-container i18n="@@workfloweditor.runNode.running">Виконується…</ng-container>
                      } @else {
                        <ng-container i18n="@@workfloweditor.runNode">Тестувати крок</ng-container>
                      }
                    </button>
                    <button mat-button type="button" data-testid="run-until" [disabled]="running()" (click)="runUntil(node.id)" i18n-title="@@workfloweditor.runUntil.hint" title="Виконати цей вузол і всі попередні; решта сценарію не виконується" i18n="@@workfloweditor.runUntil">До цього вузла</button>
                    @if (selectedPinned()) {
                      <button mat-button type="button" data-testid="unpin-output" (click)="unpin(node.id)" i18n-title="@@workfloweditor.unpin.hint" title="Відкріпити output (P)" i18n="@@workfloweditor.unpin">Відкріпити</button>
                    } @else if (selectedStep()?.status === 'success') {
                      <button mat-button type="button" data-testid="pin-output" (click)="pin(node.id)" i18n-title="@@workfloweditor.pin.hint" title="Тестові запуски віддаватимуть цей output, не виконуючи вузол (P)" i18n="@@workfloweditor.pin">Закріпити output</button>
                    } @else {
                      <button mat-button type="button" data-testid="pin-manual" (click)="pinManual(node.id)" i18n-title="@@workfloweditor.pinManual.hint" title="Закріпити output, введений вручну: тестові запуски не виконуватимуть вузол (P)" i18n="@@workfloweditor.pinManual">Ввести дані вручну</button>
                    }
                  </div>
                }
                @if (selectedPinItems(); as items) {
                  <app-pinned-data-editor panelPinned class="editor__pinned" [items]="items" [readonly]="readonly()" (save)="savePinned(node.id, $event)" />
                }
              </app-node-panel>
              @if (selectedStep(); as step) {
                <app-node-run-data [step]="step" [inputNames]="selectedDescriptor()?.inputs ?? []" [outputNames]="selectedDescriptor()?.outputs ?? []" />
              }
            </aside>
          }
        </div>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
    }

    .editor {
      display: flex;
      flex-direction: column;
      height: 100%;
    }

    .editor__readonly {
      margin-left: auto;
      font-size: 13px;
      opacity: 0.6;
    }

    .editor__bar {
      display: flex;
      align-items: center;
      gap: 16px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
      background: var(--ac-white);
    }

    .editor__tools {
      display: flex;
      gap: 4px;
      margin-left: auto;
    }

    .editor__saved {
      font-size: 13px;
      opacity: 0.6;
    }

    .editor__save-error {
      font-size: 13px;
      color: var(--mat-sys-error);
    }

    .editor__errors {
      padding: 8px 16px;
      font-size: 13px;
      color: var(--mat-sys-error);
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }

    .editor__pinned {
      margin: 0 -16px 12px;
    }

    .editor__node-actions {
      display: flex;
      flex-wrap: wrap;
      gap: 4px;
    }

    .editor__run {
      padding: 2px 10px;
      border-radius: 12px;
      font-size: 13px;
      background: var(--mat-sys-surface-variant);
    }

    .editor__run--success {
      color: #fff;
      background: var(--ac-green);
    }

    .editor__run--error,
    .editor__run--timed_out {
      color: #fff;
      background: var(--ac-red);
    }

    .editor__count {
      font-size: 13px;
      opacity: 0.6;
    }

    .editor__dirty {
      font-size: 13px;
      color: var(--mat-sys-tertiary);
    }

    .editor__body {
      display: flex;
      flex: 1;
      min-height: 0;
    }

    .editor__palette {
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
      width: 260px;
      border-right: 1px solid var(--mat-sys-outline-variant);
      overflow-y: auto;
    }

    /* Бічні панелі непрозорі й над канвасом: вузли не просвічують крізь форму (AC-Kit: біле тло, тінь). */
    .editor__palette,
    .editor__panel {
      position: relative;
      z-index: 1;
      background: var(--ac-white);
    }

    .editor__warning {
      margin: 12px 12px 0;
      font-size: 12px;
      color: var(--mat-sys-error);
    }

    .editor__canvas {
      position: relative;
      flex: 1;
      min-width: 0;
      overflow: hidden;
    }

    .editor__backdrop {
      position: absolute;
      inset: 0;
      z-index: 2;
    }

    .editor__quick-add {
      position: absolute;
      z-index: 3;
    }

    .editor__panel {
      flex-shrink: 0;
      width: 360px;
      border-left: 1px solid var(--mat-sys-outline-variant);
      box-shadow: var(--ac-shadow);
      overflow-y: auto;
    }
  `,
})
export class WorkflowEditorComponent implements OnInit {
  /** Id сценарію; зміна перезавантажує редактор (зміни попереднього зберігаються). */
  readonly workflowId = input.required<string>();

  /** Лише перегляд: без палітри, редагування, запусків і збереження. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Вбудований режим (`<openbp-canvas>`): без посилань на інші сторінки SPA. */
  readonly embedded = input(false, { transform: booleanAttribute });

  /** Нову версію збережено. */
  readonly saved = output<EditorSavedEvent>();

  /** Почато тестовий запуск. */
  readonly executionStarted = output<EditorRunEvent>();

  /** Помилка завантаження, збереження чи запуску. */
  readonly failed = output<EditorErrorEvent>();

  private readonly workflows = inject(WorkflowsApiService);
  private readonly nodes = inject(NodesApiService);
  private readonly connectors = inject(ConnectorsApiService);
  private readonly credentialsApi = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly clipboard = inject(CanvasClipboardService);
  private readonly executions = inject(ExecutionsApiService);
  private readonly dialog = inject(MatDialog);

  protected readonly defaultTitle = $localize`:@@workfloweditor.title:Редактор сценарію`;

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly toolsError = signal<string | null>(null);
  protected readonly workflow = signal<WorkflowDetail | null>(null);
  protected readonly definition = signal<WorkflowDefinition | null>(null);
  protected readonly descriptors = signal<NodeTypeInfo[]>([]);
  private readonly servers = signal<PaletteServer[]>([]);
  /** Облікові дані для вибору у вузлах (F4-03); `null` — недоступні (немає прав або помилка), поля лишаються текстовими. */
  protected readonly credentialOptions = signal<CredentialOption[] | null>(null);
  protected readonly selection = signal<CanvasSelection>(EMPTY_SELECTION);
  /** Останній тестовий запуск цього сценарію. */
  protected readonly testRun = signal<TestRun | null>(null);
  /** Запуск не вдалося почати (сценарій не збережено, помилка Api). */
  protected readonly runError = signal<string | null>(null);
  private runSubscription: Subscription | null = null;

  /** Спливаюча палітра; `null` — закрита. */
  protected readonly quickAdd = signal<QuickAddState | null>(null);
  /** Ключі останніх доданих елементів палітри (секція «Останні»). */
  private readonly recent = signal<string[]>(readRecent());
  private readonly canvasArea = viewChild<ElementRef<HTMLElement>>('canvasArea');

  private readonly history = new EditHistory<WorkflowDefinition | null>(null);
  private readonly autosave = new Autosave<SaveRequest, WorkflowDetail>(
    (r) => this.workflows.save(r.id, r.definition),
    (saved, r) => {
      // Відповідь могла прийти вже після переходу на інший сценарій. Визначення не підміняємо відповіддю —
      // користувач міг змінити його під час запиту; оновлюємо лише метадані (версію).
      if (this.workflow()?.id === r.id) this.workflow.set({ ...saved, definition: r.definition });
      this.saved.emit({ workflowId: saved.id, version: saved.version });
    },
    undefined,
    (e) => this.failed.emit({ code: apiErrorCode(e), message: apiErrorMessage(e) }),
  );

  protected readonly palette = computed(() => buildPalette(this.descriptors(), this.servers()));
  protected readonly quickAddSections = computed(() => quickAddSections(this.palette(), this.quickAdd()?.mode ?? 'step', this.recent()));
  private readonly descriptorIndex = computed(() => new DescriptorIndex(this.descriptors()));

  /** Вузол для панелі — коли виділено рівно один. */
  protected readonly selectedNode = computed<WorkflowNode | null>(() => {
    const ids = this.selection().nodes;
    if (ids.length !== 1) return null;
    return this.definition()?.nodes.find((n) => n.id === ids[0]) ?? null;
  });
  protected readonly selectedDescriptor = computed(() => {
    const node = this.selectedNode();
    return node ? this.descriptorIndex().find(node.type, node.typeVersion) : undefined;
  });
  /** Tool виділеного `mcp.tool`/`mcp.poll` з кешу конектора; не знайдено — `null`. */
  protected readonly selectedTool = computed<McpToolInfo | null>(() => {
    const node = this.selectedNode();
    if (!node || !MCP_TOOL_TYPES.includes(node.type)) return null;
    const { serverId, toolName } = node.parameters ?? {};
    return this.servers().find((s) => s.id === serverId)?.tools.find((t) => t.name === toolName) ?? null;
  });
  /** Підказки виразів для виділеного вузла (F2-19): output останнього тестового запуску, де його немає — `pinData`. */
  protected readonly selectedExpressionContext = computed(() => {
    const node = this.selectedNode();
    const def = this.definition();
    return node && def ? expressionContext(def, node.id, { ...(def.pinData ?? {}), ...runOutputs(this.testRun()) }) : EMPTY_EXPRESSION_CONTEXT;
  });
  /** Крок виділеного вузла в останньому тестовому запуску. */
  protected readonly selectedStep = computed(() => {
    const node = this.selectedNode();
    return node ? (this.testRun()?.steps.get(node.id) ?? null) : null;
  });
  /** У виділеного вузла закріплений output. */
  protected readonly selectedPinned = computed(() => {
    const node = this.selectedNode();
    return !!node && Object.hasOwn(this.definition()?.pinData ?? {}, node.id);
  });
  /** Закріплені items виділеного вузла — для редактора; не закріплено — `null`. */
  protected readonly selectedPinItems = computed<readonly unknown[] | null>(() => {
    const node = this.selectedNode();
    const pinData = this.definition()?.pinData ?? {};
    return node && Object.hasOwn(pinData, node.id) ? pinData[node.id] : null;
  });
  protected readonly runStatuses = computed(() => nodeStatuses(this.testRun()));
  protected readonly running = computed(() => isRunning(this.testRun()));
  protected readonly canUndo = this.history.canUndo;
  protected readonly canRedo = this.history.canRedo;
  protected readonly canPaste = computed(() => this.clipboard.clipboard() !== null);
  protected readonly hasNodeSelection = computed(() => this.selection().nodes.length > 0);
  protected readonly hasSelection = computed(() => this.selection().nodes.length > 0 || this.selection().edges.length > 0);
  protected readonly saveStatus = this.autosave.status;
  protected readonly validationErrors = this.autosave.validationErrors;
  protected readonly saveError = this.autosave.errorMessage;
  /** Помилки валідації без вузла (ребра, налаштування) — у панелі над канвасом. */
  protected readonly generalErrors = computed(() => this.validationErrors().filter((e) => !e.nodeId));
  /** Автозбереження завершилось (немає відкладених змін і запиту в дорозі): `true` — збережено. */
  private readonly saveSettled$ = toObservable(this.saveStatus).pipe(
    filter((s) => s !== 'pending' && s !== 'saving'),
    take(1),
    map((s) => s !== 'error'),
  );

  constructor() {
    // Незбережені зміни не губляться при виході з редактора: відправляємо їх одразу.
    this.destroyRef.onDestroy(() => {
      this.autosave.flush();
      this.stopRun();
    });
  }

  /** `workflowId` як потік: компонент перевикористовується при переході між сценаріями. */
  private readonly workflowId$ = toObservable(this.workflowId);

  ngOnInit(): void {
    // Перезавантажуємо за кожним id.
    this.workflowId$
      .pipe(
        tap(() => {
          // Зміни попереднього сценарію зберігаються до переходу; стан автозбереження — з нуля.
          this.autosave.flush();
          this.autosave.reset();
          this.loading.set(true);
          this.error.set(null);
          this.workflow.set(null);
          this.definition.set(null);
          this.history.reset(null);
          this.selection.set(EMPTY_SELECTION);
          this.quickAdd.set(null);
          this.stopRun();
          this.testRun.set(null);
          this.runError.set(null);
        }),
        switchMap((id) =>
          forkJoin({ workflow: this.workflows.get(id), descriptors: this.nodes.list() }).pipe(
            catchError((e: unknown) => {
              this.error.set(apiErrorMessage(e));
              this.failed.emit({ code: apiErrorCode(e), message: apiErrorMessage(e) });
              return of(null);
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((loaded) => {
        if (loaded) {
          this.workflow.set(loaded.workflow);
          this.definition.set(loaded.workflow.definition);
          this.history.reset(loaded.workflow.definition);
          this.descriptors.set(loaded.descriptors);
        }
        this.loading.set(false);
      });

    this.loadServers()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((servers) => this.servers.set(servers));

    this.credentialsApi
      .options()
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => this.credentialOptions.set(options));
  }

  protected add(item: PaletteItem): void {
    const def = this.definition();
    if (!def) return;
    this.remember(item);
    this.changed(addNode(def, item));
  }

  /** Канвас просить спливаючу палітру: «+» біля порту чи на ребрі, з'єднання відпущено в порожньому місці. */
  protected openQuickAdd(request: QuickAddRequest): void {
    if (this.readonly() || !this.definition()) return;
    this.showQuickAdd(request.target, 'step', request.anchor);
  }

  protected closeQuickAdd(): void {
    this.quickAdd.set(null);
  }

  /** Вибір у спливаючій палітрі: вузол туди, куди вказує ціль, і виділення на ньому. */
  protected quickPick(item: PaletteItem): void {
    const state = this.quickAdd();
    const def = this.definition();
    this.closeQuickAdd();
    if (!state || !def) return;
    const result = applyQuickAdd(def, item, state.target);
    if (!result) return;
    this.remember(item);
    this.changed(result.definition);
    this.selection.set({ nodes: [result.nodeId], edges: [] });
  }

  /**
   * `Tab`/`N`: порожній сценарій — лише тригери; виділено один вузол з виходами — наступний крок після виходу 0;
   * інакше — крок без з'єднання.
   */
  private quickAddFromKeyboard(): void {
    const def = this.definition();
    const area = this.canvasArea()?.nativeElement;
    if (!def || !area) return;
    const box = area.getBoundingClientRect();
    const center: ScreenPoint = { x: box.left + box.width / 2 - QUICK_ADD_WIDTH / 2, y: box.top + 48 };
    if (def.nodes.length === 0) {
      this.showQuickAdd({ kind: 'free' }, 'trigger', center);
      return;
    }
    const node = this.selectedNode();
    if (node && (this.selectedDescriptor()?.outputs.length ?? 0) > 0) {
      const card = area.querySelector(`[data-node-id="${CSS.escape(node.id)}"]`)?.getBoundingClientRect();
      this.showQuickAdd({ kind: 'port', from: { node: node.id, output: 0 } }, 'step', card ? { x: card.right + 48, y: card.top } : center);
      return;
    }
    this.showQuickAdd({ kind: 'free' }, 'step', center);
  }

  private showQuickAdd(target: QuickAddTarget, mode: QuickAddMode, anchor: ScreenPoint): void {
    const box = this.canvasArea()?.nativeElement.getBoundingClientRect();
    const left = box ? clamp(anchor.x - box.left, 8, box.width - QUICK_ADD_WIDTH) : anchor.x;
    const top = box ? clamp(anchor.y - box.top, 8, box.height - QUICK_ADD_HEIGHT) : anchor.y;
    this.quickAdd.set({ target, mode, title: quickAddTitle(target, mode), left, top });
  }

  private remember(item: PaletteItem): void {
    const recent = rememberRecent(this.recent(), item.key);
    this.recent.set(recent);
    writeRecent(recent);
  }

  /** Зміна вузла з панелі. */
  protected nodeEdited(node: WorkflowNode): void {
    const def = this.definition();
    if (def) this.changed(updateNode(def, node));
  }

  /** Зміна від користувача: новий крок історії й автозбереження. */
  protected changed(definition: WorkflowDefinition): void {
    if (this.readonly() || definition === this.definition()) return;
    this.history.push(definition);
    this.apply(definition);
  }

  /** Діалог «Налаштування» (F4-05): нові `settings` — звичайна зміна з історією й автозбереженням. */
  protected openSettings(): void {
    const def = this.definition();
    const id = this.workflow()?.id;
    if (!def || !id) return;
    this.dialog
      .open<WorkflowSettingsDialogComponent, WorkflowSettingsDialogData, WorkflowSettings>(WorkflowSettingsDialogComponent, {
        data: { workflowId: id, settings: def.settings, errors: this.validationErrors() },
        width: '480px',
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((settings) => {
        const current = this.definition();
        if (settings && current && settingsChanged(current.settings, settings)) this.changed({ ...current, settings });
      });
  }

  protected undo(): void {
    const definition = this.history.undo();
    if (definition) this.apply(definition);
  }

  protected redo(): void {
    const definition = this.history.redo();
    if (definition) this.apply(definition);
  }

  protected deleteSelected(): void {
    const def = this.definition();
    if (!def) return;
    const next = deleteSelection(def, this.selection());
    this.selection.set(EMPTY_SELECTION);
    this.changed(next);
  }

  protected copy(): void {
    const def = this.definition();
    const copied = def ? copySelection(def, this.selection()) : null;
    if (copied) this.clipboard.set(copied);
  }

  protected paste(): void {
    const def = this.definition();
    const clipboard = this.clipboard.clipboard();
    if (!def || !clipboard) return;
    const { definition, nodeIds } = pasteClipboard(def, clipboard, randomNodeId, this.clipboard.nextPaste());
    this.changed(definition);
    this.selection.set({ nodes: nodeIds, edges: [] });
  }

  /**
   * «Виконати»: дочекатися збереження змін (виконується поточна версія сценарію з її `pinData`), запустити
   * виконання через Api і стежити за ним через SSE — статуси вузлів на канвасі, кроки в панелі.
   */
  protected runWorkflow(): void {
    this.launch({});
  }

  /**
   * «Виконати вузол» (F2-21): лише цей вузол і його попередники; попередники з останнього запуску не виконуються вдруге —
   * їхні виходи йдуть як `runData`. Кроки інших вузлів попереднього запуску лишаються на канвасі.
   */
  protected runNode(nodeId: string): void {
    const def = this.definition();
    if (def) this.launch(singleNodeRequest(def, this.testRun(), nodeId), this.testRun()?.steps);
  }

  /** Закріпити output (вихід 0) останнього запуску вузла: далі тестові запуски віддають його, не виконуючи вузол. */
  protected pin(nodeId: string): void {
    const def = this.definition();
    const step = this.testRun()?.steps.get(nodeId);
    if (!def || step?.status !== 'success') return;
    this.changed({ ...def, pinData: { ...def.pinData, [nodeId]: step.output[0] ?? [] } });
  }

  /** «Виконати до цього вузла» (F3-51): вузол і всі попередники заново, решта сценарію — ні. */
  protected runUntil(nodeId: string): void {
    this.launch(untilNodeRequest(nodeId), this.testRun()?.steps);
  }

  /** «Ввести дані вручну»: закріпити шаблон з одним порожнім item, далі — редагування в панелі. */
  protected pinManual(nodeId: string): void {
    this.savePinned(nodeId, structuredClone([...EMPTY_PINNED_ITEMS]));
  }

  /** Відредаговані закріплені items — звичайна зміна з історією й автозбереженням. */
  protected savePinned(nodeId: string, items: ExecutionItem[]): void {
    const def = this.definition();
    if (!def || !def.nodes.some((n) => n.id === nodeId)) return;
    this.changed({ ...def, pinData: { ...def.pinData, [nodeId]: items } });
  }

  /** `P`: відкріпити закріплений, закріпити успішний output, інакше — шаблон для ручного введення. */
  private togglePin(nodeId: string): void {
    if (Object.hasOwn(this.definition()?.pinData ?? {}, nodeId)) this.unpin(nodeId);
    else if (this.testRun()?.steps.get(nodeId)?.status === 'success') this.pin(nodeId);
    else this.pinManual(nodeId);
  }

  protected unpin(nodeId: string): void {
    const def = this.definition();
    if (!def || !Object.hasOwn(def.pinData ?? {}, nodeId)) return;
    const pinData = { ...def.pinData };
    delete pinData[nodeId];
    this.changed({ ...def, pinData });
  }

  private launch(request: StartExecutionRequest, previous?: TestRun['steps']): void {
    const id = this.workflow()?.id;
    if (!id || this.running() || this.readonly()) return;
    this.stopRun();
    this.runError.set(null);
    this.autosave.flush();
    this.runSubscription = this.saveSettled$
      .pipe(
        switchMap((saved) =>
          saved
            ? this.executions.start(id, request)
            : throwError(() => new Error($localize`:@@workfloweditor.run.unsaved:Сценарій не збережено — виправте помилки, щоб виконати.`)),
        ),
        switchMap((started) => {
          this.testRun.set(startedRun(started.id, started.state, previous));
          this.executionStarted.emit({ executionId: started.id });
          return this.executions.events(started.id);
        }),
      )
      .subscribe({
        next: (event) => this.testRun.update((run) => (run ? applyEvent(run, event) : run)),
        error: (e: unknown) => {
          // Error — наша помилка (не збережено, обрив SSE); HttpErrorResponse — помилка Api.
          const message = e instanceof Error ? e.message : apiErrorMessage(e);
          this.failed.emit({ code: e instanceof Error ? 'RUN_FAILED' : apiErrorCode(e), message });
          const run = this.testRun();
          if (run) this.testRun.set(streamFailed(run, message));
          else this.runError.set(message);
        },
      });
  }

  protected runStateLabel(run: TestRun): string {
    if (run.error && run.state !== 'error' && run.state !== 'timed_out') return run.error;
    switch (run.state) {
      case 'queued':
        return $localize`:@@workfloweditor.run.queued:У черзі`;
      case 'running':
        return $localize`:@@workfloweditor.run.running:Виконується`;
      case 'cancelling':
        return $localize`:@@workfloweditor.run.cancelling:Скасовується`;
      case 'success':
        return $localize`:@@workfloweditor.run.success:Виконано успішно`;
      case 'error':
        return $localize`:@@workfloweditor.run.error:Помилка: ${run.error ?? ''}:error:`;
      case 'cancelled':
        return $localize`:@@workfloweditor.run.cancelled:Скасовано`;
      case 'timed_out':
        return $localize`:@@workfloweditor.run.timedOut:Перевищено час виконання`;
    }
  }

  private stopRun(): void {
    this.runSubscription?.unsubscribe();
    this.runSubscription = null;
  }

  /** Гарячі клавіші редактора; у полях введення не перехоплюються. */
  protected keydown(event: KeyboardEvent): void {
    // У Shadow DOM (`<openbp-canvas>`) `event.target` — сам елемент; справжня ціль — перша в composedPath.
    if (!this.definition() || this.readonly() || isEditable(event.composedPath()[0] ?? event.target)) return;
    const mod = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    // Перехоплюємо лише доступну дію — інакше працює стандартна поведінка браузера (копіювання тексту тощо).
    let action: (() => void) | null = null;
    if (mod && key === 'z' && !event.shiftKey) action = this.canUndo() ? () => this.undo() : null;
    else if (mod && ((key === 'z' && event.shiftKey) || key === 'y')) action = this.canRedo() ? () => this.redo() : null;
    else if (mod && key === 'c') action = this.hasNodeSelection() ? () => this.copy() : null;
    else if (mod && key === 'v') action = this.canPaste() ? () => this.paste() : null;
    else if (!mod && (event.key === 'Delete' || event.key === 'Backspace')) action = this.hasSelection() ? () => this.deleteSelected() : null;
    else if (!mod && !event.altKey && !event.shiftKey && (event.key === 'Tab' || key === 'n') && onCanvas(event.composedPath()[0] ?? event.target))
      action = this.quickAdd() ? null : () => this.quickAddFromKeyboard();
    else if (!mod && !event.altKey && key === 'p') {
      const node = this.selectedNode();
      action = node ? () => this.togglePin(node.id) : null;
    }
    if (!action) return;
    event.preventDefault();
    action();
  }

  /** Показує стан історії й ставить його на збереження; виділення лишається лише на наявних елементах. */
  private apply(definition: WorkflowDefinition): void {
    this.definition.set(definition);
    const ids = new Set(definition.nodes.map((n) => n.id));
    const selection = this.selection();
    const nodes = selection.nodes.filter((id) => ids.has(id));
    const edges = selection.edges.filter((i) => i < definition.connections.length);
    if (nodes.length !== selection.nodes.length || edges.length !== selection.edges.length) {
      this.selection.set({ nodes, edges });
    }
    const id = this.workflow()?.id;
    if (id) this.autosave.schedule({ id, definition });
  }

  /** Конектори з закешованими tools; помилка не ламає редактор — палітра лишається з `core.*`. */
  private loadServers(): Observable<PaletteServer[]> {
    return this.connectors.list().pipe(
      switchMap((page) => {
        const withTools = page.items.filter((c) => c.toolCount > 0);
        if (withTools.length === 0) return of([]);
        return forkJoin(withTools.map((c) => this.connectors.get(c.id)));
      }),
      map((details) => details.map((d) => ({ id: d.summary.id, name: d.summary.name, tools: d.tools }))),
      catchError((e: unknown) => {
        this.toolsError.set($localize`:@@workfloweditor.toolsError:Tools MCP-серверів недоступні: ${apiErrorMessage(e)}:error:`);
        return of([]);
      }),
    );
  }
}

/** Фокус ніде (`body`) або на канвасі: тоді `Tab` відкриває палітру, а не переводить фокус між кнопками. */
function onCanvas(target: EventTarget | null): boolean {
  if (target === document.body) return true;
  return target instanceof HTMLElement && !!target.closest('.editor__canvas');
}

function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(value, Math.max(min, max)));
}

function quickAddTitle(target: QuickAddTarget, mode: QuickAddMode): string {
  if (mode === 'trigger') return $localize`:@@workfloweditor.quickAdd.trigger:Додати тригер`;
  if (target.kind === 'edge') return $localize`:@@workfloweditor.quickAdd.insert:Вставити крок`;
  if (target.kind === 'port') return $localize`:@@workfloweditor.quickAdd.next:Наступний крок`;
  return $localize`:@@workfloweditor.quickAdd.step:Додати крок`;
}

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  // Панель вузла (прапорці, списки, кнопки) — клавіші належать формі, а не канвасу.
  if (target.closest('app-node-panel')) return true;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}
