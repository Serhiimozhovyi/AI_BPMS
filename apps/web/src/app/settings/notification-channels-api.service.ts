import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Типи каналів — як `NotificationChannelTypes` в Api. */
export const NOTIFICATION_CHANNEL_TYPES = ['email', 'telegram', 'webhook'] as const;
export type NotificationChannelType = (typeof NOTIFICATION_CHANNEL_TYPES)[number];

/** Події — як `NotificationEvents` в Api. */
export const NOTIFICATION_EVENTS = [
  'execution.failed',
  'workflow.disabled',
  'connector.unavailable',
  'worker.stale',
  'backup.failed',
] as const;

/**
 * Налаштування каналу. `email`: `connectorId` (msgraph), `from`, `to[]`; `telegram`: `connectorId`, `chatId`;
 * `webhook`: `url`, `credentialId?` (заголовки з поля `headers`). Секретів тут немає — лише посилання на credential.
 */
export interface NotificationChannelConfig {
  connectorId?: string;
  from?: string;
  to?: string[];
  chatId?: string;
  url?: string;
  credentialId?: string;
}

/** `NotificationChannel` — рядок `GET /api/v1/notification-channels` (F6-07). */
export interface NotificationChannel {
  id: string;
  name: string;
  type: NotificationChannelType;
  config: NotificationChannelConfig;
  events: string[];
  cooldownSec: number;
  enabled: boolean;
  updatedAt: string;
}

export type NotificationChannelInput = Omit<NotificationChannel, 'id' | 'updatedAt'>;

/** Типовий cooldown (Api: `NotificationChannelService.DefaultCooldownSec`). */
export const NOTIFICATION_DEFAULT_COOLDOWN_SEC = 900;

/** Назва типу каналу для людей. */
export function channelTypeLabel(type: string): string {
  switch (type) {
    case 'email':
      return $localize`:@@notifications.type.email:Пошта (Microsoft 365)`;
    case 'telegram':
      return $localize`:@@notifications.type.telegram:Telegram`;
    case 'webhook':
      return $localize`:@@notifications.type.webhook:Вебхук`;
    default:
      return type;
  }
}

/** Назва події для людей. */
export function notificationEventLabel(event: string): string {
  switch (event) {
    case 'execution.failed':
      return $localize`:@@notifications.event.executionFailed:Виконання з помилкою чи таймаутом`;
    case 'workflow.disabled':
      return $localize`:@@notifications.event.workflowDisabled:Сценарій вимкнено запобіжником`;
    case 'connector.unavailable':
      return $localize`:@@notifications.event.connectorUnavailable:Конектор недоступний`;
    case 'worker.stale':
      return $localize`:@@notifications.event.workerStale:Воркер не відповідає`;
    case 'backup.failed':
      return $localize`:@@notifications.event.backupFailed:Помилка резервної копії`;
    default:
      return event;
  }
}

/** Клієнт `/api/v1/notification-channels`: лише admin. */
@Injectable({ providedIn: 'root' })
export class NotificationChannelsApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/notification-channels`;

  list(): Observable<NotificationChannel[]> {
    return this.http.get<NotificationChannel[]>(this.base);
  }

  create(input: NotificationChannelInput): Observable<NotificationChannel> {
    return this.http.post<NotificationChannel>(this.base, input);
  }

  update(id: string, input: NotificationChannelInput): Observable<NotificationChannel> {
    return this.http.put<NotificationChannel>(`${this.base}/${id}`, input);
  }

  remove(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  test(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/test`, null);
  }
}
