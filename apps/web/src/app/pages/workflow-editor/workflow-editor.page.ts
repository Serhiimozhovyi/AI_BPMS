import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { ReteCanvasComponent } from '../../canvas/rete/rete-canvas.component';
import { demoGraph, largeGraph } from '../../canvas/spike-graph';
import { VflowCanvasComponent } from '../../canvas/vflow/vflow-canvas.component';

type Engine = 'vflow' | 'rete';

/**
 * Редактор сценарію. На час спайку F1-11 перемикає прототипи канвасу query-параметрами:
 * `?engine=vflow|rete`, `?n=200` (кількість вузлів), `?virt=1` (віртуалізація ngx-vflow).
 */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, VflowCanvasComponent, ReteCanvasComponent],
  template: `
    <section class="editor">
      <header class="editor__bar">
        <h1 class="mat-font-title-md m-0" i18n="@@workfloweditor.title">Редактор сценарію</h1>
        <nav class="editor__spike" aria-label="Прототипи канвасу" i18n-aria-label="@@workfloweditor.spike.label">
          <a routerLink="." [queryParams]="{ engine: 'vflow', n: nodeCount() }" [class.active]="engine() === 'vflow'">ngx-vflow</a>
          <a routerLink="." [queryParams]="{ engine: 'rete', n: nodeCount() }" [class.active]="engine() === 'rete'">Rete.js</a>
          <span class="editor__sep"></span>
          <a routerLink="." [queryParams]="{ engine: engine() }" [class.active]="nodeCount() === 0">3 вузли</a>
          <a routerLink="." [queryParams]="{ engine: engine(), n: 200 }" [class.active]="nodeCount() === 200">200 вузлів</a>
          <span class="editor__count">{{ graph().nodes.length }} / {{ graph().edges.length }}</span>
        </nav>
      </header>
      <div class="editor__canvas" [attr.data-engine]="engine()">
        @if (engine() === 'rete') {
          <app-rete-canvas [graph]="graph()" />
        } @else {
          <app-vflow-canvas [graph]="graph()" [virtualization]="virtualization()" />
        }
      </div>
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

    .editor__spike {
      display: flex;
      align-items: center;
      gap: 4px;
      font-size: 13px;
    }

    .editor__spike a {
      padding: 4px 10px;
      border-radius: 16px;
      text-decoration: none;
      color: inherit;
    }

    .editor__spike a.active {
      background: var(--mat-sys-secondary-container);
    }

    .editor__sep {
      width: 1px;
      height: 20px;
      margin: 0 6px;
      background: var(--mat-sys-outline-variant);
    }

    .editor__count {
      opacity: 0.6;
    }

    .editor__canvas {
      flex: 1;
      min-height: 0;
    }
  `,
})
export class WorkflowEditorPage {
  private readonly route = inject(ActivatedRoute);
  private readonly params = toSignal(this.route.queryParamMap.pipe(map((p) => ({
    engine: p.get('engine'),
    n: p.get('n'),
    virt: p.get('virt'),
  }))), { initialValue: { engine: null, n: null, virt: null } });

  protected readonly engine = computed<Engine>(() => (this.params().engine === 'rete' ? 'rete' : 'vflow'));
  protected readonly nodeCount = computed(() => Number(this.params().n) || 0);
  protected readonly virtualization = computed(() => this.params().virt === '1');
  protected readonly graph = computed(() => (this.nodeCount() > 0 ? largeGraph(this.nodeCount()) : demoGraph()));
}
