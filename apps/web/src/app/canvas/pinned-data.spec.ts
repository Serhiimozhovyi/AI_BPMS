import { EMPTY_PINNED_ITEMS, formatPinnedItems, parsePinnedItems } from './pinned-data';

describe('pinned-data', () => {
  it('валідний масив items розбирається без втрат, зокрема з binary і невідомими полями', () => {
    const items = [{ json: { a: 1, nested: { b: [1, 2] } } }, { json: {}, binary: null, pairedItem: 0 }];
    expect(parsePinnedItems(formatPinnedItems(items))).toEqual({ items });
    expect(parsePinnedItems('[]')).toEqual({ items: [] });
    expect(parsePinnedItems(formatPinnedItems(EMPTY_PINNED_ITEMS))).toEqual({ items: [{ json: {} }] });
  });

  it('некоректний JSON, не масив, item без json-об’єкта — помилка', () => {
    expect(parsePinnedItems('[{ json: 1 }')).toHaveProperty('error');
    expect(parsePinnedItems('{"json": {}}')).toEqual({ error: expect.stringContaining('масив') });
    expect(parsePinnedItems('[{"json": {}}, {"a": 1}]')).toEqual({ error: expect.stringContaining('Item 1') });
    expect(parsePinnedItems('[{"json": [1]}]')).toEqual({ error: expect.stringContaining('Item 0') });
    expect(parsePinnedItems('[{"json": null}]')).toHaveProperty('error');
    expect(parsePinnedItems('[5]')).toHaveProperty('error');
  });

  it('формат — JSON з відступом 2', () => {
    expect(formatPinnedItems([{ json: { a: 1 } }])).toBe('[\n  {\n    "json": {\n      "a": 1\n    }\n  }\n]');
  });
});
