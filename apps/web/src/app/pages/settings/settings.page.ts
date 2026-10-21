import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-2" i18n="@@settings.title">Налаштування</h1>
      <p class="opacity-70" i18n="@@settings.placeholder">Налаштування з’являться пізніше.</p>
    </section>
  `,
})
export class SettingsPage {}
