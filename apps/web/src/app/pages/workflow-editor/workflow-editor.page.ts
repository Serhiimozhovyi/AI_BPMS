import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { MOBILE_QUERY } from '../../layout/breakpoints';
import { WorkflowEditorComponent } from '../../workflow-editor/workflow-editor.component';

/**
 * `/workflows/:id[?version=N][&node=<id>]` — {@link WorkflowEditorComponent} на всю висоту під шапкою; `:id` змінюється без перестворення.
 * На телефоні (< 768 px) редактор лише для читання з підказкою «Редагування доступне на комп'ютері» (F3-84).
 */
@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [WorkflowEditorComponent],
  template: `
    @if (narrow()) {
      <p class="narrow-hint" role="status" data-testid="editor-desktop-hint" i18n="@@workfloweditor.desktopOnly">
        Редагування доступне на комп'ютері. Тут — перегляд лише для читання.
      </p>
    }
    <div class="editor">
      <app-workflow-editor [workflowId]="id()" [version]="version()" [focusNode]="node()" [readonly]="version() !== null || narrow()" />
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      /* 52px — одна смуга верхнього меню (app.scss, F9-04). */
      height: calc(100vh - 52px);
    }

    .editor {
      flex: 1;
      min-height: 0;
    }

    .narrow-hint {
      margin: 0;
      padding: 8px 16px;
      background: var(--bp-tonal);
      color: var(--bp-text);
      font: var(--bp-font-h7);
    }
  `,
})
export class WorkflowEditorPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly narrow = toSignal(inject(BreakpointObserver).observe(MOBILE_QUERY).pipe(map((s) => s.matches)), {
    initialValue: false,
  });
  protected readonly id = toSignal(this.route.paramMap.pipe(map((p) => p.get('id') ?? '')), { initialValue: '' });
  /** `?version=N` — переглянути стару версію лише для читання (F3-61). */
  /** `?node=<id>` — відкрити з виділеним вузлом (напр. упалим у виконанні, F3-81). */
  protected readonly node = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('node'))), { initialValue: null });
  protected readonly version = toSignal(this.route.queryParamMap.pipe(map((q) => (q.get('version') ? Number(q.get('version')) || null : null))), { initialValue: null });
}
