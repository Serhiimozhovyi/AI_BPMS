import { LimitAction, WorkflowDefinition } from '../workflows/workflows-api.service';

export type WorkflowSettings = WorkflowDefinition['settings'];

/** Поля діалогу «Налаштування» (F4-05) у вигляді для форми. */
export interface SettingsFormValue {
  timezone: string;
  /** Текст поля; порожньо або `0` — без власного таймауту (діє серверна межа). */
  executionTimeoutSec: string;
  errorWorkflowId: string | null;
  saveSuccessfulData: boolean;
  /** Ліміти навантаження (F6-06): текст полів; порожньо — ліміту немає. */
  concurrencyMax: string;
  concurrencyOnLimit: LimitAction;
  throttleLimit: string;
  /** Порожньо — 60 с. */
  throttlePeriodSec: string;
  throttleOnLimit: LimitAction;
  disableAfterErrors: string;
}

/** Поля лімітів, що перевіряються як цілі числа, і їхні межі (як у workflow.schema.json). */
export const LIMIT_RANGES = {
  concurrencyMax: [1, 1000],
  throttleLimit: [1, 100000],
  throttlePeriodSec: [1, 86400],
  disableAfterErrors: [0, 1000],
} as const satisfies Record<string, readonly [number, number]>;

export type LimitField = keyof typeof LIMIT_RANGES;

/** Помилки полів лімітів: поле → межі, в які значення не вкладається (порожнє поле — без помилки). */
export type LimitErrors = Partial<Record<LimitField, readonly [number, number]>>;

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
    concurrencyMax: settings.concurrency ? String(settings.concurrency.max) : '',
    concurrencyOnLimit: settings.concurrency?.onLimit ?? 'queue',
    throttleLimit: settings.throttle ? String(settings.throttle.limit) : '',
    throttlePeriodSec: settings.throttle ? String(settings.throttle.periodSec) : '',
    throttleOnLimit: settings.throttle?.onLimit ?? 'queue',
    disableAfterErrors: settings.disableAfterErrors ? String(settings.disableAfterErrors) : '',
  };
}

export function limitErrors(value: SettingsFormValue): LimitErrors {
  const errors: LimitErrors = {};
  for (const field of Object.keys(LIMIT_RANGES) as LimitField[]) {
    const text = value[field].trim();
    // Період без ліміту запусків не застосовується (поле вимкнене), тож і не блокує збереження.
    if (text === '' || (field === 'throttlePeriodSec' && value.throttleLimit.trim() === '')) continue;
    const [min, max] = LIMIT_RANGES[field];
    if (!/^\d+$/.test(text) || Number(text) < min || Number(text) > max) errors[field] = LIMIT_RANGES[field];
  }
  return errors;
}

export function timeoutError(text: string): TimeoutError {
  const trimmed = text.trim();
  return trimmed === '' || /^\d+$/.test(trimmed) ? null : 'integer';
}

/** Нові `settings`: поля діалогу поверх старих — невідомі поля налаштувань зберігаються. */
export function applySettings(settings: WorkflowSettings, value: SettingsFormValue): WorkflowSettings {
  const timeout = value.executionTimeoutSec.trim();
  const next: WorkflowSettings = {
    ...settings,
    timezone: value.timezone,
    executionTimeoutSec: timeout === '' ? 0 : Number(timeout),
    errorWorkflowId: value.errorWorkflowId,
    saveSuccessfulData: value.saveSuccessfulData,
  };
  // Порожнє поле прибирає ліміт зовсім; невідомі поля всередині ліміту зберігаються.
  const max = value.concurrencyMax.trim();
  if (max === '') delete next.concurrency;
  else next.concurrency = { ...settings.concurrency, max: Number(max), onLimit: value.concurrencyOnLimit };
  const limit = value.throttleLimit.trim();
  const period = value.throttlePeriodSec.trim();
  if (limit === '') delete next.throttle;
  else next.throttle = { ...settings.throttle, limit: Number(limit), periodSec: period === '' ? 60 : Number(period), onLimit: value.throttleOnLimit };
  const errors = Number(value.disableAfterErrors.trim() || 0);
  if (errors > 0) next.disableAfterErrors = errors;
  else delete next.disableAfterErrors;
  return next;
}

/** Чи змінились налаштування (щоб не створювати порожній крок історії і версію). */
export function settingsChanged(a: WorkflowSettings, b: WorkflowSettings): boolean {
  return (
    a.timezone !== b.timezone ||
    a.executionTimeoutSec !== b.executionTimeoutSec ||
    (a.errorWorkflowId ?? null) !== (b.errorWorkflowId ?? null) ||
    a.saveSuccessfulData !== b.saveSuccessfulData ||
    JSON.stringify(a.concurrency ?? null) !== JSON.stringify(b.concurrency ?? null) ||
    JSON.stringify(a.throttle ?? null) !== JSON.stringify(b.throttle ?? null) ||
    (a.disableAfterErrors ?? 0) !== (b.disableAfterErrors ?? 0)
  );
}
