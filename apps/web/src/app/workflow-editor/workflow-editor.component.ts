import { RouterLink } from '@angular/router';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnInit, booleanAttribute, computed, effect, inject, input, linkedSignal, output, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Observable, Subscription, catchError, filter, forkJoin, map, of, switchMap, take, tap, throwError } from 'rxjs';
import { BusinessNodeFieldsComponent } from '../business-view/business-node-fields.component';
import { NodeEvaluationsComponent } from '../evaluations/node-evaluations.component';
import { BusinessViewComponent } from '../business-view/business-view.component';
import { EditorView, readEditorView, writeEditorView } from '../business-view/editor-view';
import { BusinessRun } from '../business-view/business-run';
import { Autosave } from '../canvas/autosave';
import { LastRun, lastRunFromDetail, lastRunLabel, lastRunTimes } from '../canvas/last-run';
import { EditorToolbarComponent, ToolbarAction, ToolbarRun } from './editor-toolbar.component';
import { NodeHeaderAction, NodePanelHeaderComponent } from './node-panel-header.component';
import { ShortcutsDialogComponent } from './shortcuts-dialog.component';
import { zoomKey } from './shortcuts';
import { CanvasSelection, EMPTY_SELECTION, NeighborDirection, copySelection, deleteSelection, neighborNode, pasteClipboard, renameNode, toggleDisabled } from '../canvas/canvas-edit';
import { DescriptorIndex } from '../canvas/canvas-model';
import { addNote, randomNoteId, removeNotes, updateNote } from '../canvas/canvas-notes';
import { missingFieldsByNode } from '../canvas/missing-fields';
import { CanvasContextMenuRequest, CanvasContextTarget, MissingFieldRequest, NodeActionRequest, NodeRenameRequest, NoteEditRequest } from '../canvas/vflow/vflow-canvas.component';
import { CanvasComponent } from '../canvas/canvas.component';
import { GROUP_COLORS } from '../canvas/vflow/vflow-canvas.component';
import { CanvasClipboardService } from '../canvas/clipboard.service';
import { EditHistory } from '../canvas/edit-history';
import { PaletteItem, PaletteServer, actionKey, addNode, buildPalette, randomNodeId } from '../canvas/palette';
import { changeNodeConnection, changeNodeTool } from '../canvas/connector-actions';
import { PaletteComponent } from '../canvas/palette.component';
import { paletteExpanded, readPaletteChoice, writePaletteChoice } from '../canvas/palette-state';
import { DataFieldPick, DataPort, NodeDataViewComponent } from '../canvas/node-data-view.component';
import { PathSegment, expressionFor } from '../canvas/data-schema';
import { QuickAddMode, QuickAddRequest, QuickAddTarget, ScreenPoint, applyQuickAdd, quickAddSections, readRecent, rememberRecent, writeRecent } from '../canvas/quick-add';
import { QuickAddComponent } from '../canvas/quick-add.component';
import { EmptyStartComponent } from '../canvas/empty-start.component';
import { AssistantPanelComponent, AssistantPrompt } from '../assistant/assistant-panel.component';
import { VersionHistoryDialogComponent, VersionHistoryDialogData, VersionRestore } from '../workflows/version-history-dialog.component';
import { CanvasContextMenuComponent, CanvasMenuAction } from '../canvas/context-menu.component';
import { EMPTY_PINNED_ITEMS } from '../canvas/pinned-data';
import { PinnedDataEditorComponent } from '../canvas/pinned-data-editor.component';
import { TestRun, ancestorIds, applyEvent, isRunning, itemCounts, nodeRunErrors, nodeStatuses, runOutputs, singleNodeRequest, startedRun, streamFailed, untilNodeRequest } from '../canvas/test-run';
import { ExecutionItem, ExecutionsApiService, StartExecutionRequest, StepStatus } from '../executions/executions-api.service';
import { ConnectorCatalogEntry, ConnectorDetail, ConnectorSummary, ConnectorsApiService, McpToolInfo, apiErrorCode, apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialOption, CredentialsApiService } from '../credentials/credentials-api.service';
import { EMPTY_EXPRESSION_CONTEXT, expressionContext } from '../node-form/expression-suggest';
import { ExpressionTargetService } from '../node-form/expression-target';
import { ExpressionEditorService } from '../node-form/expression-editor-dialog.component';
import { MCP_TOOL_TYPES, updateNode } from '../node-form/node-form';
import { NewConnectionDialogComponent, NewConnectionDialogData } from '../node-form/new-connection-dialog.component';
import { SUBSCRIBE_NODE_TYPE } from '../node-form/node-form';
import { ConnectionChange, NodeConnection, NodePanelComponent } from '../node-form/node-panel.component';
import { NodeTypeInfo, NodesApiService } from '../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowDetail, WorkflowNode, WorkflowTemplate, WorkflowsApiService } from '../workflows/workflows-api.service';
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
const CONTEXT_MENU_WIDTH = 240;
const CONTEXT_MENU_HEIGHT = 300;
/** Скільки показується повідомлення над канвасом. */
const NOTICE_MS = 4000;

/** Ширина панелі вузла (F3-52): мінімум, з якої — три колонки, за замовчуванням; запам'ятовується в `localStorage`. */
const MIN_PANEL_WIDTH = 360;
const WIDE_PANEL_WIDTH = 1100;
const DEFAULT_PANEL_WIDTH = 480;
const PANEL_WIDTH_KEY = 'openbp.editor.panelWidth';
const SHOW_LAST_RUN_KEY = 'openbp.editor.showLastRun';

/** «Показувати останній запуск» на вузлах: за замовчуванням увімкнено. */
function readShowLastRun(): boolean {
  try {
    return localStorage.getItem(SHOW_LAST_RUN_KEY) !== '0';
  } catch {
    return true;
  }
}
/** Скільки місця лишається канвасу, коли панель розтягнуто; ширина палітри ліворуч. */
const CANVAS_RESERVE = 240;
const PALETTE_WIDTH = 260;

type PanelTab = 'input' | 'params' | 'output';

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
 * «Виконати до цього вузла», клавіша `P` (F3-51); панель «Вхід | Параметри | Вихід» зі схемою даних, мапінгом полів кліком і
 * перетягуванням, змінною шириною і лічильниками items на ребрах (F3-52).
 */
