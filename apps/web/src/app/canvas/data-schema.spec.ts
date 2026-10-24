import { accessorPath, buildSchema, expressionFor, insertAt, itemsTable, sampleText } from './data-schema';

describe('data-schema', () => {
  describe('buildSchema', () => {
    it('вкладені об’єкти й масиви: дерево полів зі шляхами, типами і прикладами', () => {
      const schema = buildSchema([{ json: { customer: 'Петренко', order: { number: 7, lines: [{ sku: 'A1', qty: 2 }] } } }]);

      expect(schema.map((f) => f.key)).toEqual(['customer', 'order']);
      expect(schema[0]).toMatchObject({ path: ['customer'], types: ['string'], sample: 'Петренко' });
      const order = schema[1];
      expect(order.types).toEqual(['object']);
      expect(order.sample).toBeUndefined();
      expect(order.children.map((f) => f.key)).toEqual(['number', 'lines']);
      const lines = order.children[1];
      expect(lines.types).toEqual(['array']);
      expect(lines.children.map((f) => f.key)).toEqual(['[0]']);
      const element = lines.children[0];
      expect(element.path).toEqual(['order', 'lines', 0]);
      expect(element.children.map((f) => [f.path, f.types, f.sample])).toEqual([
        [['order', 'lines', 0, 'sku'], ['string'], 'A1'],
        [['order', 'lines', 0, 'qty'], ['number'], 2],
      ]);
    });

    it('різні items: поля зливаються, різні типи одного поля — усі, приклад — перший непорожній', () => {
      const schema = buildSchema([
        { json: { id: null, a: 1 } },
        { json: { id: 'x-2', b: true } },
        { json: { id: 3, list: [1, 'два'] } },
      ]);

      expect(schema.map((f) => f.key)).toEqual(['id', 'a', 'b', 'list']);
      expect(schema[0]).toMatchObject({ types: ['null', 'string', 'number'], sample: 'x-2' });
      expect(schema[2]).toMatchObject({ types: ['boolean'], sample: true });
      expect(schema[3].children[0]).toMatchObject({ path: ['list', 0], types: ['number', 'string'], sample: 1 });
    });

    it('порожні items, порожній масив, item без json — без полів і без помилок', () => {
      expect(buildSchema([])).toEqual([]);
      expect(buildSchema([{ json: { tags: [] } }])[0]).toMatchObject({ types: ['array'], children: [] });
      expect(buildSchema([{} as never])).toEqual([]);
    });
  });

  describe('expressionFor', () => {
    it('латинські ключі — через крапку, індекси — [n]', () => {
      expect(expressionFor(['order', 'lines', 0, 'sku'])).toBe('{{ $json.order.lines[0].sku }}');
      expect(expressionFor(['$id', '_x1'])).toBe('{{ $json.$id._x1 }}');
    });

    it('нелатинські, з пробілами, крапками і лапками ключі — у ["…"] з екрануванням', () => {
      expect(expressionFor(['Клієнт', 'Назва'])).toBe('{{ $json["Клієнт"]["Назва"] }}');
      expect(expressionFor(['a.b', 'first name', '1st'])).toBe('{{ $json["a.b"]["first name"]["1st"] }}');
      expect(expressionFor(['say "hi"'])).toBe('{{ $json["say \\"hi\\""] }}');
    });

    it('поле іншого вузла — $node["Назва"].json…', () => {
      expect(expressionFor(['body', 'message', 'chat', 'id'], 'Нова заявка')).toBe('{{ $node["Нова заявка"].json.body.message.chat.id }}');
      expect(expressionFor(['x'], 'Вузол "1"')).toBe('{{ $node["Вузол \\"1\\""].json.x }}');
    });

    it('accessorPath порожнього шляху — порожній', () => {
      expect(accessorPath([])).toBe('');
    });
  });

  describe('insertAt', () => {
    it('вставка в позицію курсора і заміна виділення; курсор — за вставкою', () => {
      expect(insertAt('Замовлення №', 12, 12, '{{ $json.number }}')).toEqual({ text: 'Замовлення №{{ $json.number }}', cursor: 30 });
      expect(insertAt('ab', 1, 1, 'X')).toEqual({ text: 'aXb', cursor: 2 });
      expect(insertAt('a old b', 2, 5, 'new')).toEqual({ text: 'a new b', cursor: 5 });
      // Виділення справа наліво.
      expect(insertAt('a old b', 5, 2, 'new').text).toBe('a new b');
    });

    it('позиції поза текстом обмежуються його межами', () => {
      expect(insertAt('ab', 10, 10, 'X')).toEqual({ text: 'abX', cursor: 3 });
      expect(insertAt('ab', -3, 0, 'X')).toEqual({ text: 'Xab', cursor: 1 });
      expect(insertAt('ab', Number.NaN, Number.NaN, 'X').text).toBe('abX');
    });
  });

  it('itemsTable: колонки — ключі всіх items, вкладене — згорнуте з JSON у підказці', () => {
    const table = itemsTable([{ json: { a: 'x', nested: { b: 1, c: 2 } } }, { json: { list: [1, 2, 3], a: 5 } }]);

    expect(table.columns).toEqual(['a', 'nested', 'list']);
    expect(table.rows[0].cells.map((c) => c.text)).toEqual(['x', '{2}', '']);
    expect(table.rows[0].cells[1]).toMatchObject({ nested: true, title: '{\n  "b": 1,\n  "c": 2\n}' });
    expect(table.rows[1].cells.map((c) => [c.text, c.nested])).toEqual([
      ['5', false],
      ['', false],
      ['[3]', true],
    ]);
  });

  it('sampleText: рядки в лапках, довгі обрізаються', () => {
    expect(sampleText('Петренко')).toBe('"Петренко"');
    expect(sampleText(42)).toBe('42');
    expect(sampleText('x'.repeat(100), 10)).toBe('"xxxxxxxx…');
    expect(sampleText(undefined)).toBe('');
  });
});
