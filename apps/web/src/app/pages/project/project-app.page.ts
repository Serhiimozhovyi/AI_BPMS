import { ChangeDetectionStrategy, Component } from '@angular/core';

/**
 * `/projects/:id/app` — вкладка «Застосунок» (F8-03): місце для сторінок застосунку проєкту (списки, картки, форми).
 * Вони з'являться в F8-04; поки що — пояснення, щоб вкладка не була порожньою.
 */
@Component({
  selector: 'app-project-app-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="p-6">
      <p class="max-w-3xl opacity-70" data-testid="app-placeholder" i18n="@@project.app.placeholder">
        Тут з'являться сторінки застосунку проєкту — адмінка над таблицями проєкту: списки, картки записів і форми. Налаштування
        сторінок та ролей застосунку — у наступних оновленнях.
      </p>
    </section>
  `,
})
export class ProjectAppPage {}
