import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { PageAction, PageBlock } from '../../../projects/project-pages-api.service';
import { BpButtonDirective } from '../../../shared/button.directive';

const KINDS: readonly PageBlock['kind'][] = ['text', 'tiles', 'list', 'calendar', 'form', 'actions'];

/**
 * Блоки головної сторінки (F8-19): текст-привітання, плитки, список «найближчі», календар на сьогодні, форма та кнопки.
 * Блок посилається на сторінку проєкту (її дані й права), порядок змінюють стрілками.
 */
@Component({
  selector: 'app-builder-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule],
  template: `
    <h3 class="mb-1 mt-4 font-medium" i18n="@@bd.title">Блоки головної</h3>
    @for (b of blocks(); track $index; let i = $index) {
      <div class="mb-2 flex flex-wrap items-center gap-2 border-b pb-2" [attr.data-block]="i">
        <select class="border p-1" aria-label="Вид блоку" i18n-aria-label="@@bd.kind" [value]="b.kind" (change)="patch(i, { kind: $any($event.target).value })">
          @for (k of kinds; track k) {
            <option [value]="k" [selected]="k === b.kind">{{ kindName(k) }}</option>
          }
        </select>
        <input class="w-40 border p-1" placeholder="Підпис" i18n-placeholder="@@bd.label" [value]="b.label ?? ''" (input)="patch(i, { label: $any($event.target).value || undefined })" />
        @if (b.kind === 'text') {
          <input class="w-72 border p-1" placeholder="Текст" i18n-placeholder="@@bd.text" [value]="b.text ?? ''" (input)="patch(i, { text: $any($event.target).value })" />
        } @else if (b.kind !== 'actions') {
          @if (b.definition) {
            <span class="text-sm opacity-70" i18n="@@bd.inline">вбудоване визначення (у JSON)</span>
          } @else {
            <select class="border p-1" aria-label="Сторінка блоку" i18n-aria-label="@@bd.page" [value]="b.page ?? ''" (change)="patch(i, { page: $any($event.target).value || undefined })">
              <option value="" [selected]="!b.page">—</option>
              @for (p of pageKeys(); track p) {
                <option [value]="p" [selected]="p === b.page">{{ p }}</option>
              }
            </select>
          }
        }
        @if (b.kind === 'list') {
          <input class="w-20 border p-1" type="number" min="1" max="50" aria-label="Скільки рядків" i18n-aria-label="@@bd.limit" [value]="b.limit ?? ''" (input)="patch(i, { limit: $any($event.target).value ? +$any($event.target).value : undefined })" />
        }
        @if (b.kind === 'calendar') {
          <select class="border p-1" aria-label="Період" i18n-aria-label="@@bd.range" [value]="b.range ?? 'today'" (change)="patch(i, { range: $any($event.target).value })">
            <option value="today" [selected]="b.range !== 'week'" i18n="@@bd.range.today">сьогодні</option>
            <option value="week" [selected]="b.range === 'week'" i18n="@@bd.range.week">тиждень</option>
          </select>
        }
        <input class="w-20 border p-1" type="number" min="1" max="12" aria-label="Ширина (з 12)" i18n-aria-label="@@bd.width" [value]="b.width ?? 12" (input)="patch(i, { width: +$any($event.target).value || 12 })" />
        <input class="w-56 border p-1" [placeholder]="hint" aria-label="Умова показу" i18n-aria-label="@@bd.when" [value]="b.when ?? ''" (input)="patch(i, { when: $any($event.target).value || undefined })" />
        <button mat-button type="button" [disabled]="i === 0" (click)="move(i, -1)" aria-label="Вище" i18n-aria-label="@@bd.up">↑</button>
        <button mat-button type="button" [disabled]="i === blocks().length - 1" (click)="move(i, 1)" aria-label="Нижче" i18n-aria-label="@@bd.down">↓</button>
        <button mat-button type="button" (click)="remove(i)" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>

        @if (b.kind === 'actions') {
          <div class="w-full pl-4">
            @for (a of b.actions ?? []; track $index; let j = $index) {
              <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-block-action]="j">
                <input class="w-36 border p-1" placeholder="Підпис кнопки" i18n-placeholder="@@bd.action.label" [value]="a.label" (input)="action(i, j, { label: $any($event.target).value })" />
                <select class="border p-1" aria-label="Що робить кнопка" i18n-aria-label="@@bd.action.type" [value]="a.workflow ? 'workflow' : 'page'" (change)="actionType(i, j, $any($event.target).value)">
                  <option value="page" [selected]="!a.workflow" i18n="@@builder.action.page">Відкрити сторінку</option>
                  <option value="workflow" [selected]="!!a.workflow" i18n="@@builder.action.workflow">Запустити сценарій</option>
                </select>
                @if (a.workflow !== undefined) {
                  <select class="border p-1" aria-label="Сценарій" i18n-aria-label="@@builder.workflow" [value]="a.workflow" (change)="action(i, j, { workflow: $any($event.target).value })">
                    <option value="" [selected]="!a.workflow">—</option>
                    @for (w of workflows(); track w) {
                      <option [value]="w" [selected]="w === a.workflow">{{ w }}</option>
                    }
                  </select>
                } @else {
                  <select class="border p-1" aria-label="Сторінка" i18n-aria-label="@@builder.action.target.page" [value]="a.page ?? ''" (change)="action(i, j, { page: $any($event.target).value })">
                    <option value="" [selected]="!a.page">—</option>
                    @for (p of pageKeys(); track p) {
                      <option [value]="p" [selected]="p === a.page">{{ p }}</option>
                    }
                  </select>
                  <select class="border p-1" aria-label="Як відкрити" i18n-aria-label="@@bc.mode" [value]="a.mode ?? 'navigate'" (change)="action(i, j, { mode: $any($event.target).value })">
                    <option value="navigate" [selected]="a.mode !== 'drawer'" i18n="@@bc.mode.navigate">переходом</option>
                    <option value="drawer" [selected]="a.mode === 'drawer'" i18n="@@bc.mode.drawer">бічною панеллю</option>
                  </select>
                }
                <input class="w-48 border p-1" placeholder="Запитати підтвердження" i18n-placeholder="@@builder.action.confirm" [value]="a.confirm ?? ''" (input)="action(i, j, { confirm: $any($event.target).value || undefined })" />
                @for (r of appRoles(); track r.key) {
                  <label class="inline-flex items-center gap-1 opacity-80">
                    <input type="checkbox" [checked]="(a.roles ?? []).includes(r.key)" (change)="actionRole(i, j, r.key, $any($event.target).checked)" />
                    {{ r.name }}
                  </label>
                }
                <button mat-button type="button" (click)="removeAction(i, j)" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
              </div>
            }
            <button mat-stroked-button type="button" [attr.data-testid]="'bd-add-action-' + i" (click)="addAction(i)" i18n="@@builder.addAction" bpVariant="tonal">Додати дію</button>
          </div>
        }
      </div>
    }
    <button mat-stroked-button type="button" data-testid="b-add-block" (click)="add()" i18n="@@bd.add" bpVariant="tonal">Додати блок</button>
  `,
})
export class BuilderDashboardComponent {
  readonly blocks = input.required<PageBlock[]>();
  readonly pageKeys = input.required<string[]>();
  /** Назви сценаріїв проєкту. */
  readonly workflows = input.required<string[]>();
  readonly appRoles = input.required<{ key: string; name: string }[]>();
  readonly blocksChange = output<PageBlock[]>();

