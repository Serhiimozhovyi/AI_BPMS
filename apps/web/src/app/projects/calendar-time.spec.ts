import { describe, expect, it } from 'vitest';
import { addDays, localInput, mondayOf, shift, todayIn, viewDays, viewRange, weekday, zonedParts, zonedToInstant } from './calendar-time';

const KYIV = 'Europe/Kyiv';

describe('calendar-time', () => {
  it('розкладає момент часу у поясі застосунку', () => {
    expect(zonedParts(new Date('2026-11-02T08:30:00Z'), KYIV)).toEqual({ y: 2026, m: 11, d: 2, h: 10, mi: 30, dow: 1 });
    expect(zonedParts(new Date('2026-07-01T21:30:00Z'), KYIV)).toMatchObject({ d: 2, h: 0, mi: 30 });
  });

  it('збирає момент часу з годинника пояса, зокрема в літній час', () => {
    expect(zonedToInstant({ y: 2026, m: 11, d: 2 }, 10, 0, KYIV).toISOString()).toBe('2026-11-02T08:00:00.000Z');
    expect(zonedToInstant({ y: 2026, m: 7, d: 1 }, 10, 0, KYIV).toISOString()).toBe('2026-07-01T07:00:00.000Z');
  });

  it('тиждень починається з понеділка, місяць — цілими тижнями', () => {
    expect(weekday({ y: 2026, m: 11, d: 1 })).toBe(7);
    expect(mondayOf({ y: 2026, m: 11, d: 1 })).toEqual({ y: 2026, m: 10, d: 26 });
    const week = viewDays('week', { y: 2026, m: 11, d: 4 });
    expect(week).toHaveLength(7);
    expect(week[0]).toEqual({ y: 2026, m: 11, d: 2 });
    const month = viewDays('month', { y: 2026, m: 11, d: 15 });
    expect(month.length % 7).toBe(0);
    expect(month[0]).toEqual({ y: 2026, m: 10, d: 26 });
    expect(month[month.length - 1]).toEqual({ y: 2026, m: 12, d: 6 });
    expect(viewDays('day', { y: 2026, m: 11, d: 4 })).toEqual([{ y: 2026, m: 11, d: 4 }]);
  });

  it('діапазон запиту не довший за ліміт сервера, а межі — опівночі пояса', () => {
    const days = viewDays('month', { y: 2026, m: 8, d: 1 });
    const { from, to } = viewRange(days, KYIV);
    expect((to.getTime() - from.getTime()) / 86_400_000).toBeLessThanOrEqual(62);
    expect(zonedParts(from, KYIV)).toMatchObject({ h: 0, mi: 0 });
  });

  it('кроки вперед і назад за видом', () => {
    expect(shift('week', { y: 2026, m: 11, d: 4 }, 1)).toEqual({ y: 2026, m: 11, d: 11 });
    expect(shift('day', { y: 2026, m: 11, d: 1 }, -1)).toEqual({ y: 2026, m: 10, d: 31 });
    expect(shift('month', { y: 2026, m: 12, d: 9 }, 1)).toEqual({ y: 2027, m: 1, d: 1 });
    expect(shift('month', { y: 2026, m: 1, d: 9 }, -1)).toEqual({ y: 2025, m: 12, d: 1 });
    expect(addDays({ y: 2026, m: 12, d: 31 }, 1)).toEqual({ y: 2027, m: 1, d: 1 });
  });

  it('сьогодні й поле datetime-local', () => {
    expect(todayIn(KYIV, new Date('2026-11-02T22:30:00Z'))).toEqual({ y: 2026, m: 11, d: 2 + 1 });
    expect(localInput({ y: 2026, m: 11, d: 2 }, 9, 30)).toBe('2026-11-02T09:30');
  });
});
