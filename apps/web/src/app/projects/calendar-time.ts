/** Календарна дата без часу й поясу. */
export interface Civil {
  y: number;
  m: number;
  d: number;
}

/** Дата й час у поясі застосунку; `dow` — 1 (понеділок) … 7 (неділя). */
export interface ZonedParts extends Civil {
  h: number;
  mi: number;
  dow: number;
}

export type CalendarView = 'week' | 'day' | 'month';

const formatters = new Map<string, Intl.DateTimeFormat>();

function zone(timezone: string | undefined): string {
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: timezone });
    return timezone || 'UTC';
  } catch {
    return 'UTC';
  }
}

function formatter(tz: string): Intl.DateTimeFormat {
  let f = formatters.get(tz);
  if (!f) {
    f = new Intl.DateTimeFormat('en-US', {
      timeZone: tz,
      hourCycle: 'h23',
      year: 'numeric',
      month: 'numeric',
      day: 'numeric',
      hour: 'numeric',
      minute: 'numeric',
      weekday: 'short',
    });
    formatters.set(tz, f);
  }
  return f;
}

const WEEKDAY_INDEX: Record<string, number> = { Mon: 1, Tue: 2, Wed: 3, Thu: 4, Fri: 5, Sat: 6, Sun: 7 };

/** Розкладає момент часу на дату й час у поясі застосунку. */
export function zonedParts(instant: Date, timezone: string | undefined): ZonedParts {
  const parts = Object.fromEntries(formatter(zone(timezone)).formatToParts(instant).map((p) => [p.type, p.value]));
  return {
    y: Number(parts['year']),
    m: Number(parts['month']),
    d: Number(parts['day']),
    h: Number(parts['hour']) % 24,
    mi: Number(parts['minute']),
    dow: WEEKDAY_INDEX[parts['weekday']] ?? 1,
  };
}

/** Момент часу, коли в поясі застосунку годинник показує вказані дату й час (літній час враховано). */
export function zonedToInstant(c: Civil, h: number, mi: number, timezone: string | undefined): Date {
  const tz = zone(timezone);
  const wanted = Date.UTC(c.y, c.m - 1, c.d, h, mi);
  let guess = wanted;
  for (let i = 0; i < 2; i++) {
    const p = zonedParts(new Date(guess), tz);
    const shown = Date.UTC(p.y, p.m - 1, p.d, p.h, p.mi);
    guess += wanted - shown;
  }
  return new Date(guess);
}

export function addDays(c: Civil, n: number): Civil {
  const date = new Date(Date.UTC(c.y, c.m - 1, c.d + n));
  return { y: date.getUTCFullYear(), m: date.getUTCMonth() + 1, d: date.getUTCDate() };
}

export function sameDay(a: Civil, b: Civil): boolean {
  return a.y === b.y && a.m === b.m && a.d === b.d;
}

/** Номер дня тижня: 1 — понеділок … 7 — неділя. */
export function weekday(c: Civil): number {
  return ((new Date(Date.UTC(c.y, c.m - 1, c.d)).getUTCDay() + 6) % 7) + 1;
}

export function mondayOf(c: Civil): Civil {
  return addDays(c, 1 - weekday(c));
}

export function dayKey(c: Civil): string {
  return `${c.y}-${String(c.m).padStart(2, '0')}-${String(c.d).padStart(2, '0')}`;
}

/** Сьогоднішня дата в поясі застосунку. */
export function todayIn(timezone: string | undefined, now = new Date()): Civil {
  const p = zonedParts(now, timezone);
  return { y: p.y, m: p.m, d: p.d };
}

/** Дні, що показує вигляд: день, тиждень з понеділка чи місяць цілими тижнями. */
export function viewDays(view: CalendarView, anchor: Civil): Civil[] {
  if (view === 'day') return [anchor];
  if (view === 'week') {
    const monday = mondayOf(anchor);
    return Array.from({ length: 7 }, (_, i) => addDays(monday, i));
  }
  const first = mondayOf({ ...anchor, d: 1 });
  const last = addDays({ y: anchor.m === 12 ? anchor.y + 1 : anchor.y, m: anchor.m === 12 ? 1 : anchor.m + 1, d: 1 }, -1);
  const weeks = Math.ceil((Math.round((Date.UTC(last.y, last.m - 1, last.d) - Date.UTC(first.y, first.m - 1, first.d)) / 86_400_000) + 1) / 7);
  return Array.from({ length: weeks * 7 }, (_, i) => addDays(first, i));
}

/** Діапазон запиту `[from, to)` для днів вигляду, у поясі застосунку. */
export function viewRange(days: Civil[], timezone: string | undefined): { from: Date; to: Date } {
  return { from: zonedToInstant(days[0], 0, 0, timezone), to: zonedToInstant(addDays(days[days.length - 1], 1), 0, 0, timezone) };
}

/** Зсув опорної дати на крок вигляду: день, тиждень чи місяць. */
export function shift(view: CalendarView, anchor: Civil, direction: 1 | -1): Civil {
  if (view === 'day') return addDays(anchor, direction);
  if (view === 'week') return addDays(anchor, 7 * direction);
  const month = anchor.m - 1 + direction;
  return { y: anchor.y + Math.floor(month / 12), m: ((month % 12) + 12) % 12 + 1, d: 1 };
}

/** Значення поля `datetime-local` (`ГГГГ-ММ-ДДTГГ:ХХ`) для клітинки календаря. */
export function localInput(c: Civil, h: number, mi = 0): string {
  return `${dayKey(c)}T${String(h).padStart(2, '0')}:${String(mi).padStart(2, '0')}`;
}