  protected readonly kinds = KINDS;
  protected readonly hint = $localize`:@@bd.hint.when:показувати, якщо {{ $user.roles.includes('coordinator') }}`;

  protected kindName(kind: PageBlock['kind']): string {
    switch (kind) {
      case 'text':
        return $localize`:@@bd.kind.text:Текст`;
      case 'tiles':
        return $localize`:@@bd.kind.tiles:Плитки`;
      case 'list':
        return $localize`:@@bd.kind.list:Список`;
      case 'calendar':
        return $localize`:@@bd.kind.calendar:Календар`;
      case 'form':
        return $localize`:@@bd.kind.form:Форма`;
      default:
        return $localize`:@@bd.kind.actions:Кнопки`;
    }
  }

  private emit(next: PageBlock[]): void {
    this.blocksChange.emit(next);
  }

  protected patch(index: number, patch: Partial<PageBlock>): void {
    const next = this.blocks().map((b, i) => {
      if (i !== index) return b;
      const merged: PageBlock = { ...b, ...patch };
      for (const key of Object.keys(merged) as (keyof PageBlock)[]) if (merged[key] === undefined) delete merged[key];
      if (patch.kind && patch.kind !== b.kind) return { kind: patch.kind, label: merged.label, width: merged.width, when: merged.when };
      return merged;
    });
    this.emit(next);
  }

  protected add(): void {
    this.emit([...this.blocks(), { kind: 'list', width: 12 }]);
  }

  protected remove(index: number): void {
    this.emit(this.blocks().filter((_, i) => i !== index));
  }

  protected move(index: number, delta: number): void {
    const next = [...this.blocks()];
    const [item] = next.splice(index, 1);
    next.splice(index + delta, 0, item);
    this.emit(next);
  }

  protected action(block: number, index: number, patch: Partial<PageAction>): void {
    const actions = (this.blocks()[block].actions ?? []).map((a, i) => {
      if (i !== index) return a;
      const merged = { ...a, ...patch };
      for (const key of Object.keys(merged) as (keyof PageAction)[]) if (merged[key] === undefined) delete merged[key];
      return merged;
    });
    this.patch(block, { actions });
  }

  protected actionType(block: number, index: number, type: string): void {
    const label = this.blocks()[block].actions?.[index]?.label ?? '';
    const actions = (this.blocks()[block].actions ?? []).map((a, i) => (i === index ? (type === 'workflow' ? { label, workflow: '' } : { label, page: '' }) : a));
    this.patch(block, { actions });
  }

  protected actionRole(block: number, index: number, role: string, on: boolean): void {
    const roles = this.blocks()[block].actions?.[index]?.roles ?? [];
    const next = on ? [...roles, role] : roles.filter((r) => r !== role);
    this.action(block, index, { roles: next.length ? next : undefined });
  }

  protected addAction(block: number): void {
    this.patch(block, { actions: [...(this.blocks()[block].actions ?? []), { label: '', page: '' }] });
  }

  protected removeAction(block: number, index: number): void {
    this.patch(block, { actions: (this.blocks()[block].actions ?? []).filter((_, i) => i !== index) });
  }
}
