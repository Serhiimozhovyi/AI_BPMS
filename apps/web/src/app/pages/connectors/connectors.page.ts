import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { NotifyService } from '../../shared/notify.service';
import { ConnectorCatalogComponent } from '../../connectors/connector-catalog.component';
import { ConnectorFormComponent } from '../../connectors/connector-form.component';
import { statusLabel } from '../../connectors/connector-input';
import { ConnectorToolsComponent } from '../../connectors/connector-tools.component';
import {
  ConnectorDetail,
  ConnectorStatus,
  ConnectorSummary,
  ConnectorsApiService,
  apiErrorMessage,
} from '../../connectors/connectors-api.service';

/**
 * `/connectors`: MCP-сервери зі статусом, додавання, discovery tools і тестовий виклик (F2-14); вкладка «Каталог»
 * (F6-16, `?catalog=<id>` відкриває її з підсвіченим записом).
 */
@Component({
  selector: 'app-connectors-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatButtonToggleModule, MatProgressBarModule, ConnectorCatalogComponent, ConnectorFormComponent, ConnectorToolsComponent, EmptyStateComponent],
  template: `
    <section class="p-6">
      <div class="mb-4 flex items-center gap-4">
        <h1 class="mat-font-headline-sm" i18n="@@connectors.title">Конектори</h1>
        <mat-button-toggle-group [value]="view()" (change)="showView($event.value)" data-testid="connectors-view">
          <mat-button-toggle value="installed" data-testid="view-installed" i18n="@@connectors.view.installed">Встановлені</mat-button-toggle>
          <mat-button-toggle value="catalog" data-testid="view-catalog" i18n="@@connectors.view.catalog">Каталог</mat-button-toggle>
        </mat-button-toggle-group>
        @if (!adding() && view() === 'installed') {
          <button mat-flat-button type="button" data-testid="add-button" (click)="adding.set(true)" i18n="@@connectors.add">
            Додати MCP-сервер
          </button>
        }
      </div>

      @if (view() === 'catalog') {
        <app-connector-catalog [highlight]="highlight()" (installed)="onInstalled($event)" />
      } @else {

      @if (adding()) {
        <div class="mb-6 max-w-2xl">
          <h2 class="mb-2 font-semibold" i18n="@@connectors.form.title">Новий MCP-сервер</h2>
          <app-connector-form (created)="onCreated($event)" (cancelled)="adding.set(false)" />
        </div>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && connectors().length === 0 && !error()) {
        <app-empty-state
          icon="hub"
          i18n-title="@@connectors.empty.title"
          title="Ще немає жодного MCP-сервера"
          i18n-description="@@connectors.empty.text"
          description="Конектор дає сценаріям tools зовнішніх систем: підключіть готовий із каталогу або додайте власний MCP-сервер."
          i18n-actionLabel="@@connectors.empty.action"
          actionLabel="Відкрити каталог"
          (action)="showView('catalog')"
        />
      }

      @if (connectors().length > 0) {
        <table class="mb-6 w-full text-left" data-testid="connectors-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@connectors.col.name">Назва</th>
              <th class="p-2" i18n="@@connectors.col.transport">Транспорт</th>
              <th class="p-2" i18n="@@connectors.col.status">Стан</th>
              <th class="p-2" i18n="@@connectors.col.tools">Tools</th>
              <th class="p-2" i18n="@@connectors.col.cachedAt">Оновлено</th>
            </tr>
          </thead>
          <tbody>
            @for (c of connectors(); track c.id) {
              <tr [attr.data-connector]="c.id" [class.font-semibold]="selected()?.summary?.id === c.id">
                <td class="p-2">
                  <button mat-button type="button" (click)="select(c.id)">{{ c.name }}</button>
                </td>
                <td class="p-2">{{ c.transport }}</td>
                <td class="p-2" [class]="statusClass(c.status)" data-testid="status">{{ statusLabel(c.status) }}</td>
                <td class="p-2">{{ c.toolCount }}</td>
                <td class="p-2">{{ c.toolsCachedAt ? (c.toolsCachedAt | date: 'short') : '—' }}</td>
              </tr>
            }
          </tbody>
        </table>
      }

      @if (selected(); as d) {
        <article data-testid="connector-detail">
          <div class="mb-2 flex items-center gap-4">
            <h2 class="text-lg font-semibold">{{ d.summary.name }}</h2>
            <span [class]="statusClass(d.summary.status)">{{ statusLabel(d.summary.status) }}</span>
            <button
              mat-stroked-button
              type="button"
              data-testid="refresh-button"
              [disabled]="refreshing()"
              (click)="refresh(d.summary.id)"
              i18n="@@connectors.refresh"
            >
              Оновити tools
            </button>
          </div>
          <p class="mb-2 font-mono text-sm opacity-70" data-testid="connector-endpoint">
            @if (d.summary.transport === 'stdio') {
              {{ d.config.command }} {{ (d.config.arguments ?? []).join(' ') }}
            } @else {
              {{ d.config.url }}
            }
          </p>
          @if (detailError()) {
            <p role="alert" class="mb-2 text-red-700" data-testid="detail-error">{{ detailError() }}</p>
          }
          <app-connector-tools [connectorId]="d.summary.id" [tools]="d.tools" />
        </article>
      }
      }
    </section>
  `,
})
export class ConnectorsPage implements OnInit {
  private readonly api = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);

  /** Вкладка: встановлені конектори або каталог (F6-16). */
  readonly view = signal<'installed' | 'catalog'>('installed');
  /** Запис каталогу з `?catalog=`. */
  readonly highlight = signal<string | null>(null);
  readonly connectors = signal<ConnectorSummary[]>([]);
  readonly selected = signal<ConnectorDetail | null>(null);
  readonly loading = signal(false);
  readonly refreshing = signal(false);
  readonly adding = signal(false);
  readonly error = signal<string | null>(null);
  readonly detailError = signal<string | null>(null);

  readonly statusLabel = statusLabel;

  ngOnInit(): void {
    const catalog = this.route.snapshot.queryParamMap.get('catalog');
    if (catalog !== null) {
      this.view.set('catalog');
      this.highlight.set(catalog || null);
    }
    this.load();
  }

  showView(view: 'installed' | 'catalog'): void {
    this.view.set(view);
    void this.router.navigate([], { relativeTo: this.route, queryParams: { catalog: view === 'catalog' ? (this.highlight() ?? '') : null }, replaceUrl: true });
  }

  /** Конектор з каталогу готовий: показати його у списку встановлених. */
  onInstalled(detail: ConnectorDetail): void {
    this.showView('installed');
    this.onCreated(detail);
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.connectors.set(page.items);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  select(id: string): void {
    this.detailError.set(null);
    this.reloadDetail(id);
  }

  private reloadDetail(id: string): void {
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => this.selected.set(detail),
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  refresh(id: string): void {
    this.refreshing.set(true);
    this.detailError.set(null);
    this.api
      .refreshTools(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.refreshing.set(false);
          this.applyDetail(detail);
          this.notify.success($localize`:@@connectors.refreshed:Tools оновлено.`);
        },
        error: (e: unknown) => {
          this.refreshing.set(false);
          this.detailError.set(apiErrorMessage(e));
          // Сервер уже записав стан `error` — перечитати список і деталі.
          this.load();
          this.reloadDetail(id);
        },
      });
  }

  onCreated(detail: ConnectorDetail): void {
    this.adding.set(false);
    this.connectors.update((list) => [...list.filter((c) => c.id !== detail.summary.id), detail.summary]);
    this.selected.set(detail);
    this.notify.success($localize`:@@connectors.created:Конектор «${detail.summary.name}:name:» додано.`);
  }

  statusClass(status: ConnectorStatus): string {
    switch (status) {
      case 'ok':
        return 'text-green-700';
      case 'error':
        return 'text-red-700';
      default:
        return 'opacity-70';
    }
  }

  private applyDetail(detail: ConnectorDetail): void {
    this.selected.set(detail);
    this.connectors.update((list) => list.map((c) => (c.id === detail.summary.id ? detail.summary : c)));
  }
}
