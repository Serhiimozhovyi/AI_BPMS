import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-workflow-editor-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@workfloweditor.title">Редактор сценарію</h1>
      <p class="opacity-70" i18n="@@workfloweditor.placeholder">Канвас з’явиться після спайку F1-11.</p>
    </section>
  `,
})
export class WorkflowEditorPage {}
