import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { PageUserFilter } from '../../../projects/project-pages-api.service';
import { BpButtonDirective } from '../../../shared/button.directive';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../../shared/select-search/select-search.component';

const KINDS: readonly PageUserFilter['kind'][] = ['select', 'chips', 'period', 'search'];

/**
 * Фільтри над списком, календарем чи головною (F8-19, `filters`): колонка, вид (вибір, чипи, період, пошук), підпис і
 * значення за замовчуванням (`{{ $user.id }}` — «мої»). Користувач змінює їх на сторінці, на відміну від умов, які діють завжди.
 */
@Component({
  selector: 'app-builder-user-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    @for (f of filters(); track $index; let i = $index) {
      <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-user-filter]="i">
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Колонка фільтра" i18n-aria-label="@@bo.filter.column" [value]="f.column" (selectionChange)="patch(i, { column: $event.value })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (c of columns(); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Вид фільтра" i18n-aria-label="@@bo.filter.kind" [value]="f.kind" (selectionChange)="patch(i, { kind: $event.value })">
            <bp-select-search />
            @for (k of kinds; track k) {
              <mat-option [value]="k">{{ kindName(k) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <input class="w-40 border p-1" placeholder="Підпис" i18n-placeholder="@@bo.filter.label" [value]="f.label ?? ''" (input)="patch(i, { label: $any($event.target).value || undefined })" />
        <input
          class="w-48 border p-1"
          [placeholder]="defaultHint"
          [attr.aria-label]="defaultHint"
          [value]="defaultText(f)"
          (input)="patch(i, { default: $any($event.target).value || undefined })"
        />
        <button mat-button type="button" (click)="remove(i)" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
      </div>
    }
    <button mat-stroked-button type="button" data-testid="b-add-user-filter" (click)="add()" i18n="@@bo.filter.add" bpVariant="tonal">Додати фільтр</button>
  `,
})
export class BuilderUserFiltersComponent {
  readonly filters = input.required<PageUserFilter[]>();
  readonly columns = input.required<string[]>();
  readonly changed = output<PageUserFilter[]>();

  protected readonly kinds = KINDS;
  protected readonly defaultHint = $localize`:@@bo.filter.default:типово, напр. {{ $user.id }}`;

  protected kindName(kind: PageUserFilter['kind']): string {
    switch (kind) {
      case 'select':
        return $localize`:@@bo.filter.select:Вибір зі списку`;
      case 'chips':
        return $localize`:@@bo.filter.chips:Чипи`;
      case 'period':
        return $localize`:@@bo.filter.period:Період`;
      default:
        return $localize`:@@bo.filter.search:Пошук`;
    }
  }

  protected defaultText(f: PageUserFilter): string {
    return f.default === undefined || f.default === null ? '' : typeof f.default === 'string' ? f.default : JSON.stringify(f.default);
  }

  protected patch(index: number, patch: Partial<PageUserFilter>): void {
    this.changed.emit(this.filters().map((f, i) => (i === index ? { ...f, ...patch } : f)));
  }

  protected add(): void {
    this.changed.emit([...this.filters(), { column: '', kind: 'select' }]);
  }

  protected remove(index: number): void {
    this.changed.emit(this.filters().filter((_, i) => i !== index));
  }
}
