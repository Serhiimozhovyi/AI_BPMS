import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { NotifyService } from '../shared/notify.service';
import { ConnectorSummary, ConnectorsApiService, apiErrorMessage } from '../connectors/connectors-api.service';
import { CredentialSummary, CredentialsApiService } from '../credentials/credentials-api.service';
import {
  NOTIFICATION_CHANNEL_TYPES,
  NOTIFICATION_DEFAULT_COOLDOWN_SEC,
  NOTIFICATION_EVENTS,
  NotificationChannel,
  NotificationChannelInput,
  NotificationChannelType,
  NotificationChannelsApiService,
  channelTypeLabel,
  notificationEventLabel,
} from './notification-channels-api.service';
import { BpButtonDirective } from '../shared/button.directive';

/** Чернетка форми каналу: поля всіх типів, у запит іде лише потрібне вибраному типу. */
interface ChannelDraft {
  name: string;
  type: NotificationChannelType;
  connectorId: string;
  from: string;
  to: string;
  chatId: string;
  url: string;
  credentialId: string;
  events: string[];
  cooldownMin: number;
  enabled: boolean;
}

const emptyDraft = (): ChannelDraft => ({
  name: '',
  type: 'telegram',
  connectorId: '',
  from: '',
  to: '',
  chatId: '',
  url: '',
  credentialId: '',
  events: [...NOTIFICATION_EVENTS],
  cooldownMin: NOTIFICATION_DEFAULT_COOLDOWN_SEC / 60,
  enabled: true,
});

/** Тіло запиту з чернетки. */
export function channelInput(d: ChannelDraft): NotificationChannelInput {
  const config =
    d.type === 'email'
      ? { connectorId: d.connectorId, from: d.from.trim(), to: d.to.split(/[,;\s]+/).filter(Boolean) }
      : d.type === 'telegram'
        ? { connectorId: d.connectorId, chatId: d.chatId.trim() }
        : { url: d.url.trim(), ...(d.credentialId ? { credentialId: d.credentialId } : {}) };
  return {
    name: d.name.trim(),
    type: d.type,
    config,
    events: d.events,
    cooldownSec: Math.round(d.cooldownMin * 60),
    enabled: d.enabled,
  };
}

/** Чернетка з наявного каналу. */
function draftOf(c: NotificationChannel): ChannelDraft {
  return {
    ...emptyDraft(),
    name: c.name,
    type: c.type,
    connectorId: c.config.connectorId ?? '',
    from: c.config.from ?? '',
    to: (c.config.to ?? []).join(', '),
    chatId: c.config.chatId ?? '',
    url: c.config.url ?? '',
    credentialId: c.config.credentialId ?? '',
    events: [...c.events],
    cooldownMin: c.cooldownSec / 60,
    enabled: c.enabled,
  };
}

/**
 * Блок «Сповіщення» у `/settings` (F6-07), лише для admin: куди писати про помилки виконань, вимкнені запобіжником
 * сценарії, недоступні конектори, воркери без heartbeat і невдалі бекапи. Пошта й Telegram ідуть через конектори,
 * вебхук — POST JSON із заголовками з облікових даних.
 */
