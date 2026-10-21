import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-executions-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@executions.title">Виконання</h1>
      <p class="opacity-70" i18n="@@executions.placeholder">Журнал виконань з’явиться у фазі 3.</p>
    </section>
  `,
})
export class ExecutionsPage {}
