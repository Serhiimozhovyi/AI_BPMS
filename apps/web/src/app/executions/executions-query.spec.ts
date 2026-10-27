import { convertToParamMap } from '@angular/router';
import { describe, expect, it } from 'vitest';
import {
  executionsQueryParams,
  formatExecutionDuration,
  fromIsoDay,
  parseExecutionsQuery,
  sameExecutionsQuery,
  toIsoDay,
} from './executions-query';

describe('executions-query', () => {
  it('порожня адреса — фільтри за замовчуванням', () => {
    expect(parseExecutionsQuery(convertToParamMap({}))).toEqual({
      workflowId: null,
      status: null,
      from: '',
      to: '',
      correlationKey: '',
      hasOpenViolations: false,
      page: 1,
    });
  });

  it('розбирає всі фільтри й ігнорує зіпсовані значення', () => {
    const q = parseExecutionsQuery(
      convertToParamMap({ workflowId: 'w1', status: 'error', from: '2026-10-01', to: 'вчора', correlationKey: ' k1 ', hasOpenViolations: 'true', page: '3' }),
    );
    expect(q).toEqual({ workflowId: 'w1', status: 'error', from: '2026-10-01', to: '', correlationKey: 'k1', hasOpenViolations: true, page: 3 });
    expect(parseExecutionsQuery(convertToParamMap({ status: 'wat', page: '-2' })).status).toBeNull();
    expect(parseExecutionsQuery(convertToParamMap({ page: 'x' })).page).toBe(1);
  });

  it('значення за замовчуванням прибирають параметр з адреси', () => {
    const params = executionsQueryParams(parseExecutionsQuery(convertToParamMap({ status: 'error' })));
    expect(params).toEqual({ workflowId: null, status: 'error', from: null, to: null, correlationKey: null, hasOpenViolations: null, page: null });
  });

  it('sameExecutionsQuery порівнює за значенням', () => {
    const a = parseExecutionsQuery(convertToParamMap({ status: 'error', page: '1' }));
    const b = parseExecutionsQuery(convertToParamMap({ status: 'error' }));
    expect(sameExecutionsQuery(a, b)).toBe(true);
    expect(sameExecutionsQuery(a, { ...b, page: 2 })).toBe(false);
  });

  it('день: Date ↔ yyyy-MM-dd', () => {
    expect(toIsoDay(new Date(2026, 9, 7))).toBe('2026-10-07');
    expect(fromIsoDay('2026-10-07')?.getDate()).toBe(7);
    expect(fromIsoDay('2026-02-31')).toBeNull();
    expect(fromIsoDay('')).toBeNull();
  });

  it('тривалість: мс, секунди, хвилини, години, без завершення', () => {
    const t = (ms: number) => formatExecutionDuration('2026-10-27T10:00:00Z', new Date(Date.parse('2026-10-27T10:00:00Z') + ms).toISOString());
    expect(t(450)).toBe('450 мс');
    expect(t(2000)).toBe('2 с');
    expect(t(2500)).toBe('2,5 с');
    expect(t(125_000)).toBe('2 хв 5 с');
    expect(t(3_900_000)).toBe('1 год 5 хв');
    expect(formatExecutionDuration('2026-10-27T10:00:00Z', null)).toBe('—');
    expect(formatExecutionDuration(null, null)).toBe('—');
  });
});
