import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { forkJoin } from 'rxjs';
import { EmptyStateComponent } from '../../shared/empty-state.component';
import { NotifyService } from '../../shared/notify.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { CredentialFormComponent } from '../../credentials/credential-form.component';
import { DOCUMENT } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { oauthLabel, usageLabel } from '../../credentials/credential-input';
import { CredentialSummary, CredentialTypeInfo, CredentialsApiService } from '../../credentials/credentials-api.service';

/** `/credentials`: облікові дані без значень секретів — список, створення за типом, редагування, видалення (F4-02). */
@Component({
  selector: 'app-credentials-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatProgressBarModule, CredentialFormComponent, EmptyStateComponent],
  template: `
    <section class="p-6">
      <div class="mb-2 flex items-center gap-4">
        @if (!embedded()) {
          <h1 class="mat-font-headline-sm" i18n="@@credentials.title">Облікові дані</h1>
        }
        @if (!adding() && types().length > 0) {
          <button mat-flat-button type="button" data-testid="add-button" (click)="startAdd()" i18n="@@credentials.add">
            Додати облікові дані
          </button>
        }
      </div>
      <p class="mb-4 max-w-3xl opacity-70" i18n="@@credentials.hint">
        Ключі, токени й паролі для конекторів, AI-вузлів і webhook-ів. Значення шифруються й після збереження більше не показуються.
      </p>

      @if (adding()) {
        <div class="mb-6 max-w-2xl rounded-lg border border-ac-line p-4">
          <h2 class="mb-2 font-semibold" i18n="@@credentials.form.newTitle">Нові облікові дані</h2>
          <app-credential-form [types]="types()" (saved)="onSaved($event)" (cancelled)="adding.set(false)" />
        </div>
      }

      @if (oauthResult(); as r) {
        <p
          role="status"
          class="mb-4 max-w-3xl rounded-lg p-3"
          [class.state-ok]="r.ok"
          [class.state-bad]="!r.ok"
          data-testid="oauth-result"
        >
          {{ r.message }}
        </p>
      }

      @if (loading()) {
        <mat-progress-bar mode="indeterminate" />
      }
      @if (error()) {
        <p role="alert" class="mb-4 text-red-700" data-testid="page-error">{{ error() }}</p>
      }

      @if (!loading() && credentials().length === 0 && !error()) {
        <app-empty-state
          icon="key"
          i18n-title="@@credentials.empty.title"
          title="Ще немає облікових даних"
          i18n-description="@@credentials.empty.text"
          description="Тут зберігаються ключі, токени й паролі для конекторів, AI-вузлів і webhook-ів — у сценаріях лишається лише посилання на них."
          [actionLabel]="types().length > 0 ? addLabel : null"
          (action)="startAdd()"
        />
      }

      @if (credentials().length > 0) {
        <table class="w-full text-left" data-testid="credentials-table">
          <thead>
            <tr class="border-b border-ac-line">
              <th class="p-2" i18n="@@credentials.col.name">Назва</th>
              <th class="p-2" i18n="@@credentials.col.type">Тип</th>
              <th class="p-2" i18n="@@credentials.col.fields">Заповнено</th>
              <th class="p-2" i18n="@@credentials.col.connections">Підключень</th>
              <th class="p-2" i18n="@@credentials.col.usage">Де використовується</th>
              <th class="p-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (c of credentials(); track c.id) {
              <tr class="border-b border-ac-line-2 align-top" [attr.data-credential]="c.id">
                <td class="p-2 font-medium">{{ c.name }}</td>
                <td class="p-2">{{ typeLabel(c.type) }}</td>
                <td class="p-2 text-sm">
                  @if (c.error) {
                    <span class="text-red-700" data-testid="decrypt-error">{{ c.error }}</span>
                  } @else if (c.oauth; as o) {
                    <span
                      class="rounded px-2 py-0.5"
                      [class.state-ok]="o.status === 'connected'"
                      [class.state-bad]="o.status === 'expired' || o.status === 'error'"
                      [class.state-muted]="o.status === 'notConnected'"
                      data-testid="oauth-status"
                      [attr.data-status]="o.status"
                      >{{ oauthLabel(o.status) }}</span
                    >
                  } @else {
                    {{ visibleFields(c).join(', ') || '—' }}
                  }
                </td>
                <td class="p-2 text-sm" data-testid="connection-count">{{ c.usedBy.connectors.length }}</td>
                <td class="p-2 text-sm">{{ usage(c) }}</td>
                <td class="p-2 text-right whitespace-nowrap">
                  @if (confirming() === c.id) {
                    <span class="mr-2" i18n="@@credentials.delete.confirm">Видалити?</span>
                    <button mat-flat-button type="button" data-testid="confirm-delete" (click)="remove(c)" i18n="@@credentials.delete.yes">Так</button>
                    <button mat-button type="button" data-testid="cancel-delete" (click)="confirming.set(null)" i18n="@@credentials.delete.no">Ні</button>
                  } @else {
                    @if (c.type === 'oauth2') {
                      <button mat-stroked-button type="button" data-testid="oauth-connect" [disabled]="connecting() === c.id" (click)="connect(c)">
                        @if (c.oauth?.status === 'notConnected') {
                          <span i18n="@@credentials.oauth.connect">Підключити</span>
                        } @else {
                          <span i18n="@@credentials.oauth.reconnect">Підключити знову</span>
                        }
                      </button>
                    }
                    <button mat-button type="button" data-testid="edit" (click)="editing.set(c.id)" i18n="@@credentials.edit">Змінити</button>
                    <button mat-button type="button" data-testid="delete" (click)="confirming.set(c.id)" i18n="@@credentials.delete">Видалити</button>
                  }
                </td>
              </tr>
              @if (editing() === c.id) {
                <tr>
                  <td colspan="6" class="p-2">
                    <div class="max-w-2xl rounded-lg border border-ac-line p-4">
                      <app-credential-form [types]="types()" [credential]="c" (saved)="onSaved($event)" (cancelled)="editing.set(null)" />
                    </div>
                  </td>
                </tr>
              }
            }
          </tbody>
        </table>
      }
    </section>
  `,
  styles: `
    .state-ok {
      background: var(--ac-green-15);
    }

    .state-bad {
      background: var(--ac-red-15);
    }

    .state-muted {
      background: var(--ac-cool-grey);
    }
  `,
})
export class CredentialsPage implements OnInit {
  /** У сторінці «Підключення» (F3-99) заголовок показує батьківська сторінка. */
  readonly embedded = input(false);
  private readonly api = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notify = inject(NotifyService);
  protected readonly addLabel = $localize`:@@credentials.empty.action:Додати облікові дані`;
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);

  readonly credentials = signal<CredentialSummary[]>([]);
  readonly types = signal<CredentialTypeInfo[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly adding = signal(false);
  readonly editing = signal<string | null>(null);
  readonly confirming = signal<string | null>(null);

  readonly connecting = signal<string | null>(null);
  /** Результат повернення з OAuth-провайдера (`?oauth=connected|error&code=…`). */
  readonly oauthResult = signal<{ ok: boolean; message: string } | null>(null);

  readonly usage = usageLabel;
  readonly oauthLabel = oauthLabel;

  ngOnInit(): void {
    const query = this.route.snapshot.queryParamMap;
    const oauth = query.get('oauth');
    if (oauth === 'connected') {
      this.oauthResult.set({ ok: true, message: $localize`:@@credentials.oauth.done:Підключено: токен отримано, AI_BPMS оновлюватиме його сам.` });
    } else if (oauth === 'error') {
      const code = query.get('code') ?? '';
      this.oauthResult.set({
        ok: false,
        message:
          code === 'access_denied'
            ? $localize`:@@credentials.oauth.denied:Підключення скасовано: доступ не надано.`
            : code === 'invalid_state'
              ? $localize`:@@credentials.oauth.invalidState:Посилання підключення застаріло або вже використане. Натисніть «Підключити» ще раз.`
              : $localize`:@@credentials.oauth.failed:Не вдалося підключити (${code}:code:).`,
      });
    }

    this.loading.set(true);
    forkJoin([this.api.types(), this.api.list()])
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ([types, list]) => {
          this.types.set(types);
          this.credentials.set(list);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  /** Заповнені поля підписами типу (F3-94: «Токен бота», а не `token`); службові (токени OAuth2) не показуються. */
  visibleFields(c: CredentialSummary): string[] {
    const fields = this.types().find((t) => t.type === c.type)?.fields ?? [];
    const managed = new Set(fields.filter((f) => f.kind === 'managed').map((f) => f.name));
    const titles = new Map(fields.filter((f) => f.kind !== 'secretMap').map((f) => [f.name, f.title]));
    return c.fields.filter((f) => !managed.has(f)).map((f) => titles.get(f) ?? f);
  }

  /** OAuth2: адреса провайдера від Api → перехід; повернення — на цю ж сторінку з `?oauth=…`. */
  connect(c: CredentialSummary): void {
    this.connecting.set(c.id);
    this.error.set(null);
    this.api
      .startOAuth(c.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ authorizationUrl }) => this.document.location.assign(authorizationUrl),
        error: (e: unknown) => {
          this.connecting.set(null);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  typeLabel(type: string): string {
    return this.types().find((t) => t.type === type)?.displayName ?? type;
  }

  startAdd(): void {
    this.editing.set(null);
    this.adding.set(true);
  }

  onSaved(saved: CredentialSummary): void {
    this.notify.success($localize`:@@credentials.saved:Облікові дані «${saved.name}:name:» збережено.`);
    this.adding.set(false);
    this.editing.set(null);
    this.credentials.update((list) => {
      const rest = list.filter((c) => c.id !== saved.id);
      return [...rest, saved].sort((a, b) => a.name.localeCompare(b.name));
    });
  }

  remove(c: CredentialSummary): void {
    this.error.set(null);
    this.api
      .delete(c.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.confirming.set(null);
          this.credentials.update((list) => list.filter((x) => x.id !== c.id));
          this.notify.success($localize`:@@credentials.removed:Облікові дані «${c.name}:name:» видалено.`);
        },
        error: (e: unknown) => {
          this.confirming.set(null);
          this.notify.error($localize`:@@credentials.removeFailed:Не вдалося видалити облікові дані.`, apiErrorMessage(e));
          this.error.set(apiErrorMessage(e));
        },
      });
  }
}
