import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router } from '@angular/router';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { CredentialsApiService } from '../../credentials/credentials-api.service';
import { NotifyService } from '../../shared/notify.service';
import { ConnectorCatalogComponent } from '../../connectors/connector-catalog.component';
import { ConnectorFormComponent } from '../../connectors/connector-form.component';
import { agoLabel, needsCheck, statusLabel } from '../../connectors/connector-input';
import { ConnectorToolsComponent } from '../../connectors/connector-tools.component';
import {
  ConnectorDetail,
  ConnectorStatus,
  ConnectorSummary,
  ConnectorsApiService,
  apiErrorCode,
  apiErrorMessage,
} from '../../connectors/connectors-api.service';

/**
 * `/connectors`: MCP-сервери зі статусом, додавання, discovery tools і тестовий виклик (F2-14); вкладка «Каталог»
 * (F6-16, `?catalog=<id>` відкриває її з підсвіченим записом).
 */
@Component({
  selector: 'app-connectors-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatProgressBarModule, ConnectorCatalogComponent, ConnectorFormComponent, ConnectorToolsComponent, EmptyStateComponent],
  template: `
    <section class="p-6">
      <div class="mb-4 flex items-center gap-4">
        @if (!embedded()) {
          <h1 class="mat-font-headline-sm" i18n="@@connectors.title">Конектори</h1>
          <mat-button-toggle-group [value]="currentView()" (change)="showView($event.value)" data-testid="connectors-view">
            <mat-button-toggle value="installed" data-testid="view-installed" i18n="@@connectors.view.installed">Встановлені</mat-button-toggle>
            <mat-button-toggle value="catalog" data-testid="view-catalog" i18n="@@connectors.view.catalog">Каталог</mat-button-toggle>
          </mat-button-toggle-group>
        }
        @if (!adding() && currentView() === 'installed') {
          <button mat-flat-button type="button" data-testid="add-button" (click)="adding.set(true)" i18n="@@connectors.add">
            Додати MCP-сервер
          </button>
        }
      </div>

      @if (currentView() === 'catalog') {
        <app-connector-catalog [highlight]="highlight() ?? highlightId()" (installed)="onInstalled($event)" />
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
              <th class="p-2" i18n="@@connectors.col.tools">Дії</th>
              <th class="p-2" i18n="@@connectors.col.cachedAt">Оновлено</th>
              <th class="p-2"><span class="sr-only" i18n="@@connectors.col.actions">Дії</span></th>
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
                <td class="p-2">
                  <button mat-button type="button" color="warn" data-testid="row-delete" (click)="startDelete(c.id)" i18n="@@connectors.delete">Видалити</button>
                </td>
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
              data-testid="check-connection"
              [disabled]="refreshing()"
              (click)="refresh(d.summary.id)"
              i18n="@@connectors.check"
            >
              Перевірити з'єднання
            </button>
            <button mat-button type="button" color="warn" data-testid="delete-button" [disabled]="deleting()" (click)="confirmingDelete.set(true)" i18n="@@connectors.delete">
              Видалити
            </button>
          </div>
          @if (confirmingDelete()) {
            <div class="mb-3 max-w-2xl rounded-md border border-red-300 p-3" role="alertdialog" data-testid="delete-confirm-box">
              <p class="mb-2" i18n="@@connectors.delete.confirm">Видалити підключення «{{ d.summary.name }}»? Сценарії, що його використовують, перестануть працювати.</p>
              @if (d.credentialId) {
                <mat-checkbox [checked]="deleteCredential()" (change)="deleteCredential.set($event.checked)" data-testid="delete-credential" i18n="@@connectors.delete.credential">
                  Видалити також облікові дані (якщо їх більше ніхто не використовує)
                </mat-checkbox>
              }
              <div class="mt-2 flex gap-2">
                <button mat-flat-button type="button" color="warn" data-testid="delete-confirm" [disabled]="deleting()" (click)="confirmDelete(d)" i18n="@@connectors.delete.yes">Так, видалити</button>
                <button mat-button type="button" data-testid="delete-cancel" (click)="confirmingDelete.set(false)" i18n="@@connectors.delete.no">Ні</button>
              </div>
            </div>
          }
          <p class="mb-2 font-mono text-sm opacity-70" data-testid="connector-endpoint">
            @if (d.summary.transport === 'stdio') {
              {{ d.config.command }} {{ (d.config.arguments ?? []).join(' ') }}
            } @else {
              {{ d.config.url }}
            }
          </p>
          <p class="mb-2 text-sm" data-testid="connection-state" [class]="statusClass(d.summary.status)">{{ connectionState(d.summary) }}</p>
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
  private readonly credentials = inject(CredentialsApiService);

  /** У сторінці «Підключення» (F3-99): без власного заголовка й перемикача, вкладку задає батьківська сторінка. */
  readonly embedded = input(false);
  readonly tab = input<'installed' | 'catalog' | null>(null);
  readonly highlightId = input<string | null>(null);
  readonly viewChange = output<'installed' | 'catalog'>();
  /** Вкладка: встановлені конектори або каталог (F6-16). */
  readonly view = signal<'installed' | 'catalog'>('installed');
  readonly currentView = computed(() => this.tab() ?? this.view());
  /** Запис каталогу з `?catalog=`. */
  readonly highlight = signal<string | null>(null);
  readonly connectors = signal<ConnectorSummary[]>([]);
  /** Причина збою останньої перевірки з'єднання (F3-100). */
  private readonly connectError = signal<string | null>(null);
  readonly selected = signal<ConnectorDetail | null>(null);
  readonly loading = signal(false);
  readonly refreshing = signal(false);
  readonly confirmingDelete = signal(false);
  readonly deleting = signal(false);
  readonly deleteCredential = signal(true);
  readonly adding = signal(false);
  readonly error = signal<string | null>(null);
  readonly detailError = signal<string | null>(null);

  readonly statusLabel = statusLabel;

  ngOnInit(): void {
    const catalog = this.embedded() ? null : this.route.snapshot.queryParamMap.get('catalog');
    if (catalog !== null) {
      this.view.set('catalog');
      this.highlight.set(catalog || null);
    }
    this.load();
  }

  showView(view: 'installed' | 'catalog'): void {
    if (this.embedded()) {
      this.viewChange.emit(view);
      return;
    }
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
    this.connectError.set(null);
    this.reloadDetail(id, true);
  }

  /** «Підключено · 12 дій · перевірено 5 хв тому» або причина збою (F3-100). */
  connectionState(summary: ConnectorSummary): string {
    if (summary.status === 'error') {
      const reason = this.connectError();
      return reason ? $localize`:@@connectors.state.errorReason:Не вдалося підключитись: ${reason}:reason:` : $localize`:@@connectors.state.error:Не вдалося підключитись.`;
    }
    if (summary.status !== 'ok') return $localize`:@@connectors.state.unknown:З'єднання ще не перевірено.`;
    const actions = $localize`:@@connectors.state.actions:${summary.toolCount}:count: дій`;
    const when = summary.toolsCachedAt ? ' · ' + $localize`:@@connectors.state.checked:перевірено ${agoLabel(summary.toolsCachedAt)}:ago:` : '';
    return $localize`:@@connectors.state.ok:Підключено` + ` · ${actions}${when}`;
  }

  private reloadDetail(id: string, autoCheck = false): void {
    this.api
      .get(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.selected.set(detail);
          if (autoCheck && needsCheck(detail.summary)) this.refresh(id, true);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  /** «Видалити» в рядку: відкрити підключення й одразу запитати підтвердження. */
  startDelete(id: string): void {
    this.detailError.set(null);
    this.confirmingDelete.set(true);
    this.reloadDetail(id);
  }

  /**
   * Видалення підключення. 409 (використовують активні сценарії) показується текстом Api зі списком сценаріїв.
   * Облікові дані видаляються окремо й лише на вимогу: якщо їх ще хтось використовує, Api відмовить — підключення
   * при цьому вже видалене, про це повідомляємо окремо.
   */
  confirmDelete(detail: ConnectorDetail): void {
    this.deleting.set(true);
    this.detailError.set(null);
    const { id, name } = detail.summary;
    this.api
      .delete(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.deleting.set(false);
          this.confirmingDelete.set(false);
          this.selected.set(null);
          this.connectors.update((list) => list.filter((c) => c.id !== id));
          this.notify.success($localize`:@@connectors.deleted:Підключення «${name}:name:» видалено.`);
          if (detail.credentialId && this.deleteCredential()) this.removeCredential(detail.credentialId);
        },
        error: (e: unknown) => {
          this.deleting.set(false);
          const hint = apiErrorCode(e) === 'CONNECTOR_IN_USE' ? ' ' + $localize`:@@connectors.delete.inUseHint:Деактивуйте ці сценарії або приберіть із них це підключення — тоді його можна видалити.` : '';
          this.detailError.set(apiErrorMessage(e) + hint);
        },
      });
  }

  private removeCredential(id: string): void {
    this.credentials
      .delete(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.notify.success($localize`:@@connectors.credentialDeleted:Облікові дані видалено.`),
        error: (e: unknown) => this.notify.error($localize`:@@connectors.credentialKept:Облікові дані лишилися: ${apiErrorMessage(e)}:reason:`),
      });
  }

  /** `silent` — автоперевірка при відкритті картки: без сповіщення про успіх. */
  refresh(id: string, silent = false): void {
    this.refreshing.set(true);
    this.detailError.set(null);
    this.api
      .refreshTools(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (detail) => {
          this.refreshing.set(false);
          this.connectError.set(null);
          this.applyDetail(detail);
          if (!silent) this.notify.success($localize`:@@connectors.refreshed:З'єднання перевірено, список дій оновлено.`);
        },
        error: (e: unknown) => {
          this.refreshing.set(false);
          this.connectError.set(apiErrorMessage(e));
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
