import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { PageMetrics } from '../../projects/project-pages-api.service';

/** Плитки й діаграми сторінки `metrics` (F8-04); використовується й у блоку `tiles` головної (F8-18). */
@Component({
  selector: 'app-page-metrics',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (metrics().tiles.length) {
      <div class="mb-6 grid max-w-5xl grid-cols-[repeat(auto-fill,minmax(12rem,1fr))] gap-3" data-testid="app-tiles">
        @for (t of metrics().tiles; track $index) {
          <div class="rounded border border-ac-line p-3" [attr.data-tile]="t.key ?? $index">
            <div class="opacity-70">{{ t.label }}</div>
            <div class="text-2xl font-semibold" data-testid="tile-value">{{ number(t.value) }}</div>
          </div>
        }
      </div>
    }
    @for (chart of metrics().charts; track $index; let ci = $index) {
      <h3 class="mb-2 mt-4 font-semibold">{{ chart.label || chart.groupBy }}</h3>
      @if (chart.points.length === 0) {
        <p class="opacity-70" i18n="@@app.page.empty">Записів немає.</p>
      } @else {
        <ul class="mb-4 max-w-3xl" [attr.data-chart]="ci">
          @for (p of chart.points; track p.label) {
            <li class="grid grid-cols-[10rem_1fr_5rem] items-center gap-2 py-0.5" [attr.data-point]="p.label">
              <span class="truncate">{{ p.label }}</span>
              <span class="block h-3 rounded bg-ac-kit" [style.width.%]="barWidth(chart.points, p.value)"></span>
              <span class="text-right">{{ number(p.value) }}</span>
            </li>
          }
        </ul>
      }
    }
  `,
})
export class PageMetricsComponent {
  readonly metrics = input.required<PageMetrics>();

  protected number(value: number | null): string {
    return value === null ? '—' : new Intl.NumberFormat('uk-UA', { maximumFractionDigits: 2 }).format(value);
  }

  /** Ширина стовпчика діаграми у відсотках від найбільшого значення. */
  protected barWidth(points: { value: number | null }[], value: number | null): number {
    const max = Math.max(0, ...points.map((p) => p.value ?? 0));
    return max > 0 && value !== null && value > 0 ? Math.max(2, (value / max) * 100) : 0;
  }
}
