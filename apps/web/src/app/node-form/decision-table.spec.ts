import { ANY_CELL, columnNames, emptyRule, isDecisionRulesSchema, moveRule, normalizeRules, serializeRules, withOperator, withRight } from './decision-table';
import { DECISION_TABLE_SCHEMA } from './test-schemas';
import { DECISION_RULES_FIELD, schemaFields } from './schema-fields';

describe('таблиця рішень (F6-19)', () => {
  it('впізнає масив правил за when/then і дає йому табличне поле', () => {
    const rule = (DECISION_TABLE_SCHEMA['$defs'] as Record<string, Record<string, unknown>>)['rule'];
    expect(isDecisionRulesSchema(rule)).toBe(true);
    expect(isDecisionRulesSchema({ properties: { name: {}, value: {} } })).toBe(false);

    const fields = schemaFields(DECISION_TABLE_SCHEMA);
    const rules = fields.find((f) => f.key === 'rules')!;
    expect(rules.type).toBe(DECISION_RULES_FIELD);
    expect(rules.props?.label).toBe('Правила');
    expect(fields.map((f) => f.key)).toEqual(['inputs', 'outputs', 'rules', 'hitPolicy']);
  });

  it('назви колонок; безіменна — #номер', () => {
    expect(columnNames([{ name: 'сегмент' }, { name: ' ' }, {}])).toEqual(['сегмент', '#2', '#3']);
    expect(columnNames(undefined)).toEqual([]);
  });

  it('normalizeRules доповнює комірки «-» і порожніми виходами, невідоме в комірці — «-»', () => {
    expect(normalizeRules([{ when: [{ operator: 'equals', right: 'vip' }, 5], then: [1] }, null], 3, 2)).toEqual([
      { when: [{ operator: 'equals', right: 'vip' }, ANY_CELL, ANY_CELL], then: [1, undefined] },
      { when: [ANY_CELL, ANY_CELL, ANY_CELL], then: [undefined, undefined] },
    ]);
    expect(normalizeRules('x', 1, 1)).toEqual([]);
  });

  it('оператор і праве значення комірки; «-» скидає умову', () => {
    expect(withOperator(ANY_CELL, 'gt')).toEqual({ operator: 'gt' });
    expect(withOperator({ operator: 'gt', right: 5 }, 'lte')).toEqual({ operator: 'lte', right: 5 });
    expect(withOperator({ operator: 'gt', right: 5 }, ANY_CELL)).toBe(ANY_CELL);
    expect(withRight(ANY_CELL, 'vip')).toEqual({ operator: 'equals', right: 'vip' });
    expect(withRight({ operator: 'gt', right: 5 }, undefined)).toEqual({ operator: 'gt' });
  });

  it('нове правило, переміщення, серіалізація без undefined', () => {
    const rules = [emptyRule(1, 2), { when: [{ operator: 'isTrue' }], then: ['a', undefined] }];
    expect(rules[0]).toEqual({ when: [ANY_CELL], then: [undefined, undefined] });
    expect(moveRule(rules, 1, -1)[0]).toBe(rules[1]);
    expect(moveRule(rules, 0, -1)).toEqual(rules);
    expect(serializeRules(rules)).toEqual([
      { when: ['-'], then: [null, null] },
      { when: [{ operator: 'isTrue' }], then: ['a', null] },
    ]);
  });
});
