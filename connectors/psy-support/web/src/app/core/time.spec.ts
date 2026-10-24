import { formatDateTime, isoToZoned, zonedToIso } from './time';

describe('час у поясі Europe/Kyiv', () => {
    it('переводить місцевий час у UTC узимку й улітку', () => {
        expect(zonedToIso('2025-11-12T10:00', 'Europe/Kyiv')).toBe('2025-11-12T08:00:00.000Z');
        expect(zonedToIso('2026-07-01T10:00', 'Europe/Kyiv')).toBe('2026-07-01T07:00:00.000Z');
    });

    it('показує UTC у місцевому часі з днем тижня', () => {
        expect(formatDateTime('2025-11-12T08:00:00Z', 'Europe/Kyiv')).toBe('ср, 12.11, 10:00');
        expect(isoToZoned('2026-07-01T07:00:00Z', 'Europe/Kyiv')).toBe('2026-07-01T10:00');
        expect(formatDateTime(null, 'Europe/Kyiv')).toBe('—');
    });
});
