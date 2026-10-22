import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { map } from 'rxjs';
import { demoGraph, largeGraph } from '../../canvas/spike-graph';
import { VflowCanvasComponent } from '../../canvas/vflow/vflow-canvas.component';

/**
 * Редактор сценарію. До F2-F показує прототип канвасу з ADR-003 на демо-графі;
 * `?n=200` вмикає згенерований граф для перевірки продуктивності, `?virt=0` вимикає віртуалізацію.
 */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, VflowCanvasComponent],
  template: `
    <section class="editor">
      <header class="editor__bar">
        <h1 class="mat-font-title-md m-0" i18n="@@workfloweditor.title">Редактор сценарію</h1>
        <nav class="editor__spike" aria-label="Прототип канвасу" i18n-aria-label="@@workfloweditor.spike.label">
          <a routerLink="." [class.active]="nodeCount() === 0" i18n="@@workfloweditor.spike.demo">3 вузли</a>
          <a routerLink="." [queryParams]="{ n: 200 }" [class.active]="nodeCount() === 200" i18n="@@workfloweditor.spike.large">200 вузлів</a>
          <span class="editor__count">{{ graph().nodes.length }} / {{ graph().edges.length }}</span>
        </nav>
      </header>
      <div class="editor__canvas">
        <app-vflow-canvas [graph]="graph()" [virtualization]="virtualization()" />
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

    .editor__count {
      margin-left: 8px;
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
  private readonly params = toSignal(
    this.route.queryParamMap.pipe(map((p) => ({ n: p.get('n'), virt: p.get('virt') }))),
    { initialValue: { n: null, virt: null } },
  );

  protected readonly nodeCount = computed(() => Number(this.params().n) || 0);
  protected readonly virtualization = computed(() => this.params().virt !== '0');
  protected readonly graph = computed(() => (this.nodeCount() > 0 ? largeGraph(this.nodeCount()) : demoGraph()));
}