@Component({
  selector: 'app-workflow-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'keydown($event)' },
  imports: [RouterLink, EditorToolbarComponent, NodePanelHeaderComponent, MatButtonModule, MatProgressBarModule, BusinessViewComponent, BusinessNodeFieldsComponent, CanvasComponent, PaletteComponent, QuickAddComponent, CanvasContextMenuComponent, EmptyStartComponent, AssistantPanelComponent, NodePanelComponent, NodeDataViewComponent, PinnedDataEditorComponent, NodeEvaluationsComponent],
  providers: [ExpressionTargetService, ExpressionEditorService],
  template: `
    <section class="editor">
      <app-editor-toolbar
        [workflow]="workflow()"
        [definition]="definition()"
        [defaultTitle]="defaultTitle"
        [groupName]="groupName()"
        [embedded]="embedded()"
        [readonly]="readonly()"
        [version]="version()"
        [view]="view()"
        [canUndo]="canUndo()"
        [canRedo]="canRedo()"
        [canPaste]="canPaste()"
        [hasSelection]="hasSelection()"
        [hasNodeSelection]="hasNodeSelection()"
        [assistantOpen]="assistantOpen()"
        [running]="running()"
        [run]="toolbarRun()"
        [lastRun]="lastRunChip()"
        [saveStatus]="saveStatus()"
        (action)="toolbarAction($event)"
        (viewChange)="setView($event)"
        (renamed)="renameWorkflow($event)"
        (published)="published($event)"
        (discarded)="draftDiscarded($event)"
      />
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
        @if (view() === 'business') {
          <app-business-view class="editor__business" [definition]="def" [descriptors]="descriptors()" [connectors]="businessConnectors()" [fileName]="workflow()?.name ?? 'scenario'" [execution]="businessRun()" [editable]="!readonly()" (stepPick)="openStep($event)" (definitionChange)="changed($event)" />
        } @else {
        <div class="editor__body">
          @if (!readonly() && !paletteOpen()) {
            <aside class="editor__rail" data-testid="palette-rail" i18n-aria-label="@@workfloweditor.rail" aria-label="Додавання кроків">
              <button #railAdd type="button" class="editor__rail-add" data-testid="rail-add" i18n-title="@@workfloweditor.rail.add.hint" title="Додати крок (Tab)" i18n-aria-label="@@workfloweditor.rail.add" aria-label="Додати крок" (click)="quickAddFromRail(railAdd)">
                <span class="material-symbols-outlined" aria-hidden="true">add</span>
              </button>
              <span class="editor__rail-label" aria-hidden="true" i18n="@@workfloweditor.rail.label">Додати крок</span>
              <button type="button" class="editor__rail-expand" data-testid="palette-expand" i18n-title="@@workfloweditor.rail.expand.hint" title="Показати палітру вузлів" i18n-aria-label="@@workfloweditor.rail.expand" aria-label="Показати палітру вузлів" (click)="setPaletteOpen(true)">
                <span class="material-symbols-outlined" aria-hidden="true">keyboard_double_arrow_right</span>
              </button>
            </aside>
          }
          @if (!readonly() && paletteOpen()) {
            <aside class="editor__palette">
              <div class="editor__palette-head">
                <span class="editor__palette-title" i18n="@@workfloweditor.palette.title">Вузли</span>
                <button type="button" class="editor__rail-expand" data-testid="palette-collapse" i18n-title="@@workfloweditor.palette.collapse.hint" title="Згорнути палітру: більше місця для сценарію" i18n-aria-label="@@workfloweditor.palette.collapse" aria-label="Згорнути палітру" (click)="setPaletteOpen(false)">
                  <span class="material-symbols-outlined" aria-hidden="true">keyboard_double_arrow_left</span>
                </button>
              </div>
              @if (toolsError()) {
                <p role="status" class="editor__warning" data-testid="tools-error">
                  {{ toolsError() }}
                  <button type="button" class="editor__warning-retry" data-testid="tools-retry" (click)="reloadServers()" i18n="@@workfloweditor.toolsRetry">Повторити</button>
                </p>
              } @else if (noConnectors()) {
                <p role="status" class="editor__hint" data-testid="no-connectors">
                  @if (embedded()) {
                    <ng-container i18n="@@workfloweditor.noConnectorsEmbedded">Конекторів ще немає.</ng-container>
                  } @else {
                    <ng-container i18n="@@workfloweditor.noConnectors">Конекторів ще немає — додайте в розділі</ng-container>
                    <a class="underline" routerLink="/connections" i18n="@@workfloweditor.noConnectorsLink">Підключення</a>.
                  }
                </p>
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
              [itemCounts]="itemCounts()"
              [runErrors]="runErrors()"
              [runTimes]="runTimes()"
              [viewportKey]="viewportKey()"
              [lastRunToggle]="lastRunToggle()"
              (lastRunToggled)="setShowLastRun($event)"
              [missingFields]="missingFields()"
              [selection]="selection()"
              [readonly]="readonly()"
              (definitionChange)="changed($event)"
              (selectionChange)="selection.set($event)"
              (quickAdd)="openQuickAdd($event)"
              (missingClick)="openMissing($event)"
              [renaming]="renaming()"
              (nodeAction)="nodeAction($event)"
              (renamed)="renamed($event)"
              (contextMenu)="openContextMenu($event)"
              [editingNote]="editingNote()"
              (noteEditStart)="startNoteEdit($event)"
              (noteEdit)="noteEdited($event)"
              (noteDelete)="deleteNote($event)"
            />
            @if (!def.nodes.length && !readonly() && !quickAdd() && !assistantOpen()) {
              <app-empty-start class="absolute left-1/2 top-1/2 z-[2] -translate-x-1/2 -translate-y-1/2" (addTrigger)="startWithTrigger($event)" (pick)="startFromTemplate($event)" (describe)="assistantAction('describe')" />
            }
            @if (notice(); as text) {
              <p class="absolute bottom-4 left-1/2 z-[2] m-0 -translate-x-1/2 rounded-md bg-ac-kit px-3 py-1.5 text-[13px] text-white" role="status" data-testid="editor-notice">{{ text }}</p>
            }
            @if (contextMenu(); as menu) {
              <div class="editor__backdrop" data-testid="context-menu-backdrop" (pointerdown)="closeContextMenu()" (contextmenu)="$event.preventDefault(); closeContextMenu()"></div>
              <app-canvas-context-menu
                class="editor__menu"
                [style.left.px]="menu.left"
                [style.top.px]="menu.top"
                [kind]="menu.target.kind"
                [nodeDisabled]="menuNodeDisabled()"
                [nodePinned]="menuNodePinned()"
                [canPaste]="canPaste()"
                [running]="running()"
                (action)="menuAction($event)"
                (closed)="closeContextMenu()"
              />
            }
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
          @if (assistantOpen() && !readonly()) {
            <aside class="editor__panel" data-testid="assistant-aside" [style.width.px]="panelWidth()">
              <app-assistant-panel [workflowId]="workflowId()" [version]="workflow()?.version ?? null" [definition]="def" [prompt]="assistantPrompt()" (definitionChange)="assistantChanged($event)" (closed)="assistantOpen.set(false)" />
            </aside>
          } @else if (selectedNode(); as node) {
            <aside class="editor__panel" data-testid="editor-panel" [class.editor__panel--wide]="wide()" [style.width.px]="panelWidth()">
              <div
                class="editor__resize"
                role="separator"
                tabindex="0"
                aria-orientation="vertical"
                data-testid="panel-resize"
                [attr.aria-valuenow]="panelWidth()"
                [attr.aria-valuemin]="minPanelWidth"
                i18n-aria-label="@@workfloweditor.panel.resize"
                aria-label="Ширина панелі"
                i18n-title="@@workfloweditor.panel.resizeHint"
                title="Перетягніть, щоб змінити ширину; ширше 1100 px — три колонки"
                (pointerdown)="startResize($event)"
                (keydown)="resizeKey($event)"
              ></div>
              <app-node-panel-header
                [node]="node"
                [typeLabel]="selectedDescriptor()?.displayName ?? node.type"
                [groupColor]="selectedGroupColor()"
                [readonly]="readonly()"
                [running]="running()"
                [pinned]="selectedPinned()"
                [canPin]="selectedStep()?.status === 'success'"
                [hasError]="!!selectedStep()?.error"
                [renameRequest]="headRenameRequest()"
                (action)="headerAction($event, node.id)"
                (renamed)="headerRenamed(node.id, $event)"
              />
              @if (!wide()) {
                <nav class="editor__tabs" role="tablist">
                  @for (t of panelTabs; track t.id) {
                    <button type="button" role="tab" class="editor__tab" [class.editor__tab--on]="panelTab() === t.id" [attr.aria-selected]="panelTab() === t.id" [attr.data-testid]="'panel-tab-' + t.id" (click)="panelTab.set(t.id)">
                      {{ t.label }}
                    </button>
                  }
                </nav>
              }
              <div class="editor__io">
                <div #inputCol tabindex="-1" class="editor__col editor__col--data" data-testid="panel-input" [hidden]="!wide() && panelTab() !== 'input'">
                  <app-node-data-view side="input" [title]="inputTitle" [ports]="inputPorts()" [mappable]="!readonly()" [expression]="inputExpression()" (pick)="mapField($event)">
                    @if (inputSources().length > 0) {
                      <label dataHeader class="editor__source">
                        <span i18n="@@workfloweditor.input.node">Вузол:</span>
                        <select class="border" data-testid="input-source" [value]="inputSource()" (change)="inputSource.set($any($event.target).value)">
                          <option value="" i18n="@@workfloweditor.input.direct">Вхід ($json)</option>
                          @for (o of inputSources(); track o.id) {
                            <option [value]="o.id">{{ o.name }}</option>
                          }
                        </select>
                      </label>
                    }
                    @if (!readonly() && inputPorts().length > 0) {
                      <p dataNote class="editor__map-note" [class.editor__map-note--warn]="mapHint()" data-testid="map-hint" role="status">
                        @if (mapHint()) {
                          <ng-container i18n="@@workfloweditor.map.noTarget">Спершу клацніть у поле параметра — туди вставиться вираз.</ng-container>
                        } @else if (activeTarget(); as field) {
                          <ng-container i18n="@@workfloweditor.map.target">Клік по полю вставляє вираз у «{{ field }}»; поле можна й перетягнути.</ng-container>
                        } @else {
                          <ng-container i18n="@@workfloweditor.map.hint">Клацніть у поле параметра, потім — на поле даних, або перетягніть поле даних у параметр.</ng-container>
                        }
                      </p>
                    }
                    <div dataEmpty class="editor__empty" data-testid="input-empty">
                      @if (selectedDescriptor()?.inputs?.length === 0) {
                        <p class="m-0" i18n="@@workfloweditor.input.trigger">Тригер не має вхідних даних — він починає сценарій.</p>
                      } @else {
                        <p class="m-0" i18n="@@workfloweditor.input.empty">Виконайте попередній вузол, щоб побачити дані</p>
                        @if (!readonly()) {
                          <button mat-stroked-button type="button" data-testid="input-run-until" [disabled]="running()" (click)="runUntil(node.id)" i18n="@@workfloweditor.input.runUntil">Виконати до цього вузла</button>
                        }
                      }
                    </div>
                  </app-node-data-view>
                </div>
                <div class="editor__col editor__col--params" data-testid="panel-params" [hidden]="!wide() && panelTab() !== 'params'">
                  <app-node-panel [node]="node" [descriptor]="selectedDescriptor()" [tool]="selectedTool()" [serverTools]="selectedServerTools()" [connections]="nodeConnections()" [credentials]="credentialOptions()" [expressionContext]="selectedExpressionContext()" [readonly]="readonly()" [compactHeader]="true" (nodeChange)="nodeEdited($event)" (toolChange)="toolChanged(node.id, $event)" (connectionChange)="connectionChanged(node.id, $event)" (newConnection)="newConnectionFor(node.id)">
                    @if (selectedPinItems(); as items) {
                      <app-pinned-data-editor panelPinned class="editor__pinned" [items]="items" [readonly]="readonly()" (save)="savePinned(node.id, $event)" />
                    }
                  </app-node-panel>
                  <app-business-node-fields [definition]="def" [nodeId]="node.id" [descriptors]="descriptors()" [connectors]="businessConnectors()" [readonly]="readonly()" (definitionChange)="changed($event)" />
                  @if (evaluable(node.type)) {
                    <app-node-evaluations [workflowId]="workflowId()" [nodeId]="node.id" [readonly]="readonly()" />
                  }
                </div>
                <div class="editor__col editor__col--data" data-testid="node-run" [attr.data-status]="selectedStep()?.status ?? null" [hidden]="!wide() && panelTab() !== 'output'">
                  <app-node-data-view side="output" [title]="outputTitle" [ports]="outputPorts()">
                    @if (selectedStep(); as step) {
                      <span dataHeader class="editor__step editor__step--{{ step.status }}" data-testid="node-run-status">{{ stepStatusLabel(step.status) }}</span>
                    } @else if (selectedPinItems()) {
                      <span dataHeader class="editor__step" data-testid="node-run-status" i18n="@@workfloweditor.output.pinned">закріплено</span>
                    }
                    @if (selectedStep()?.error; as error) {
                      <div dataNote class="editor__step-error" role="alert" data-testid="node-run-error">
                        <p class="m-0" data-testid="node-run-error-message">{{ error.message }}</p>
                        @if (error.itemIndex !== null && error.itemIndex !== undefined) {
                          <p class="m-0 text-ac-cool-grey-4" data-testid="node-run-error-item" i18n="@@workfloweditor.error.item">Вузол зупинився на item {{ error.itemIndex }} вхідних даних.</p>
                        }
                        @if (error.details) {
                          <details class="max-w-full text-ac-cool-grey-4" data-testid="node-run-error-details">
                            <summary i18n="@@workfloweditor.error.details">Технічні подробиці</summary>
                            <pre class="m-0 overflow-x-auto text-xs break-words whitespace-pre-wrap">{{ error.details }}</pre>
                          </details>
                        }
                        @if (selectedDescriptor()?.inputs?.length) {
                          <button mat-stroked-button type="button" data-testid="node-run-error-input" (click)="showInput()" i18n="@@workfloweditor.error.showInput">Показати вхідні дані</button>
                        }
                      </div>
                    }
                    <p dataEmpty class="editor__empty m-0" data-testid="output-empty" i18n="@@workfloweditor.output.empty">Виконайте вузол («Тестувати крок»), щоб побачити вихід.</p>
                  </app-node-data-view>
                </div>
              </div>
            </aside>
          }
        </div>
        }
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






    .editor__errors {
      padding: 8px 16px;
      font-size: 13px;
      color: var(--mat-sys-error);
      border-bottom: 1px solid var(--mat-sys-outline-variant);
    }

    .editor__pinned {
      margin: 0 -16px 12px;
    }









    .editor__business {
      flex: 1;
      min-height: 0;
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
      width: 260px; /* PALETTE_WIDTH */
      border-right: 1px solid var(--mat-sys-outline-variant);
      overflow-y: auto;
    }

    .editor__palette-head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 8px 8px 0 16px;
    }

    .editor__palette-title {
      font: 700 11px/16px Ubuntu, sans-serif;
      color: var(--ac-vivid);
      text-transform: uppercase;
    }

    /* Згорнута палітра (F3-89): смуга 48 px з «+ Додати крок» і кнопкою розгортання. */
    .editor__rail {
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
      align-items: center;
      gap: 8px;
      width: 48px; /* RAIL_WIDTH */
      padding: 8px 0;
      border-right: 1px solid var(--mat-sys-outline-variant);
    }

    .editor__rail-add,
    .editor__rail-expand {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 36px;
      height: 36px;
      padding: 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      cursor: pointer;
    }

    .editor__rail-add {
      background: var(--ac-kit);
      color: #fff;
    }

    .editor__rail-expand {
      background: transparent;
      color: var(--ac-vivid);
    }

    .editor__rail-add:hover,
    .editor__rail-expand:hover {
      filter: brightness(0.95);
    }

    .editor__rail-expand:hover {
      background: var(--ac-button);
      filter: none;
    }

    .editor__rail-add:focus-visible,
    .editor__rail-expand:focus-visible {
      outline: 2px solid var(--ac-kit);
      outline-offset: 2px;
    }

    .editor__rail-label {
      margin-top: 4px;
      color: var(--ac-cool-grey-4, #6b6580);
      font-size: 12px;
      white-space: nowrap;
      writing-mode: vertical-rl;
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

    .editor__warning-retry {
      margin-left: 4px;
      padding: 0;
      border: 0;
      background: none;
      color: inherit;
      font: inherit;
      text-decoration: underline;
      cursor: pointer;
    }

    .editor__hint {
      margin: 12px 12px 0;
      font-size: 12px;
      color: var(--ac-cool-grey-4);
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

    .editor__menu {
      position: absolute;
      z-index: 3;
    }


    /* Панель вузла (F3-52): ширина перетягується за ліву межу; від 1100 px — три колонки, вужче — вкладки. */
    .editor__panel {
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
      max-width: calc(100% - 240px);
      border-left: 1px solid var(--mat-sys-outline-variant);
      box-shadow: var(--ac-shadow);
    }

    .editor__resize {
      position: absolute;
      top: 0;
      bottom: 0;
      left: -3px;
      z-index: 2;
      width: 6px;
      cursor: col-resize;
    }

    .editor__resize:hover,
    .editor__resize:focus-visible {
      background: var(--ac-kit);
      opacity: 0.4;
      outline: none;
    }

    .editor__tabs {
      display: flex;
      flex-shrink: 0;
      border-bottom: 1px solid var(--ac-line);
    }

    .editor__tab {
      flex: 1;
      padding: 8px 4px;
      border: 0;
      border-bottom: 2px solid transparent;
      background: none;
      color: var(--ac-cool-grey-4);
      font: 400 14px/20px Ubuntu, sans-serif;
      cursor: pointer;
    }

    .editor__tab--on {
      border-bottom-color: var(--ac-kit);
      color: var(--ac-kit);
    }

    .editor__io {
      display: flex;
      flex: 1;
      min-height: 0;
    }

    .editor__col {
      flex: 1;
      min-width: 0;
      overflow-y: auto;
    }

    .editor__col[hidden] {
      display: none;
    }

    .editor__panel--wide .editor__col--data {
      background: var(--ac-cool-grey-2);
    }

    .editor__panel--wide .editor__col--params {
      flex: 1.2;
      border-right: 1px solid var(--ac-line);
      border-left: 1px solid var(--ac-line);
    }

    .editor__source {
      display: inline-flex;
      align-items: center;
      gap: 4px;
      font-size: 12px;
    }

    .editor__source select {
      min-height: 28px;
      padding: 0 6px;
      border: 1px solid var(--ac-line);
      border-radius: var(--ac-radius-sm);
      background: var(--ac-white);
      font-size: 12px;
    }

    .editor__map-note {
      margin: 0 0 8px;
      font-size: 12px;
      color: var(--ac-cool-grey-4);
    }

    .editor__map-note--warn {
      color: var(--mat-sys-error);
    }

    .editor__empty {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 8px;
      font-size: 13px;
      color: var(--ac-cool-grey-4);
    }

    .editor__step {
      padding: 0 8px;
      border-radius: 8px;
      font-size: 12px;
      color: #fff;
      background: var(--ac-cool-grey-4);
    }

    .editor__step--success {
      background: var(--ac-green);
    }

    .editor__step--error {
      background: var(--ac-red);
    }

    .editor__step-error {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      gap: 6px;
      margin: 0 0 8px;
      font-size: 13px;
      color: var(--mat-sys-error);
    }

  `,
})
export class WorkflowEditorComponent implements OnInit {
  /** Id сценарію; зміна перезавантажує редактор (зміни попереднього зберігаються). */
  readonly workflowId = input.required<string>();

