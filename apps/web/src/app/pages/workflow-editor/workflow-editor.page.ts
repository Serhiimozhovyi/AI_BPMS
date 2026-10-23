import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { Observable, catchError, forkJoin, map, of, switchMap, tap } from 'rxjs';
import { CanvasComponent } from '../../canvas/canvas.component';
import { PaletteItem, PaletteServer, addNode, buildPalette } from '../../canvas/palette';
import { PaletteComponent } from '../../canvas/palette.component';
import { ConnectorsApiService, apiErrorMessage } from '../../connectors/connectors-api.service';
import { NodeTypeInfo, NodesApiService } from '../../nodes/nodes-api.service';
import { WorkflowDefinition, WorkflowDetail, WorkflowsApiService } from '../../workflows/workflows-api.service';

/**
 * `/workflows/:id` — редактор сценарію (F2-16): сценарій з Api на канвасі, палітра `core.*` і tools
 * MCP-серверів, додавання вузла. Збереження змін — автозбереження F2-17.
 */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, MatButtonModule, MatProgressBarModule, CanvasComponent, PaletteComponent],
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
        @if (dirty()) {
          <span class="editor__dirty" data-testid="dirty" i18n="@@workfloweditor.dirty">Є незбережені зміни</span>
        }
      </header>
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
            <app-canvas [definition]="def" [descriptors]="descriptors()" (definitionChange)="changed($event)" />
          </div>
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
  `,
})
export class WorkflowEditorPage implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly workflows = inject(WorkflowsApiService);
  private readonly nodes = inject(NodesApiService);
  private readonly connectors = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly defaultTitle = $localize`:@@workfloweditor.title:Редактор сценарію`;

  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly toolsError = signal<string | null>(null);
  protected readonly workflow = signal<WorkflowDetail | null>(null);
  protected readonly definition = signal<WorkflowDefinition | null>(null);
  protected readonly descriptors = signal<NodeTypeInfo[]>([]);
  private readonly servers = signal<PaletteServer[]>([]);
  protected readonly dirty = signal(false);

  protected readonly palette = computed(() => buildPalette(this.descriptors(), this.servers()));

  ngOnInit(): void {
    // Компонент перевикористовується при переході між сценаріями — перезавантажуємо за кожним :id.
    this.route.paramMap
      .pipe(
        map((p) => p.get('id') ?? ''),
        tap(() => {
          this.loading.set(true);
          this.error.set(null);
          this.workflow.set(null);
          this.definition.set(null);
          this.dirty.set(false);
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

  protected changed(definition: WorkflowDefinition): void {
    this.definition.set(definition);
    this.dirty.set(true);
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
