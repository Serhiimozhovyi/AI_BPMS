import { WorkflowDefinition } from '../workflows/workflows-api.service';

export type WorkflowSettings = WorkflowDefinition['settings'];

/** Поля діалогу «Налаштування» (F4-05) у вигляді для форми. */
export interface SettingsFormValue {
  timezone: string;
  /** Текст поля; порожньо або `0` — без власного таймауту (діє серверна межа). */
  executionTimeoutSec: string;
  errorWorkflowId: string | null;
  saveSuccessfulData: boolean;
}

/** Помилка поля таймауту: `integer` — не ціле число ≥ 0. Серверну межу перевіряє Api при збереженні. */
export type TimeoutError = 'integer' | null;

/** Часові пояси IANA для вибору; `UTC` першим, поточне значення — завжди в списку, навіть якщо рушій його не знає. */
export function timezones(current: string, supported: readonly string[] = supportedTimezones()): string[] {
  const zones = supported.filter((z) => z !== 'UTC');
  if (current && current !== 'UTC' && !zones.includes(current)) zones.unshift(current);
  return ['UTC', ...zones];
}

function supportedTimezones(): string[] {
  const intl = Intl as { supportedValuesOf?: (key: string) => string[] };
  return intl.supportedValuesOf?.('timeZone') ?? [];
}

export function settingsFormValue(settings: WorkflowSettings): SettingsFormValue {
  return {
    timezone: settings.timezone || 'UTC',
    executionTimeoutSec: settings.executionTimeoutSec > 0 ? String(settings.executionTimeoutSec) : '',
    errorWorkflowId: settings.errorWorkflowId ?? null,
    saveSuccessfulData: settings.saveSuccessfulData,
  };
}

export function timeoutError(text: string): TimeoutError {
  const trimmed = text.trim();
  return trimmed === '' || /^\d+$/.test(trimmed) ? null : 'integer';
}

/** Нові `settings`: поля діалогу поверх старих — невідомі поля налаштувань зберігаються. */
export function applySettings(settings: WorkflowSettings, value: SettingsFormValue): WorkflowSettings {
  const timeout = value.executionTimeoutSec.trim();
  return {
    ...settings,
    timezone: value.timezone,
    executionTimeoutSec: timeout === '' ? 0 : Number(timeout),
    errorWorkflowId: value.errorWorkflowId,
    saveSuccessfulData: value.saveSuccessfulData,
  };
}

/** Чи змінились налаштування (щоб не створювати порожній крок історії і версію). */
export function settingsChanged(a: WorkflowSettings, b: WorkflowSettings): boolean {
  return (
    a.timezone !== b.timezone ||
    a.executionTimeoutSec !== b.executionTimeoutSec ||
    (a.errorWorkflowId ?? null) !== (b.errorWorkflowId ?? null) ||
    a.saveSuccessfulData !== b.saveSuccessfulData
  );
}