  /** Лише перегляд: без палітри, редагування, запусків і збереження. */
  readonly readonly = input(false, { transform: booleanAttribute });

  /** Переглянути стару версію (F3-61, `?version=N`): канвас лише для читання з визначенням цієї версії. */
  readonly version = input<number | null>(null);

  /** Вузол, який виділити після завантаження (`?node=`, F3-81): напр. упалий у виконанні; невідомий id ігнорується. */
  readonly focusNode = input<string | null>(null);

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
  /** Конекторів у системі немає взагалі — у палітрі нейтральна підказка замість помилки. */
  protected readonly noConnectors = signal(false);
  protected readonly workflow = signal<WorkflowDetail | null>(null);
  protected readonly definition = signal<WorkflowDefinition | null>(null);
  protected readonly descriptors = signal<NodeTypeInfo[]>([]);
  /** Назва групи сценарію (F3-64); без групи — `null`. */
  protected readonly groupName = signal<string | null>(null);
  private readonly servers = signal<PaletteServer[]>([]);
  /** Усі підключення зі списку (і без tools): поле «Підключення» панелі вузла. */
  private readonly connectionList = signal<ConnectorSummary[]>([]);
  /** Каталог конекторів для палітри (F3-96): записи показуються й без підключення. */
  private readonly catalog = signal<ConnectorCatalogEntry[]>([]);
  /** Облікові дані для вибору у вузлах (F4-03); `null` — недоступні (немає прав або помилка), поля лишаються текстовими. */
  protected readonly credentialOptions = signal<CredentialOption[] | null>(null);
  protected readonly selection = signal<CanvasSelection>(EMPTY_SELECTION);
  /** «Бізнес | Технічний» (F3-66): з `?view=` або останній вибраний. */
  protected readonly view = signal<EditorView>(readEditorView());
  /** Тестовий запуск на бізнес-схемі (F3-69): лише кроки цього запуску, без кроків попереднього. */
  protected readonly businessRun = computed((): BusinessRun | null => {
    const run = this.testRun();
    if (!run) return null;
    return { state: run.state, steps: [...run.steps.values()].filter((s) => run.current.has(s.nodeId)) };
  });
  protected readonly businessConnectors = computed(() => this.servers().map((s) => ({ id: s.id, name: s.name })));
  /** Останній тестовий запуск цього сценарію. */
  protected readonly testRun = signal<TestRun | null>(null);
  /** Запуск не вдалося почати (сценарій не збережено, помилка Api). */
  protected readonly runError = signal<string | null>(null);
  private runSubscription: Subscription | null = null;

  /** Вузол з inline-полем назви на картці (F3-58); `null` — немає. */
  protected readonly renaming = signal<string | null>(null);
  /** Контекстне меню канвасу (F3-58); `null` — закрите. */
  protected readonly contextMenu = signal<{ target: CanvasContextTarget; left: number; top: number; at?: [number, number] } | null>(null);
  /** Нотатка з відкритим полем тексту (F3-62); `null` — немає. */
  protected readonly editingNote = signal<string | null>(null);
  /** Коротке повідомлення над канвасом (напр. назва вузла зайнята); зникає саме. */
  protected readonly notice = signal<string | null>(null);
  private noticeTimer: ReturnType<typeof setTimeout> | null = null;

  /** Спливаюча палітра; `null` — закрита. */
  protected readonly quickAdd = signal<QuickAddState | null>(null);
  /** Ключі останніх доданих елементів палітри (секція «Останні»). */
  private readonly recent = signal<string[]>(readRecent());
  private readonly canvasArea = viewChild<ElementRef<HTMLElement>>('canvasArea');
  private readonly canvas = viewChild(CanvasComponent);
  private readonly inputCol = viewChild<ElementRef<HTMLElement>>('inputCol');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

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

  protected readonly palette = computed(() => buildPalette(this.descriptors(), this.servers(), this.recent(), this.catalog()));
  protected readonly quickAddSections = computed(() => quickAddSections(this.palette(), this.quickAdd()?.mode ?? 'step', this.recent()));
  private readonly descriptorIndex = computed(() => new DescriptorIndex(this.descriptors()));

  // ---------- палітра: розгорнута чи смуга (F3-89) ----------

