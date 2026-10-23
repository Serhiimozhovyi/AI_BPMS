import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, catchError, forkJoin, map, of, switchMap, tap } from 'rxjs';
import { Autosave } from '../../canvas/autosave';
import { CanvasSelection, EMPTY_SELECTION, copySelection, deleteSelection, pasteClipboard } from '../../canvas/canvas-edit';
import { DescriptorIndex } from '../../canvas/canvas-model';
import { CanvasComponent } from '../../canvas/canvas.component';
import { CanvasClipboardService } from '../../canvas/clipboard.service';
import { EditHistory } from '../../canvas/edit-history';
import { PaletteItem, PaletteServer, addNode, buildPalette, randomNodeId } from '../../canvas/palette';
import { PaletteComponent } from '../../canvas/palette.component';
import { ConnectorsApiService, McpToolInfo, apiErrorMessage } from '../../connectors/connectors-api.service';
import { MCP_TOOL_TYPES, updateNode } from '../../node-form/node-form';
import { NodePanelComponent } from '../../node-form/node-panel.component';
import { NodeTypeInfo, NodesApiService } from '../../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowDetail, WorkflowNode, WorkflowsApiService } from '../../workflows/workflows-api.service';

/** Що зберігає автозбереження: визначення разом з id сценарію, якому воно належить. */
interface SaveRequest {
  id: string;
  definition: WorkflowDefinition;
}

