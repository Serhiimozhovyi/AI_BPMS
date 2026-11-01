import type { VisibleIf } from './form-layout';
import { hiddenFields, hiddenState, isEmptyValue, isVisible } from './visible-if';

// Спільна таблиця кейсів з xUnit (tests/form-cases); типів node у tsconfig.spec немає, тому мінімальні оголошення.
declare const process: { cwd(): string };
// @ts-expect-error — `node:fs` без типів node
const fs: { readFileSync(path: string, encoding: string): string } = await import('node:fs');

interface Case {
  name: string;
  rule: VisibleIf;
  values: Record<string, unknown>;
  expected: boolean;
}

/** Тести Angular запускаються з `apps/web`. */
const cases = (JSON.parse(fs.readFileSync(`${process.cwd()}/../../tests/form-cases/visible-if.cases.json`, 'utf8')) as { cases: Case[] }).cases;

describe('isVisible (F11-06) — спільна таблиця з сервером', () => {
  it('таблиця покриває всі оператори', () => {
    for (const op of ['eq', 'ne', 'contains', 'isEmpty', 'isNotEmpty', 'gt', 'lt']) {
      expect(cases.some((c) => c.rule.operator === op)).toBe(true);
    }
  });

  it.each(cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    expect(isVisible(c.rule, c.values)).toBe(c.expected);
  });

  it('порожнє: немає, null, порожній рядок і масив; 0 і false — ні', () => {
    expect([undefined, null, '', []].map(isEmptyValue)).toEqual([true, true, true, true]);
    expect([0, false, ' '].map(isEmptyValue)).toEqual([false, false, false]);
  });
});

describe('hiddenState (F11-06)', () => {
  const schema = {
    type: 'object',
    properties: { kind: { type: 'string' }, reason: { type: 'string' }, doctor: { type: 'string' }, note: { type: 'string' } },
    'x-openbp-layout': {
      sections: [
        { rows: [[{ field: 'kind' }], [{ field: 'reason', visibleIf: { field: 'kind', operator: 'eq', value: 'other' } }]] },
        { title: 'Лікарняний', visibleIf: { field: 'kind', operator: 'eq', value: 'sick' }, rows: [[{ field: 'doctor' }]] },
        { rows: [[{ field: 'note', visibleIf: { field: 'reason', operator: 'isNotEmpty' } }]] },
      ],
    },
  };

  it('ховає за умовою поля, секції й ланцюжком — як сервер', () => {
    const state = hiddenState(schema, { kind: 'annual', reason: 'щось', doctor: 'Іваненко', note: 'x' });
    expect([...state.sections]).toEqual([1]);
    expect([...state.fields].sort()).toEqual(['doctor', 'note', 'reason']);
  });

  it('поле з виконаною умовою видно', () => {
    expect([...hiddenFields(schema, { kind: 'other', reason: 'щось' })]).toEqual(['doctor']);
    expect([...hiddenFields(schema, { kind: 'sick' })].sort()).toEqual(['note', 'reason']);
  });

  it('схема без розмітки нічого не ховає', () => {
    expect(hiddenFields({ type: 'object', properties: { a: {} } }, {}).size).toBe(0);
  });
});
