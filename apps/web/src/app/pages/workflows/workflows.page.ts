import { CdkDrag, CdkDragDrop, CdkDragHandle, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatChipsModule } from '@angular/material/chips';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggle, MatSlideToggleModule } from '@angular/material/slide-toggle';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Observable, forkJoin } from 'rxjs';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ExecutionState, executionStateLabel } from '../../executions/executions-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { CreateWorkflowDialog, CreateWorkflowDialogData, CreateWorkflowResult } from '../../workflows/create-workflow-dialog.component';
import { ImportWorkflowDialog, ImportWorkflowDialogData } from '../../workflows/import-workflow-dialog.component';
import { TriggerInfo, triggerInfo } from '../../workflows/trigger-types';
import { NO_GROUP, WorkflowSection, groupSections, readCollapsed, recount, reorder, writeCollapsed } from '../../workflows/workflow-groups';
import { WorkflowDetail, WorkflowGroup, WorkflowImportResult, WorkflowSummary, WorkflowsApiService, toSummary } from '../../workflows/workflows-api.service';
import { CardTableDirective } from '../../shared/card-table.directive';

/**
 * `/workflows`: список сценаріїв, створення, перейменування, активація, видалення, перехід у канвас (F2-15); групи
 * (F3-64): секції груп зі згортанням, фільтр-чіпи й пошук, створення/перейменування/видалення груп, порядок груп і
 * перенесення сценаріїв перетягуванням або з меню рядка. `?group=<id>` — відкрити з фільтром за групою. Експорт у файл
 * і імпорт з файлу з перемапінгом конекторів і облікових даних (F6-09). Групи — це проєкти (F6-14): вибраний у шапці
 * проєкт стає фільтром, «Створити» створює сценарій у ньому, меню групи веде до її учасників.
 */