  private readonly paletteChoice = signal<boolean | null>(readPaletteChoice());
  /** Типове значення, яке визначається один раз при відкритті сценарію: порожній — розгорнута, з вузлами — смуга. */
  private readonly paletteDefault = signal(true);
  protected readonly paletteOpen = computed(() => paletteExpanded(this.paletteChoice(), this.paletteDefault()));

  protected setPaletteOpen(open: boolean): void {
    this.paletteChoice.set(open);
    writePaletteChoice(open);
  }

  /** «+ Додати крок» зі смуги: та сама спливаюча палітра, що й на `Tab`, але біля кнопки. */
  protected quickAddFromRail(button: HTMLElement): void {
    const box = button.getBoundingClientRect();
    this.quickAddFromKeyboard({ x: box.right + 8, y: box.top });
  }

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
  /** Усі tools конектора виділеного `mcp.*` — для списків `x-openbp-options` (F3-54); конектора немає — `null`. */
  protected readonly selectedServerTools = computed<readonly McpToolInfo[] | null>(() => {
    const node = this.selectedNode();
    if (!node || !MCP_TOOL_TYPES.includes(node.type)) return null;
    return this.servers().find((s) => s.id === node.parameters?.['serverId'])?.tools ?? null;
  });
  /** Усі підключення (навіть без tools) для поля «Підключення» панелі вузла (F3-95). */
  protected readonly nodeConnections = computed<NodeConnection[]>(() =>
    this.connectionList().map((c) => ({ id: c.id, name: c.name, catalogId: c.catalogId ?? null, tools: this.servers().find((s) => s.id === c.id)?.tools ?? [] })),
  );
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
  // ---------- панель «Вхід | Параметри | Вихід» (F3-52) ----------

  private readonly expressionTargets = inject(ExpressionTargetService);
  /** Редактор виразів з полів (F3-56): попередній перегляд — у цьому сценарії. */
  private readonly expressionEditor = inject(ExpressionEditorService);
  protected readonly minPanelWidth = MIN_PANEL_WIDTH;
  protected readonly inputTitle = $localize`:@@workfloweditor.input.title:Вхід`;
  protected readonly outputTitle = $localize`:@@workfloweditor.output.title:Вихід`;
  protected readonly panelTabs: readonly { id: PanelTab; label: string }[] = [
    { id: 'input', label: this.inputTitle },
    { id: 'params', label: $localize`:@@workfloweditor.params.title:Параметри` },
    { id: 'output', label: this.outputTitle },
  ];
  /** Ширина панелі вузла, px. */
  protected readonly panelWidth = signal(clamp(readPanelWidth(), MIN_PANEL_WIDTH, window.innerWidth - CANVAS_RESERVE - PALETTE_WIDTH));
  /** Досить місця для трьох колонок; інакше — вкладки. */
  protected readonly wide = computed(() => this.panelWidth() >= WIDE_PANEL_WIDTH);
  protected readonly panelTab = signal<PanelTab>('params');
  /** Клік по полю входу, а поля параметра з фокусом ще не було. */
  protected readonly mapHint = signal(false);
  protected readonly activeTarget = this.expressionTargets.activeLabel;

  /** Джерело даних входу: `''` — вхід вузла (`$json`), інакше id попередника (`$node["…"]`); новий вузол — знову вхід. */
  protected readonly inputSource = linkedSignal({ source: () => this.selectedNode()?.id, computation: () => '' });

  /** Попередники виділеного вузла з даними (останній запуск або `pinData`) — для перемикача «Вузол: …». */
  protected readonly inputSources = computed(() => {
    const node = this.selectedNode();
    const def = this.definition();
    if (!node || !def) return [];
    const ancestors = ancestorIds(def, node.id);
    return def.nodes.filter((n) => ancestors.has(n.id) && this.outputPortsOf(n.id).length > 0).map((n) => ({ id: n.id, name: n.name }));
  });

  /** Items входу: вхід кроку вузла з останнього запуску, інакше — виходи попередників за вхідними ребрами; або output обраного вузла. */
  protected readonly inputPorts = computed<DataPort[]>(() => {
    const node = this.selectedNode();
    const def = this.definition();
    if (!node || !def) return [];
    const source = this.inputSource();
    if (source) return this.outputPortsOf(source);
    const names = this.selectedDescriptor()?.inputs ?? [];
    const step = this.selectedStep();
    if (step && step.input.some((items) => items.length > 0)) return portsOf(step.input, names);
    const ports: DataPort[] = [];
    names.forEach((name, input) => {
      for (const c of def.connections) {
        if (c.to.node !== node.id || c.to.input !== input) continue;
        const items = this.outputItems(c.from.node, c.from.output);
        if (items) {
          ports.push({ label: name, items });
          return;
        }
      }
    });
    return ports;
  });

  /** Вираз для поля входу: `$json…` або `$node["Назва"].json…` для обраного попередника. */
  protected readonly inputExpression = computed(() => {
    const source = this.inputSource();
    const name = source ? this.definition()?.nodes.find((n) => n.id === source)?.name : undefined;
    return (path: PathSegment[]) => expressionFor(path, name);
  });

  /** Вихід: порти кроку останнього запуску; без запуску — закріплені items. */
  protected readonly outputPorts = computed<DataPort[]>(() => {
    const step = this.selectedStep();
    const names = this.selectedDescriptor()?.outputs ?? [];
    if (step) return portsOf(step.output, names);
    const pinned = this.selectedPinItems();
    return pinned ? [{ label: names[0] ?? 'main', items: pinned as ExecutionItem[] }] : [];
  });

  /** Підписи «N items» на ребрах після запуску. */
  /** Останнє виконання сценарію, завантажене при відкритті (F3-76); `null` — ще не запускався чи не вдалося завантажити. */
  protected readonly lastRun = signal<LastRun | null>(null);

  /** Після будь-якої правки вузла останній запуск уже не відповідає графу — значки на вузлах зникають. */
  private readonly lastRunStale = signal(false);

  /** Перемикач «Показувати останній запуск» у керуванні канвасу; вибір запам'ятовується. */
  protected readonly showLastRun = signal(readShowLastRun());

  /** Що малювати на вузлах: поточний тестовий запуск, а за його відсутності — свіжий останній запуск. */
  private readonly shownRun = computed(() => this.testRun() ?? (this.showLastRun() && !this.lastRunStale() ? (this.lastRun()?.run ?? null) : null));

  /** Кнопка «Останній запуск» на канвасі є, коли є що показувати. */
  protected readonly lastRunToggle = computed(() => (this.lastRun() && !this.lastRunStale() && !this.testRun() ? this.showLastRun() : null));

  protected readonly runTimes = computed(() => lastRunTimes(this.shownRun()));

  /** «Останній запуск: 18:10, помилка» у шапці. */
  protected readonly lastRunChip = computed((): ToolbarRun | null => {
    const last = this.lastRun();
    return last && !this.testRun() ? { state: last.summary.state, label: lastRunLabel(last.summary) } : null;
  });

  protected setShowLastRun(show: boolean): void {
    this.showLastRun.set(show);
    try {
      localStorage.setItem(SHOW_LAST_RUN_KEY, show ? '1' : '0');
    } catch {
      // Сховище недоступне — вибір діє до перезавантаження.
    }
  }

  protected readonly itemCounts = computed(() => itemCounts(this.shownRun()));

