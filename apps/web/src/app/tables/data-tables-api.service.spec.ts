import { cellText, inputValue, keyColumn } from './data-tables-api.service';

describe('data-tables helpers', () => {
  it('inputValue: порожнє — null, число й булеве розпізнаються, нерозпізнане лишається рядком для Api', () => {
    expect(inputValue({ name: 'a', type: 'string' }, '  ')).toBeNull();
    expect(inputValue({ name: 'a', type: 'string' }, ' Київ ')).toBe(' Київ ');
    expect(inputValue({ name: 'a', type: 'number' }, '4.5')).toBe(4.5);
    expect(inputValue({ name: 'a', type: 'number' }, 'багато')).toBe('багато');
    expect(inputValue({ name: 'a', type: 'boolean' }, 'true')).toBe(true);
    expect(inputValue({ name: 'a', type: 'json' }, '["a",1]')).toEqual(['a', 1]);
  });

  it('inputValue: некоректний JSON — помилка з назвою колонки', () => {
    expect(() => inputValue({ name: 'tags', type: 'json' }, '[')).toThrow(/tags/);
  });

  it('cellText і keyColumn', () => {
    expect(cellText(null)).toBe('');
    expect(cellText({ a: 1 })).toBe('{"a":1}');
    expect(cellText(false)).toBe('false');
    expect(keyColumn({ columns: [{ name: 'a', type: 'string' }, { name: 'id', type: 'number', key: true }] })?.name).toBe('id');
  });
});
