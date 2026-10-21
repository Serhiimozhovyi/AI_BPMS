import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-connectors-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@connectors.title">Конектори</h1>
      <p class="opacity-70" i18n="@@connectors.placeholder">Реєстрація MCP-серверів з’явиться у фазі 2.</p>
    </section>
  `,
})
export class ConnectorsPage {}
