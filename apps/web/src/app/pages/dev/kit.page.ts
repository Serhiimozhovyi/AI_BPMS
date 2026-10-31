import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { BpButtonDirective } from '../../shared/button.directive';
import { FormDesignerComponent } from '../../forms/designer/form-designer.component';
import type { JsonSchema } from '../../forms/form-fields';

/**
 * Сторінка-зразок компонентів дизайн-системи (`/dev/kit`, лише в dev-збірці — маршрут реєструється під `isDevMode()`).
 * F9-02: кнопки за AC-Kit; наступні задачі фази 9 додають сюди свої секції. F11-02: конструктор форм.
 */
@Component({
  selector: 'app-dev-kit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, BpButtonDirective, FormDesignerComponent],
  template: `
    <section class="p-6" data-testid="kit-buttons">
      <h1 class="mat-font-headline-sm mb-4">Кнопки</h1>
      @for (size of sizes; track size) {
        <div class="mb-4 flex flex-wrap items-center gap-3" [attr.data-size]="size">
          <button mat-flat-button type="button" [bpSize]="size">Filled</button>
          <button mat-flat-button type="button" [bpSize]="size" bpVariant="tonal">Tonal</button>
          <button mat-stroked-button type="button" [bpSize]="size">Outlined</button>
          <button mat-button type="button" [bpSize]="size">Text</button>
          <button mat-flat-button type="button" [bpSize]="size" bpVariant="danger">Danger</button>
          <button mat-flat-button type="button" [bpSize]="size" disabled>Disabled</button>
          <button mat-icon-button type="button" [bpSize]="size" aria-label="Додати">
            <mat-icon>add</mat-icon>
          </button>
          <button
            mat-icon-button
            type="button"
            [bpSize]="size"
            bpVariant="close"
            aria-label="Закрити"
          >
            <mat-icon>close</mat-icon>
          </button>
        </div>
      }
    </section>
    <section class="p-6" data-testid="kit-form-designer">
      <h1 class="mat-font-headline-sm mb-4">Конструктор форм</h1>
      <bp-form-designer [schema]="formSchema" />
    </section>
  `,
})
export class DevKitPage {
  protected readonly sizes = ['md', 'sm'] as const;
  protected readonly formSchema: JsonSchema = {
    type: 'object',
    required: ['name', 'from', 'to'],
    properties: {
      name: { type: 'string', title: 'ПІБ', minLength: 2 },
      from: { type: 'string', format: 'date', title: 'З' },
      to: { type: 'string', format: 'date', title: 'По' },
      kind: { type: 'string', title: 'Вид відпустки', oneOf: [{ const: 'annual', title: 'Щорічна' }, { const: 'unpaid', title: 'За свій рахунок' }] },
      reason: { type: 'string', format: 'textarea', title: 'Причина' },
    },
    'x-openbp-layout': {
      sections: [
        { title: 'Хто і коли', rows: [[{ field: 'name' }], [{ field: 'from', width: 6 }, { field: 'to', width: 6 }]] },
        { title: 'Подробиці', collapsible: true, rows: [[{ field: 'kind' }], [{ field: 'reason' }]] },
      ],
    },
  };
}