@Component({
  selector: 'app-workflows-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CardTableDirective,
    DatePipe,
    ReactiveFormsModule,
    RouterLink,
    CdkDropList,
    CdkDropListGroup,
    CdkDrag,
    CdkDragHandle,
    MatButtonModule,
    MatChipsModule,
    MatFormFieldModule,
    MatInputModule,
    MatMenuModule,
    MatProgressBarModule,
    MatSlideToggleModule,
  ],
  template: `
    <section class="p-6">
      <div class="mb-4 flex flex-wrap items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@workflows.title">Сценарії</h1>
        <button mat-flat-button type="button" data-testid="create-button" (click)="startCreate()" i18n="@@workflows.create">
          Створити сценарій
        </button>
        @if (!creatingGroup()) {
          <button mat-stroked-button type="button" data-testid="new-group-button" (click)="startCreateGroup()" i18n="@@workflows.groups.new">
            Нова група
          </button>
        }
        <button mat-stroked-button type="button" data-testid="import-button" (click)="openImport()" i18n="@@workflows.import">Імпорт</button>
      </div>

      @if (creatingGroup()) {
        <form class="mb-6 flex max-w-2xl items-start gap-2" data-testid="group-form" [formGroup]="groupForm" (ngSubmit)="createGroup()">
          <mat-form-field appearance="outline" class="flex-1">
            <mat-label i18n="@@workflows.groups.name">Назва групи</mat-label>
            <input matInput [formControl]="groupName" name="groupName" data-testid="group-name" />
            @if (groupName.hasError('required')) {
              <mat-error i18n="@@workflows.groups.name.required">Вкажіть назву групи</mat-error>
            }
          </mat-form-field>
          <button mat-flat-button type="submit" class="mt-2" data-testid="group-submit" [disabled]="busy()" i18n="@@workflows.groups.create">Створити групу</button>
          <button mat-button type="button" class="mt-2" (click)="cancelCreateGroup()" i18n="@@workflows.form.cancel">Скасувати</button>
        </form>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && workflows().length === 0 && groups().length === 0 && !error()) {
        <p class="opacity-70" data-testid="empty" i18n="@@workflows.empty">
          Ще немає жодного сценарію. Створіть перший і зберіть його на канвасі.
        </p>
      }

      @if (workflows().length > 0 || groups().length > 0) {
        <div class="filters">
          <input
            type="search"
            class="border filters__search"
            data-testid="workflows-search"
            i18n-placeholder="@@workflows.search"
            placeholder="Пошук за назвою"
            i18n-aria-label="@@workflows.search"
            aria-label="Пошук за назвою"
            [value]="search()"
            (input)="search.set($any($event.target).value)"
          />
          @if (groups().length > 0) {
            <mat-chip-listbox multiple aria-label="Групи" i18n-aria-label="@@workflows.groups.filter" data-testid="group-chips">
              @for (g of orderedGroups(); track g.id) {
                <mat-chip-option data-testid="group-chip" [attr.data-chip]="g.id" [selected]="chips().has(g.id)" (selectionChange)="chip(g.id, $event.selected)">
                  {{ g.name }}
                </mat-chip-option>
              }
              <mat-chip-option data-testid="group-chip" [attr.data-chip]="noGroup" [selected]="chips().has(noGroup)" (selectionChange)="chip(noGroup, $event.selected)" i18n="@@workflows.groups.none">
                Без групи
              </mat-chip-option>
            </mat-chip-listbox>
          }
        </div>

        @if (sections().length === 0) {
          <p class="opacity-70" data-testid="nothing-found" i18n="@@workflows.nothingFound">Нічого не знайдено.</p>
        }

        <div class="sections" cdkDropList data-testid="workflows-table" [cdkDropListDisabled]="!canReorder()" (cdkDropListDropped)="sectionDropped($event)">
          <div cdkDropListGroup>
            @for (s of sections(); track s.id) {
              <section class="group" cdkDrag [cdkDragDisabled]="!s.group || !canReorder()" [attr.data-section]="s.id">
                @let open = isOpen(s);
                @if (showHeaders()) {
                  <header class="group__header">
                    @if (s.group && canReorder()) {
                      <span class="material-symbols-outlined group__handle" cdkDragHandle data-testid="group-handle" i18n-title="@@workflows.groups.drag" title="Перетягніть, щоб змінити порядок" aria-hidden="true">drag_indicator</span>
                    } @else {
                      <span class="group__handle-space" aria-hidden="true"></span>
                    }
                    @if (s.group && renamingGroupId() === s.id) {
                      <form class="flex flex-1 items-center gap-2" data-testid="group-rename-form" [formGroup]="groupRenameForm" (ngSubmit)="saveGroupRename(s.id)">
                        <input class="rounded border px-2 py-1" [formControl]="groupRenameName" name="groupRename" data-testid="group-rename-input" aria-label="Нова назва групи" i18n-aria-label="@@workflows.groups.rename.label" (keydown.escape)="renamingGroupId.set(null)" />
                        <button mat-button type="submit" [disabled]="busy()" i18n="@@workflows.rename.save">Зберегти</button>
                        <button mat-button type="button" (click)="renamingGroupId.set(null)" i18n="@@workflows.rename.cancel">Скасувати</button>
                      </form>
                    } @else {
                      <button type="button" class="group__toggle" data-testid="section-toggle" [attr.aria-expanded]="open" (click)="toggle(s.id)">
                        <span class="material-symbols-outlined group__chevron" [class.group__chevron--open]="open" aria-hidden="true">chevron_right</span>
                        <span class="group__name">{{ s.group?.name ?? noGroupLabel }}</span>
                        <span class="group__count" data-testid="section-count">{{ s.workflows.length }}</span>
                      </button>
                    }
                    @if (s.group && deletingGroupId() === s.id) {
                      <span class="group__confirm" data-testid="group-delete-text" i18n="@@workflows.groups.delete.confirm">Видалити групу? Сценаріїв у ній: {{ s.group.workflowCount }}, вони лишаться без групи.</span>
                      <button mat-button type="button" color="warn" data-testid="group-delete-confirm" [disabled]="busy()" (click)="deleteGroup(s.id)" i18n="@@workflows.delete.yes">Так, видалити</button>
                      <button mat-button type="button" (click)="deletingGroupId.set(null)" i18n="@@workflows.delete.no">Ні</button>
                    } @else if (renamingGroupId() !== s.id) {
                      <button mat-button type="button" data-testid="section-create" (click)="startCreate(s.group?.id ?? null)" i18n="@@workflows.groups.createHere">+ Сценарій</button>
                      @if (s.group) {
                        <button mat-icon-button type="button" data-testid="group-menu" [matMenuTriggerFor]="groupMenu" i18n-aria-label="@@workflows.groups.menu" aria-label="Дії з групою">
                          <span class="material-symbols-outlined" aria-hidden="true">more_vert</span>
                        </button>
                        <mat-menu #groupMenu="matMenu">
                          <a mat-menu-item data-testid="group-members" [routerLink]="['/projects', s.id]" i18n="@@workflows.groups.members">Учасники</a>
                          <button mat-menu-item type="button" data-testid="group-rename" (click)="startGroupRename(s.group)" i18n="@@workflows.rename">Перейменувати</button>
                          <button mat-menu-item type="button" data-testid="group-delete" (click)="deletingGroupId.set(s.id)" i18n="@@workflows.delete">Видалити</button>
                        </mat-menu>
                      }
                    }
                  </header>
                }
                <table appCardTable class="w-full text-left group__table" [hidden]="!open">
                  <colgroup>
                    <col class="col--drag" />
                    <col />
                    <col class="col--trigger" />
                    <col class="col--last" />
                    <col class="col--active" />
                    <col class="col--version" />
                    <col class="col--updated" />
                    <col class="col--actions" />
                  </colgroup>
                  <thead [class.sr-only]="!$first">
                      <tr>
                        <th class="p-2" data-no-label><span class="sr-only" i18n="@@workflows.col.drag">Перетягнути</span></th>
                        <th class="p-2" data-no-label i18n="@@workflows.col.name">Назва</th>
                        <th class="p-2" i18n="@@workflows.col.trigger">Тригер</th>
                        <th class="p-2" i18n="@@workflows.col.lastRun">Останній запуск</th>
                        <th class="p-2" i18n="@@workflows.col.active">Активний</th>
                        <th class="p-2" i18n="@@workflows.col.version">Версія</th>
                        <th class="p-2" i18n="@@workflows.col.updatedAt">Змінено</th>
                        <th class="p-2" data-no-label><span class="sr-only" i18n="@@workflows.col.actions">Дії</span></th>
                      </tr>
                    </thead>
                  <tbody cdkDropList [cdkDropListData]="s.id" [attr.data-drop]="s.id" (cdkDropListDropped)="workflowDropped($event)">
                    @for (w of s.workflows; track w.id) {
                      <tr cdkDrag [cdkDragData]="w" [attr.data-workflow]="w.id">
                        <td class="card-hide-mobile p-2">
                          <span class="material-symbols-outlined row__handle" cdkDragHandle data-testid="row-handle" i18n-title="@@workflows.drag" title="Перетягніть в іншу групу" aria-hidden="true">drag_indicator</span>
                        </td>
                        <td class="p-2">
                          @if (renamingId() === w.id) {
                            <form class="flex items-center gap-2" data-testid="rename-form" [formGroup]="renameForm" (ngSubmit)="saveRename(w.id)">
                              <input
                                class="rounded border px-2 py-1"
                                [formControl]="renameName"
                                name="rename"
                                data-testid="rename-input"
                                aria-label="Нова назва"
                                i18n-aria-label="@@workflows.rename.label"
                                (keydown.escape)="cancelRename()"
                              />
                              <button mat-button type="submit" [disabled]="busy()" i18n="@@workflows.rename.save">Зберегти</button>
                              <button mat-button type="button" (click)="cancelRename()" i18n="@@workflows.rename.cancel">Скасувати</button>
                            </form>
                          } @else {
                            <a class="font-medium hover:underline" [routerLink]="['/workflows', w.id]" data-testid="open-link">{{ w.name }}</a>
                          }
                        </td>
                        <td class="p-2" data-testid="trigger-cell">
                          @if (w.triggerType) {
                            @let t = trigger(w.triggerType);
                            <span class="inline-flex items-center gap-1" [attr.data-trigger]="w.triggerType">
                              <span class="material-symbols-outlined row__trigger-icon" aria-hidden="true">{{ t.icon }}</span>{{ t.label }}
                            </span>
                          } @else if (w.triggerType === null) {
                            <span class="opacity-60" i18n="@@workflows.trigger.none">Немає</span>
                          }
                        </td>
                        <td class="p-2" data-testid="last-run-cell">
                          @if (w.lastExecution; as last) {
                            <a class="block hover:underline" data-testid="last-run-link" [routerLink]="['/executions', last.id]">
                              <span [class]="stateClass(last.status)" data-testid="last-run-state">{{ stateLabel(last.status) }}</span>
                              @if (last.startedAt) {
                                <span class="block text-sm" data-testid="last-run-at">{{ last.startedAt | date: 'dd.MM.yyyy HH:mm' }}</span>
                              }
                            </a>
                          } @else if (w.lastExecution === null) {
                            <span class="opacity-60" i18n="@@workflows.lastRun.never">Не запускався</span>
                          }
                        </td>
                        <td class="p-2">
                          <span data-testid="active-toggle-hint" [attr.title]="toggleHint(w)">
                            <mat-slide-toggle
                              data-testid="active-toggle"
                              [checked]="w.active"
                              [disabled]="busy() || noTrigger(w)"
                              (change)="toggleActive(w, $event.checked, $event.source)"
                              [attr.aria-label]="w.active ? deactivateLabel : activateLabel"
                            />
                          </span>
                        </td>
                        <td class="p-2">{{ w.version }}</td>
                        <td class="p-2">{{ w.updatedAt | date: 'short' }}</td>
                        <td class="p-2 text-right">
                          @if (deletingId() === w.id) {
                            <div class="row__confirm">
                              <span i18n="@@workflows.delete.confirm">Видалити сценарій разом з історією виконань?</span>
                              <span class="whitespace-nowrap">
                                <button mat-button type="button" color="warn" data-testid="delete-confirm" [disabled]="busy()" (click)="confirmDelete(w.id)" i18n="@@workflows.delete.yes">
                                  Так, видалити
                                </button>
                                <button mat-button type="button" (click)="deletingId.set(null)" i18n="@@workflows.delete.no">Ні</button>
                              </span>
                            </div>
                          } @else {
                            <div class="row__actions">
                              <a mat-icon-button class="row__action" [routerLink]="['/workflows', w.id]" data-testid="open-button" [attr.title]="openLabel">
                                <span class="material-symbols-outlined" aria-hidden="true">open_in_new</span>
                                <span class="sr-only" i18n="@@workflows.open">Відкрити</span>
                              </a>
                              <button mat-icon-button class="row__action" type="button" data-testid="row-menu" [attr.title]="moreLabel" [attr.aria-label]="moreLabel" [matMenuTriggerFor]="rowMenu">
                                <span class="material-symbols-outlined" aria-hidden="true">more_horiz</span>
                              </button>
                              <mat-menu #rowMenu="matMenu">
                                <a mat-menu-item data-testid="open-new-tab" [routerLink]="['/workflows', w.id]" target="_blank" i18n="@@workflows.openNewTab">Відкрити в новій вкладці</a>
                                <button mat-menu-item type="button" data-testid="rename-button" (click)="startRename(w)" i18n="@@workflows.rename">Перейменувати</button>
                                <button mat-menu-item type="button" data-testid="move-button" [matMenuTriggerFor]="moveMenu" i18n="@@workflows.moveToGroup">Перенести в групу</button>
                                <button mat-menu-item type="button" data-testid="export-button" [disabled]="busy()" (click)="exportWorkflow(w)" i18n="@@workflows.export">Експортувати</button>
                                <button
                                  mat-menu-item
                                  type="button"
                                  class="row__action--danger"
                                  data-testid="delete-button"
                                  [disabled]="w.active"
                                  [attr.title]="w.active ? deleteActiveHint : deleteLabel"
                                  (click)="startDelete(w.id)"
                                  i18n="@@workflows.delete"
                                >
                                  Видалити
                                </button>
                              </mat-menu>
                              <mat-menu #moveMenu="matMenu">
                                @for (g of orderedGroups(); track g.id) {
                                  <button mat-menu-item type="button" data-testid="move-to" [attr.data-target]="g.id" [disabled]="w.groupId === g.id" (click)="move(w, g.id)">{{ g.name }}</button>
                                }
                                <button mat-menu-item type="button" data-testid="move-to" [attr.data-target]="noGroup" [disabled]="!w.groupId" (click)="move(w, null)" i18n="@@workflows.groups.none">Без групи</button>
                                <button mat-menu-item type="button" data-testid="move-to-new" (click)="startCreateGroup(w.id)" i18n="@@workflows.groups.newEllipsis">Нова група…</button>
                              </mat-menu>
                            </div>
                          }
                        </td>
                      </tr>
                    } @empty {
                      <tr class="group__empty">
                        <td class="p-2 opacity-60" colspan="8" data-testid="section-empty" i18n="@@workflows.groups.empty">Порожньо — перетягніть сюди сценарій.</td>
                      </tr>
                    }
                  </tbody>
                </table>
              </section>
            }
          </div>
        </div>
      }
    </section>
  `,
  styles: `
    .filters {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 12px;
      margin-bottom: 16px;
    }

    .filters__search {
      width: 280px;
      max-width: 100%;
    }

    .group {
      margin-bottom: 16px;
    }

    .group__header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 4px;
      min-height: 40px;
    }

    /* Колонки однакові в усіх секціях: кожна секція — окрема таблиця. */
    .group__table {
      table-layout: fixed;
    }

    .col--drag {
      width: 40px;
    }

    .col--trigger {
      width: 170px;
    }

    .col--last {
      width: 170px;
    }

    .col--active {
      width: 110px;
    }

    .col--version {
      width: 90px;
    }

    .col--updated {
      width: 160px;
    }

    .col--actions {
      width: 104px;
    }

    .row__trigger-icon {
      font-size: 18px;
      color: var(--ac-cool-grey-4);
    }

    .row__actions {
      display: flex;
      justify-content: flex-end;
      gap: 4px;
    }

    .row__action {
      color: var(--ac-kit, #0d736d);
    }

    .row__action--danger:not([disabled]) {
      color: var(--ac-red, #ff6060);
    }

    .row__confirm {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: flex-end;
      gap: 4px 8px;
      text-align: right;
    }

    .group__handle-space {
      width: 24px;
    }

    .group__handle,
    .row__handle {
      color: var(--ac-cool-grey-4);
      cursor: grab;
    }

    .group__toggle {
      display: flex;
      flex: 1;
      align-items: center;
      gap: 6px;
      min-width: 0;
      padding: 4px 4px 4px 0;
      border: 0;
      border-radius: var(--ac-radius-sm);
      background: none;
      color: var(--ac-vivid);
      font: 700 16px/22px Ubuntu, sans-serif;
      text-align: left;
      cursor: pointer;
    }

    .group__toggle:hover,
    .group__toggle:focus-visible {
      background: var(--ac-cool-grey-2);
    }

    .group__chevron {
      font-size: 20px;
      color: var(--ac-cool-grey-4);
      transition: transform 0.15s ease;
    }

    .group__chevron--open {
      transform: rotate(90deg);
    }

    .group__count {
      padding: 0 8px;
      border-radius: 10px;
      background: var(--ac-button);
      font-size: 12px;
      font-weight: 400;
    }

    .group__confirm {
      font-size: 13px;
    }

    .cdk-drag-preview {
      box-shadow: var(--ac-shadow);
      background: var(--ac-white);
    }

    .cdk-drag-placeholder {
      opacity: 0.4;
    }

    .cdk-drop-list-dragging .cdk-drag {
      transition: transform 0.2s ease;
    }
  `,
})
export class WorkflowsPage implements OnInit {
  private readonly api = inject(WorkflowsApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);
  private readonly dialog = inject(MatDialog);
  private readonly context = inject(ProjectContextService);

