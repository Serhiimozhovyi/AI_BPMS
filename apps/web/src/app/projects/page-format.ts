import type { ChipTone } from '../shared/chip/chip.component';
import type { PageColor, PageComputed, PageRow } from './project-pages-api.service';

/** Колонка для показу: підпис і тип з Api, формат, підписи й кольори — з визначення сторінки. */
export interface DisplayColumn {
  column: string;
  label: string;
  type: string;
  format?: string | null;
  labels?: Record<string, string>;
  colors?: Record<string, PageColor>;
  /** Обчислювана колонка (`computed`): значення в `data[column]`, сортувати за нею не можна. */
  computed?: PageComputed;
}

/** Підготовлена комірка: текст і, для `format: badge`, колір мітки. */
export interface CellView {
  text: string;
  color?: PageColor;
}

const DASH = '—';

const WEEKDAYS = ['понеділок', 'вівторок', 'середа', 'четвер', 'пʼятниця', 'субота', 'неділя'];

/** Колір чипа `bp-chip` для кольору мітки сторінки (`format: badge`, F8-16). */
export const BADGE_TONES: Record<PageColor, ChipTone> = {
  gray: 'gray',
  green: 'green',
  yellow: 'orange',
  red: 'red',
  blue: 'blue',
  purple: 'violet',
};

/** Класи Tailwind міток за кольором для вузьких місць (календар, плитки). */
export const BADGE_CLASSES: Record<PageColor, string> = {
  gray: 'bg-bp-bg text-bp-text-muted',
  green: 'bg-bp-success/20 text-bp-text',
  yellow: 'bg-bp-warning/20 text-bp-text',
  red: 'bg-bp-danger/20 text-bp-text',
  blue: 'bg-bp-info/20 text-bp-text',
  purple: 'bg-bp-cat-violet/20 text-bp-text',
};

function zone(timezone: string | undefined): string {
  try {
    new Intl.DateTimeFormat('uk-UA', { timeZone: timezone });
    return timezone || 'UTC';
  } catch {
    return 'UTC';
  }
}

function parseDate(value: unknown): Date | null {
  if (typeof value !== 'string') return null;
  const date = new Date(/^\d{4}-\d{2}-\d{2}$/.test(value) ? `${value}T00:00:00Z` : value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/** `date` зберігається без часу: показуємо в UTC, щоб пояс не зсунув день. */
function dateOnly(value: unknown): boolean {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value);
}

/** Телефон: цифри `380671234567` чи `0671234567` → `+38 (067) 123-45-67`; інше — як є. */
export function formatPhone(value: string): string {
  const digits = value.replace(/\D/g, '');
  const national = digits.length === 12 && digits.startsWith('38') ? digits.slice(2) : digits.length === 10 && digits.startsWith('0') ? digits : null;
  if (!national) return value;
  return `+38 (${national.slice(0, 3)}) ${national.slice(3, 6)}-${national.slice(6, 8)}-${national.slice(8, 10)}`;
}

function numberText(value: number): string {
  return new Intl.NumberFormat('uk-UA', { maximumFractionDigits: 2 }).format(value);
}

function booleanText(value: boolean, labels: Record<string, string> | undefined): string {
  return labels?.[String(value)] ?? (value ? $localize`:@@app.page.yes:так` : $localize`:@@app.page.no:ні`);
}

/**
 * Значення комірки для людей за `format` колонки (docs/projects.md §3.6): дати й час у поясі застосунку, день тижня,
 * підпис значення, кольорова мітка, телефон, «без ліміту». Показане значення `ref` (`display`) має перевагу над ключем.
 */
export function cellView(row: PageRow, column: DisplayColumn, timezone = 'UTC'): CellView {
  const raw = row.data[column.column];
  const shown = row.display?.[column.column];
  const format = column.format ?? undefined;

  if (column.computed) return { text: computedText(row, column, raw) };

  if (shown !== undefined && shown !== null) {
    return format === 'badge' ? { text: shown, color: badgeColor(column, raw) } : { text: shown };
  }
  if (raw === null || raw === undefined || raw === '') return { text: DASH };

  const tz = zone(timezone);
  switch (format ?? column.type) {
    case 'badge':
      return { text: label(column, raw), color: badgeColor(column, raw) };
    case 'enum':
      return { text: label(column, raw) };
    case 'boolean':
      return typeof raw === 'boolean' ? { text: booleanText(raw, column.labels) } : { text: String(raw) };
    case 'capacity':
      return typeof raw === 'number' && raw === 0 ? { text: column.labels?.['0'] ?? $localize`:@@app.page.unlimited:без ліміту` } : plain(raw);
    case 'phone':
      return { text: typeof raw === 'string' ? formatPhone(raw) : String(raw) };
    case 'weekday':
      return { text: weekday(raw, tz) };
    case 'time':
      return { text: time(raw, tz) };
    case 'date':
      return { text: formatDate(raw, tz, false) };
    case 'datetime':
      return { text: formatDate(raw, tz, true) };
    default:
      return plain(raw);
  }
}

function plain(raw: unknown): CellView {
  if (typeof raw === 'boolean') return { text: booleanText(raw, undefined) };
  if (typeof raw === 'number') return { text: numberText(raw) };
  return { text: typeof raw === 'object' ? JSON.stringify(raw) : String(raw) };
}

function label(column: DisplayColumn, raw: unknown): string {
  const key = String(raw);
  return column.labels?.[key] ?? key;
}

function badgeColor(column: DisplayColumn, raw: unknown): PageColor {
  return column.colors?.[String(raw)] ?? 'gray';
}

function formatDate(raw: unknown, tz: string, withTime: boolean): string {
  const date = parseDate(raw);
  if (!date) return String(raw);
  const options: Intl.DateTimeFormatOptions = { weekday: 'short', day: 'numeric', month: 'short' };
  if (withTime && !dateOnly(raw)) {
    options.hour = '2-digit';
    options.minute = '2-digit';
  }
  return new Intl.DateTimeFormat('uk-UA', { ...options, timeZone: dateOnly(raw) ? 'UTC' : tz }).format(date);
}

function time(raw: unknown, tz: string): string {
  const date = typeof raw === 'string' && !/^\d{2}:\d{2}/.test(raw) ? parseDate(raw) : null;
  if (!date) return String(raw);
  return new Intl.DateTimeFormat('uk-UA', { hour: '2-digit', minute: '2-digit', timeZone: tz }).format(date);
}

function weekday(raw: unknown, tz: string): string {
  if (typeof raw === 'number' && raw >= 1 && raw <= 7) return WEEKDAYS[raw - 1];
  const date = parseDate(raw);
  return date ? new Intl.DateTimeFormat('uk-UA', { weekday: 'long', timeZone: dateOnly(raw) ? 'UTC' : tz }).format(date) : String(raw);
}

/** Обчислювана колонка: число, а з `of` — «3 / 8» (`0` у `of` — без ліміту). */
function computedText(row: PageRow, column: DisplayColumn, value: unknown): string {
  const count = typeof value === 'number' ? numberText(value) : DASH;
  const of = column.computed?.of ? row.data[column.computed.of] : undefined;
  if (typeof of !== 'number') return count;
  return of === 0 ? `${count} / ${column.labels?.['0'] ?? $localize`:@@app.page.unlimited:без ліміту`}` : `${count} / ${numberText(of)}`;
}
