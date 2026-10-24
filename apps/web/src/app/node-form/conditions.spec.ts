import { assignmentsFromInput, inputFieldNames, isUnary, operatorLabel } from './conditions';

describe('conditions', () => {
  it('оператори без правого значення', () => {
    expect(['isEmpty', 'isNotEmpty', 'isTrue', 'isFalse'].every(isUnary)).toBe(true);
    expect(isUnary('equals')).toBe(false);
    expect(isUnary(undefined)).toBe(false);
  });

  it('підписи операторів українською; невідомий — як є', () => {
    expect(operatorLabel('notEquals')).toBe('не дорівнює');
    expect(operatorLabel('contains')).toBe('містить');
    expect(operatorLabel('regex')).toBe('regex');
  });

  it('assignmentsFromInput: поля верхнього рівня без наявних, вирази з ["…"] для нелатинських', () => {
    expect(assignmentsFromInput({ a: 1, 'Ціна': 2, b: { c: 3 } }, [{ name: 'a', value: 5 }, null, 'x'])).toEqual([
      { name: 'Ціна', value: '{{ $json["Ціна"] }}' },
      { name: 'b', value: '{{ $json.b }}' },
    ]);
    expect(assignmentsFromInput(undefined, [])).toEqual([]);
    expect(assignmentsFromInput([1, 2], [])).toEqual([]);
  });

  it('inputFieldNames: за початком без регістру, без точного збігу', () => {
    const input = { total: 1, Title: 2, sum: 3 };
    expect(inputFieldNames(input, 't')).toEqual(['total', 'Title']);
    expect(inputFieldNames(input, 'total')).toEqual([]);
    expect(inputFieldNames(input, '')).toEqual(['total', 'Title', 'sum']);
    expect(inputFieldNames(null, 't')).toEqual([]);
  });
});
