/** Час у поясі застосунку (Europe/Kyiv) незалежно від поясу браузера. */

function parts(date: Date, timeZone: string): Record<string, number> {
    const fmt = new Intl.DateTimeFormat('en-US', {
        timeZone,
        hourCycle: 'h23',
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
    });
    const result: Record<string, number> = {};
    for (const p of fmt.formatToParts(date)) {
        if (p.type !== 'literal') {
            result[p.type] = Number(p.value);
        }
    }
    return result;
}

function offsetMs(date: Date, timeZone: string): number {
    const p = parts(date, timeZone);
    const asUtc = Date.UTC(
        p['year'],
        p['month'] - 1,
        p['day'],
        p['hour'],
        p['minute'],
        p['second'],
    );
    return asUtc - Math.floor(date.getTime() / 1000) * 1000;
}

/** `2025-11-12T10:00` (місцевий час поясу) → ISO UTC. */
export function zonedToIso(local: string, timeZone: string): string {
    const [d, t] = local.split('T');
    const [y, m, day] = d.split('-').map(Number);
    const [hh, mm] = (t ?? '00:00').split(':').map(Number);
    const guess = Date.UTC(y, m - 1, day, hh, mm);
    let utc = guess - offsetMs(new Date(guess), timeZone);
    utc = guess - offsetMs(new Date(utc), timeZone);
    return new Date(utc).toISOString();
}

/** ISO → `2025-11-12T10:00` у поясі (для `<input type="datetime-local">`). */
export function isoToZoned(iso: string, timeZone: string): string {
    const p = parts(new Date(iso), timeZone);
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${p['year']}-${pad(p['month'])}-${pad(p['day'])}T${pad(p['hour'])}:${pad(p['minute'])}`;
}

/** `ср, 12.11, 10:00`. */
export function formatDateTime(iso: string | null | undefined, timeZone: string): string {
    if (!iso) {
        return '—';
    }
    const p = parts(new Date(iso), timeZone);
    const weekday = ['нд', 'пн', 'вт', 'ср', 'чт', 'пт', 'сб'][
        new Date(Date.UTC(p['year'], p['month'] - 1, p['day'])).getUTCDay()
    ];
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${weekday}, ${pad(p['day'])}.${pad(p['month'])}, ${pad(p['hour'])}:${pad(p['minute'])}`;
}

/** Початок сьогоднішнього дня в поясі + `days` днів → ISO. */
export function dayStartIso(timeZone: string, days = 0): string {
    const p = parts(new Date(), timeZone);
    const pad = (n: number) => String(n).padStart(2, '0');
    const base = new Date(Date.UTC(p['year'], p['month'] - 1, p['day'] + days));
    return zonedToIso(
        `${base.getUTCFullYear()}-${pad(base.getUTCMonth() + 1)}-${pad(base.getUTCDate())}T00:00`,
        timeZone,
    );
}

export const WEEKDAYS = [
    '',
    'Понеділок',
    'Вівторок',
    'Середа',
    'Четвер',
    'Пʼятниця',
    'Субота',
    'Неділя',
];
