import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { MatAutocompleteModule, MatAutocompleteSelectedEvent } from '@angular/material/autocomplete';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ConnectorResource, ConnectorSummary, ConnectorsApiService, apiErrorMessage } from '../connectors/connectors-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';

/**
 * Блок «Подія конектора» панелі вузла `mcp.subscribe` (F6-20): конектор зі списку встановлених і ресурс подій з
 * `resources/list` (кеш на сервері — хвилина). Ресурс можна ввести й вручну (шаблон `kit://changes/{entity}` — з
 * підставленим значенням). Зміна конектора скидає ресурс. Решта параметрів вузла — звичайна форма панелі.
 */
@Component({
  selector: 'app-subscribe-source',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, MatAutocompleteModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <div class="source" data-testid="subscribe-source" [attr.inert]="readonly() ? '' : null">
      <mat-form-field appearance="outline" class="source__field" subscriptSizing="dynamic">
        <mat-label i18n="@@subscribe.connector">Підключення</mat-label>
        <mat-select data-testid="subscribe-connector" [value]="serverId()" (selectionChange)="connectorPicked($event.value)">
          @for (c of connectors(); track c.id) {
            <mat-option [value]="c.id">{{ c.name }}</mat-option>
          }
          @if (serverId() && !known()) {
            <mat-option [value]="serverId()">{{ serverId() }} — <span i18n="@@subscribe.connector.missing">не знайдено</span></mat-option>
          }
          <mat-option value="__new__" data-testid="subscribe-connection-new" i18n="@@nodeform.connection.new">+ Нове підключення…</mat-option>
        </mat-select>
      </mat-form-field>

      <mat-form-field appearance="outline" class="source__field" subscriptSizing="dynamic">
        <mat-label i18n="@@subscribe.resource">Ресурс подій</mat-label>
        <input
          matInput
          data-testid="subscribe-resource"
          [formControl]="resource"
          [matAutocomplete]="list"
          (input)="resourceTyped($any($event.target).value)"
        />
        <mat-autocomplete #list="matAutocomplete" (optionSelected)="resourcePicked($event)">
          @for (r of resources(); track r.uri) {
            <mat-option [value]="r.uri" data-testid="subscribe-option" [attr.data-uri]="r.uri">
              <span>{{ r.name }}</span>
              <span class="source__uri">{{ r.uri }}</span>
              @if (r.description) {
                <span class="source__desc">{{ r.description }}</span>
              }
            </mat-option>
          }
        </mat-autocomplete>
        <button
          mat-icon-button
          matSuffix
          type="button"
          data-testid="subscribe-refresh"
          [disabled]="loading() || !serverId()"
          aria-label="Оновити список ресурсів"
          i18n-aria-label="@@subscribe.refresh"
          (click)="load(true)"
        >↻</button>
      </mat-form-field>

      @if (error(); as e) {
        <p class="source__note source__note--error" role="alert" data-testid="subscribe-error">{{ e }}</p>
      } @else if (serverId() && loaded() && resources().length === 0) {
        <p class="source__note" role="status" data-testid="subscribe-empty" i18n="@@subscribe.empty">
          Конектор не віддає ресурсів подій. Введіть URI вручну або використайте вебхук чи опитування за розкладом.
        </p>
      } @else if (templateSelected()) {
        <p class="source__note" role="status" data-testid="subscribe-template" i18n="@@subscribe.template">
          Це шаблон URI — підставте значення замість {{ '{' }}…{{ '}' }}.
        </p>
      }
    </div>
  `,
  styles: `
    .source {
      display: flex;
      flex-direction: column;
      gap: 12px;
      margin-bottom: 12px;
    }

    .source__field {
      width: 100%;
    }

    .source__uri {
      margin-left: 8px;
      font-family: monospace;
      font-size: 12px;
      opacity: 0.6;
    }

    .source__desc {
      display: block;
      font-size: 12px;
      opacity: 0.7;
    }

    .source__note {
      margin: 0;
      font-size: 13px;
      opacity: 0.8;
    }

    .source__note--error {
      color: var(--mat-sys-error);
      opacity: 1;
    }
  `,
})
export class SubscribeSourceComponent implements OnInit {
  private readonly api = inject(ConnectorsApiService);
  private readonly destroyRef = inject(DestroyRef);

  /** Вузол `mcp.subscribe`. */
  readonly node = input.required<WorkflowNode>();
  readonly readonly = input(false);

  /** Змінений вузол (`serverId`/`resourceUri`). */
  readonly nodeChange = output<WorkflowNode>();

  /** «+ Нове підключення…»: редактор відкриває діалог встановлення. */
  readonly newConnection = output<void>();

  protected readonly connectors = signal<ConnectorSummary[]>([]);
  protected readonly resources = signal<ConnectorResource[]>([]);
  protected readonly loading = signal(false);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly resource = new FormControl('', { nonNullable: true });

  protected readonly serverId = computed(() => text(this.node().parameters?.['serverId']));
  protected readonly known = computed(() => this.connectors().some((c) => c.id === this.serverId()));
  protected readonly templateSelected = computed(() => {
    const uri = text(this.node().parameters?.['resourceUri']);
    return uri.includes('{') && this.resources().some((r) => r.isTemplate && r.uri === uri);
  });

  private loadedFor = '';
  private listedFor = '';
  private readonly listed = signal(false);

  constructor() {
    // Підключення, створене з панелі (F3-95), ще не у списку: перечитати його один раз для цього `serverId`.
    effect(() => {
      const id = this.serverId();
      if (!id || !this.listed() || this.listedFor === id || this.known()) return;
      untracked(() => {
        this.listedFor = id;
        this.api
          .list()
          .pipe(takeUntilDestroyed(this.destroyRef))
          .subscribe({ next: (page) => this.connectors.set(page.items), error: () => undefined });
      });
    });
  }

  ngOnInit(): void {
    this.resource.setValue(text(this.node().parameters?.['resourceUri']), { emitEvent: false });
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (page) => {
          this.connectors.set(page.items);
          this.listed.set(true);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
    this.load(false);
  }

  protected load(refresh: boolean): void {
    const id = this.serverId();
    if (!id) {
      this.resources.set([]);
      return;
    }
    if (!refresh && this.loadedFor === id) return;
    this.loadedFor = id;
    this.loading.set(true);
    this.error.set(null);
    this.api
      .resources(id, refresh)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (items) => {
          this.resources.set(items);
          this.loaded.set(true);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.resources.set([]);
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  protected connectorPicked(id: string): void {
    if (this.readonly()) return;
    if (id === '__new__') {
      this.newConnection.emit();
      return;
    }
    if (id === this.serverId()) return;
    this.resource.setValue('', { emitEvent: false });
    this.loaded.set(false);
    this.emit({ serverId: id, resourceUri: undefined });
    this.load(false);
  }

  protected resourcePicked(event: MatAutocompleteSelectedEvent): void {
    this.emit({ resourceUri: String(event.option.value) });
  }

  protected resourceTyped(value: string): void {
    this.emit({ resourceUri: value || undefined });
  }

  private emit(change: Record<string, string | undefined>): void {
    if (this.readonly()) return;
    const parameters = { ...(this.node().parameters ?? {}) } as Record<string, unknown>;
    for (const [key, value] of Object.entries(change)) {
      if (value === undefined) delete parameters[key];
      else parameters[key] = value;
    }
    this.nodeChange.emit({ ...this.node(), parameters });
  }
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : '';
}
