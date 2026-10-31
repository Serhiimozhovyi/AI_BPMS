import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';
import { CardTableDirective } from '../../shared/card-table.directive';
import { ChipComponent, ChipTone } from '../../shared/chip/chip.component';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { AuthService } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import {
  TASK_STATUSES,
  TaskFilter,
  TaskStatus,
  TaskSummary,
  TasksApiService,
  assigneeLabel,
  taskStatusLabel,
} from '../../tasks/tasks-api.service';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { SelectSearchComponent } from '../../shared/select-search/select-search.component';

/**
 * `/tasks` (F6-02): задачі погодження вузлів `core.approval` — «Мої» (виконавець — я, мій email чи моя роль) і «Усі»
 * для admin, фільтр за статусом (типово відкриті). Рядок веде на картку `/tasks/:id` з формою рішення.
 */
@Component({
  selector: 'app-tasks-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, RouterLink, ChipComponent, MatProgressBarModule, EmptyStateComponent, CardTableDirective, MatFormFieldModule, MatSelectModule, SelectSearchComponent],
  template: `
    <section class="p-6">
      <h1 class="mat-font-headline-sm mb-4" i18n="@@tasks.title">Задачі</h1>

      <div class="mb-4 flex flex-wrap items-end gap-4" data-testid="filters">
        @if (isAdmin()) {
          <div class="flex flex-col text-sm">
            <span id="filter-scope-label" i18n="@@tasks.filter.scope">Чиї</span>
            <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
              <mat-select data-testid="filter-scope" aria-labelledby="filter-scope-label" [value]="scope()" (selectionChange)="setScope($event.value)">
                <bp-select-search />
                <mat-option value="mine" i18n="@@tasks.filter.mine">Мої</mat-option>
                <mat-option value="all" i18n="@@tasks.filter.all">Усі</mat-option>
              </mat-select>
            </mat-form-field>
          </div>
        }
        <div class="flex flex-col text-sm">
          <span id="filter-status-label" i18n="@@tasks.filter.status">Статус</span>
          <mat-form-field appearance="outline" class="bp-field-compact" subscriptSizing="dynamic">
            <mat-select data-testid="filter-status" aria-labelledby="filter-status-label" [value]="status() ?? ''" (selectionChange)="setStatus($event.value)">
              <bp-select-search />
              <mat-option value="" i18n="@@tasks.filter.allStatuses">Усі статуси</mat-option>
              @for (s of statuses; track s) {
                <mat-option [value]="s">{{ statusLabel(s) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }
      @if (!loading() && !error() && tasks().length === 0) {
        <app-empty-state
          icon="task_alt"
          i18n-title="@@tasks.empty.title"
          title="Задач за цими умовами немає"
          i18n-description="@@tasks.empty.text"
          description="Задачі з'являються, коли сценарій доходить до вузла «Погодження»: додайте його у сценарій, і виконавець побачить задачу тут."
          i18n-actionLabel="@@tasks.empty.action"
          actionLabel="Відкрити сценарії"
          (action)="openWorkflows()"
        />
      }

      @if (tasks().length > 0) {
        <table appCardTable class="mb-2 w-full text-left" data-testid="tasks-table">
          <thead>
            <tr>
              <th class="p-2" data-no-label i18n="@@tasks.col.title">Задача</th>
              <th class="p-2" i18n="@@tasks.col.workflow">Сценарій</th>
              <th class="p-2" i18n="@@tasks.col.assignee">Виконавець</th>
              <th class="p-2" i18n="@@tasks.col.status">Статус</th>
              <th class="p-2" i18n="@@tasks.col.due">Строк</th>
            </tr>
          </thead>
          <tbody>
            @for (t of tasks(); track t.id) {
              <tr class="border-t" data-testid="task-row">
                <td class="p-2">
                  <a class="underline" [routerLink]="['/tasks', t.id]" data-testid="task-link">{{ t.title }}</a>
                  @if (t.escalated) {
                    <bp-chip class="ml-2" tone="orange" i18n="@@tasks.escalated">ескалація</bp-chip>
                  }
                </td>
                <td class="p-2">{{ t.workflowName }}</td>
                <td class="p-2">{{ assignee(t) }}</td>
                <td class="p-2" data-testid="task-status"><bp-chip [tone]="statusTone(t.status)">{{ statusLabel(t.status) }}{{ t.decision ? ' · ' + t.decision : '' }}</bp-chip></td>
                <td class="p-2">{{ t.dueAt ? (t.dueAt | date: 'dd.MM.yyyy HH:mm') : '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
        <p class="text-sm opacity-70" data-testid="total" i18n="@@tasks.total">Усього: {{ total() }}</p>
      }
    </section>
  `,
})
export class TasksPage implements OnInit {
  private readonly api = inject(TasksApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly statuses = TASK_STATUSES;

  protected statusTone(status: TaskStatus): ChipTone {
    switch (status) {
      case 'open':
        return 'blue';
      case 'done':
        return 'green';
      case 'expired':
        return 'orange';
      default:
        return 'gray';
    }
  }
  protected readonly scope = signal<'mine' | 'all'>('mine');
  protected readonly status = signal<TaskStatus | null>('open');
  protected readonly tasks = signal<TaskSummary[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  protected readonly isAdmin = computed(() => this.auth.user()?.role === 'admin');

  ngOnInit(): void {
    this.load();
  }

  protected setScope(value: string): void {
    this.scope.set(value === 'all' ? 'all' : 'mine');
    this.load();
  }

  protected setStatus(value: string): void {
    this.status.set((TASK_STATUSES as readonly string[]).includes(value) ? (value as TaskStatus) : null);
    this.load();
  }

  protected statusLabel(status: TaskStatus): string {
    return taskStatusLabel(status);
  }

  protected assignee(task: TaskSummary): string {
    return assigneeLabel(task.assignee);
  }

  private load(): void {
    const filter: TaskFilter = { scope: this.scope(), status: this.status() };
    this.loading.set(true);
    this.error.set('');
    this.api.list(filter).subscribe({
      next: (page) => {
        this.tasks.set(page.items);
        this.total.set(page.total);
        this.loading.set(false);
      },
      error: (error: unknown) => {
        this.tasks.set([]);
        this.error.set(apiErrorMessage(error));
        this.loading.set(false);
      },
    });
  }

  openWorkflows(): void {
    void this.router.navigateByUrl('/workflows');
  }
}