  protected readonly runStatuses = computed(() => nodeStatuses(this.shownRun()));
  /** Повідомлення помилок вузлів останнього запуску — перший рядок на картці (F3-57). */
  protected readonly runErrors = computed(() => nodeRunErrors(this.shownRun()));
  /** Незаповнені обов'язкові параметри вузлів — «не заповнено: N» на картці до збереження (F3-57). */
  protected readonly missingFields = computed(() => {
    const def = this.definition();
    if (!def || this.readonly()) return new Map<string, string[]>();
    const index = this.descriptorIndex();
    const servers = this.servers();
    return missingFieldsByNode(
      def.nodes,
      (n) => index.find(n.type, n.typeVersion),
      (n) => {
        if (!MCP_TOOL_TYPES.includes(n.type)) return null;
        const { serverId, toolName } = n.parameters ?? {};
        return servers.find((s) => s.id === serverId)?.tools.find((t) => t.name === toolName) ?? null;
      },
    );
  });
  protected readonly running = computed(() => isRunning(this.testRun()));
  protected readonly canUndo = this.history.canUndo;
  protected readonly canRedo = this.history.canRedo;
  protected readonly canPaste = computed(() => this.clipboard.clipboard() !== null);
  protected readonly hasNodeSelection = computed(() => this.selection().nodes.length > 0);
  protected readonly hasSelection = computed(
    () => this.selection().nodes.length > 0 || this.selection().edges.length > 0 || (this.selection().notes?.length ?? 0) > 0,
  );
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
    effect(() => (this.expressionEditor.workflowId = this.workflowId()));
    // Панель вузла, що відкрилася, звужує канвас: вибраний вузол не має лишитися за його краєм (F3-88).
    effect(() => {
      const id = this.selectedNode()?.id;
      if (id) untracked(() => this.canvas()?.revealNode(id));
    });
    // Незбережені зміни не губляться при виході з редактора: відправляємо їх одразу.
    this.destroyRef.onDestroy(() => {
      this.autosave.flush();
      this.stopRun();
    });
  }

  /** `workflowId` як потік: компонент перевикористовується при переході між сценаріями. */
  /** Id і версія для перегляду: зміна будь-чого перезавантажує редактор. */
  private readonly source$ = toObservable(computed(() => ({ id: this.workflowId(), version: this.version() })));

  ngOnInit(): void {
    // Перезавантажуємо за кожним id (і версією для перегляду).
    this.source$
      .pipe(
        tap(() => {
          // Зміни попереднього сценарію зберігаються до переходу; стан автозбереження — з нуля.
          this.autosave.flush();
          this.autosave.reset();
          this.loading.set(true);
          this.error.set(null);
          this.workflow.set(null);
          this.groupName.set(null);
          this.definition.set(null);
          this.history.reset(null);
          this.selection.set(EMPTY_SELECTION);
          this.quickAdd.set(null);
          this.stopRun();
          this.testRun.set(null);
          this.runError.set(null);
          this.lastRun.set(null);
          this.lastRunStale.set(false);
        }),
        switchMap(({ id, version }) =>
          forkJoin({
            workflow: this.workflows.get(id),
            descriptors: this.nodes.list(),
            version: version ? this.workflows.version(id, version) : of(null),
          }).pipe(
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
          this.definition.set(loaded.version ?? loaded.workflow.definition);
          this.paletteDefault.set((loaded.version ?? loaded.workflow.definition).nodes.length === 0);
          this.history.reset(loaded.version ?? loaded.workflow.definition);
          this.descriptors.set(loaded.descriptors);
          this.loadGroupName(loaded.workflow);
          if (!loaded.version) this.loadLastRun(loaded.workflow.id);
          const focus = this.focusNode();
          if (focus && (loaded.version ?? loaded.workflow.definition).nodes.some((n) => n.id === focus)) {
            this.selection.set({ nodes: [focus], edges: [] });
          }
        }
        this.loading.set(false);
      });

    this.reloadServers();

    this.credentialsApi
      .options()
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((options) => this.credentialOptions.set(options));
  }

  protected add(item: PaletteItem): void {
    if (!this.definition()) return;
    this.withConnection(item, (resolved) => {
      const def = this.definition();
      if (!def) return;
      this.remember(resolved);
      this.changed(addNode(def, resolved));
    });
  }

  /**
   * Дія чи подія конектора з каталогу (F3-96) отримує підключення: єдине; з кількох — останнє використане в цьому
   * сценарії, інакше перше за назвою; без підключень — діалог «Нове підключення» (скасування — вузол не додається).
   */
  private withConnection(item: PaletteItem, then: (resolved: PaletteItem) => void): void {
    if (item.serverId || !item.connectorId) {
      then(item);
      return;
    }
    const connections = this.connectionList()
      .filter((c) => c.catalogId === item.connectorId)
      .sort((a, b) => a.name.localeCompare(b.name, 'uk'));
    if (connections.length > 0) {
      const ids = new Set(connections.map((c) => c.id));
      const used = [...(this.definition()?.nodes ?? [])].reverse().map((n) => n.parameters?.['serverId']).find((id): id is string => typeof id === 'string' && ids.has(id));
      then({ ...item, serverId: used ?? connections[0].id });
      return;
    }
    this.dialog
      .open<NewConnectionDialogComponent, NewConnectionDialogData, ConnectorDetail>(NewConnectionDialogComponent, {
        width: '720px',
        maxWidth: '95vw',
        data: { catalogId: item.connectorId },
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((detail) => {
        if (!detail) return;
        this.loadServers()
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((servers) => {
            this.servers.set(servers);
            then({ ...item, serverId: detail.summary.id });
          });
      });
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
    this.closeQuickAdd();
    if (!state || !this.definition()) return;
    this.withConnection(item, (resolved) => {
      const def = this.definition();
      if (!def) return;
      const result = applyQuickAdd(def, resolved, state.target);
      if (!result) return;
      this.remember(resolved);
      this.changed(result.definition);
      this.selection.set({ nodes: [result.nodeId], edges: [] });
    });
  }

  /**
   * `Tab`/`N`: порожній сценарій — лише тригери; виділено один вузол з виходами — наступний крок після виходу 0;
   * інакше — крок без з'єднання.
   */
  private quickAddFromKeyboard(anchor?: ScreenPoint): void {
    const def = this.definition();
    const area = this.canvasArea()?.nativeElement;
    if (!def || !area) return;
    const box = area.getBoundingClientRect();
    const center: ScreenPoint = anchor ?? { x: box.left + box.width / 2 - QUICK_ADD_WIDTH / 2, y: box.top + 48 };
    if (def.nodes.length === 0) {
      this.showQuickAdd({ kind: 'free' }, 'trigger', center);
      return;
    }
    const node = this.selectedNode();
    if (node && (this.selectedDescriptor()?.outputs.length ?? 0) > 0) {
      const card = area.querySelector(`[data-node-id="${CSS.escape(node.id)}"]`)?.getBoundingClientRect();
      this.showQuickAdd({ kind: 'port', from: { node: node.id, output: 0 } }, 'step', card && !anchor ? { x: card.right + 48, y: card.top } : center);
      return;
    }
    this.showQuickAdd({ kind: 'free' }, 'step', center);
  }

  /** Панель «Помічник» замість панелі вузла (F3-60). */
  protected readonly assistantOpen = signal(false);
  protected readonly assistantPrompt = signal<AssistantPrompt | null>(null);
  private assistantSeq = 0;

  protected toggleAssistant(): void {
    this.assistantOpen.update((open) => !open);
  }

  /** Відповідь помічника — одна правка з історією: Ctrl+Z скасовує її цілком. */
  protected assistantChanged(definition: WorkflowDefinition): void {
    this.changed(definition);
  }

  /** Швидкі дії: пояснити вузол, виправити помилку (з текстом помилки й входом), написати вираз; «Описати процес». */
  protected assistantAction(kind: 'explain' | 'fix' | 'expression' | 'describe', node?: WorkflowNode): void {
    const label = node ? `«${node.name}» (${node.id})` : '';
    let text: string;
    let send = true;
    if (kind === 'explain') text = $localize`:@@workfloweditor.assistant.explainPrompt:Поясни простими словами, що робить вузол ${label}:node: і які дані він отримує та віддає.`;
    else if (kind === 'fix') {
      const step = this.selectedStep();
      const input = JSON.stringify(step?.input?.[0]?.slice(0, 3) ?? []).slice(0, 2000);
      const error = [step?.error?.message, step?.error?.details].filter(Boolean).join('\n');
      text = $localize`:@@workfloweditor.assistant.fixPrompt:Вузол ${label}:node: завершився помилкою:\n${error}:error:\nВхідні дані: ${input}:input:\nВиправ вузол.`;
    } else if (kind === 'expression') {
      text = $localize`:@@workfloweditor.assistant.expressionPrompt:Напиши вираз {{ }} для вузла ${label}:node:: `;
      send = false;
    } else {
      text = '';
      send = false;
    }
    this.assistantOpen.set(true);
    this.assistantPrompt.set({ text, send, seq: ++this.assistantSeq });
  }

  /** Порожній сценарій → «Додати тригер» (F3-59). */
  protected startWithTrigger(anchor: ScreenPoint): void {
    this.showQuickAdd({ kind: 'free' }, 'trigger', anchor);
  }

  /** Порожній сценарій → «Почати з прикладу»: визначення шаблону з назвою цього сценарію — звичайна правка з історією. */
  protected startFromTemplate(template: WorkflowTemplate): void {
    const def = this.definition();
    if (!def) return;
    this.selection.set(EMPTY_SELECTION);
    this.changed({ ...template.definition, name: def.name });
  }

  private showQuickAdd(target: QuickAddTarget, mode: QuickAddMode, anchor: ScreenPoint): void {
    const box = this.canvasArea()?.nativeElement.getBoundingClientRect();
    const left = box ? clamp(anchor.x - box.left, 8, box.width - QUICK_ADD_WIDTH) : anchor.x;
    const top = box ? clamp(anchor.y - box.top, 8, box.height - QUICK_ADD_HEIGHT) : anchor.y;
    this.quickAdd.set({ target, mode, title: quickAddTitle(target, mode), left, top });
  }

  private remember(item: Pick<PaletteItem, 'key'>): void {
    const recent = rememberRecent(this.recent(), item.key);
    this.recent.set(recent);
    writeRecent(recent);
  }

  /** Подвійний клік по вузлу: виділити, показати вкладку параметрів і сфокусувати перше поле форми. */
  protected openNode(nodeId: string): void {
    this.selection.set({ nodes: [nodeId], edges: [] });
    this.panelTab.set('params');
    let tries = 0;
    const focus = () => {
      const root = this.host.nativeElement;
      const control = root.querySelector<HTMLElement>(
        '[data-testid="panel-params"] [data-testid="node-parameters"] input:not([type="hidden"]), [data-testid="panel-params"] [data-testid="node-parameters"] textarea, [data-testid="panel-params"] [data-testid="node-parameters"] mat-select, [data-testid="panel-params"] [data-testid="node-parameters"] [contenteditable="true"]',
      );
      if (control) control.focus();
      else if (++tries < 20) requestAnimationFrame(focus);
    };
    requestAnimationFrame(focus);
  }

  /** «не заповнено: N» на картці: відкрити вузол на вкладці параметрів і поставити фокус на перше незаповнене поле. */
  protected openMissing(request: MissingFieldRequest): void {
    this.selection.set({ nodes: [request.nodeId], edges: [] });
    this.panelTab.set('params');
    const key = request.field.split('.').pop() ?? request.field;
    const root = this.host.nativeElement;
    let tries = 0;
    // Панель і поля formly з'являються за кілька кадрів після виділення.
    const focus = () => {
      const field = root.querySelector<HTMLElement>(`[data-testid="panel-params"] [data-field="${CSS.escape(key)}"]`);
      const control = field?.querySelector<HTMLElement>('input, textarea, mat-select, [contenteditable="true"], [tabindex]:not([tabindex="-1"])');
      if (control) {
        control.scrollIntoView?.({ block: 'center' });
        control.focus();
      } else if (++tries < 20) {
        requestAnimationFrame(focus);
      }
    };
    requestAnimationFrame(focus);
  }

  /** Кнопка «Показати вхідні дані» в помилці вузла: на вузькому екрані — вкладка входу, на широкому — прокрутка до неї. */
  protected showInput(): void {
    if (!this.wide()) {
      this.panelTab.set('input');
      return;
    }
    const col = this.inputCol()?.nativeElement;
    col?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
    col?.focus?.();
  }

  /** Тулбар над карткою вузла (F3-58). */
  protected nodeAction(request: NodeActionRequest): void {
    // Подвійний клік: лише відкриває панель і ставить фокус на перше поле (перейменування — F2, тулбар або клік по назві в панелі).
    if (request.action === 'open') {
      this.openNode(request.nodeId);
      return;
    }
    if (this.readonly()) return;
    switch (request.action) {
      case 'run':
        this.runNode(request.nodeId);
        break;
      case 'toggle':
        this.toggleNodes([request.nodeId]);
        break;
      case 'duplicate':
        this.duplicate([request.nodeId]);
        break;
      case 'rename':
        this.startRename(request.nodeId);
        break;
      case 'delete':
        this.selection.set({ nodes: [request.nodeId], edges: [] });
        this.deleteSelected();
        break;
    }
  }

  /** Вимкнути/увімкнути вузли (`D`): вимкнений вузол не виконується, дані йдуть повз нього. */
  protected toggleNodes(ids: readonly string[]): void {
    const def = this.definition();
    if (def) this.changed(toggleDisabled(def, ids));
  }

  /** Дублювати (`Ctrl+D`): копія поруч, з'єднання між дубльованими вузлами зберігаються; буфер обміну не змінюється. */
  protected duplicate(ids: readonly string[]): void {
    const def = this.definition();
    const copied = def ? copySelection(def, { nodes: ids, edges: [] }) : null;
    if (!def || !copied) return;
    const { definition, nodeIds } = pasteClipboard(def, copied, randomNodeId);
    this.changed(definition);
    this.selection.set({ nodes: nodeIds, edges: [] });
  }

  protected startRename(nodeId: string): void {
    if (this.readonly() || !this.definition()?.nodes.some((n) => n.id === nodeId)) return;
    this.renaming.set(nodeId);
  }

  /** Кольор групи виділеного вузла — для мітки в шапці панелі. */
  protected readonly selectedGroupColor = computed(() => GROUP_COLORS[this.selectedDescriptor()?.group ?? 'unknown']);
  /** Лічильник запитів «перейменувати» з клавіші F2 — шапка панелі відкриває inline-поле назви (F3-88). */
  protected readonly headRenameRequest = signal(0);

  /** Дії шапки панелі вузла (F3-88). */
  protected headerAction(action: NodeHeaderAction, nodeId: string): void {
    const node = this.definition()?.nodes.find((n) => n.id === nodeId);
    switch (action) {
      case 'test': return this.runNode(nodeId);
      case 'until': return this.runUntil(nodeId);
      case 'pin': return this.pin(nodeId);
      case 'unpin': return this.unpin(nodeId);
      case 'pinManual': return this.pinManual(nodeId);
      case 'explain': return this.assistantAction('explain', node);
      case 'fix': return this.assistantAction('fix', node);
      case 'expression': return this.assistantAction('expression', node);
      case 'close': return this.closePanel();
    }
  }

  /** Нова назва з поля в шапці панелі: ті самі правила, що й для inline-поля картки (зайнята чи порожня назва — повідомлення). */
  protected headerRenamed(nodeId: string, name: string): void {
    const def = this.definition();
    if (!def) return;
    const result = renameNode(def, nodeId, name);
    if (result.error === 'taken') this.showNotice($localize`:@@workfloweditor.rename.taken:Вузол із такою назвою вже є. Оберіть іншу назву.`);
    else if (result.error === 'empty') this.showNotice($localize`:@@workfloweditor.rename.empty:Назва не може бути порожньою.`);
    else if (result.definition) this.changed(result.definition);
  }

  /** «×» і Esc: закрити панель — знімає виділення, фокус повертається на канвас. */
  protected closePanel(): void {
    this.selection.set(EMPTY_SELECTION);
    this.canvasArea()?.nativeElement.querySelector<HTMLElement>('[tabindex="0"], [tabindex="-1"]')?.focus?.();
  }

  /** Inline-поле назви: нова назва (посилання `$node["…"]` переписуються) або скасування; зайнята назва — повідомлення. */
  protected renamed(request: NodeRenameRequest): void {
    if (this.renaming() !== request.nodeId) return;
    this.renaming.set(null);
    const def = this.definition();
    if (!def || request.name === null) return;
    const result = renameNode(def, request.nodeId, request.name);
    if (result.error === 'taken') this.showNotice($localize`:@@workfloweditor.rename.taken:Вузол із такою назвою вже є. Оберіть іншу назву.`);
    else if (result.error === 'empty') this.showNotice($localize`:@@workfloweditor.rename.empty:Назва не може бути порожньою.`);
    else if (result.definition) this.changed(result.definition);
  }

  protected openContextMenu(request: CanvasContextMenuRequest): void {
    if (this.readonly() || !this.definition()) return;
    const { target } = request;
    // Меню діє на те, що під вказівником: вузол поза виділенням стає виділеним.
    if (target.kind === 'node' && !this.selection().nodes.includes(target.nodeId)) this.selection.set({ nodes: [target.nodeId], edges: [] });
    if (target.kind === 'edge') this.selection.set({ nodes: [], edges: [target.edgeIndex] });
    if (target.kind === 'note' && !this.selection().notes?.includes(target.noteId)) this.selection.set({ nodes: [], edges: [], notes: [target.noteId] });
    const box = this.canvasArea()?.nativeElement.getBoundingClientRect();
    const left = box ? clamp(request.anchor.x - box.left, 8, box.width - CONTEXT_MENU_WIDTH) : request.anchor.x;
    const top = box ? clamp(request.anchor.y - box.top, 8, box.height - CONTEXT_MENU_HEIGHT) : request.anchor.y;
    this.contextMenu.set({ target, left, top, at: request.at });
  }

  protected closeContextMenu(): void {
    this.contextMenu.set(null);
  }

  protected readonly menuNodeDisabled = computed(() => {
    const target = this.contextMenu()?.target;
    return target?.kind === 'node' && !!this.definition()?.nodes.find((n) => n.id === target.nodeId)?.disabled;
  });

  protected readonly menuNodePinned = computed(() => {
    const target = this.contextMenu()?.target;
    return target?.kind === 'node' && Object.hasOwn(this.definition()?.pinData ?? {}, target.nodeId);
  });

  protected menuAction(action: CanvasMenuAction): void {
    const menu = this.contextMenu();
    this.closeContextMenu();
    if (!menu) return;
    const { target } = menu;
    const nodes = target.kind === 'node' ? this.selection().nodes : [];
    switch (action) {
      case 'run':
        if (target.kind === 'node') this.runNode(target.nodeId);
        break;
      case 'toggle':
        this.toggleNodes(nodes);
        break;
      case 'duplicate':
        this.duplicate(nodes);
        break;
      case 'rename':
        if (target.kind === 'node') this.startRename(target.nodeId);
        break;
      case 'pin':
        if (target.kind === 'node') this.togglePin(target.nodeId);
        break;
      case 'copy':
        this.copy();
        break;
      case 'paste':
        this.paste();
        break;
      case 'delete':
        this.deleteSelected();
        break;
      case 'insert':
        if (target.kind === 'edge') this.showQuickAdd({ kind: 'edge', edgeIndex: target.edgeIndex }, 'step', this.menuAnchor(menu));
        break;
      case 'add':
        this.showQuickAdd({ kind: 'free' }, this.definition()?.nodes.length ? 'step' : 'trigger', this.menuAnchor(menu));
        break;
      case 'selectAll':
        this.selectAll();
        break;
      case 'addNote':
        this.addNoteAt(menu.at ?? this.canvas()?.viewportCenter() ?? [0, 0]);
        break;
      case 'editNote':
        if (target.kind === 'note') this.startNoteEdit(target.noteId);
        break;
    }
  }

  // ---------- Нотатки (F3-62) ----------

  /** Нова порожня нотатка з лівим верхнім кутом у `at` (координати канвасу) — одразу з відкритим полем тексту. */
  protected addNoteAt(at: [number, number]): void {
    const def = this.definition();
    if (!def || this.readonly()) return;
    const { definition, id } = addNote(def, at, randomNoteId);
    this.changed(definition);
    this.selection.set({ nodes: [], edges: [], notes: [id] });
    this.editingNote.set(id);
  }

  protected startNoteEdit(id: string): void {
    if (this.readonly() || !this.definition()?.notes?.some((n) => n.id === id)) return;
    this.selection.set({ nodes: [], edges: [], notes: [id] });
    this.editingNote.set(id);
  }

  /** Поле тексту закрито: новий текст — крок історії; скасування щойно доданої порожньої нотатки прибирає її. */
  protected noteEdited(request: NoteEditRequest): void {
    if (this.editingNote() !== request.id) return;
    this.editingNote.set(null);
    const def = this.definition();
    if (!def) return;
    const note = def.notes?.find((n) => n.id === request.id);
    if (!note) return;
    if (request.text === null) {
      if (!note.text) this.changed(removeNotes(def, [request.id]));
      return;
    }
    const next = updateNote(def, request.id, { text: request.text });
    if (next !== def) this.changed(next);
  }

  protected deleteNote(id: string): void {
    const def = this.definition();
    if (!def || this.readonly()) return;
    if (this.editingNote() === id) this.editingNote.set(null);
    this.selection.set(EMPTY_SELECTION);
    this.changed(removeNotes(def, [id]));
  }

  protected selectAll(): void {
    const def = this.definition();
    if (def) this.selection.set({ nodes: def.nodes.map((n) => n.id), edges: def.connections.map((_, i) => i) });
  }

  /** Точка меню в координатах вікна — туди ж відкривається спливаюча палітра. */
  private menuAnchor(menu: { left: number; top: number }): ScreenPoint {
    const box = this.canvasArea()?.nativeElement.getBoundingClientRect();
    return { x: (box?.left ?? 0) + menu.left, y: (box?.top ?? 0) + menu.top };
  }

  /** Стрілки: перехід виділення на сусідній вузол. */
  private moveSelection(direction: NeighborDirection): void {
    const node = this.selectedNode();
    const def = this.definition();
    const next = node && def ? neighborNode(def, node.id, direction) : null;
    if (next) this.selection.set({ nodes: [next], edges: [] });
  }

  private showNotice(text: string): void {
    this.notice.set(text);
    if (this.noticeTimer) clearTimeout(this.noticeTimer);
    this.noticeTimer = setTimeout(() => this.notice.set(null), NOTICE_MS);
  }

  /** Інша дія конектора з панелі (F3-55): `arguments` скидаються, з'єднання й позиція лишаються; дія — у «Останніх». */
  protected toolChanged(nodeId: string, tool: McpToolInfo): void {
    const def = this.definition();
    const node = def?.nodes.find((n) => n.id === nodeId);
    if (!def || !node) return;
    const serverId = String(node.parameters?.['serverId'] ?? '');
    const previous = this.servers().find((s) => s.id === serverId)?.tools.find((t) => t.name === node.parameters?.['toolName']) ?? null;
    this.changed(changeNodeTool(def, nodeId, tool, previous));
    this.remember({ key: actionKey(this.connectorIdOf(serverId), tool.name) });
  }

  /** Id конектора підключення в палітрі: запис каталогу, якщо він є в каталозі, інакше id підключення. */
  private connectorIdOf(serverId: string): string {
    const catalogId = this.connectionList().find((c) => c.id === serverId)?.catalogId;
    return catalogId && this.catalog().some((e) => e.manifest.id === catalogId) ? catalogId : serverId;
  }

  /** Інше підключення з панелі (F3-95): дія й `arguments` лишаються, якщо така дія є в новому підключенні. */
  protected connectionChanged(nodeId: string, change: ConnectionChange): void {
    const def = this.definition();
    const node = def?.nodes.find((n) => n.id === nodeId);
    if (!def || !node) return;
    const previous = this.servers().find((s) => s.id === node.parameters?.['serverId'])?.tools.find((t) => t.name === node.parameters?.['toolName']) ?? null;
    const next = changeNodeConnection(def, nodeId, change.serverId, change.tool, previous);
    // Ресурс подій належить підключенню: інше підключення — інші ресурси.
    this.changed(node.type === SUBSCRIBE_NODE_TYPE ? clearResource(next, nodeId) : next);
  }

  /** «+ Нове підключення…» (F3-95): діалог встановлення з каталогу; готове підключення підставляється у вузол. */
  protected newConnectionFor(nodeId: string): void {
    const node = this.definition()?.nodes.find((n) => n.id === nodeId);
    if (!node) return;
    const catalogId = this.connectionList().find((c) => c.id === node.parameters?.['serverId'])?.catalogId ?? null;
    this.dialog
      .open<NewConnectionDialogComponent, NewConnectionDialogData, ConnectorDetail>(NewConnectionDialogComponent, { width: '720px', maxWidth: '95vw', data: { catalogId } })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((detail) => {
        if (!detail) return;
        this.loadServers()
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe((servers) => {
            this.servers.set(servers);
            this.connectionChanged(nodeId, this.changeFor(nodeId, detail, servers));
          });
      });
  }

  /** Дія вузла лишається, якщо є в новому підключенні; інакше — його перша дія (аргументи скидаються). */
  private changeFor(nodeId: string, detail: ConnectorDetail, servers: readonly PaletteServer[]): ConnectionChange {
    const node = this.definition()?.nodes.find((n) => n.id === nodeId);
    const tools = servers.find((s) => s.id === detail.summary.id)?.tools ?? detail.tools;
    const keep = !node?.parameters?.['toolName'] || tools.length === 0 || tools.some((t) => t.name === node.parameters?.['toolName']);
    return { serverId: detail.summary.id, tool: keep ? null : tools[0] };
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

  /** «Історія» (F3-61): «Відновити» — визначення старої версії з поточною назвою, звичайна правка → нова версія. */
  protected openHistory(): void {
    const id = this.workflow()?.id;
    if (!id) return;
    this.dialog
      .open<VersionHistoryDialogComponent, VersionHistoryDialogData, VersionRestore>(VersionHistoryDialogComponent, {
        data: { workflowId: id, currentVersion: this.workflow()?.version ?? 0, readonly: this.readonly() },
        width: '640px',
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((restore) => {
        const def = this.definition();
        if (!restore || !def) return;
        this.selection.set(EMPTY_SELECTION);
        this.changed({ ...restore.definition, name: def.name });
        this.showNotice($localize`:@@workfloweditor.history.restored:Відновлено версію ${restore.version}:version: — збережеться як нова версія.`);
      });
  }

  /** «Опублікувати» (F6-10): визначення в редакторі — те саме, оновлюються лише метадані. */
  protected published(detail: WorkflowDetail): void {
    const def = this.definition();
    this.workflow.set({ ...detail, definition: def ?? detail.definition });
    this.showNotice($localize`:@@workfloweditor.published:Опубліковано версію ${detail.publishedVersion}:version: — тригери виконують її.`);
  }

  /** «Скасувати зміни» (F6-10): чернетка — опублікована версія; без автозбереження, бо нічого не змінено. */
  protected draftDiscarded(detail: WorkflowDetail): void {
    this.autosave.reset();
    this.selection.set(EMPTY_SELECTION);
    this.workflow.set(detail);
    this.definition.set(detail.definition);
    this.history.reset(detail.definition);
    this.showNotice($localize`:@@workfloweditor.discarded:Чернетку повернуто до опублікованої версії ${detail.publishedVersion}:version:.`);
  }

  /** «Гарячі клавіші» (F3-90): довідка, `?`. */
  protected openShortcuts(): void {
    this.dialog.open(ShortcutsDialogComponent, { width: '720px', maxWidth: '95vw' });
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

  protected readonly toolbarRun = computed((): ToolbarRun | null => {
    const run = this.testRun();
    return run ? { state: run.state, label: this.runStateLabel(run) } : null;
  });

  protected toolbarAction(action: ToolbarAction): void {
    switch (action) {
      case 'undo': return this.undo();
      case 'redo': return this.redo();
      case 'copy': return this.copy();
      case 'paste': return this.paste();
      case 'delete': return this.deleteSelected();
      case 'assistant': return this.toggleAssistant();
      case 'history': return this.openHistory();
      case 'settings': return this.openSettings();
      case 'shortcuts': return this.openShortcuts();
      case 'run': return this.runWorkflow();
    }
  }

  /** Перейменування в шапці (F3-75): звичайна правка визначення, тож undo/redo й автозбереження працюють як завжди. */
  protected renameWorkflow(name: string): void {
    const def = this.definition();
    if (def) this.changed({ ...def, name });
  }

  protected runStateLabel(run: TestRun): string {
    if (run.error && run.state !== 'error' && run.state !== 'timed_out') return run.error;
    switch (run.state) {
      case 'queued':
        return $localize`:@@workfloweditor.run.queued:У черзі`;
      case 'running':
        return $localize`:@@workfloweditor.run.running:Виконується`;
      case 'waiting':
        return $localize`:@@workfloweditor.run.waiting:Очікує: вузол «Очікування» продовжить виконання в призначений час`;
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

  /** Клік по полю входу: вираз — у поле параметра, що останнім мало фокус; у вкладках — показати параметри. */
  protected mapField(pick: DataFieldPick): void {
    const inserted = this.expressionTargets.insert(this.inputExpression()(pick.path));
    this.mapHint.set(!inserted);
    if (inserted && !this.wide()) this.panelTab.set('params');
  }

  /** Перетягування лівої межі панелі; ширина запам'ятовується після відпускання. */
  protected startResize(event: PointerEvent): void {
    if (event.button !== 0) return;
    event.preventDefault();
    const startX = event.clientX;
    const start = this.panelWidth();
    const move = (e: PointerEvent) => this.setPanelWidth(start + startX - e.clientX, false);
    const up = () => {
      document.removeEventListener('pointermove', move);
      document.removeEventListener('pointerup', up);
      writePanelWidth(this.panelWidth());
    };
    document.addEventListener('pointermove', move);
    document.addEventListener('pointerup', up);
  }

  /** Стрілки на межі панелі: ширше/вужче на 40 px. */
  protected resizeKey(event: KeyboardEvent): void {
    const delta = event.key === 'ArrowLeft' ? 40 : event.key === 'ArrowRight' ? -40 : 0;
    if (!delta) return;
    event.preventDefault();
    this.setPanelWidth(this.panelWidth() + delta, true);
  }

  private setPanelWidth(width: number, save: boolean): void {
    // Панель ділить місце з канвасом; не виміряно (немає розмітки) — вікно.
    const canvas = this.canvasArea()?.nativeElement.getBoundingClientRect().width ?? 0;
    const shared = canvas > 0 ? canvas + this.panelWidth() : window.innerWidth;
    this.panelWidth.set(Math.round(clamp(width, MIN_PANEL_WIDTH, shared - CANVAS_RESERVE)));
    if (save) writePanelWidth(this.panelWidth());
  }

  /** Оцінки якості (F6-17) — для вузлів ШІ. */
  protected evaluable(type: string): boolean {
    return type === 'ai.llm' || type === 'ai.agent';
  }

  protected stepStatusLabel(status: StepStatus): string {
    switch (status) {
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
  }

  /** Items виходу `port` вузла: успішний крок останнього запуску, інакше `pinData` (лише вихід 0). */
  private outputItems(nodeId: string, port: number): ExecutionItem[] | undefined {
    const step = this.testRun()?.steps.get(nodeId);
    if (step?.status === 'success') return step.output[port];
    const pinned = this.definition()?.pinData?.[nodeId];
    return port === 0 && Array.isArray(pinned) ? (pinned as ExecutionItem[]) : undefined;
  }

  /** Усі виходи вузла з даними — коли вхід береться з обраного попередника. */
  private outputPortsOf(nodeId: string): DataPort[] {
    const node = this.definition()?.nodes.find((n) => n.id === nodeId);
    const names = node ? (this.descriptorIndex().find(node.type, node.typeVersion)?.outputs ?? []) : [];
    const step = this.testRun()?.steps.get(nodeId);
    if (step?.status === 'success') return portsOf(step.output, names);
    const pinned = this.outputItems(nodeId, 0);
    return pinned ? [{ label: names[0] ?? 'main', items: pinned }] : [];
  }

  /** Гарячі клавіші редактора; у полях введення не перехоплюються. */
  protected setView(view: EditorView): void {
    this.view.set(view);
    writeEditorView(view, !this.embedded());
    if (view === 'business') this.quickAdd.set(null);
  }

  /** Клік на крок бізнес-вигляду: технічний вигляд з цим вузлом виділеним і відкритою панеллю. */
  protected openStep(nodeId: string): void {
    this.setView('technical');
    this.selection.set({ nodes: [nodeId], edges: [] });
  }

  protected keydown(event: KeyboardEvent): void {
    // У Shadow DOM (`<openbp-canvas>`) `event.target` — сам елемент; справжня ціль — перша в composedPath.
    if (event.key === 'Escape' && this.escapePressed(event)) return;
    if (!this.definition() || this.readonly() || isEditable(event.composedPath()[0] ?? event.target)) return;
    const mod = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    // Перехоплюємо лише доступну дію — інакше працює стандартна поведінка браузера (копіювання тексту тощо).
    let action: (() => void) | null = null;
    if (mod && key === 'z' && !event.shiftKey) action = this.canUndo() ? () => this.undo() : null;
    else if (mod && ((key === 'z' && event.shiftKey) || key === 'y')) action = this.canRedo() ? () => this.redo() : null;
    // У бізнес-вигляді структура не змінюється — з клавіш діють лише скасувати й повторити.
    else if (this.view() === 'business') action = null;
    else if (mod && key === 'c') action = this.hasNodeSelection() ? () => this.copy() : null;
    else if (mod && key === 'v') action = this.canPaste() ? () => this.paste() : null;
    else if (!mod && (event.key === 'Delete' || event.key === 'Backspace')) action = this.hasSelection() ? () => this.deleteSelected() : null;
    else if (!mod && !event.altKey && !event.shiftKey && (event.key === 'Tab' || key === 'n') && onCanvas(event.composedPath()[0] ?? event.target))
      action = this.quickAdd() ? null : () => this.quickAddFromKeyboard();
    else if (!mod && !event.altKey && key === 'p') {
      const node = this.selectedNode();
      action = node ? () => this.togglePin(node.id) : null;
    }
    else if (!mod && !event.altKey && event.key === 'F2') {
      const node = this.selectedNode();
      action = node ? () => this.headRenameRequest.update((n) => n + 1) : null;
    } else if (!mod && !event.altKey && !event.shiftKey && key === 'd') action = this.hasNodeSelection() ? () => this.toggleNodes(this.selection().nodes) : null;
    else if (mod && key === 'd') action = this.hasNodeSelection() ? () => this.duplicate(this.selection().nodes) : null;
    else if (mod && key === 'a') action = () => this.selectAll();
    else if (!mod && !event.altKey && event.shiftKey && key === 's') action = () => this.addNoteAt(this.canvas()?.viewportCenter() ?? [0, 0]);
    else if (event.key === '?' && !mod && !event.altKey) action = () => this.openShortcuts();
    else if (zoomKey(event) && onCanvas(event.composedPath()[0] ?? event.target)) {
      const zoom = zoomKey(event)!;
      action = () => this.canvas()?.zoom(zoom);
    } else if (mod && event.key === 'Enter') action = this.running() || !this.definition()?.nodes.length ? null : () => this.runWorkflow();
    else if (!mod && !event.altKey && !event.shiftKey && ARROWS[event.key]) {
      const direction = ARROWS[event.key];
      action = this.selectedNode() ? () => this.moveSelection(direction) : null;
    }
    if (!action) return;
    event.preventDefault();
    action();
  }

  /**
   * Escape (F3-88): у полі введення — лише знімає з нього фокус; поза ним закриває панель вузла. Відкрите меню, діалог, список
   * чи редактор виразів обробляють Escape самі. `true` — подія оброблена.
   */
  private escapePressed(event: KeyboardEvent): boolean {
    const target = event.composedPath()[0] ?? event.target;
    if (event.defaultPrevented || !this.selectedNode() || this.view() === 'business' || this.quickAdd() || this.contextMenu() || this.renaming()) return false;
    const element = target instanceof HTMLElement ? target : null;
    if (element?.closest('.cdk-overlay-container, .monaco-editor, [role="menu"], [role="listbox"]')) return false;
    if (element && (['INPUT', 'TEXTAREA', 'SELECT'].includes(element.tagName) || element.isContentEditable)) {
      element.blur();
      event.preventDefault();
      return true;
    }
    event.preventDefault();
    this.closePanel();
    return true;
  }

  /** Показує стан історії й ставить його на збереження; виділення лишається лише на наявних елементах. */
  private apply(definition: WorkflowDefinition): void {
    this.definition.set(definition);
    this.lastRunStale.set(true);
    const ids = new Set(definition.nodes.map((n) => n.id));
    const noteIds = new Set((definition.notes ?? []).map((n) => n.id));
    const selection = this.selection();
    const nodes = selection.nodes.filter((id) => ids.has(id));
    const edges = selection.edges.filter((i) => i < definition.connections.length);
    const notes = (selection.notes ?? []).filter((id) => noteIds.has(id));
    if (nodes.length !== selection.nodes.length || edges.length !== selection.edges.length || notes.length !== (selection.notes?.length ?? 0)) {
      this.selection.set(notes.length ? { nodes, edges, notes } : { nodes, edges });
    }
    const editing = this.editingNote();
    if (editing && !noteIds.has(editing)) this.editingNote.set(null);
    const id = this.workflow()?.id;
    if (id) this.autosave.schedule({ id, definition });
  }

  /** Назва групи — з `GET /workflow-groups`; помилка не заважає редактору (назва просто не показується). */
  /** Останнє виконання сценарію (F3-76): значки на вузлах і підпис у шапці; помилка завантаження не заважає редактору. */
  private loadLastRun(workflowId: string): void {
    this.executions
      .list({ workflowId }, 1, 1)
      .pipe(
        switchMap((page) => (page.items[0] ? this.executions.get(page.items[0].id) : of(null))),
        catchError(() => of(null)),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((detail) => {
        if (detail && this.workflow()?.id === workflowId && !this.lastRunStale()) this.lastRun.set(lastRunFromDetail(detail));
      });
  }

  /** Ключ збереження положення полотна: id сценарію; старі версії (`?version=N`) і вбудований режим не запам'ятовуються. */
  protected readonly viewportKey = computed(() => (this.version() || this.embedded() ? null : (this.workflow()?.id ?? null)));

  private loadGroupName(workflow: WorkflowDetail): void {
    const groupId = workflow.groupId;
    if (!groupId) return;
    this.workflows
      .listGroups()
      .pipe(
        catchError(() => of([])),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((groups) => {
        if (this.workflow()?.id === workflow.id) this.groupName.set(groups.find((g) => g.id === groupId)?.name ?? null);
      });
  }

  /** Завантажує конектори для палітри; повторюється кнопкою «Повторити» біля помилки. */
  protected reloadServers(): void {
    this.toolsError.set(null);
    this.loadServers()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((servers) => this.servers.set(servers));
  }

  /**
   * Конектори з закешованими tools і каталог (F3-96); помилка не ламає редактор — палітра лишається з `core.*`.
   * Каталог недоступний (немає прав) — палітра показує лише встановлені конектори.
   */
  private loadServers(): Observable<PaletteServer[]> {
    const catalog$ = this.connectors.catalog().pipe(
      catchError(() => of([] as ConnectorCatalogEntry[])),
      tap((catalog) => this.catalog.set(catalog)),
    );
    return forkJoin([this.connectors.list(), catalog$]).pipe(
      // Підключення зі статусом помилки перевіряються при відкритті палітри (F3-100): збій лишає статус `error`.
      switchMap(([page]) => {
        const failed = page.items.filter((c) => c.status === 'error');
        if (failed.length === 0) return of(page.items);
        return forkJoin(failed.map((c) => this.connectors.refreshTools(c.id).pipe(catchError(() => of(null))))).pipe(
          map((checked) => page.items.map((c) => checked.find((d) => d?.summary.id === c.id)?.summary ?? c)),
        );
      }),
      switchMap((items): Observable<PaletteServer[]> => {
        this.noConnectors.set(items.length === 0);
        this.connectionList.set(items);
        const withTools = items.filter((c) => c.toolCount > 0);
        if (withTools.length === 0) return of([]);
        return forkJoin(withTools.map((c) => this.connectors.get(c.id))).pipe(
          map((details) => details.map((d): PaletteServer => ({ id: d.summary.id, name: d.summary.name, tools: d.tools, catalogId: d.summary.catalogId ?? null }))),
        );
      }),
      catchError((e: unknown) => {
        this.toolsError.set($localize`:@@workfloweditor.toolsError:Tools MCP-серверів недоступні: ${apiErrorMessage(e)}:error:`);
        return of([]);
      }),
    );
  }
}

/** Прибирає `resourceUri` вузла `mcp.subscribe`. */
function clearResource(definition: WorkflowDefinition, nodeId: string): WorkflowDefinition {
  return {
    ...definition,
    nodes: definition.nodes.map((n) => {
      if (n.id !== nodeId) return n;
      const parameters = { ...(n.parameters ?? {}) } as Record<string, unknown>;
      delete parameters['resourceUri'];
      return { ...n, parameters };
    }),
  };
}

/** Фокус ніде (`body`) або на канвасі: тоді `Tab` відкриває палітру, а не переводить фокус між кнопками. */
/** Стрілки → напрямок переходу між вузлами (F3-58). */
const ARROWS: Record<string, NeighborDirection> = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };

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
  // Панель вузла (форма, дані входу/виходу, межа ширини) — клавіші належать їй, а не канвасу.
  if (target.closest('app-node-panel, .editor__panel')) return true;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}

/** Порти з назвами з дескриптора; порт одразу за описаними — `error` вузла з `continueOnFail` (docs/workflow-format.md). */
function portsOf(ports: ExecutionItem[][], names: readonly string[]): DataPort[] {
  return ports.map((items, i) => ({ label: names[i] ?? (i === names.length ? 'error' : `#${i}`), items }));
}

function readPanelWidth(): number {
  try {
    const value = Number(localStorage.getItem(PANEL_WIDTH_KEY));
    return Number.isFinite(value) && value > 0 ? value : DEFAULT_PANEL_WIDTH;
  } catch {
    return DEFAULT_PANEL_WIDTH;
  }
}

function writePanelWidth(width: number): void {
  try {
    localStorage.setItem(PANEL_WIDTH_KEY, String(width));
  } catch {
    // Сховище недоступне — ширина не запам'ятається.
  }
}
