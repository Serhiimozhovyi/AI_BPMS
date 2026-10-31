import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { PageUserFilter } from '../../../projects/project-pages-api.service';
import { BpButtonDirective } from '../../../shared/button.directive';

const KINDS: readonly PageUserFilter['kind'][] = ['select', 'chips', 'period', 'search'];

/**
 * Фільтри над списком, календарем чи головною (F8-19, `filters`): колонка, вид (вибір, чипи, період, пошук), підпис і
 * значення за замовчуванням (`{{ $user.id }}` — «мої»). Користувач змінює їх на сторінці, на відміну від умов, які діють завжди.
 */
@Component({
  selector: 'app-builder-user-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule],
  template: `
    @for (f of filters(); track $index; let i = $index) {
      <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-user-filter]="i">
        <select class="border p-1" aria-label="Колонка фільтра" i18n-aria-label="@@bo.filter.column" [value]="f.column" (change)="patch(i, { column: $any($event.target).value })">
          <option value="" [selected]="!f.column">—</option>
          @for (c of columns(); track c) {
            <option [value]="c" [selected]="c === f.column">{{ c }}</option>
          }
        </select>
        <select class="border p-1" aria-label="Вид фільтра" i18n-aria-label="@@bo.filter.kind" [value]="f.kind" (change)="patch(i, { kind: $any($event.target).value })">
          @for (k of kinds; track k) {
            <option [value]="k" [selected]="k === f.kind">{{ kindName(k) }}</option>
          }
        </select>
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
    <button mat-button type="button" data-testid="b-add-user-filter" (click)="add()" i18n="@@bo.filter.add">Додати фільтр</button>
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
