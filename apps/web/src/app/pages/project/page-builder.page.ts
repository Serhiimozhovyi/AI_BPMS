import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { catchError, debounceTime, forkJoin, map, of, switchMap, tap } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { FormDesignerComponent } from '../../forms/designer/form-designer.component';
import type { TableColumn } from '../../forms/designer/form-designer.model';
import type { JsonSchema } from '../../forms/form-fields';
import { ProjectContextService } from '../../projects/project-context.service';
import { SampleKind, sampleFor } from '../../projects/page-builder-options';
import {
  ActionRow,
  ChartRow,
  FILTER_OPERATORS,
  FilterRow,
  TileRow,
  VALUELESS_OPERATORS,
  emptyAction,
  emptyChart,
  emptyFilter,
  emptyTile,
  fromActions,
  fromCharts,
  fromFilters,
  fromTiles,
  toActions,
  toCharts,
  toFilters,
  toTiles,
} from '../../projects/page-builder.model';
import {
  PAGE_AGGREGATES,
  PAGE_KINDS,
  PageBlock,
  PageCalendar,
  PageColumn,
  PageDefinition,
  PageField,
  PageKind,
  PagePreview,
  ProjectPagesApiService,
} from '../../projects/project-pages-api.service';
import { DataTableSummary, DataTablesApiService } from '../../tables/data-tables-api.service';
import { WorkflowSummary, WorkflowsApiService } from '../../workflows/workflows-api.service';
import { HiddenRow, designerSchema, fromDesignerSchema, hiddenFields, hiddenRows } from './page-form-model';
import { BuilderCalendarComponent } from './builder/builder-calendar.component';
import { BuilderDashboardComponent } from './builder/builder-dashboard.component';
import { BuilderListOptionsComponent, ListExtras } from './builder/builder-list-options.component';
import { BuilderUserFiltersComponent } from './builder/builder-user-filters.component';
import { PagePreviewComponent } from './builder/page-preview.component';
import { BpButtonDirective } from '../../shared/button.directive';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';

type SubmitMode = 'insert' | 'upsert' | 'update' | 'workflow';

/**
 * `/projects/:id/app/_builder[/<key>]` — конструктор сторінки застосунку формою (F8-05, `docs/projects.md` §3.5):
 * тип, таблиця, колонки з таблиці, умови, дії зі списку сценаріїв проєкту. Те, чого форма не покриває (`related`,
 * `display`, фіксовані `value`), зберігається як було; усе визначення видно й редагується у вкладці JSON. Валідацію робить Api.
 */
