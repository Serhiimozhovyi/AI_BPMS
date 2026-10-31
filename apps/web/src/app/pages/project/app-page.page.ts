import { ViewAsService } from '../../auth/view-as.service';
import { NgTemplateOutlet } from '@angular/common';
import { ChipComponent, ChipTone } from '../../shared/chip/chip.component';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router, RouterLink, convertToParamMap } from '@angular/router';
import { combineLatest, of } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { BADGE_CLASSES, BADGE_TONES, CellView, DisplayColumn, cellView } from '../../projects/page-format';
import {
  PageAction,
  PageActionResult,
  PageFilterValue,
  PageMetrics,
  PageOpenMode,
  PageRecord,
  PageRow,
  PageRows,
  PageSubmitResult,
  ProjectPageView,
  ProjectPagesApiService,
} from '../../projects/project-pages-api.service';
import { ExpandOpen, PageExpandComponent } from './page-expand.component';
import { PageCalendarComponent } from './page-calendar.component';
import { PageMetricsComponent } from './page-metrics.component';
import { PageDashboardComponent } from './page-dashboard.component';
import { PageDrawerComponent } from './page-drawer.component';
import { PageFiltersComponent, emptyFilters } from './page-filters.component';
import { PageFormComponent } from './page-form.component';

/** Бокова панель форми: сторінка-форма, рядок для зміни і початкові значення. */
interface DrawerState {
  page: string;
  rowKey: string | null;
  prefill: Record<string, unknown>;
}

/**
 * `/projects/:id/app/<key>[/<rowKey>]` — сторінка застосунку (F8-04): список (пошук, сортування, сторінки, дії), картка
 * рядка з пов'язаними списками або форма (поля з типів колонок тим самим рушієм форм, що й публічні форми F6-03).
 * Фільтри сторінки й права застосовує Api; тут — лише показ і переходи. Список (F8-16): панель фільтрів користувача, мітки й
 * формати колонок, обчислювані колонки, показ таблицею чи картками, підлеглі списки під рядком і форма в боковій панелі.
 */