@Component({
  selector: 'app-notification-channels-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <div class="mb-2 flex items-center gap-4">
      <h2 class="text-lg font-semibold" i18n="@@notifications.title">Сповіщення</h2>
      @if (!draft()) {
        <button mat-stroked-button type="button" data-testid="channel-add" (click)="startCreate()" i18n="@@notifications.add">Новий канал</button>
      }
    </div>
    <p class="mb-4 max-w-3xl opacity-70" i18n="@@notifications.hint">
      Куди повідомляти адміністратора про збої. Про ту саму подію одного сценарію (конектора, воркера) канал пише не частіше, ніж раз за вказаний інтервал.
    </p>

    @if (error()) {
      <p role="alert" class="mb-4 text-red-700" data-testid="channels-error">{{ error() }}</p>
    }
    @if (notice()) {
      <p role="status" class="mb-4" data-testid="channels-notice">{{ notice() }}</p>
    }

    @if (draft(); as d) {
      <div class="mb-6 max-w-3xl rounded-lg border border-bp-line p-4" data-testid="channel-form">
        <div class="flex flex-wrap gap-2">
          <mat-form-field appearance="outline" class="w-72">
            <mat-label i18n="@@notifications.form.name">Назва</mat-label>
            <input matInput data-testid="channel-name" [value]="d.name" (input)="patch({ name: text($event) })" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="w-56">
            <mat-label i18n="@@notifications.form.type">Куди</mat-label>
            <mat-select data-testid="channel-type" [value]="d.type" (selectionChange)="patch({ type: $event.value })">
              @for (t of types; track t) {
                <mat-option [value]="t">{{ typeLabel(t) }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </div>

        @if (d.type !== 'webhook') {
          <mat-form-field appearance="outline" class="w-72">
            <mat-label i18n="@@notifications.form.connector">Конектор</mat-label>
            <mat-select data-testid="channel-connector" [value]="d.connectorId" (selectionChange)="patch({ connectorId: $event.value })">
              <mat-option value="">—</mat-option>
              @for (c of connectors(); track c.id) {
                <mat-option [value]="c.id">{{ c.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        }
        @if (d.type === 'email') {
          <div class="flex flex-wrap gap-2">
            <mat-form-field appearance="outline" class="w-72">
              <mat-label i18n="@@notifications.form.from">Від кого (скринька)</mat-label>
              <input matInput data-testid="channel-from" [value]="d.from" (input)="patch({ from: text($event) })" />
            </mat-form-field>
            <mat-form-field appearance="outline" class="w-96">
              <mat-label i18n="@@notifications.form.to">Кому (через кому)</mat-label>
              <input matInput data-testid="channel-to" [value]="d.to" (input)="patch({ to: text($event) })" />
            </mat-form-field>
          </div>
        }
        @if (d.type === 'telegram') {
          <mat-form-field appearance="outline" class="w-72">
            <mat-label i18n="@@notifications.form.chatId">Chat ID</mat-label>
            <input matInput data-testid="channel-chat" [value]="d.chatId" (input)="patch({ chatId: text($event) })" />
          </mat-form-field>
        }
        @if (d.type === 'webhook') {
          <div class="flex flex-wrap gap-2">
            <mat-form-field appearance="outline" class="w-96">
              <mat-label i18n="@@notifications.form.url">URL</mat-label>
              <input matInput data-testid="channel-url" [value]="d.url" (input)="patch({ url: text($event) })" />
            </mat-form-field>
            <mat-form-field appearance="outline" class="w-72">
              <mat-label i18n="@@notifications.form.credential">Заголовки з облікових даних</mat-label>
              <mat-select data-testid="channel-credential" [value]="d.credentialId" (selectionChange)="patch({ credentialId: $event.value })">
                <mat-option value="" i18n="@@notifications.form.noCredential">Без заголовків</mat-option>
                @for (c of credentials(); track c.id) {
                  <mat-option [value]="c.id">{{ c.name }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          </div>
        }

        <fieldset class="mb-4">
          <legend class="mb-1 font-semibold" i18n="@@notifications.form.events">Події</legend>
          @for (e of events; track e) {
            <label class="flex items-center gap-2">
              <input type="checkbox" [attr.data-event]="e" [checked]="d.events.includes(e)" (change)="toggleEvent(e)" />
              {{ eventLabel(e) }}
            </label>
          }
        </fieldset>

        <div class="flex flex-wrap items-start gap-4">
          <mat-form-field appearance="outline" class="w-56">
            <mat-label i18n="@@notifications.form.cooldown">Не частіше, ніж раз на (хв)</mat-label>
            <input matInput type="number" min="0" max="1440" data-testid="channel-cooldown" [value]="d.cooldownMin" (input)="patch({ cooldownMin: +text($event) })" />
          </mat-form-field>
          <label class="mt-4 flex items-center gap-2">
            <input type="checkbox" data-testid="channel-enabled" [checked]="d.enabled" (change)="patch({ enabled: !d.enabled })" />
            <span i18n="@@notifications.form.enabled">Увімкнено</span>
          </label>
        </div>

        <div class="flex gap-2">
          <button mat-flat-button type="button" data-testid="channel-save" [disabled]="saving() || !d.name.trim() || d.events.length === 0" (click)="save()" i18n="@@notifications.form.save">
            Зберегти
          </button>
          <button mat-button type="button" data-testid="channel-cancel" (click)="cancel()" i18n="@@notifications.form.cancel">Скасувати</button>
        </div>
      </div>
    }

    @if (!loading() && channels().length === 0 && !draft()) {
      <p class="mb-6 opacity-70" data-testid="channels-empty" i18n="@@notifications.empty">Каналів ще немає — збої видно лише в журналі виконань.</p>
    }

    @if (channels().length > 0) {
      <div class="overflow-x-auto">
        <table class="mb-6 w-full max-w-5xl text-left" data-testid="channels-table">
          <thead>
            <tr>
              <th class="p-2" i18n="@@notifications.col.name">Канал</th>
              <th class="p-2" i18n="@@notifications.col.type">Куди</th>
              <th class="p-2" i18n="@@notifications.col.events">Події</th>
              <th class="p-2"></th>
            </tr>
          </thead>
          <tbody>
            @for (c of channels(); track c.id) {
              <tr [attr.data-channel]="c.id" [class.opacity-50]="!c.enabled">
                <td class="p-2">
                  {{ c.name }}
                  @if (!c.enabled) {
                    <span class="block text-sm" i18n="@@notifications.disabled">вимкнено</span>
                  }
                </td>
                <td class="p-2">{{ typeLabel(c.type) }}</td>
                <td class="p-2 text-sm">{{ eventsText(c.events) }}</td>
                <td class="whitespace-nowrap p-2">
                  <button mat-button type="button" data-action="test" [disabled]="testing() === c.id" (click)="test(c)" i18n="@@notifications.test">Перевірити</button>
                  <button mat-button type="button" data-action="edit" (click)="startEdit(c)" i18n="@@notifications.edit">Змінити</button>
                  <button bpVariant="danger" mat-button type="button" data-action="delete" (click)="remove(c)" i18n="@@notifications.delete">Видалити</button>
                </td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class NotificationChannelsSection implements OnInit {
  private readonly api = inject(NotificationChannelsApiService);
  private readonly connectorsApi = inject(ConnectorsApiService);
  private readonly credentialsApi = inject(CredentialsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly notify = inject(NotifyService);

  readonly types = NOTIFICATION_CHANNEL_TYPES;
  readonly events = NOTIFICATION_EVENTS;
  readonly typeLabel = channelTypeLabel;
  readonly eventLabel = notificationEventLabel;

  readonly channels = signal<NotificationChannel[]>([]);
  readonly connectors = signal<ConnectorSummary[]>([]);
  readonly credentials = signal<CredentialSummary[]>([]);
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly testing = signal<string | null>(null);
  readonly error = signal<string | null>(null);
  readonly notice = signal<string | null>(null);
  readonly draft = signal<ChannelDraft | null>(null);
  readonly editingId = signal<string | null>(null);
  private optionsLoaded = false;

  ngOnInit(): void {
    this.load();
  }

  text(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }

  eventsText(events: string[]): string {
    return events.map(notificationEventLabel).join(', ');
  }

  patch(change: Partial<ChannelDraft>): void {
    const d = this.draft();
    if (d) this.draft.set({ ...d, ...change });
  }

  toggleEvent(event: string): void {
    const d = this.draft();
    if (!d) return;
    this.patch({ events: d.events.includes(event) ? d.events.filter((e) => e !== event) : [...d.events, event] });
  }

  startCreate(): void {
    this.editingId.set(null);
    this.draft.set(emptyDraft());
    this.loadOptions();
  }

  startEdit(channel: NotificationChannel): void {
    this.editingId.set(channel.id);
    this.draft.set(draftOf(channel));
    this.loadOptions();
  }

  cancel(): void {
    this.draft.set(null);
    this.editingId.set(null);
  }

  save(): void {
    const d = this.draft();
    if (!d) return;
    const id = this.editingId();
    const input = channelInput(d);
    this.saving.set(true);
    this.error.set(null);
    (id ? this.api.update(id, input) : this.api.create(input)).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.saving.set(false);
        this.cancel();
        this.load();
        this.notify.success(id ? $localize`:@@notifications.updated:Канал збережено.` : $localize`:@@notifications.created:Канал додано.`);
      },
      error: (e: unknown) => {
        this.saving.set(false);
        this.error.set(apiErrorMessage(e));
      },
    });
  }

  remove(channel: NotificationChannel): void {
    this.error.set(null);
    this.api
      .remove(channel.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.load();
          this.notify.success($localize`:@@notifications.removed:Канал «${channel.name}:name:» видалено.`);
        },
        error: (e: unknown) => this.error.set(apiErrorMessage(e)),
      });
  }

  test(channel: NotificationChannel): void {
    this.testing.set(channel.id);
    this.error.set(null);
    this.notice.set(null);
    this.api
      .test(channel.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => {
          this.testing.set(null);
          this.notice.set($localize`:@@notifications.testSent:Тестове повідомлення надіслано в «${channel.name}:name:».`);
        },
        error: (e: unknown) => {
          this.testing.set(null);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  private load(): void {
    this.loading.set(true);
    this.api
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (items) => {
          this.channels.set(items);
          this.loading.set(false);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loading.set(false);
        },
      });
  }

  /** Конектори й облікові дані для вибору — лише коли відкрито форму. */
  private loadOptions(): void {
    if (this.optionsLoaded) return;
    this.optionsLoaded = true;
    this.connectorsApi
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (page) => this.connectors.set(page.items), error: () => this.connectors.set([]) });
    this.credentialsApi
      .list()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (items) => this.credentials.set(items), error: () => this.credentials.set([]) });
  }
}
