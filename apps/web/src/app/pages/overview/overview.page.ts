import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectTransferApiService, downloadBundle } from '../../projects/project-transfer-api.service';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';
import {
  OVERVIEW_RANGES,
  Overview,
  OverviewApiService,
  OverviewRange,
  formatDuration,
  formatRate,
  overviewRangeLabel,
} from '../../overview/overview-api.service';

/**
 * `/` — «Огляд» (F6-07), перша сторінка після входу: скільки виконань за 24 год / 7 / 30 днів, частка успішних,
 * p50/p95 тривалості, топ-5 сценаріїв за помилками і що зараз у черзі, виконується чи чекає.
 */
@Component({
  selector: 'app-overview-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatProgressBarModule, RouterLink, BpTabsComponent],
  template: `
    <section class="p-6">
      <div class="mb-4 flex flex-wrap items-center gap-4">
        @if (!projectId) {
          <h1 class="mat-font-headline-sm" i18n="@@overview.title">Огляд</h1>
        }
        <bp-tabs variant="pills" [tabs]="rangeTabs" [value]="range()" (valueChange)="select($any($event))" label="Період" i18n-label="@@overview.range.label" />
        @if (projectId && canExport()) {
          <div class="ml-auto flex flex-wrap items-center gap-2">
            <input
              class="rounded border border-ac-line px-2 py-1"
              type="text"
              [value]="rowsFor()"
              (input)="rowsFor.set($any($event.target).value)"
              placeholder="Таблиці з рядками: settings, materials"
              i18n-placeholder="@@overview.export.rows"
              aria-label="Таблиці, рядки яких додати в шаблон"
              i18n-aria-label="@@overview.export.rowsLabel"
              data-testid="project-export-rows"
            />
            <button mat-stroked-button type="button" data-testid="project-export" [disabled]="exporting()" (click)="exportProject()" i18n="@@overview.export">Експортувати проєкт</button>
          </div>
        }
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (data(); as d) {
        <div class="mb-6 grid max-w-5xl grid-cols-2 gap-4 md:grid-cols-4">
          <div class="rounded-lg border border-ac-line p-4" data-testid="stat-total">
            <div class="text-sm opacity-70" i18n="@@overview.total">Виконань</div>
            <div class="text-2xl font-semibold">{{ d.total }}</div>
            <a
              class="text-sm underline"
              routerLink="/executions"
              [queryParams]="{ status: 'failed' }"
              data-testid="stat-failed"
              i18n="@@overview.failedCount"
              >з помилкою: {{ d.failed }}</a
            >
          </div>
          <div class="rounded-lg border border-ac-line p-4" data-testid="stat-rate">
            <div class="text-sm opacity-70" i18n="@@overview.successRate">Успішних</div>
            <div class="text-2xl font-semibold">{{ rate(d.successRate) }}</div>
          </div>
          <div class="rounded-lg border border-ac-line p-4" data-testid="stat-p50">
            <div class="text-sm opacity-70" i18n="@@overview.p50">Медіана тривалості</div>
            <div class="text-2xl font-semibold">{{ duration(d.p50Ms) }}</div>
          </div>
          <div class="rounded-lg border border-ac-line p-4" data-testid="stat-p95">
            <div class="text-sm opacity-70" i18n="@@overview.p95">95 % виконань</div>
            <div class="text-2xl font-semibold">{{ duration(d.p95Ms) }}</div>
          </div>
        </div>

        <h2 class="mb-2 text-lg font-semibold" i18n="@@overview.now">Зараз</h2>
        <div class="mb-6 flex flex-wrap gap-6" data-testid="now">
          @if (projectId) {
            <span data-testid="now-active">
              <span i18n="@@overview.now.active">Активних сценаріїв</span>: <b>{{ d.activeWorkflows ?? 0 }}</b>
            </span>
            <a routerLink="/tasks" data-testid="now-tasks">
              <span i18n="@@overview.now.tasks">Відкритих задач</span>: <b>{{ d.openTasks ?? 0 }}</b>
            </a>
          }
          <a routerLink="/executions" data-testid="now-running">
            <span i18n="@@overview.now.running">Виконуються</span>: <b>{{ d.running }}</b>
          </a>
          <a routerLink="/executions" data-testid="now-waiting">
            <span i18n="@@overview.now.waiting">Чекають</span>: <b>{{ d.waiting }}</b>
          </a>
          <a routerLink="/executions" data-testid="now-queued">
            <span i18n="@@overview.now.queued">У черзі</span>: <b>{{ d.queued }}</b>
          </a>
          <a routerLink="/executions" [queryParams]="{ hasOpenViolations: true }" data-testid="now-violations">
            <span i18n="@@overview.now.violations">З порушеннями правил</span>: <b>{{ d.openViolations ?? 0 }}</b>
          </a>
        </div>

        <h2 class="mb-2 text-lg font-semibold" i18n="@@overview.topFailing">Найбільше помилок</h2>
        @if (d.topFailing.length === 0) {
          <p class="opacity-70" data-testid="top-empty" i18n="@@overview.topFailing.empty">Помилок за цей період немає.</p>
        } @else {
          <div class="overflow-x-auto">
            <table class="w-full max-w-3xl text-left" data-testid="top-failing">
              <thead>
                <tr>
                  <th class="p-2" i18n="@@overview.col.workflow">Сценарій</th>
                  <th class="p-2" i18n="@@overview.col.failed">Помилок</th>
                  <th class="p-2" i18n="@@overview.col.total">Усього</th>
                </tr>
              </thead>
              <tbody>
                @for (w of d.topFailing; track w.workflowId) {
                  <tr [attr.data-workflow]="w.workflowId">
                    <td class="p-2">
                      <a class="underline" [routerLink]="['/workflows', w.workflowId]">{{ w.name ?? w.workflowId }}</a>
                    </td>
                    <td class="p-2">
                      <a
                        class="underline"
                        routerLink="/executions"
                        [queryParams]="{ workflowId: w.workflowId, status: 'failed' }"
                        >{{ w.failed }}</a
                      >
                    </td>
                    <td class="p-2">{{ w.total }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        <h2 class="mb-2 mt-6 text-lg font-semibold" i18n="@@overview.subscriptions">Підписки на події</h2>
        @if (!d.subscriptions || d.subscriptions.length === 0) {
          <p class="opacity-70" data-testid="subscriptions-empty" i18n="@@overview.subscriptions.empty">Активних підписок на події конекторів немає.</p>
        } @else {
          <div class="overflow-x-auto">
            <table class="w-full max-w-5xl text-left" data-testid="subscriptions">
              <thead>
                <tr>
                  <th class="p-2" i18n="@@overview.col.workflow">Сценарій</th>
                  <th class="p-2" i18n="@@overview.sub.connector">Конектор</th>
                  <th class="p-2" i18n="@@overview.sub.resource">Ресурс</th>
                  <th class="p-2" i18n="@@overview.sub.worker">Воркер</th>
                  <th class="p-2" i18n="@@overview.sub.lease">Оренда до</th>
                  <th class="p-2" i18n="@@overview.sub.state">Стан</th>
                </tr>
              </thead>
              <tbody>
                @for (s of d.subscriptions; track s.workflowId + s.nodeId) {
                  <tr [attr.data-subscription]="s.workflowId + '/' + s.nodeId" [attr.data-state]="s.state">
                    <td class="p-2">
                      <a class="underline" [routerLink]="['/workflows', s.workflowId]">{{ s.workflowName }}</a>
                      <div class="text-sm opacity-70">{{ s.nodeName ?? s.nodeId }}</div>
                    </td>
                    <td class="p-2">{{ s.serverName ?? s.serverId }}</td>
                    <td class="p-2 font-mono text-sm">{{ s.resourceUri }}</td>
                    <td class="p-2">{{ s.workerId ?? '—' }}</td>
                    <td class="p-2">{{ s.leasedUntil ? (s.leasedUntil | date: 'HH:mm:ss') : '—' }}</td>
                    <td class="p-2">
                      @if (s.state === 'held') {
                        <span i18n="@@overview.sub.state.held">Відкрита</span>
                      } @else {
                        <span class="text-amber-700" i18n="@@overview.sub.state.free">Немає власника</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }

        <p class="mt-4 max-w-3xl text-sm opacity-60" i18n="@@overview.hint">
          Лічильники оновлюються раз на 5 хвилин; тривалість оцінена за інтервалами, тому показано верхню межу.
        </p>
      }
    </section>
  `,
})
export class OverviewPage implements OnInit {
  private readonly api = inject(OverviewApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** Усередині `/projects/:id/overview` (F8-03) огляд звужений до проєкту; на `/` — уся видима область. */
  readonly projectId = inject(ActivatedRoute).parent?.snapshot.paramMap.get('id') ?? null;

  readonly ranges = OVERVIEW_RANGES;
  readonly range = signal<OverviewRange>('24h');
  readonly data = signal<Overview | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly projects = inject(ProjectContextService);
  private readonly transfer = inject(ProjectTransferApiService);
  readonly rowsFor = signal('');
  readonly exporting = signal(false);

  /** Шаблон проєкту (F8-06) експортує editor проєкту чи admin. */
  readonly canExport = computed(() => {
    const role = this.projects.projects().find((p) => p.id === this.projectId)?.role;
    return role === 'editor' || role === 'admin';
  });

  readonly rangeLabel = overviewRangeLabel;
  readonly rangeTabs: BpTab[] = OVERVIEW_RANGES.map((r) => ({ value: r, label: overviewRangeLabel(r) }));
  readonly duration = formatDuration;
  readonly rate = formatRate;

  ngOnInit(): void {
    this.load();
  }

  /** Скачує шаблон проєкту JSON-файлом; рядки додаються лише для перелічених таблиць (без чутливих колонок). */
  exportProject(): void {
    if (!this.projectId) return;
    this.exporting.set(true);
    this.error.set(null);
    const rows = this.rowsFor().split(',').map((t) => t.trim()).filter((t) => t.length > 0);
    this.transfer.export(this.projectId, rows).subscribe({
      next: (bundle) => {
        this.exporting.set(false);
        downloadBundle(bundle);
      },
      error: (e: unknown) => {
        this.exporting.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  select(range: OverviewRange): void {
    if (range === this.range()) return;
    this.range.set(range);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .get(this.range(), this.projectId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (d) => {
          this.data.set(d);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }
}