/**
 * `/workflows/:id` — редактор сценарію: сценарій з Api на канвасі, палітра `core.*` і tools MCP-серверів (F2-16);
 * з'єднання портів, видалення, копіювання/вставка, переміщення, undo/redo і автозбереження кожної зміни в нову
 * версію з помилками валідації на вузлах (F2-17); панель з формою виділеного вузла (F2-18).
 */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '(document:keydown)': 'keydown($event)' },
  imports: [RouterLink, MatButtonModule, MatProgressBarModule, CanvasComponent, PaletteComponent, NodePanelComponent],
  template: `
    <section class="editor">
      <header class="editor__bar">
        <a mat-button routerLink="/workflows" i18n="@@workfloweditor.back">← Сценарії</a>
        <h1 class="mat-font-title-md m-0" data-testid="workflow-name">
          {{ workflow()?.name ?? defaultTitle }}
        </h1>
        @if (definition(); as def) {
          <span class="editor__count" data-testid="counts" i18n="@@workfloweditor.counts">
            вузлів: {{ def.nodes.length }} · ребер: {{ def.connections.length }}
          </span>
        }
        @if (definition()) {
          <span class="editor__tools">
            <button mat-button type="button" data-testid="undo" [disabled]="!canUndo()" (click)="undo()" i18n-title="@@workfloweditor.undo.hint" title="Скасувати (Ctrl+Z)" i18n="@@workfloweditor.undo">Скасувати</button>
            <button mat-button type="button" data-testid="redo" [disabled]="!canRedo()" (click)="redo()" i18n-title="@@workfloweditor.redo.hint" title="Повторити (Ctrl+Shift+Z)" i18n="@@workfloweditor.redo">Повторити</button>
            <button mat-button type="button" data-testid="copy" [disabled]="!hasNodeSelection()" (click)="copy()" i18n-title="@@workfloweditor.copy.hint" title="Копіювати (Ctrl+C)" i18n="@@workfloweditor.copy">Копіювати</button>
            <button mat-button type="button" data-testid="paste" [disabled]="!canPaste()" (click)="paste()" i18n-title="@@workfloweditor.paste.hint" title="Вставити (Ctrl+V)" i18n="@@workfloweditor.paste">Вставити</button>
            <button mat-button type="button" data-testid="delete" [disabled]="!hasSelection()" (click)="deleteSelected()" i18n-title="@@workfloweditor.delete.hint" title="Видалити (Delete)" i18n="@@workfloweditor.delete">Видалити</button>
          </span>
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
          <aside class="editor__palette">
            @if (toolsError()) {
              <p role="status" class="editor__warning" data-testid="tools-error">{{ toolsError() }}</p>
            }
            <app-palette [sections]="palette()" (pick)="add($event)" />
          </aside>
          <div class="editor__canvas">
            <app-canvas
              [definition]="def"
              [descriptors]="descriptors()"
              [errors]="validationErrors()"
              [selection]="selection()"
              (definitionChange)="changed($event)"
              (selectionChange)="selection.set($event)"
            />
          </div>
          @if (selectedNode(); as node) {
            <aside class="editor__panel">
              <app-node-panel [node]="node" [descriptor]="selectedDescriptor()" [tool]="selectedTool()" (nodeChange)="nodeEdited($event)" />
            </aside>
          }
        </div>
      }
    </section>
  `,
  styles: `
    .editor {
      display: flex;
      flex-direction: column;
      height: calc(100vh - 64px);
    }

    .editor__bar {
      display: flex;
      align-items: center;
      gap: 16px;
      padding: 8px 16px;
      border-bottom: 1px solid var(--mat-sys-outline-variant);
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
      width: 260px;
      border-right: 1px solid var(--mat-sys-outline-variant);
      overflow-y: auto;
    }

    .editor__warning {
      margin: 12px 12px 0;
      font-size: 12px;
      color: var(--mat-sys-error);
    }

    .editor__canvas {
      flex: 1;
      min-width: 0;
    }

    .editor__panel {
      width: 360px;
      border-left: 1px solid var(--mat-sys-outline-variant);
      overflow-y: auto;
    }
  `,
})
export class WorkflowEditorPage implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly workflows = inject(WorkflowsApiService);
  private readonly nodes = inject(NodesApiService);
  private readonly connectors = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly clipboard = inject(CanvasClipboardService);

  protected readonly defaultTitle = $localize`:@@workfloweditor.title:Редактор сценарію`;

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly toolsError = signal<string | null>(null);
  protected readonly workflow = signal<WorkflowDetail | null>(null);
  protected readonly definition = signal<WorkflowDefinition | null>(null);
  protected readonly descriptors = signal<NodeTypeInfo[]>([]);
  private readonly servers = signal<PaletteServer[]>([]);
  protected readonly selection = signal<CanvasSelection>(EMPTY_SELECTION);

  private readonly history = new EditHistory<WorkflowDefinition | null>(null);
  private readonly autosave = new Autosave<SaveRequest, WorkflowDetail>(
    (r) => this.workflows.save(r.id, r.definition),
    (saved, r) => {
      // Відповідь могла прийти вже після переходу на інший сценарій. Визначення не підміняємо відповіддю —
      // користувач міг змінити його під час запиту; оновлюємо лише метадані (версію).
      if (this.workflow()?.id === r.id) this.workflow.set({ ...saved, definition: r.definition });
    },
  );

  protected readonly palette = computed(() => buildPalette(this.descriptors(), this.servers()));
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

  constructor() {
    // Незбережені зміни не губляться при виході з редактора: відправляємо їх одразу.
    this.destroyRef.onDestroy(() => this.autosave.flush());
  }

  ngOnInit(): void {
    // Компонент перевикористовується при переході між сценаріями — перезавантажуємо за кожним :id.
    this.route.paramMap
      .pipe(
        map((p) => p.get('id') ?? ''),
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
        }),
        switchMap((id) =>
          forkJoin({ workflow: this.workflows.get(id), descriptors: this.nodes.list() }).pipe(
            catchError((e: unknown) => {
              this.error.set(apiErrorMessage(e));
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
  }

  protected add(item: PaletteItem): void {
    const def = this.definition();
    if (!def) return;
    this.changed(addNode(def, item));
  }

  /** Зміна вузла з панелі. */
  protected nodeEdited(node: WorkflowNode): void {
    const def = this.definition();
    if (def) this.changed(updateNode(def, node));
  }

  /** Зміна від користувача: новий крок історії й автозбереження. */
  protected changed(definition: WorkflowDefinition): void {
    if (definition === this.definition()) return;
    this.history.push(definition);
    this.apply(definition);
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

  /** Гарячі клавіші редактора; у полях введення не перехоплюються. */
  protected keydown(event: KeyboardEvent): void {
    if (!this.definition() || isEditable(event.target)) return;
    const mod = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();
    // Перехоплюємо лише доступну дію — інакше працює стандартна поведінка браузера (копіювання тексту тощо).
    let action: (() => void) | null = null;
    if (mod && key === 'z' && !event.shiftKey) action = this.canUndo() ? () => this.undo() : null;
    else if (mod && ((key === 'z' && event.shiftKey) || key === 'y')) action = this.canRedo() ? () => this.redo() : null;
    else if (mod && key === 'c') action = this.hasNodeSelection() ? () => this.copy() : null;
    else if (mod && key === 'v') action = this.canPaste() ? () => this.paste() : null;
    else if (!mod && (event.key === 'Delete' || event.key === 'Backspace')) action = this.hasSelection() ? () => this.deleteSelected() : null;
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

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  // Панель вузла (прапорці, списки, кнопки) — клавіші належать формі, а не канвасу.
  if (target.closest('app-node-panel')) return true;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}
