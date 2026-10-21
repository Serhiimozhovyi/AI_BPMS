import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-workflows-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@workflows.title">Сценарії</h1>
      <p class="opacity-70" i18n="@@workflows.placeholder">Список сценаріїв з’явиться у фазі 2.</p>
    </section>
  `,
})
export class WorkflowsPage {}