@Component({
  selector: 'app-app-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    NgTemplateOutlet,
    ChipComponent,
    RouterLink,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    PageFiltersComponent,
    PageExpandComponent,
    PageFormComponent,
    PageDrawerComponent,
    PageCalendarComponent,
    PageMetricsComponent,
    PageDashboardComponent,
  ],
  template: `
    <section class="px-6 pb-6">
      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="page-error">{{ error() }}</p>
      }
      @if (notice(); as n) {
        <p role="status" class="mb-2" [class.text-red-700]="n.state !== 'success'" data-testid="action-result" [attr.data-state]="n.state">{{ n.text }}</p>
      }
      @if (view(); as v) {
        <div class="mb-3 flex flex-wrap items-center gap-3">
          <h2 class="text-lg font-semibold" data-testid="app-page-title">{{ v.page.name }}</h2>
          @if (v.canEdit) {
            <a mat-button data-testid="app-edit-page" [routerLink]="['/projects', projectId, 'app', '_builder', v.page.key]" i18n="@@app.page.edit">Налаштувати</a>
          }
          @if ((v.page.kind === 'list' || v.page.kind === 'calendar') && v.page.definition.create; as create) {
            <span class="inline-flex" [attr.title]="readOnly() ? readOnlyTip : null"><button mat-flat-button type="button" data-testid="app-create" [disabled]="readOnly()" (click)="open(create.page, undefined, create.mode)" i18n="@@app.page.create">Створити</button></span>
          }
        </div>

        @switch (v.page.kind) {
          @case ('list') {
            @if (v.page.definition.filters?.length) {
              <app-page-filters [filters]="v.page.definition.filters!" [options]="v.filterOptions" [values]="filterState()" (changed)="filtersChanged($event)" />
            }
            @if (v.page.definition.search?.length) {
              <mat-form-field appearance="outline" class="w-72">
                <mat-label i18n="@@app.page.search">Пошук</mat-label>
                <input matInput data-testid="app-search" [value]="search()" (keydown.enter)="find($any($event.target).value)" (blur)="find($any($event.target).value)" />
              </mat-form-field>
            }
            @if (rows(); as r) {
              @if (r.items.length === 0) {
                <p class="opacity-70" data-testid="app-rows-empty">{{ v.page.definition.empty ?? emptyText }}</p>
              } @else if (v.page.definition.layout === 'cards') {
                <ul class="grid max-w-6xl grid-cols-[repeat(auto-fill,minmax(18rem,1fr))] gap-3" data-testid="app-cards">
                  @for (row of r.items; track row.key) {
                    <li class="rounded border border-bp-line p-4" [attr.data-row]="row.key">
                      <div class="flex items-start gap-2">
                        <div class="grow">
                          @let title = cardCell(row, v.page.definition.card?.title);
                          <div class="font-semibold" data-testid="card-title">{{ title.text }}</div>
                          @if (v.page.definition.card?.subtitle; as sub) {
                            <div class="opacity-70" data-testid="card-subtitle">{{ cardCell(row, sub).text }}</div>
                          }
                        </div>
                        @if (v.page.definition.expand?.length) {
                          <button type="button" class="rounded px-2" [attr.aria-expanded]="isExpanded(row.key)" [attr.data-expand-toggle]="row.key" [attr.aria-label]="expandLabel" (click)="toggle(row.key)">
                            {{ isExpanded(row.key) ? '▾' : '▸' }}
                          </button>
                        }
                      </div>
                      <dl class="mt-2 grid grid-cols-[auto_1fr] gap-x-3 gap-y-1">
                        @for (c of cardColumns(); track c.column) {
                          @let cell = cellOf(row, c);
                          <dt class="opacity-70">{{ c.label }}</dt>
                          <dd [attr.data-field]="c.column">
                            @if (cell.color) {
                              <bp-chip [tone]="tone(cell.color)">{{ cell.text }}</bp-chip>
                            } @else {
                              {{ cell.text }}
                            }
                          </dd>
                        }
                      </dl>
                      @if (row.actions.length) {
                        <div class="mt-2 flex flex-wrap gap-1">
                          @for (i of row.actions; track i) {
                            <span class="inline-flex" [attr.title]="readOnly() && !v.page.definition.actions![i].page ? readOnlyTip : null"><button mat-button type="button" [attr.data-action]="i" [disabled]="readOnly() && !v.page.definition.actions![i].page" (click)="act(v.page.definition.actions![i], i, row)">
                              {{ v.page.definition.actions![i].label }}
                            </button></span>
                          }
                        </div>
                      }
                      @if (isExpanded(row.key)) {
                        <div class="mt-3 border-t border-bp-line pt-3">
                          <app-page-expand
                            [projectId]="projectId"
                            [pageKey]="v.page.key"
                            [rowKey]="row.key"
                            [expand]="v.page.definition.expand!"
                            [timezone]="v.timezone ?? 'UTC'"
                            [version]="expandVersion()"
                            (open)="openFromExpand($event)"
                            (result)="expandResult($event.action, $event.result)"
                          />
                        </div>
                      }
                    </li>
                  }
                </ul>
                <ng-container *ngTemplateOutlet="pager" />
              } @else {
                <div class="overflow-x-auto">
                  <table class="w-full max-w-5xl text-left" data-testid="app-rows">
                    <thead>
                      <tr>
                        @if (v.page.definition.expand?.length) {
                          <th class="w-8 p-2"></th>
                        }
                        @for (c of columns(); track c.column) {
                          <th class="p-2">
                            @if (c.computed) {
                              <span class="font-semibold">{{ c.label }}</span>
                            } @else {
                              <button type="button" class="font-semibold" [attr.data-sort]="c.column" (click)="sortBy(c.column)">
                                {{ c.label }}
                                @if (sort() === c.column) {
                                  <span aria-hidden="true">{{ descending() ? '↓' : '↑' }}</span>
                                }
                              </button>
                            }
                          </th>
                        }
                        <th class="p-2"></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of r.items; track row.key) {
                        <tr [attr.data-row]="row.key">
                          @if (v.page.definition.expand?.length) {
                            <td class="p-2">
                              <button type="button" class="rounded px-1" [attr.aria-expanded]="isExpanded(row.key)" [attr.data-expand-toggle]="row.key" [attr.aria-label]="expandLabel" (click)="toggle(row.key)">
                                {{ isExpanded(row.key) ? '▾' : '▸' }}
                              </button>
                            </td>
                          }
                          @for (c of columns(); track c.column) {
                            @let cell = cellOf(row, c);
                            <td class="p-2">
                              @if (cell.color) {
                                <bp-chip [tone]="tone(cell.color)" [attr.data-badge]="cell.color">{{ cell.text }}</bp-chip>
                              } @else {
                                {{ cell.text }}
                              }
                            </td>
                          }
                          <td class="whitespace-nowrap p-2 text-right">
                            @for (i of row.actions; track i) {
                              <span class="inline-flex" [attr.title]="readOnly() && !v.page.definition.actions![i].page ? readOnlyTip : null"><button mat-button type="button" [attr.data-action]="i" [disabled]="readOnly() && !v.page.definition.actions![i].page" (click)="act(v.page.definition.actions![i], i, row)">
                                {{ v.page.definition.actions![i].label }}
                              </button></span>
                            }
                          </td>
                        </tr>
                        @if (isExpanded(row.key)) {
                          <tr [attr.data-expand-row]="row.key">
                            <td class="bg-bp-surface-2 p-3" [attr.colspan]="columns().length + 2">
                              <app-page-expand
                                [projectId]="projectId"
                                [pageKey]="v.page.key"
                                [rowKey]="row.key"
                                [expand]="v.page.definition.expand!"
                                [timezone]="v.timezone ?? 'UTC'"
                                [version]="expandVersion()"
                                (open)="openFromExpand($event)"
                                (result)="expandResult($event.action, $event.result)"
                              />
                            </td>
                          </tr>
                        }
                      }
                    </tbody>
                  </table>
                </div>
                <ng-container *ngTemplateOutlet="pager" />
              }
              <ng-template #pager>
                @if (r.total > r.pageSize) {
                  <div class="mt-2 flex items-center gap-2" data-testid="app-pager">
                    <button mat-button type="button" [disabled]="r.page <= 1" (click)="go(r.page - 1)" i18n="@@app.page.prev">Назад</button>
                    <span>{{ r.page }} / {{ pages(r) }}</span>
                    <button mat-button type="button" [disabled]="r.page >= pages(r)" (click)="go(r.page + 1)" i18n="@@app.page.next">Далі</button>
                  </div>
                }
              </ng-template>
            }
          }
          @case ('detail') {
            @if (record(); as rec) {
              <dl class="mb-4 grid max-w-3xl grid-cols-[minmax(8rem,auto)_1fr] gap-x-4 gap-y-2" data-testid="app-detail">
                @for (c of columns(); track c.column) {
                  @let cell = cellOf(rec.row, c);
                  <dt class="opacity-70">{{ c.label }}</dt>
                  <dd [attr.data-field]="c.column">
                    @if (cell.color) {
                      <bp-chip [tone]="tone(cell.color)">{{ cell.text }}</bp-chip>
                    } @else {
                      {{ cell.text }}
                    }
                  </dd>
                }
              </dl>
              @if (rec.row.actions.length) {
                <div class="mb-4 flex gap-2">
                  @for (i of rec.row.actions; track i) {
                    <span class="inline-flex" [attr.title]="readOnly() && !v.page.definition.actions![i].page ? readOnlyTip : null"><button mat-stroked-button type="button" [attr.data-action]="i" [disabled]="readOnly() && !v.page.definition.actions![i].page" (click)="act(v.page.definition.actions![i], i, rec.row)">
                      {{ v.page.definition.actions![i].label }}
                    </button></span>
                  }
                </div>
              }
              @for (rel of rec.related; track $index; let ri = $index) {
                <h3 class="mb-2 mt-4 font-semibold" [attr.data-related]="ri">{{ rel.label }}</h3>
                @if (rel.rows.length === 0) {
                  <p class="opacity-70" i18n="@@app.page.empty">Записів немає.</p>
                } @else {
                  <table class="w-full max-w-5xl text-left">
                    <thead>
                      <tr>
                        @for (c of rel.columns; track c.column) {
                          <th class="p-2">{{ c.label }}</th>
                        }
                        <th class="p-2"></th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (row of rel.rows; track row.key) {
                        <tr [attr.data-row]="row.key">
                          @for (c of rel.columns; track c.column) {
                            <td class="p-2">{{ cellOf(row, c).text }}</td>
                          }
                          <td class="whitespace-nowrap p-2 text-right">
                            @for (i of row.actions; track i) {
                              <button mat-button type="button" (click)="open(v.page.definition.related![ri].actions![i].page!, row.key)">
                                {{ v.page.definition.related![ri].actions![i].label }}
                              </button>
                            }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                }
              }
            }
          }
          @case ('metrics') {
            @if (metrics(); as m) {
              <app-page-metrics [metrics]="m" />
            }
          }
          @case ('dashboard') {
            <app-page-dashboard [projectId]="projectId" [view]="v" [version]="expandVersion()" (open)="openFromExpand($event)" />
          }
          @case ('calendar') {
            <app-page-calendar
              [projectId]="projectId"
              [view]="v"
              [version]="expandVersion()"
              (open)="openFromExpand($event)"
              (act)="act($event.action, $event.index, $event.row)"
            />
          }
          @case ('form') {
            <app-page-form [projectId]="projectId" [view]="v" [rowKey]="rowKey" [prefill]="prefill" (submitted)="formDone($event)" />
          }
        }
      }
    </section>
    @if (drawer(); as d) {
      <app-page-drawer
        [projectId]="projectId"
        [pageKey]="d.page"
        [rowKey]="d.rowKey"
        [prefill]="d.prefill"
        (closed)="drawer.set(null)"
        (saved)="drawerSaved($event)"
        (unsupported)="drawerUnsupported(d)"
      />
    }
  `,
})
export class AppPagePage implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  /** Перегляд «як користувач» (F12-02): дії, що змінюють дані, неактивні. */
  protected readonly readOnly = inject(ViewAsService).active;
  protected readonly readOnlyTip = $localize`:@@viewAs.disabled:У режимі перегляду дані не змінюються`;
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** `projects/:id` → `app` → `:key`: проєкт — у діда маршруту. */
  readonly projectId = this.route.parent?.parent?.snapshot.paramMap.get('id') ?? '';
  readonly view = signal<ProjectPageView | null>(null);
  readonly rows = signal<PageRows | null>(null);
  readonly record = signal<PageRecord | null>(null);
  readonly metrics = signal<PageMetrics | null>(null);
  /** Підсумок останньої дії чи форми зі сценарієм: повідомлення сценарію або помилка. */
  readonly notice = signal<{ state: PageActionResult['state']; text: string } | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly search = signal('');
  readonly sort = signal<string | null>(null);
  readonly descending = signal(false);
  readonly page = signal(1);

  /** Значення фільтрів користувача: з відповіді Api (зі значеннями за замовчуванням), далі — вибір людини. */
  readonly filterState = signal<Record<string, PageFilterValue>>({});
  /** Розгорнуті рядки списку (підлеглі списки). */
  readonly expanded = signal<ReadonlySet<string>>(new Set());
  /** Змінюється, коли підлеглі списки треба перечитати. */
  readonly expandVersion = signal(0);
  /** Відкрита бокова панель форми. */
  readonly drawer = signal<DrawerState | null>(null);

  /** Колонки списку чи полів картки: підписи й типи з Api, формат, мітки й кольори — з визначення, обчислювані — з `computed`. */
  readonly columns = computed<DisplayColumn[]>(() => {
    const v = this.view();
    if (!v) return [];
    const def = v.page.definition;
    if (v.page.kind === 'list') {
      return (def.columns ?? []).map((dc) => {
        const computedColumn = def.computed?.find((x) => x.key === dc.column);
        if (computedColumn) {
          return { column: computedColumn.key, label: dc.label ?? computedColumn.label, type: 'number', format: dc.format, labels: dc.labels, computed: computedColumn };
        }
        const vc = v.columns.find((c) => c.column === dc.column);
        return { column: dc.column, label: vc?.label ?? dc.label ?? dc.column, type: vc?.type ?? 'string', format: dc.format ?? vc?.format, labels: dc.labels, colors: dc.colors };
      });
    }
    return v.columns.map((vc) => {
      const field = def.fields?.find((f) => f.column === vc.column);
      return { column: vc.column, label: vc.label, type: vc.type, format: field?.format ?? vc.format, labels: field?.labels, colors: field?.colors };
    });
  });

  /** Колонки картки без заголовка й підзаголовка. */
  readonly cardColumns = computed(() => {
    const card = this.view()?.page.definition.card;
    const title = card?.title ?? this.columns()[0]?.column;
    return this.columns().filter((c) => c.column !== title && c.column !== card?.subtitle);
  });

  /** Початкові значення форми з адреси (`?колонка=значення`): так підлеглий список передає ключ батьківського рядка. */
  prefill: Record<string, unknown> = {};
  rowKey: string | null = null;

  protected readonly emptyText = $localize`:@@app.page.empty:Записів немає.`;
  protected readonly expandLabel = $localize`:@@app.page.expandToggle:Підлеглі записи`;

  private key = '';
  private filtersTouched = false;

  ngOnInit(): void {
    // Перехід між сторінками застосунку перевикористовує компонент — слухаємо параметри.
    combineLatest([this.route.paramMap, this.route.queryParamMap ?? of(convertToParamMap({}))])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(([params, query]) => {
        this.key = params.get('key') ?? '';
        this.rowKey = params.get('rowKey');
        this.prefill = Object.fromEntries(query.keys.map((k) => [k, query.get(k)]));
        this.load();
      });
  }

  cellOf(row: PageRow, column: DisplayColumn): CellView {
    return cellView(row, column, this.view()?.timezone ?? 'UTC');
  }

  /** Комірка заголовка чи підзаголовка картки: колонка з визначення або перша колонка списку. */
  cardCell(row: PageRow, name: string | undefined): CellView {
    const column = this.columns().find((c) => c.column === name) ?? (name ? { column: name, label: name, type: 'string' } : this.columns()[0]);
    return column ? this.cellOf(row, column) : { text: '—' };
  }

  badge(color: keyof typeof BADGE_CLASSES): string {
    return BADGE_CLASSES[color];
  }

  tone(color: keyof typeof BADGE_TONES): ChipTone {
    return BADGE_TONES[color];
  }

  pages(rows: PageRows): number {
    return Math.max(1, Math.ceil(rows.total / rows.pageSize));
  }

  find(text: string): void {
    if (text === this.search()) return;
    this.search.set(text);
    this.page.set(1);
    this.loadRows();
  }

  sortBy(column: string): void {
    if (this.sort() === column) this.descending.update((d) => !d);
    else {
      this.sort.set(column);
      this.descending.set(false);
    }
    this.loadRows();
  }

  go(page: number): void {
    this.page.set(page);
    this.loadRows();
  }

  filtersChanged(state: Record<string, PageFilterValue>): void {
    this.filterState.set(state);
    this.filtersTouched = true;
    this.page.set(1);
    this.loadRows();
  }

  isExpanded(rowKey: string): boolean {
    return this.expanded().has(rowKey);
  }

  toggle(rowKey: string): void {
    this.expanded.update((set) => {
      const next = new Set(set);
      if (!next.delete(rowKey)) next.add(rowKey);
      return next;
    });
  }

  /**
   * Сторінка застосунку з ключем рядка (картка, форма зміни): переходом чи боковою панеллю (`mode: drawer`).
   * `prefill` — початкові значення форми створення; у переході йдуть у адресу.
   */
  open(page: string, rowKey?: string, mode?: PageOpenMode, prefill?: Record<string, unknown>): void {
    if (mode === 'drawer') {
      this.drawer.set({ page, rowKey: rowKey ?? null, prefill: prefill ?? {} });
      return;
    }
    const commands = ['/projects', this.projectId, 'app', page, ...(rowKey ? [rowKey] : [])];
    if (prefill && Object.keys(prefill).length > 0) void this.router.navigate(commands, { queryParams: prefill });
    else void this.router.navigate(commands);
  }

  openFromExpand(request: ExpandOpen): void {
    this.open(request.page, request.rowKey, request.mode, request.prefill);
  }

  /** Результат дії над підлеглим рядком: повідомлення сценарію; підлеглі списки вже перечитані. */
  expandResult(action: PageAction, result: PageActionResult): void {
    this.showResult(result, action.workflow ? $localize`:@@app.page.done:Готово.` : '');
    this.loadRows();
  }

  /** Форму в панелі збережено: панель закривається, список і підлеглі списки перечитуються. */
  drawerSaved(result: PageSubmitResult): void {
    this.drawer.set(null);
    if (result.result) this.showResult(result.result, '');
    this.expandVersion.update((n) => n + 1);
    if (this.view()?.page.kind === 'list') this.loadRows();
    else if (this.view()?.page.kind === 'detail') this.loadRecord();
  }

  /** Сторінка не форма: панель для неї не годиться — відкриваємо переходом. */
  drawerUnsupported(state: DrawerState): void {
    this.drawer.set(null);
    this.open(state.page, state.rowKey ?? undefined, 'navigate', state.prefill);
  }

  /** Форму сторінки збережено: повідомлення сценарію і перехід `then`. */
  formDone(result: PageSubmitResult): void {
    if (result.result) this.showResult(result.result, '');
    if (result.then) this.open(result.then);
  }

  act(action: PageAction, index: number, row: PageRow): void {
    if (action.page) {
      this.open(action.page, row.key, action.mode);
      return;
    }
    if (action.confirm && !window.confirm(action.confirm)) return;
    this.error.set(null);
    this.notice.set(null);
    this.api.runAction(this.projectId, this.key, row.key, index).subscribe({
      next: (result) => {
        this.showResult(result, action.workflow ? $localize`:@@app.page.done:Готово.` : '');
        if (this.view()?.page.kind === 'detail') {
          // Видалений рядок на картці вже не відкрити; сценарій міг змінити дані — читаємо знову.
          if (action.workflow) this.loadRecord();
          else this.open(this.view()!.page.definition.create?.page ?? this.key);
        } else {
          this.expandVersion.update((n) => n + 1);
          if (this.view()?.page.kind === 'list') this.loadRows();
        }
      },
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.view.set(null);
    this.rows.set(null);
    this.record.set(null);
    this.metrics.set(null);
    this.notice.set(null);
    this.drawer.set(null);
    this.expanded.set(new Set());
    this.filterState.set({});
    this.filtersTouched = false;
    this.api.get(this.projectId, this.key).subscribe({
      next: (view) => {
        this.view.set(view);
        this.loading.set(false);
        this.sort.set(view.page.definition.sort?.column ?? null);
        this.descending.set(view.page.definition.sort?.direction === 'desc');
        this.search.set('');
        this.page.set(1);
        this.filterState.set(emptyFilters(view.page.definition.filters ?? []));
        if (view.page.kind === 'list') this.loadRows();
        else if (view.page.kind === 'detail') this.loadRecord();
        else if (view.page.kind === 'metrics') this.loadMetrics();
        else this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private loadRows(): void {
    this.loading.set(true);
    this.api
      .rows(this.projectId, this.key, {
        search: this.search() || undefined,
        sort: this.sort() ?? undefined,
        direction: this.sort() ? (this.descending() ? 'desc' : 'asc') : undefined,
        page: this.page(),
        filters: this.filtersTouched ? this.filterState() : undefined,
      })
      .subscribe({
        next: (rows) => {
          this.rows.set(rows);
          this.loading.set(false);
          // Значення за замовчуванням (`default`) діяли на сервері — панель показує їх, поки людина не змінила фільтри.
          if (!this.filtersTouched) {
            this.filterState.set({ ...emptyFilters(this.view()?.page.definition.filters ?? []), ...(rows.filters ?? {}) });
          }
        },
        error: (e: unknown) => this.fail(e),
      });
  }

  private loadMetrics(): void {
    this.api.metrics(this.projectId, this.key).subscribe({
      next: (metrics) => {
        this.metrics.set(metrics);
        this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  /** Повідомлення сценарію (`message`) або запасний текст; відмова й помилка — червоним. */
  private showResult(result: PageActionResult, fallback: string): void {
    const text =
      result.message ||
      (result.state === 'running'
        ? $localize`:@@app.page.running:Сценарій ще виконується, результат буде у виконаннях проєкту.`
        : result.state === 'success'
          ? fallback
          : $localize`:@@app.page.failed:Дію не виконано.`);
    this.notice.set(text ? { state: result.state, text } : null);
  }

  private loadRecord(): void {
    if (!this.rowKey) {
      this.loading.set(false);
      this.error.set($localize`:@@app.page.noRow:Картку відкривають зі списку: не вказано запис.`);
      return;
    }
    this.api.row(this.projectId, this.key, this.rowKey).subscribe({
      next: (record) => {
        this.record.set(record);
        this.loading.set(false);
      },
      error: (e: unknown) => this.fail(e),
    });
  }

  private fail(e: unknown): void {
    this.loading.set(false);
    this.error.set(
      e instanceof HttpErrorResponse && e.status === 404
        ? $localize`:@@app.page.notFound:Сторінку чи запис не знайдено або вони не для вашої ролі.`
        : apiErrorMessage(e),
    );
  }
}
