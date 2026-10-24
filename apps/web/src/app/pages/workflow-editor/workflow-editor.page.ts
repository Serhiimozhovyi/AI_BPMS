import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { WorkflowEditorComponent } from '../../workflow-editor/workflow-editor.component';

/** `/workflows/:id[?version=N]` — {@link WorkflowEditorComponent} на всю висоту під шапкою; `:id` змінюється без перестворення. */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [WorkflowEditorComponent],
  template: `<app-workflow-editor [workflowId]="id()" [version]="version()" [readonly]="version() !== null" />`,
  styles: `
    :host {
      display: block;
      /* 106px — шапка AC-Kit: 60px верхній рядок + 46px навігація (app.scss). */
      height: calc(100vh - 106px);
    }
  `,
})
export class WorkflowEditorPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly id = toSignal(this.route.paramMap.pipe(map((p) => p.get('id') ?? '')), { initialValue: '' });
  /** `?version=N` — переглянути стару версію лише для читання (F3-61). */
  protected readonly version = toSignal(this.route.queryParamMap.pipe(map((q) => (q.get('version') ? Number(q.get('version')) || null : null))), { initialValue: null });
}
