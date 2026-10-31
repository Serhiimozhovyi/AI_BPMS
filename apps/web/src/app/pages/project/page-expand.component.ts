import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { ChipComponent, ChipTone } from '../../shared/chip/chip.component';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { BADGE_CLASSES, BADGE_TONES, DisplayColumn, cellView } from '../../projects/page-format';
import {
  PageAction,
  PageActionResult,
  PageExpandRows,
  PageOpenMode,
  PageRelated,
  PageRow,
  ProjectPagesApiService,
} from '../../projects/project-pages-api.service';
import { BpButtonDirective } from '../../shared/button.directive';

/** Запит відкрити сторінку застосунку з підлеглого списку (картка, форма зміни чи створення). */
export interface ExpandOpen {
  page: string;
  rowKey?: string;
  mode?: PageOpenMode;
  /** Початкові значення форми створення: `by` = ключ батьківського рядка. */
  prefill?: Record<string, unknown>;
}

/**
 * Підлеглі списки під розгорнутим рядком списку (F8-16, `expand`): рядки пов'язаних таблиць з діями над ними
 * (сценарії, видалення, переходи) і кнопкою «Додати» з підставленим ключем батьківського рядка.
 * Дані й дії — `GET|POST …/rows/{rowKey}/expand/{index}` (F8-15); права й `when` дій перевіряє Api.
 */
@Component({
  selector: 'app-page-expand',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatProgressBarModule, ChipComponent],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" />
    }
    @if (error()) {
      <p role="alert" class="text-red-700" data-testid="expand-error">{{ error() }}</p>
    }
    @for (list of lists(); track $index; let i = $index) {
      @let def = expand()[i];
      <div class="mb-3" [attr.data-expand]="i">
        <div class="mb-1 flex items-center gap-3">
          <h4 class="font-semibold">{{ list.label }}</h4>
          @if (def.create; as create) {
            <button mat-stroked-button type="button" [attr.data-expand-create]="i" (click)="add(def, create.page, create.mode)" i18n="@@app.page.expandAdd" bpVariant="tonal">Додати</button>
          }
        </div>
        @if (list.rows.length === 0) {
          <p class="opacity-70" data-testid="expand-empty">{{ def.empty ?? emptyText }}</p>
        } @else {
          <table class="w-full text-left">
            <thead>
              <tr>
                @for (c of columns(i); track c.column) {
                  <th class="p-1.5 font-medium opacity-70">{{ c.label }}</th>
                }
                <th class="p-1.5"></th>
              </tr>
            </thead>
            <tbody>
              @for (row of list.rows; track row.key) {
                <tr [attr.data-sub-row]="row.key">
                  @for (c of columns(i); track c.column) {
                    @let cell = view(row, c);
                    <td class="p-1.5">
                      @if (cell.color) {
                        <bp-chip [tone]="tone(cell.color)">{{ cell.text }}</bp-chip>
                      } @else {
                        {{ cell.text }}
                      }
                    </td>
                  }
                  <td class="whitespace-nowrap p-1.5 text-right">
                    @for (a of row.actions; track a) {
                      <button mat-button type="button" [attr.data-sub-action]="a" (click)="act(i, def.actions![a], a, row)">{{ def.actions![a].label }}</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        }
      </div>
    }
  `,
})
export class PageExpandComponent {
  private readonly api = inject(ProjectPagesApiService);

  readonly projectId = input.required<string>();
  readonly pageKey = input.required<string>();
  readonly rowKey = input.required<string>();
  readonly expand = input.required<PageRelated[]>();
  readonly timezone = input('UTC');
  /** Зміна значення перечитує підлеглі списки (після збереження в панелі чи дії над рядком). */
  readonly version = input(0);

  /** Відкрити сторінку (перехід чи панель). */
  readonly open = output<ExpandOpen>();
  /** Результат дії-сценарію для повідомлення на сторінці. */
  readonly result = output<{ action: PageAction; result: PageActionResult }>();

  readonly lists = signal<PageExpandRows[]>([]);
  readonly loading = signal(false);
  readonly error = signal('');
  protected readonly emptyText = $localize`:@@app.page.expandEmpty:Записів немає.`;

  constructor() {
    effect(() => {
      this.version();
      this.load();
    });
  }

  protected columns(index: number): DisplayColumn[] {
    const list = this.lists()[index];
    const defined = this.expand()[index]?.columns ?? [];
    return (list?.columns ?? []).map((c) => {
      const d = defined.find((x) => x.column === c.column);
      return { column: c.column, label: c.label, type: c.type, format: d?.format ?? c.format, labels: d?.labels, colors: d?.colors };
    });
  }

  protected view(row: PageRow, column: DisplayColumn) {
    return cellView(row, column, this.timezone());
  }

  protected badge(color: keyof typeof BADGE_CLASSES): string {
    return BADGE_CLASSES[color];
  }

  protected tone(color: keyof typeof BADGE_TONES): ChipTone {
    return BADGE_TONES[color];
  }

  protected add(def: PageRelated, page: string, mode?: PageOpenMode): void {
    this.open.emit({ page, mode, prefill: { [def.by]: this.rowKey() } });
  }

  protected act(index: number, action: PageAction, actionIndex: number, row: PageRow): void {
    if (action.page) {
      this.open.emit({ page: action.page, rowKey: row.key, mode: action.mode });
      return;
    }
    if (action.confirm && !window.confirm(action.confirm)) return;
    this.error.set('');
    this.api.runExpandAction(this.projectId(), this.pageKey(), this.rowKey(), index, row.key, actionIndex).subscribe({
      next: (result) => {
        this.result.emit({ action, result });
        this.load();
      },
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }

  private load(): void {
    const total = this.expand().length;
    if (total === 0) return;
    this.loading.set(true);
    this.error.set('');
    forkJoin(Array.from({ length: total }, (_, i) => this.api.expand(this.projectId(), this.pageKey(), this.rowKey(), i))).subscribe({
      next: (lists) => {
        this.lists.set(lists);
        this.loading.set(false);
      },
      error: (e: unknown) => {
        this.loading.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }
}