  readonly workflows = signal<WorkflowSummary[]>([]);
  readonly groups = signal<WorkflowGroup[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly creatingGroup = signal(false);
  /** Сценарій, який після створення групи переноситься в неї («Перемістити → Нова група…»). */
  private readonly moveAfterCreate = signal<string | null>(null);
  readonly renamingId = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);
  readonly renamingGroupId = signal<string | null>(null);
  readonly deletingGroupId = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly search = signal('');
  /** Вибрані фільтр-чіпи груп (порожньо — усі). */
  readonly chips = signal<ReadonlySet<string>>(new Set());
  private readonly collapsed = signal<Record<string, boolean>>(readCollapsed());

  readonly renameName = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly groupName = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly groupRenameName = new FormControl('', { nonNullable: true, validators: [Validators.required] });
  readonly renameForm = new FormGroup({ name: this.renameName });
  readonly groupForm = new FormGroup({ name: this.groupName });
  readonly groupRenameForm = new FormGroup({ name: this.groupRenameName });

  readonly activateLabel = $localize`:@@workflows.activate:Активувати`;
  readonly deactivateLabel = $localize`:@@workflows.deactivate:Деактивувати`;
  readonly deleteActiveHint = $localize`:@@workflows.delete.activeHint:Спершу деактивуйте сценарій`;
  readonly openLabel = $localize`:@@workflows.open:Відкрити`;
  readonly moreLabel = $localize`:@@workflows.more:Інші дії`;
  readonly deleteLabel = $localize`:@@workflows.delete:Видалити`;
  readonly noGroupLabel = $localize`:@@workflows.groups.none:Без групи`;
  readonly noGroup = NO_GROUP;