@Component({
  selector: 'app-page-builder',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, NgTemplateOutlet, RouterLink, BuilderCalendarComponent, BuilderDashboardComponent, BuilderListOptionsComponent, BuilderUserFiltersComponent, PagePreviewComponent, FormDesignerComponent, MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    <div class="grid gap-6 px-6 pb-8 pt-2 xl:grid-cols-2">
    <section class="min-w-0" data-testid="page-builder">
      <h2 class="mb-3 text-lg font-semibold">
        @if (editing()) {
          <ng-container i18n="@@builder.edit">Сторінка «{{ name() }}»</ng-container>
        } @else {
          <ng-container i18n="@@builder.new">Нова сторінка</ng-container>
        }
      </h2>
      @if (error()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="builder-error">{{ error() }}</p>
      }

      <div class="mb-4 grid grid-cols-[minmax(9rem,auto)_1fr] items-center gap-x-4 gap-y-2">
        <label for="b-key" i18n="@@builder.key">Ключ (адреса)</label>
        <input id="b-key" class="border p-1" data-testid="b-key" [value]="key()" [disabled]="editing()" (input)="key.set($any($event.target).value)" />
        <label for="b-name" i18n="@@builder.name">Назва в меню</label>
        <input id="b-name" class="border p-1" data-testid="b-name" [value]="name()" (input)="name.set($any($event.target).value)" />
        <label id="b-kind-label" for="b-kind" i18n="@@builder.kind">Тип</label>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select id="b-kind" aria-labelledby="b-kind-label" data-testid="b-kind" [value]="kind()" [disabled]="editing()" (selectionChange)="kind.set($event.value)">
            <bp-select-search />
            @for (k of kinds; track k) {
              <mat-option [value]="k">{{ kindLabel(k) }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
        <label id="b-table-label" for="b-table" i18n="@@builder.table">Таблиця</label>
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select id="b-table" aria-labelledby="b-table-label" data-testid="b-table" [value]="table()" (selectionChange)="setTable($event.value)">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (t of tables(); track t.id) {
              <mat-option [value]="t.name">{{ t.name }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      </div>

      @if (!editing()) {
        <div class="mb-4 flex flex-wrap items-center gap-2">
          <label id="b-sample-label" for="b-sample" i18n="@@builder.sample">Почати зі зразка</label>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select id="b-sample" aria-labelledby="b-sample-label" data-testid="b-sample" value="" (selectionChange)="applySample($event.value); $event.source.value = ''">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              <mat-option value="list" i18n="@@builder.sample.list">Список з панеллю фільтрів</mat-option>
              <mat-option value="calendar" i18n="@@builder.sample.calendar">Календар</mat-option>
              <mat-option value="dashboard" i18n="@@builder.sample.dashboard">Головна</mat-option>
            </mat-select>
          </mat-form-field>
        </div>
      }

      @if (appRoles().length) {
        <fieldset class="mb-4">
          <legend class="font-medium" i18n="@@builder.roles">Кому видно сторінку (порожньо — всім учасникам)</legend>
          @for (r of appRoles(); track r.key) {
            <label class="mr-4 inline-flex items-center gap-1">
              <input type="checkbox" [attr.data-role]="r.key" [checked]="roles().includes(r.key)" (change)="roles.set(toggle(roles(), r.key, $any($event.target).checked))" />
              {{ r.name }}
            </label>
          }
        </fieldset>
      }

      @if (kind() === 'list') {
        <fieldset class="mb-4" data-testid="b-columns">
          <legend class="font-medium" i18n="@@builder.columns">Колонки списку</legend>
          @for (c of columnNames(); track c) {
            <label class="mr-4 inline-flex items-center gap-1">
              <input type="checkbox" [attr.data-column]="c" [checked]="columns().includes(c)" (change)="columns.set(toggleOrdered(columns(), c, $any($event.target).checked))" />
              {{ c }}
            </label>
          }
        </fieldset>
        <fieldset class="mb-4">
          <legend class="font-medium" i18n="@@builder.search">Пошук по колонках</legend>
          @for (c of columnNames(); track c) {
            <label class="mr-4 inline-flex items-center gap-1">
              <input type="checkbox" [checked]="search().includes(c)" (change)="search.set(toggle(search(), c, $any($event.target).checked))" />
              {{ c }}
            </label>
          }
        </fieldset>
        <div class="mb-4 flex flex-wrap items-center gap-2">
          <label id="b-sort-label" for="b-sort" i18n="@@builder.sort">Сортування</label>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select id="b-sort" aria-labelledby="b-sort-label" [value]="sortColumn()" (selectionChange)="sortColumn.set($event.value)">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              @for (c of columnNames(); track c) {
                <mat-option [value]="c">{{ c }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select aria-label="Напрямок" i18n-aria-label="@@builder.direction" [value]="sortDirection()" (selectionChange)="sortDirection.set($event.value)">
              <bp-select-search />
              <mat-option value="asc">↑</mat-option>
              <mat-option value="desc">↓</mat-option>
            </mat-select>
          </mat-form-field>
          <label for="b-size" i18n="@@builder.pageSize">Рядків на сторінці</label>
          <input id="b-size" class="w-20 border p-1" type="number" min="1" max="200" [value]="pageSize()" (input)="pageSize.set($any($event.target).value)" />
        </div>
        <app-builder-list-options
          [extras]="listExtras()"
          [columnNames]="columnNames()"
          [selected]="selectedColumns()"
          [tables]="tables()"
          [pageKeys]="pageKeys()"
          (extrasChange)="listExtras.set($event)"
          (columnsChange)="setColumnOverrides($event)"
        />
      }

      @if (kind() === 'calendar') {
        <app-builder-calendar [calendar]="calendar()" [columns]="columnNames()" [pageKeys]="pageKeys()" (calendarChange)="calendar.set($event)" />
      }

      @if (kind() === 'calendar' || kind() === 'dashboard') {
        <h3 class="mb-1 mt-4 font-medium" i18n="@@bo.list.filters">Фільтри над списком</h3>
        <app-builder-user-filters [filters]="listExtras().filters ?? []" [columns]="columnNames()" (changed)="listExtras.set({ ...listExtras(), filters: $event.length ? $event : undefined })" />
      }

      @if (kind() === 'dashboard') {
        <app-builder-dashboard [blocks]="blocks()" [pageKeys]="pageKeys()" [workflows]="workflowNames()" [appRoles]="appRoles()" (blocksChange)="blocks.set($event)" />
      }

      @if (kind() === 'detail') {
        <fieldset class="mb-4" data-testid="b-fields">
          <legend class="font-medium" i18n="@@builder.fields">Поля</legend>
          @for (c of columnNames(); track c) {
            <span class="mr-4 inline-flex items-center gap-1">
              <label class="inline-flex items-center gap-1">
                <input type="checkbox" [attr.data-field]="c" [checked]="fields().includes(c)" (change)="fields.set(toggleOrdered(fields(), c, $any($event.target).checked))" />
                {{ c }}
              </label>
            </span>
          }
        </fieldset>
      }

      @if (kind() === 'form') {
        <fieldset class="mb-4" data-testid="b-fields">
          <legend class="font-medium" i18n="@@builder.fields">Поля</legend>
          @if (table()) {
            <bp-form-designer [schema]="formSchema()" [columns]="designerColumns()" (schemaChange)="formSchema.set($event)" />
          } @else {
            <p class="text-sm opacity-70" data-testid="b-fields-hint" i18n="@@builder.fields.pickTable">Оберіть таблицю — її колонки стануть полями форми.</p>
          }
        </fieldset>
        <fieldset class="mb-4" data-testid="b-hidden">
          <legend class="font-medium" i18n="@@builder.hidden">Приховані поля (ставить сервер)</legend>
          <p class="mb-2 text-sm opacity-70" i18n="@@builder.hidden.hint">Не показуються на формі: значення ставить сервер — вираз (наприклад, {{ '{{ $user.id }}' }}) або копія з рядка, вибраного в полі-посиланні форми.</p>
          @for (h of hiddenList(); track $index; let i = $index) {
            <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-hidden]="i">
              <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                <mat-select aria-label="Колонка" i18n-aria-label="@@builder.hidden.column" [attr.data-testid]="'b-hidden-column-' + i" [value]="h.column" (selectionChange)="setHidden(i, { column: $event.value })">
                  <bp-select-search />
                  @for (c of columnNames(); track c) {
                    <mat-option [value]="c">{{ c }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                <mat-select aria-label="Звідки значення" i18n-aria-label="@@builder.hidden.mode" [attr.data-testid]="'b-hidden-mode-' + i" [value]="h.mode" (selectionChange)="setHidden(i, { mode: $event.value })">
                  <bp-select-search />
                  <mat-option value="value" i18n="@@builder.hidden.value">Значення або вираз</mat-option>
                  <mat-option value="copy" i18n="@@builder.hidden.copy">Копія з пов'язаного рядка</mat-option>
                </mat-select>
              </mat-form-field>
              <input class="w-64 border p-1" [attr.data-testid]="'b-hidden-text-' + i" [placeholder]="h.mode === 'copy' ? hints.copyFrom : hints.value" [attr.aria-label]="h.mode === 'copy' ? hints.copyFrom : hints.value" [value]="h.text" (input)="setHidden(i, { text: $any($event.target).value })" />
              <button mat-button type="button" bpVariant="danger" [attr.data-testid]="'b-hidden-remove-' + i" (click)="hiddenList.set(removeAt(hiddenList(), i))" i18n="@@builder.remove">Прибрати</button>
            </div>
          }
          <button mat-stroked-button type="button" bpVariant="tonal" data-testid="b-hidden-add" (click)="hiddenList.set([...hiddenList(), { column: '', mode: 'value', text: '' }])" i18n="@@builder.hidden.add">Додати приховане поле</button>
        </fieldset>
      }

      @if (kind() === 'form') {
        <div class="mb-4 flex flex-wrap items-center gap-2">
          <label id="b-submit-label" for="b-submit" i18n="@@builder.onSubmit">Що робить форма</label>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select id="b-submit" aria-labelledby="b-submit-label" data-testid="b-submit" [value]="submitMode()" (selectionChange)="submitMode.set($event.value)">
              <bp-select-search />
              <mat-option value="insert" i18n="@@builder.submit.insert">Додає рядок</mat-option>
              <mat-option value="upsert" i18n="@@builder.submit.upsert">Додає або оновлює за ключем</mat-option>
              <mat-option value="update" i18n="@@builder.submit.update">Змінює рядок</mat-option>
              <mat-option value="workflow" i18n="@@builder.submit.workflow">Запускає сценарій</mat-option>
            </mat-select>
          </mat-form-field>
          @if (submitMode() === 'workflow') {
            <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
              <mat-select data-testid="b-submit-workflow" aria-label="Сценарій" i18n-aria-label="@@builder.workflow" [value]="submitWorkflow()" (selectionChange)="submitWorkflow.set($event.value)">
                <bp-select-search />
                <mat-option value="">—</mat-option>
                @for (w of workflows(); track w.id) {
                  <mat-option [value]="w.name">{{ w.name }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          }
          <label id="b-then-label" for="b-then" i18n="@@builder.then">Після — перейти на</label>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select id="b-then" aria-labelledby="b-then-label" [value]="thenPage()" (selectionChange)="thenPage.set($event.value)">
              <bp-select-search />
              <mat-option value="">—</mat-option>
              @for (p of pageKeys(); track p) {
                <mat-option [value]="p">{{ p }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>
      }

      @if (kind() !== 'metrics' && kind() !== 'dashboard') {
        <h3 class="mb-1 mt-4 font-medium" i18n="@@builder.filters">Умови (діють на сервері, їх не можна зняти)</h3>
        @for (f of filters(); track $index; let i = $index) {
          <div class="mb-1 flex flex-wrap items-center gap-2" [attr.data-filter]="i">
            <ng-container *ngTemplateOutlet="filterTpl; context: { $implicit: f, columns: columnNames(), set: setFilter(i) }" />
            <button mat-button type="button" (click)="filters.set(removeAt(filters(), i))" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
          </div>
        }
        <button mat-stroked-button type="button" data-testid="b-add-filter" (click)="filters.set([...filters(), emptyFilter()])" i18n="@@builder.addFilter" bpVariant="tonal">Додати умову</button>

        @if (kind() !== 'form') {
          <h3 class="mb-1 mt-4 font-medium" i18n="@@builder.actions">Дії над рядком</h3>
          @for (a of actions(); track $index; let i = $index) {
            <div class="mb-2 flex flex-wrap items-center gap-2" [attr.data-action]="i">
              <input class="w-40 border p-1" placeholder="Підпис" i18n-placeholder="@@builder.action.label" [value]="a.label" (input)="setAction(i, { label: $any($event.target).value })" />
              <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                <mat-select aria-label="Вид дії" i18n-aria-label="@@builder.action.type" [value]="a.type" (selectionChange)="setAction(i, { type: $event.value, target: '' })">
                  <bp-select-search />
                  <mat-option value="page" i18n="@@builder.action.page">Відкрити сторінку</mat-option>
                  <mat-option value="workflow" i18n="@@builder.action.workflow">Запустити сценарій</mat-option>
                  <mat-option value="delete" i18n="@@builder.action.delete">Видалити рядок</mat-option>
                </mat-select>
              </mat-form-field>
              @if (a.type === 'page') {
                <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                  <mat-select aria-label="Сторінка" i18n-aria-label="@@builder.action.target.page" [value]="a.target" (selectionChange)="setAction(i, { target: $event.value })">
                    <bp-select-search />
                    <mat-option value="">—</mat-option>
                    @for (p of pageKeys(); track p) {
                      <mat-option [value]="p">{{ p }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
                <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                  <mat-select aria-label="Як відкрити" i18n-aria-label="@@bc.mode" [value]="a.mode" (selectionChange)="setAction(i, { mode: $event.value })">
                    <bp-select-search />
                    <mat-option value="navigate" i18n="@@bc.mode.navigate">переходом</mat-option>
                    <mat-option value="drawer" i18n="@@bc.mode.drawer">бічною панеллю</mat-option>
                  </mat-select>
                </mat-form-field>
              } @else if (a.type === 'workflow') {
                <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                  <mat-select aria-label="Сценарій" i18n-aria-label="@@builder.action.target.workflow" [value]="a.target" (selectionChange)="setAction(i, { target: $event.value })">
                    <bp-select-search />
                    <mat-option value="">—</mat-option>
                    @for (w of workflows(); track w.id) {
                      <mat-option [value]="w.name">{{ w.name }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
              }
              <input class="w-48 border p-1" placeholder="Запитати підтвердження" i18n-placeholder="@@builder.action.confirm" [value]="a.confirm" (input)="setAction(i, { confirm: $any($event.target).value })" />
              <input class="w-56 border p-1" [placeholder]="hints.when" [attr.aria-label]="hints.whenLabel" [value]="a.when" (input)="setAction(i, { when: $any($event.target).value })" />
              @for (r of appRoles(); track r.key) {
                <label class="inline-flex items-center gap-1 opacity-80">
                  <input type="checkbox" [checked]="a.roles.includes(r.key)" (change)="setAction(i, { roles: toggle(a.roles, r.key, $any($event.target).checked) })" />
                  {{ r.name }}
                </label>
              }
              <button mat-button type="button" (click)="actions.set(removeAt(actions(), i))" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
            </div>
          }
          <button mat-stroked-button type="button" data-testid="b-add-action" (click)="actions.set([...actions(), emptyAction()])" i18n="@@builder.addAction" bpVariant="tonal">Додати дію</button>
        }

        @if (kind() === 'list' || kind() === 'calendar') {
          <div class="mb-4 mt-4 flex items-center gap-2">
            <label id="b-create-label" for="b-create" i18n="@@builder.create">Кнопка «Створити» веде на форму</label>
            <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
              <mat-select id="b-create" aria-labelledby="b-create-label" [value]="createPage()" (selectionChange)="createPage.set($event.value)">
                <bp-select-search />
                <mat-option value="">—</mat-option>
                @for (p of pageKeys(); track p) {
                  <mat-option [value]="p">{{ p }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            @if (createPage()) {
              <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
                <mat-select aria-label="Як відкрити" i18n-aria-label="@@bc.mode" [value]="createMode()" (selectionChange)="createMode.set($event.value)">
                  <bp-select-search />
                  <mat-option value="navigate" i18n="@@bc.mode.navigate">переходом</mat-option>
                  <mat-option value="drawer" i18n="@@bc.mode.drawer">бічною панеллю</mat-option>
                </mat-select>
              </mat-form-field>
            }
          </div>
        }
      } @else if (kind() === 'metrics') {
        <h3 class="mb-1 font-medium" i18n="@@builder.tiles">Плитки</h3>
        @for (t of tiles(); track $index; let i = $index) {
          <div class="mb-2 flex flex-wrap items-center gap-2" [attr.data-tile]="i">
            <input class="w-40 border p-1" placeholder="Підпис" i18n-placeholder="@@builder.tile.label" [value]="t.label" (input)="setTile(i, { label: $any($event.target).value })" />
            <input class="w-24 border p-1" placeholder="ключ" i18n-placeholder="@@builder.tile.key" [value]="t.key" (input)="setTile(i, { key: $any($event.target).value })" />
            <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
              <mat-select aria-label="Що показує плитка" i18n-aria-label="@@builder.tile.mode" [value]="t.mode" (selectionChange)="setTile(i, { mode: $event.value })">
                <bp-select-search />
                <mat-option value="aggregate" i18n="@@builder.tile.aggregate">Агрегат</mat-option>
                <mat-option value="expression" i18n="@@builder.tile.expression">Вираз над плитками</mat-option>
              </mat-select>
            </mat-form-field>
            @if (t.mode === 'expression') {
              <input class="w-72 border p-1" [placeholder]="hints.expression" [attr.aria-label]="hints.expressionLabel" [value]="t.expression" (input)="setTile(i, { expression: $any($event.target).value })" />
            } @else {
              <ng-container *ngTemplateOutlet="aggregateTpl; context: { $implicit: t, set: setTile(i) }" />
              <ng-container *ngTemplateOutlet="filterTpl; context: { $implicit: t.where, columns: columnsOf(t.table), set: setTileFilter(i) }" />
            }
            <button mat-button type="button" (click)="tiles.set(removeAt(tiles(), i))" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
          </div>
        }
        <button mat-stroked-button type="button" data-testid="b-add-tile" (click)="tiles.set([...tiles(), emptyTile()])" i18n="@@builder.addTile" bpVariant="tonal">Додати плитку</button>

        <h3 class="mb-1 mt-4 font-medium" i18n="@@builder.charts">Діаграми</h3>
        @for (c of charts(); track $index; let i = $index) {
          <div class="mb-2 flex flex-wrap items-center gap-2" [attr.data-chart]="i">
            <input class="w-40 border p-1" placeholder="Підпис" i18n-placeholder="@@builder.tile.label" [value]="c.label" (input)="setChart(i, { label: $any($event.target).value })" />
            <input class="w-48 border p-1" placeholder="групувати: колонка або month(колонка)" i18n-placeholder="@@builder.chart.groupBy" [value]="c.groupBy" (input)="setChart(i, { groupBy: $any($event.target).value })" />
            <ng-container *ngTemplateOutlet="aggregateTpl; context: { $implicit: c, set: setChart(i) }" />
            <ng-container *ngTemplateOutlet="filterTpl; context: { $implicit: c.where, columns: columnsOf(c.table), set: setChartFilter(i) }" />
            <button mat-button type="button" (click)="charts.set(removeAt(charts(), i))" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
          </div>
        }
        <button mat-stroked-button type="button" data-testid="b-add-chart" (click)="charts.set([...charts(), emptyChart()])" i18n="@@builder.addChart" bpVariant="tonal">Додати діаграму</button>
      }

      <details class="mt-6" [open]="showJson()" (toggle)="showJson.set($any($event.target).open)">
        <summary i18n="@@builder.json">JSON визначення (для того, чого немає у формі)</summary>
        <textarea class="mt-2 h-64 w-full border p-2 font-mono" data-testid="b-json" [value]="jsonText()" (input)="jsonDraft.set($any($event.target).value)"></textarea>
        <button mat-stroked-button type="button" data-testid="b-json-apply" (click)="applyJson()" i18n="@@builder.json.apply">Застосувати JSON</button>
      </details>

      <div class="mt-6 flex gap-2">
        <button mat-flat-button type="button" data-testid="b-save" [disabled]="saving()" (click)="save()" i18n="@@builder.save">Зберегти</button>
        <a mat-button [routerLink]="['/projects', projectId, 'app']" i18n="@@builder.cancel">Скасувати</a>
        @if (editing()) {
          <button bpVariant="danger" mat-button type="button" data-testid="b-delete" (click)="remove()" i18n="@@builder.delete">Видалити сторінку</button>
        }
      </div>
    </section>

    <aside class="min-w-0 xl:sticky xl:top-2 xl:max-h-[calc(100vh-1rem)] xl:self-start xl:overflow-auto" data-testid="builder-preview" aria-live="polite">
      <h3 class="mb-2 font-medium" i18n="@@builder.preview">Перегляд на даних проєкту</h3>
      @if (previewError()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="preview-error">{{ previewError() }}</p>
      }
      @if (previewData(); as p) {
        <div class="rounded border border-bp-line p-3" [class.opacity-50]="previewError()">
          <app-page-preview [data]="p" />
        </div>
      } @else if (!previewError()) {
        <p class="opacity-70" i18n="@@builder.preview.none">Виберіть таблицю — тут з'явиться сторінка.</p>
      }
    </aside>
    </div>

    <ng-template #filterTpl let-row let-columns="columns" let-set="set">
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select aria-label="Колонка умови" i18n-aria-label="@@builder.filter.column" [value]="row.column" (selectionChange)="set({ column: $event.value })">
          <bp-select-search />
          <mat-option value="">—</mat-option>
          @for (c of columns; track c) {
            <mat-option [value]="c">{{ c }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select aria-label="Оператор" i18n-aria-label="@@builder.filter.operator" [value]="row.operator" (selectionChange)="set({ operator: $event.value })">
          <bp-select-search />
          @for (o of operators; track o) {
            <mat-option [value]="o">{{ o }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      @if (!valueless(row.operator)) {
        <input class="w-48 border p-1" [placeholder]="hints.value" [attr.aria-label]="hints.value" [value]="row.value" (input)="set({ value: $any($event.target).value })" />
      }
    </ng-template>

    <ng-template #aggregateTpl let-row let-set="set">
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select aria-label="Агрегат" i18n-aria-label="@@builder.aggregate" [value]="row.aggregate" (selectionChange)="set({ aggregate: $event.value })">
          <bp-select-search />
          @for (a of aggregates; track a) {
            <mat-option [value]="a">{{ a }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
      @if (row.aggregate !== 'count') {
        <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
          <mat-select aria-label="Числова колонка" i18n-aria-label="@@builder.aggregate.column" [value]="row.column" (selectionChange)="set({ column: $event.value })">
            <bp-select-search />
            <mat-option value="">—</mat-option>
            @for (c of numberColumns(row.table); track c) {
              <mat-option [value]="c">{{ c }}</mat-option>
            }
          </mat-select>
        </mat-form-field>
      }
      <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
        <mat-select aria-label="Таблиця" i18n-aria-label="@@builder.aggregate.table" [value]="row.table" (selectionChange)="set({ table: $event.value })">
          <bp-select-search />
          <mat-option value="" i18n="@@builder.aggregate.pageTable">таблиця сторінки</mat-option>
          @for (t of tables(); track t.id) {
            <mat-option [value]="t.name">{{ t.name }}</mat-option>
          }
        </mat-select>
      </mat-form-field>
    </ng-template>
  `,
})
export class PageBuilderPage implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  private readonly tablesApi = inject(DataTablesApiService);
  private readonly workflowsApi = inject(WorkflowsApiService);
  private readonly context = inject(ProjectContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  /** Підказки з прикладами виразів (фігурні дужки не можна писати в шаблоні напряму). */
  readonly hints = {
    when: $localize`:@@builder.hint.when:показувати, якщо {{ $row.status === 'active' }}`,
    whenLabel: $localize`:@@builder.hint.whenLabel:Умова показу дії`,
    expression: $localize`:@@builder.hint.expression:{{ $tiles.booked / $tiles.slots }}`,
    expressionLabel: $localize`:@@builder.hint.expressionLabel:Вираз над плитками`,
    value: $localize`:@@builder.hint.value:значення або {{ $user.id }}`,
    copyFrom: $localize`:@@builder.hint.copyFrom:колонка_посилання.колонка`,
  };

  readonly kinds = PAGE_KINDS;
  readonly aggregates = PAGE_AGGREGATES;
  readonly operators = FILTER_OPERATORS;
  readonly emptyFilter = emptyFilter;
  readonly emptyAction = emptyAction;
  readonly emptyTile = emptyTile;
  readonly emptyChart = emptyChart;

  /** `projects/:id` → `app` → `_builder`: проєкт — у діда маршруту. */
  readonly projectId = this.route.parent?.parent?.snapshot.paramMap.get('id') ?? this.route.parent?.snapshot.paramMap.get('id') ?? '';
  readonly editing = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);

  readonly tables = signal<DataTableSummary[]>([]);
  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly pageKeys = signal<string[]>([]);
  readonly appRoles = computed(() => this.context.projects().find((p) => p.id === this.projectId)?.appRoles ?? []);

  readonly key = signal('');
  readonly name = signal('');
  readonly kind = signal<PageKind>('list');
  readonly roles = signal<string[]>([]);
  readonly table = signal('');
  readonly columns = signal<string[]>([]);
  readonly search = signal<string[]>([]);
  readonly sortColumn = signal('');
  readonly sortDirection = signal<'asc' | 'desc'>('asc');
  readonly pageSize = signal('');
  readonly fields = signal<string[]>([]);
  /** Схема форми в конструкторі (F11-05): видимі поля, розмітка; джерело правди для `fields`/`formLayout` форми. */
  readonly formSchema = signal<JsonSchema>({ type: 'object', properties: {} });
  /** Поля форми, які ставить сервер (`value`/`copyFrom`) — поза конструктором. */
  readonly hiddenList = signal<HiddenRow[]>([]);
  readonly submitMode = signal<SubmitMode>('insert');
  readonly submitWorkflow = signal('');
  readonly thenPage = signal('');
  readonly createPage = signal('');
  readonly createMode = signal<'navigate' | 'drawer'>('navigate');
  /** Параметри колонок (формат, підписи, кольори, `display`) вибраних колонок списку. */
  readonly columnOverrides = signal<PageColumn[]>([]);
  /** Фільтри користувача, обчислювані колонки, картки, `expand`, `empty` (F8-14). */
  readonly listExtras = signal<ListExtras>({});
  readonly calendar = signal<PageCalendar>({ start: '', defaultMinutes: 60 });
  readonly blocks = signal<PageBlock[]>([]);
  private readonly loaded = signal(false);
  readonly previewData = signal<PagePreview | null>(null);
  readonly previewError = signal<string | null>(null);
  readonly filters = signal<FilterRow[]>([]);
  readonly actions = signal<ActionRow[]>([]);
  readonly tiles = signal<TileRow[]>([]);
  readonly charts = signal<ChartRow[]>([]);
  readonly showJson = signal(false);
  readonly jsonDraft = signal<string | null>(null);

  /** Те, що форма не редагує (`related`, `display`, фіксовані `value`…), — зберігається як було. */
  private base: PageDefinition = { table: '' };

  readonly columnNames = computed(() => this.columnsOf(this.table()));
  readonly workflowNames = computed(() => this.workflows().map((w) => w.name));
  /** Колонки таблиці для палітри конструктора форми: без тих, що вже сховані (ставить сервер). */
  readonly designerColumns = computed<TableColumn[]>(() => {
    const hidden = new Set(this.hiddenList().map((h) => h.column));
    return this.tableColumnsOf(this.table()).filter((c) => !hidden.has(c.name));
  });
  readonly selectedColumns = computed<PageColumn[]>(() => this.columns().map((column) => this.columnOverrides().find((c) => c.column === column) ?? { column }));

  readonly definition = computed<PageDefinition>(() => {
    const kind = this.kind();
    const table = this.table();
    if (kind === 'metrics') {
      return { table, tiles: toTiles(this.tiles()), charts: toCharts(this.charts()) };
    }

    const extras = this.listExtras();
    if (kind === 'dashboard') {
      const dash: PageDefinition = { table };
      if (extras.filters?.length) dash.filters = extras.filters;
      dash.blocks = this.blocks();
      return dash;
    }

    const def: PageDefinition = { table };
    const filter = toFilters(this.filters());
    if (filter.length) def.filter = filter;
    if (kind === 'calendar') {
      def.calendar = this.calendar();
      if (extras.filters?.length) def.filters = extras.filters;
      const create = this.createPage();
      if (create) def.create = this.createMode() === 'drawer' ? { page: create, mode: 'drawer' } : { page: create };
    } else if (kind === 'list') {
      def.columns = this.selectedColumns();
      Object.assign(def, extras);
      if (this.search().length) def.search = this.search();
      if (this.sortColumn()) def.sort = { column: this.sortColumn(), direction: this.sortDirection() };
      if (this.pageSize()) def.pageSize = Number(this.pageSize());
      const create = this.createPage();
      if (create) def.create = this.createMode() === 'drawer' ? { page: create, mode: 'drawer' } : { page: create };
    } else if (kind === 'form') {
      const base = this.base.fields ?? [];
      const result = fromDesignerSchema(this.formSchema(), base, hiddenFields(this.hiddenList(), base));
      def.fields = result.fields;
      if (result.formLayout) def.formLayout = result.formLayout;
    } else {
      def.fields = this.fields().map((column): PageField => {
        const kept = this.base.fields?.find((f) => f.column === column);
        return { ...(kept ?? { column }) };
      });
      if (this.base.related?.length) def.related = this.base.related;
    }

    if (kind !== 'form') {
      const actions = toActions(this.actions());
      if (actions.length) def.actions = actions;
    } else {
      def.onSubmit = this.submitMode() === 'workflow' ? { workflow: this.submitWorkflow() } : { operation: this.submitMode() as 'insert' | 'upsert' | 'update' };
      if (this.thenPage()) def.then = { page: this.thenPage() };
    }

    return def;
  });

  readonly jsonText = computed(() => this.jsonDraft() ?? JSON.stringify(this.definition(), null, 2));

  constructor() {
    const request = computed(() => {
      const kind = this.kind();
      const ready = this.table() || kind === 'metrics' || kind === 'dashboard';
      if (!ready || !this.loaded()) return null;
      const key = this.key().trim();
      return { key: /^[a-z0-9][a-z0-9_-]*$/i.test(key) ? key : 'preview', input: { name: this.name().trim() || key || 'preview', kind, definition: this.definition(), roles: this.roles() } };
    });
    toObservable(request)
      .pipe(
        debounceTime(500),
        switchMap((r) =>
          r
            ? this.api.preview(this.projectId, r.key, r.input).pipe(
                tap(() => this.previewError.set(null)),
                map((data): PagePreview | null => data),
                catchError((e: unknown) => {
                  this.previewError.set(apiErrorMessage(e));
                  return of(undefined);
                }),
              )
            : of(null),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((data) => {
        if (data === undefined) return;
        this.previewData.set(data);
      });
  }

  ngOnInit(): void {
    const editKey = this.route.snapshot.paramMap.get('key');
    forkJoin({
      tables: this.tablesApi.list(),
      workflows: this.workflowsApi.list({}),
      pages: this.api.list(this.projectId),
      page: editKey ? this.api.get(this.projectId, editKey) : of(null),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ tables, workflows, pages, page }) => {
          this.tables.set(tables.filter((t) => !t.projectId || t.projectId === this.projectId));
          this.workflows.set(workflows.items.filter((w) => w.groupId === this.projectId));
          this.pageKeys.set(pages.map((p) => p.key));
          this.loaded.set(true);
          if (page) {
            this.editing.set(true);
            this.key.set(page.page.key);
            this.name.set(page.page.name);
            this.kind.set(page.page.kind);
            this.roles.set(page.page.roles);
            this.load(page.page.definition);
          }
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  kindLabel(kind: PageKind): string {
    switch (kind) {
      case 'list':
        return $localize`:@@builder.kind.list:Список`;
      case 'detail':
        return $localize`:@@builder.kind.detail:Картка`;
      case 'form':
        return $localize`:@@builder.kind.form:Форма`;
      case 'calendar':
        return $localize`:@@builder.kind.calendar:Календар`;
      case 'dashboard':
        return $localize`:@@builder.kind.dashboard:Головна`;
      default:
        return $localize`:@@builder.kind.metrics:Показники`;
    }
  }

  valueless(operator: string): boolean {
    return VALUELESS_OPERATORS.includes(operator);
  }

  columnsOf(table: string): string[] {
    const name = table || this.table();
    return this.tables().find((t) => t.name.toLowerCase() === name.toLowerCase())?.columns.map((c) => c.name) ?? [];
  }

  /** Колонки таблиці для конструктора форми (тип колонки задає тип поля). */
  tableColumnsOf(table: string): TableColumn[] {
    const found = this.tables().find((t) => t.name.toLowerCase() === table.toLowerCase());
    return (found?.columns ?? []).map((c) => ({ name: c.name, type: c.type, values: c.values }));
  }

  /** Інша таблиця — інші колонки: поля форми й приховані поля попередньої таблиці не лишаються. */
  setTable(name: string): void {
    if (name === this.table()) return;
    this.table.set(name);
    this.formSchema.set({ type: 'object', properties: {} });
    this.hiddenList.set([]);
  }

  setHidden(index: number, patch: Partial<HiddenRow>): void {
    this.hiddenList.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  numberColumns(table: string): string[] {
    const name = (table || this.table()).toLowerCase();
    return this.tables().find((t) => t.name.toLowerCase() === name)?.columns.filter((c) => c.type === 'number' && !c.sensitive).map((c) => c.name) ?? [];
  }

  toggle(list: string[], value: string, on: boolean): string[] {
    return on ? (list.includes(value) ? list : [...list, value]) : list.filter((v) => v !== value);
  }

  /** Вибір колонок зберігає порядок таблиці. */
  toggleOrdered(list: string[], value: string, on: boolean): string[] {
    const next = this.toggle(list, value, on);
    const order = this.columnNames();
    return [...next].sort((a, b) => order.indexOf(a) - order.indexOf(b));
  }

  removeAt<T>(list: T[], index: number): T[] {
    return list.filter((_, i) => i !== index);
  }

  setFilter(index: number): (patch: Partial<FilterRow>) => void {
    return (patch) => this.filters.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  setAction(index: number, patch: Partial<ActionRow>): void {
    this.actions.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }

  setTile(index: number, patch: Partial<TileRow>): void;
  setTile(index: number): (patch: Partial<TileRow>) => void;
  setTile(index: number, patch?: Partial<TileRow>): ((patch: Partial<TileRow>) => void) | void {
    const apply = (p: Partial<TileRow>) => this.tiles.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...p } : r)));
    if (patch) apply(patch);
    else return apply;
  }

  setTileFilter(index: number): (patch: Partial<FilterRow>) => void {
    return (patch) => this.tiles.update((rows) => rows.map((r, i) => (i === index ? { ...r, where: { ...r.where, ...patch } } : r)));
  }

  setChart(index: number, patch: Partial<ChartRow>): void;
  setChart(index: number): (patch: Partial<ChartRow>) => void;
  setChart(index: number, patch?: Partial<ChartRow>): ((patch: Partial<ChartRow>) => void) | void {
    const apply = (p: Partial<ChartRow>) => this.charts.update((rows) => rows.map((r, i) => (i === index ? { ...r, ...p } : r)));
    if (patch) apply(patch);
    else return apply;
  }

  setChartFilter(index: number): (patch: Partial<FilterRow>) => void {
    return (patch) => this.charts.update((rows) => rows.map((r, i) => (i === index ? { ...r, where: { ...r.where, ...patch } } : r)));
  }

  setColumnOverrides(next: PageColumn[]): void {
    this.columnOverrides.update((all) => [...all.filter((c) => !next.some((n) => n.column === c.column)), ...next]);
  }

  /** Підставляє зразок за таблицею (вибраною чи першою) і зберігає ключ, якщо його вже введено. */
  applySample(kind: string): void {
    if (!kind) return;
    const table = this.tables().find((t) => t.name === this.table()) ?? this.tables()[0];
    const sample = sampleFor(kind as SampleKind, table, this.pageKeys());
    this.key.set(sample.key);
    this.name.set(sample.name);
    this.kind.set(sample.kind);
    this.load(sample.definition);
  }

  /** Застосовує текст JSON до форми; хибний JSON лишає форму без змін. */
  applyJson(): void {
    const draft = this.jsonDraft();
    if (draft === null) return;
    try {
      const parsed = JSON.parse(draft) as PageDefinition;
      this.error.set(null);
      this.load(parsed);
      this.jsonDraft.set(null);
    } catch {
      this.error.set($localize`:@@builder.json.invalid:JSON визначення некоректний.`);
    }
  }

  save(): void {
    this.error.set(null);
    this.saving.set(true);
    const key = this.key().trim();
    this.api.save(this.projectId, key, { name: this.name().trim(), kind: this.kind(), definition: this.definition(), roles: this.roles() }).subscribe({
      next: () => {
        this.saving.set(false);
        void this.router.navigate(['/projects', this.projectId, 'app', key]);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  remove(): void {
    if (!window.confirm($localize`:@@builder.delete.confirm:Видалити сторінку? Дані таблиць не зачіпаються.`)) return;
    this.api.remove(this.projectId, this.key()).subscribe({
      next: () => void this.router.navigate(['/projects', this.projectId, 'app']),
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }

  private load(def: PageDefinition): void {
    this.base = def;
    this.table.set(def.table ?? '');
    this.columns.set((def.columns ?? []).map((c) => c.column));
    this.search.set(def.search ?? []);
    this.sortColumn.set(def.sort?.column ?? '');
    this.sortDirection.set(def.sort?.direction ?? 'asc');
    this.pageSize.set(def.pageSize ? String(def.pageSize) : '');
    this.fields.set((def.fields ?? []).map((f) => f.column));
    this.formSchema.set(designerSchema(def, this.tableColumnsOf(def.table ?? '')));
    this.hiddenList.set(hiddenRows(def.fields));
    this.submitMode.set(def.onSubmit?.workflow ? 'workflow' : ((def.onSubmit?.operation as SubmitMode | undefined) ?? 'insert'));
    this.submitWorkflow.set(def.onSubmit?.workflow ?? '');
    this.thenPage.set(def.then?.page ?? '');
    this.createPage.set(def.create?.page ?? '');
    this.createMode.set(def.create?.mode === 'drawer' ? 'drawer' : 'navigate');
    this.columnOverrides.set(def.columns ?? []);
    this.listExtras.set({
      ...(def.filters?.length ? { filters: def.filters } : {}),
      ...(def.computed?.length ? { computed: def.computed } : {}),
      ...(def.layout ? { layout: def.layout } : {}),
      ...(def.card ? { card: def.card } : {}),
      ...(def.expand?.length ? { expand: def.expand } : {}),
      ...(def.empty ? { empty: def.empty } : {}),
    });
    this.calendar.set(def.calendar ?? { start: '', defaultMinutes: 60 });
    this.blocks.set(def.blocks ?? []);
    this.filters.set(fromFilters(def.filter));
    this.actions.set(fromActions(def.actions));
    this.tiles.set(fromTiles(def.tiles));
    this.charts.set(fromCharts(def.charts));
  }
}
