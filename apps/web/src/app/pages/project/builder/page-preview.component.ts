import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { BADGE_CLASSES, DisplayColumn, cellView } from '../../../projects/page-format';
import { PagePreview, PageRow } from '../../../projects/project-pages-api.service';
import { PageCalendarComponent } from '../page-calendar.component';
import { PageDashboardComponent } from '../page-dashboard.component';
import { PageFormComponent } from '../page-form.component';
import { PageMetricsComponent } from '../page-metrics.component';

/**
 * Перегляд чернетки сторінки на даних проєкту (F8-19): те, що повертає `POST …/preview`, малюється тими самими
 * компонентами, що й справжня сторінка, але без запитів, дій і збереження.
 */
@Component({
  selector: 'app-page-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [PageCalendarComponent, PageDashboardComponent, PageFormComponent, PageMetricsComponent],
  template: `
    @let v = data().view;
    <div class="pointer-events-auto" data-testid="preview" [attr.data-kind]="v.page.kind">
      @switch (v.page.kind) {
        @case ('list') {
          @if (rows().length === 0) {
            <p class="opacity-70" data-testid="preview-empty">{{ v.page.definition.empty || emptyText }}</p>
          } @else if (v.page.definition.layout === 'cards') {
            <ul class="grid gap-2" data-testid="preview-cards">
              @for (row of rows(); track row.key) {
                <li class="rounded border border-ac-line p-3">
                  <div class="font-medium">{{ titleOf(row) }}</div>
                  @if (subtitleOf(row)) {
                    <div class="opacity-70">{{ subtitleOf(row) }}</div>
                  }
                  @for (c of cardColumns(); track c.column) {
                    <div class="text-sm">
                      <span class="opacity-60">{{ c.label }}:</span>
                      {{ cell(row, c).text }}
                    </div>
                  }
                </li>
              }
            </ul>
          } @else {
            <table class="w-full text-left" data-testid="preview-table">
              <thead>
                <tr>
                  @for (c of columns(); track c.column) {
                    <th class="border-b p-1">{{ c.label }}</th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (row of rows(); track row.key) {
                  <tr>
                    @for (c of columns(); track c.column) {
                      <td class="border-b p-1">
                        @let cv = cell(row, c);
                        @if (cv.color) {
                          <span class="rounded px-2 py-0.5 text-sm" [class]="badge(cv.color)">{{ cv.text }}</span>
                        } @else {
                          {{ cv.text }}
                        }
                      </td>
                    }
                  </tr>
                }
              </tbody>
            </table>
          }
        }
        @case ('metrics') {
          @if (data().metrics; as m) {
            <app-page-metrics [metrics]="m" />
          }
        }
        @case ('calendar') {
          @if (data().events; as e) {
            <app-page-calendar projectId="" [view]="v" [preview]="e" />
          }
        }
        @case ('dashboard') {
          @if (data().dashboard; as d) {
            <app-page-dashboard projectId="" [view]="v" [preview]="d" />
          }
        }
        @case ('form') {
          <app-page-form projectId="" [view]="v" [preview]="true" />
        }
        @default {
          <dl class="grid grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-1" data-testid="preview-detail">
            @for (c of v.columns; track c.column) {
              <dt class="opacity-70">{{ c.label }}</dt>
              <dd>…</dd>
            }
          </dl>
        }
      }
    </div>
  `,
})
export class PagePreviewComponent {
  readonly data = input.required<PagePreview>();

  protected readonly emptyText = $localize`:@@preview.empty:Записів немає.`;
  protected readonly rows = computed<PageRow[]>(() => this.data().rows?.items ?? []);

  protected readonly columns = computed<DisplayColumn[]>(() => {
    const v = this.data().view;
    const def = v.page.definition;
    return (def.columns ?? []).map((dc) => {
      const computedColumn = def.computed?.find((x) => x.key === dc.column);
      if (computedColumn) return { column: computedColumn.key, label: dc.label ?? computedColumn.label, type: 'number', format: dc.format, labels: dc.labels, computed: computedColumn };
      const vc = v.columns.find((c) => c.column === dc.column);
      return { column: dc.column, label: dc.label ?? vc?.label ?? dc.column, type: vc?.type ?? 'string', format: dc.format ?? vc?.format, labels: dc.labels, colors: dc.colors };
    });
  });

  private readonly titleColumn = computed(() => this.data().view.page.definition.card?.title ?? this.columns()[0]?.column);
  protected readonly cardColumns = computed(() => {
    const card = this.data().view.page.definition.card;
    return this.columns().filter((c) => c.column !== this.titleColumn() && c.column !== card?.subtitle);
  });

  protected cell(row: PageRow, column: DisplayColumn) {
    return cellView(row, column, this.data().view.timezone ?? 'UTC');
  }

  protected titleOf(row: PageRow): string {
    const c = this.columns().find((x) => x.column === this.titleColumn());
    return c ? this.cell(row, c).text : '';
  }

  protected subtitleOf(row: PageRow): string {
    const c = this.columns().find((x) => x.column === this.data().view.page.definition.card?.subtitle);
    return c ? this.cell(row, c).text : '';
  }

  protected badge(color: keyof typeof BADGE_CLASSES): string {
    return BADGE_CLASSES[color];
  }
}