  readonly orderedGroups = computed(() => [...this.groups()].sort((a, b) => a.sortOrder - b.sortOrder));
  readonly sections = computed(() => groupSections(this.groups(), this.workflows(), { groups: this.chips(), search: this.search() }));
  /** Без груп — звичайний список без заголовків секцій. */
  readonly showHeaders = computed(() => this.groups().length > 0);
  /** Порядок груп змінюється лише тоді, коли видно всі групи (без фільтрів): інакше індекси секцій не збігаються з групами. */
  readonly canReorder = computed(() => this.chips().size === 0 && this.search().trim() === '');

  constructor() {
    // Проєкт у шапці (F6-14) — фільтр списку; `?group=` при відкритті сторінки має пріоритет над збереженим вибором.
    let first = true;
    effect(() => {
      const current = this.context.current();
      if (first && this.route.snapshot.queryParamMap.get('group')) {
        first = false;
        return;
      }
      first = false;
      this.chips.set(current === null ? new Set() : new Set([current]));
    });
  }

  ngOnInit(): void {
    const group = this.route.snapshot.queryParamMap.get('group');
    if (group) this.chips.set(new Set([group]));
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({ page: this.api.list(), groups: this.api.listGroups() })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ page, groups }) => {
          this.workflows.set(page.items);
          this.groups.set(groups);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  isOpen(section: WorkflowSection): boolean {
    return this.search().trim() !== '' || !this.collapsed()[section.id];
  }

  toggle(sectionId: string): void {
    const next = { ...this.collapsed(), [sectionId]: !this.collapsed()[sectionId] };
    this.collapsed.set(next);
    writeCollapsed(next);
  }

  chip(id: string, selected: boolean): void {
    const next = new Set(this.chips());
    if (selected) next.add(id);
    else next.delete(id);
    this.chips.set(next);
  }

  /**
   * Діалог «Створити сценарій» (F3-80): назва і група. Без аргументу група — проєкт, вибраний у шапці (F6-14);
   * `null` — без групи. Після підтвердження: `POST /workflows` з порожнім визначенням → перехід у канвас нового сценарію.
   */
  startCreate(groupId?: string | null): void {
    this.dialog
      .open<CreateWorkflowDialog, CreateWorkflowDialogData, CreateWorkflowResult>(CreateWorkflowDialog, {
        data: { groups: this.orderedGroups(), groupId: groupId === undefined ? this.context.defaultProjectId() : groupId },
        width: '480px',
      })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((result) => {
        if (result) this.create(result);
      });
  }

  private create(result: CreateWorkflowResult): void {
    this.run(this.api.create(result.name, result.groupId), (detail) => {
      this.upsert(detail);
      void this.router.navigate(['/workflows', detail.id]);
    });
  }

  startCreateGroup(moveWorkflowId: string | null = null): void {
    this.groupName.reset('');
    this.moveAfterCreate.set(moveWorkflowId);
    this.creatingGroup.set(true);
  }

  cancelCreateGroup(): void {
    this.creatingGroup.set(false);
    this.moveAfterCreate.set(null);
  }

  createGroup(): void {
    const name = this.groupName.value.trim();
    if (!name) {
      this.groupName.setValue('');
      this.groupName.markAsTouched();
      return;
    }
    this.run(this.api.createGroup(name), (group) => {
      this.creatingGroup.set(false);
      this.groups.update((list) => [...list, group]);
      this.context.projects.update((list) => [...list, group]);
      const workflow = this.workflows().find((w) => w.id === this.moveAfterCreate());
      this.moveAfterCreate.set(null);
      if (workflow) this.move(workflow, group.id);
    });
  }

  startGroupRename(group: WorkflowGroup): void {
    this.deletingGroupId.set(null);
    this.groupRenameName.reset(group.name);
    this.renamingGroupId.set(group.id);
  }

  saveGroupRename(id: string): void {
    const name = this.groupRenameName.value.trim();
    if (!name) return;
    if (this.groups().find((g) => g.id === id)?.name === name) {
      this.renamingGroupId.set(null);
      return;
    }
    this.run(this.api.renameGroup(id, name), (group) => {
      this.renamingGroupId.set(null);
      this.groups.update((list) => list.map((g) => (g.id === id ? group : g)));
    });
  }

  /** Видалити групу: її сценарії лишаються без групи. */
  deleteGroup(id: string): void {
    this.run(this.api.deleteGroup(id), () => {
      this.deletingGroupId.set(null);
      this.groups.update((list) => list.filter((g) => g.id !== id));
      this.context.projects.update((list) => list.filter((g) => g.id !== id));
      this.workflows.update((list) => list.map((w) => (w.groupId === id ? { ...w, groupId: null } : w)));
      this.chip(id, false);
    });
  }

  /** Секцію групи перетягнуто: новий порядок одразу, на сервер — `PUT /workflow-groups/order`; помилка — повернути. */
  sectionDropped(event: CdkDragDrop<unknown>): void {
    const previous = this.groups();
    // Індекси — серед секцій; «Без групи» завжди остання й не перетягується, тож індекси груп ті самі.
    const to = Math.min(event.currentIndex, previous.length - 1);
    const next = reorder(previous, event.previousIndex, to);
    if (next.every((g, i) => g.id === this.orderedGroups()[i]?.id)) return;
    this.groups.set(next);
    this.run(
      this.api.reorderGroups(next.map((g) => g.id)),
      (groups) => this.groups.set(groups),
      () => this.groups.set(previous),
    );
  }

  /** Рядок перетягнуто в іншу секцію — перенести сценарій у її групу. */
  workflowDropped(event: CdkDragDrop<string, string, WorkflowSummary>): void {
    if (event.previousContainer === event.container) return;
    const target = event.container.data;
    this.move(event.item.data, target === NO_GROUP ? null : target);
  }

  /** `PATCH /workflows/{id} { groupId }`: сценарій одразу в новій секції; помилка — повернути. */
  move(workflow: WorkflowSummary, groupId: string | null): void {
    if ((workflow.groupId ?? null) === groupId) return;
    const previous = this.workflows();
    this.setWorkflows(previous.map((w) => (w.id === workflow.id ? { ...w, groupId } : w)));
    this.run(
      this.api.move(workflow.id, groupId),
      (detail) => this.upsert(detail),
      () => this.setWorkflows(previous),
    );
  }

  startRename(w: WorkflowSummary): void {
    this.deletingId.set(null);
    this.renameName.reset(w.name);
    this.renamingId.set(w.id);
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  saveRename(id: string): void {
    const name = this.renameName.value.trim();
    if (!name) return;
    const current = this.workflows().find((w) => w.id === id);
    if (current?.name === name) {
      this.renamingId.set(null);
      return;
    }
    this.run(this.api.rename(id, name), (detail) => {
      this.renamingId.set(null);
      this.upsert(detail);
    });
  }

  /** Активація валідує сценарій на сервері; при відмові перемикач повертається у попередній стан. */
  toggleActive(w: WorkflowSummary, active: boolean, toggle?: MatSlideToggle): void {
    const request = active ? this.api.activate(w.id) : this.api.deactivate(w.id);
    this.run(
      request,
      (detail) => this.upsert(detail),
      () => {
        if (toggle) toggle.checked = w.active;
      },
    );
  }

  trigger(type: string): TriggerInfo {
    return triggerInfo(type);
  }

  stateLabel(state: ExecutionState): string {
    return executionStateLabel(state);
  }

  stateClass(state: ExecutionState): string {
    switch (state) {
      case 'success':
        return 'text-green-700';
      case 'error':
      case 'timed_out':
        return 'text-red-700';
      case 'waiting':
        return 'text-blue-700';
      default:
        return 'opacity-70';
    }
  }

  /** Сценарій без тригера не активується (сервер відхилить): перемикач вимкнено, поки тригер не додано. */
  noTrigger(w: WorkflowSummary): boolean {
    return !w.active && w.triggerType === null;
  }

  /** Підказка до перемикача «Активний»: чому вимкнений або що станеться при вмиканні. */
  toggleHint(w: WorkflowSummary): string | null {
    if (this.noTrigger(w)) return $localize`:@@workflows.activate.noTrigger:Спочатку додайте тригер у редакторі — без нього сценарій не активується`;
    if (!w.active && w.publishedVersion == null) return $localize`:@@workflows.activate.publishes:Активація опублікує поточну версію сценарію`;
    return null;
  }

  /** Діалог імпорту; новий сценарій з'являється у списку (у групі вибраного фільтра, якщо вибрано одну групу). */
  openImport(): void {
    const chips = [...this.chips()];
    const groupId = chips.length === 1 && chips[0] !== this.noGroup ? chips[0] : null;
    this.dialog
      .open<ImportWorkflowDialog, ImportWorkflowDialogData, WorkflowImportResult>(ImportWorkflowDialog, { data: { groupId }, width: '640px' })
      .afterClosed()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((result) => {
        if (result) this.upsert(result.workflow);
      });
  }

  /** Завантажує `<назва>.openbp.json` з визначенням і маніфестом залежностей. */
  exportWorkflow(w: WorkflowSummary): void {
    this.run(this.api.exportWorkflow(w.id), (exported) => {
      const blob = new Blob([JSON.stringify(exported, null, 2) + '\n'], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = `${exportFileName(w.name)}.openbp.json`;
      link.click();
      URL.revokeObjectURL(url);
    });
  }

  startDelete(id: string): void {
    this.renamingId.set(null);
    this.deletingId.set(id);
  }

  confirmDelete(id: string): void {
    this.run(this.api.delete(id), () => {
      this.deletingId.set(null);
      this.setWorkflows(this.workflows().filter((w) => w.id !== id));
    });
  }

  private run<T>(
    request: Observable<T>,
    onSuccess: (value: T) => void,
    onError?: () => void,
  ): void {
    this.busy.set(true);
    this.error.set(null);
    request.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: (value) => {
        this.busy.set(false);
        onSuccess(value);
      },
      error: (e: unknown) => {
        this.busy.set(false);
        this.error.set(apiErrorMessage(e));
        onError?.();
      },
    });
  }

  /** Новий список сценаріїв і лічильники груп за ним. */
  private setWorkflows(list: WorkflowSummary[]): void {
    this.workflows.set(list);
    this.groups.update((groups) => recount(groups, list));
  }

  private upsert(detail: WorkflowDetail): void {
    const summary = toSummary(detail);
    const list = this.workflows();
    // Відповідь про сценарій не містить тригера й останнього запуску — вони лишаються з рядка списку.
    this.setWorkflows(list.some((w) => w.id === summary.id) ? list.map((w) => (w.id === summary.id ? { ...w, ...summary } : w)) : [summary, ...list]);
  }
}

/** Назва файлу експорту з назви сценарію: без символів, недозволених у файлових системах. */
export function exportFileName(name: string): string {
  const printable = [...name].map((ch) => (ch.charCodeAt(0) < 32 ? ' ' : ch)).join('');
  const cleaned = printable.replace(/[\\/:*?"<>|]+/g, ' ').replace(/\s+/g, ' ').trim();
  return cleaned || 'workflow';
}
