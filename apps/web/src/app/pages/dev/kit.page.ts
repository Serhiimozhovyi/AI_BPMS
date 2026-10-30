import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { BpButtonDirective } from '../../shared/button.directive';

/**
 * Сторінка-зразок компонентів дизайн-системи (`/dev/kit`, лише в dev-збірці — маршрут реєструється під `isDevMode()`).
 * F9-02: кнопки за AC-Kit; наступні задачі фази 9 додають сюди свої секції.
 */
@Component({
  selector: 'app-dev-kit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, BpButtonDirective],
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
  `,
})
export class DevKitPage {
  protected readonly sizes = ['md', 'sm'] as const;
}
