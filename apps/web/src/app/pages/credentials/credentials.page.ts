import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-credentials-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@credentials.title">Облікові дані</h1>
      <p class="opacity-70" i18n="@@credentials.placeholder">Керування credentials з’явиться у фазі 4.</p>
    </section>
  `,
})
export class CredentialsPage {}
