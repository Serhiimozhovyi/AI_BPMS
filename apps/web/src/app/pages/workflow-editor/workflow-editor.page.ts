import { BreakpointObserver } from '@angular/cdk/layout';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { map } from 'rxjs';
import { MOBILE_QUERY } from '../../layout/breakpoints';
import { WorkflowEditorComponent } from '../../workflow-editor/workflow-editor.component';

/**
 * `/workflows/:id[?version=N]` — {@link WorkflowEditorComponent} на всю висоту під шапкою; `:id` змінюється без перестворення.
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
      <app-workflow-editor [workflowId]="id()" [version]="version()" [readonly]="version() !== null || narrow()" />
    </div>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      /* 106px — шапка AC-Kit: 60px верхній рядок + 46px навігація (app.scss); на телефоні навігації в шапці немає. */
      height: calc(100vh - 106px);
    }

    .editor {
      flex: 1;
      min-height: 0;
    }

    .narrow-hint {
      margin: 0;
      padding: 8px 16px;
      background: var(--ac-button);
      color: var(--ac-vivid);
      font: 500 14px/20px Ubuntu, sans-serif;
    }

    @media (max-width: 767.98px) {
      :host {
        height: calc(100vh - 60px);
      }
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
  protected readonly version = toSignal(this.route.queryParamMap.pipe(map((q) => (q.get('version') ? Number(q.get('version')) || null : null))), { initialValue: null });
}
