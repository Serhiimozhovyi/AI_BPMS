import { MAX_OPTIONS, OptionsSource, acceptsSearch, dependencies, extractOptions, filterOptions, modeFor, parseOptionsSource, resolveArguments, valueAt } from './dynamic-options';

describe('dynamic-options', () => {
  const source = (overrides: Partial<OptionsSource> = {}): OptionsSource => ({
    tool: 'list',
    arguments: {},
    valuePath: 'id',
    labelPaths: ['name'],
    dependsOn: [],
    ...overrides,
  });

  describe('parseOptionsSource', () => {
    it('повна анотація; labelPath рядком або масивом', () => {
      expect(
        parseOptionsSource({ tool: 'list_catalog_kontragenty', arguments: { select: ['Ref_Key', 'Description'] }, itemsPath: 'result', valuePath: 'Ref_Key', labelPath: 'Description' }),
      ).toEqual({ tool: 'list_catalog_kontragenty', arguments: { select: ['Ref_Key', 'Description'] }, itemsPath: 'result', valuePath: 'Ref_Key', labelPaths: ['Description'], dependsOn: [] });
      expect(parseOptionsSource({ tool: 'get_updates', valuePath: 'message.chat.id', labelPath: ['message.chat.title', 'message.chat.first_name'], dependsOn: ['x'] })).toMatchObject({
        labelPaths: ['message.chat.title', 'message.chat.first_name'],
        dependsOn: ['x'],
        arguments: {},
      });
    });

    it('не за схемою — null (поле лишається звичайним)', () => {
      expect(parseOptionsSource(undefined)).toBeNull();
      expect(parseOptionsSource({ valuePath: 'id' })).toBeNull();
      expect(parseOptionsSource({ tool: 'list' })).toBeNull();
      expect(parseOptionsSource({ tool: 'list', valuePath: 'id', arguments: [] })).toBeNull();
      expect(parseOptionsSource({ tool: 'list', valuePath: 'id', labelPath: [1] })).toBeNull();
      expect(parseOptionsSource({ tool: 'list', valuePath: 'id', dependsOn: 'x' })).toBeNull();
    });
  });

  describe('resolveArguments', () => {
    it('{{ $params.x }} цілим рядком — значення як є, у шаблоні — текстом; вкладені об’єкти теж', () => {
      expect(resolveArguments({ customer: '{{ $params.customer_id }}', filter: "Owner_Key eq guid'{{ $params.customer_id }}'", nested: [{ n: '{{$params.n}}' }] }, { customer_id: 'c-1', n: 7 })).toEqual({
        ok: true,
        arguments: { customer: 'c-1', filter: "Owner_Key eq guid'c-1'", nested: [{ n: 7 }] },
      });
    });

    it('параметр не задано або він — вираз: список не завантажити', () => {
      expect(resolveArguments({ a: '{{ $params.x }}', b: 'id {{ $params.y }}' }, { y: '{{ $json.id }}' })).toEqual({ ok: false, missing: ['x', 'y'] });
      expect(resolveArguments({ top: 10 }, {})).toEqual({ ok: true, arguments: { top: 10 } });
    });

    it('dependencies — dependsOn і посилання в arguments без повторів', () => {
      expect(dependencies(source({ dependsOn: ['a'], arguments: { x: '{{ $params.b }}', y: '{{ $params.a }}' } }))).toEqual(['a', 'b']);
    });
  });

  describe('extractOptions', () => {
    it('itemsPath і шляхи через крапки; підпис — перший непорожній, інакше значення; повтори — один раз', () => {
      const content = {
        result: [
          { message: { chat: { id: 1001, first_name: 'Олена' } } },
          { message: { chat: { id: 2002, title: 'Група', first_name: 'x' } } },
          { message: { chat: { id: 1001, first_name: 'Олена' } } },
          { callback_query: {} },
          { message: { chat: { id: 3003 } } },
        ],
      };
      const result = extractOptions(content, source({ itemsPath: 'result', valuePath: 'message.chat.id', labelPaths: ['message.chat.title', 'message.chat.first_name'] }));
      expect(result).toEqual({
        items: [
          { value: 1001, label: 'Олена' },
          { value: 2002, label: 'Група' },
          { value: 3003, label: '3003' },
        ],
        truncated: false,
      });
    });

    it('без itemsPath: масив або result; не масив — null', () => {
      expect(extractOptions([{ id: 'a', name: 'A' }], source())?.items).toEqual([{ value: 'a', label: 'A' }]);
      expect(extractOptions({ result: [{ id: 'b' }] }, source())?.items).toEqual([{ value: 'b', label: 'b' }]);
      expect(extractOptions({ value: 1 }, source())).toBeNull();
      expect(extractOptions(null, source())).toBeNull();
    });

    it(`більше ${MAX_OPTIONS} — обрізано`, () => {
      const many = Array.from({ length: MAX_OPTIONS + 3 }, (_, i) => ({ id: i }));
      const result = extractOptions(many, source());
      expect(result?.items).toHaveLength(MAX_OPTIONS);
      expect(result?.truncated).toBe(true);
    });

    it('valueAt: індекси масивів, відсутній шлях — undefined', () => {
      expect(valueAt({ a: [{ b: 2 }] }, 'a.0.b')).toBe(2);
      expect(valueAt({ a: 1 }, 'a.b')).toBeUndefined();
      expect(valueAt(5, undefined)).toBe(5);
    });
  });

  it('modeFor: вираз / зі списку / введене; поки список не завантажено — список', () => {
    const items = [{ value: 1001, label: 'Олена' }];
    expect(modeFor('{{ $json.chat.id }}', items)).toBe('expression');
    expect(modeFor('Привіт {{ $json.name }}', null)).toBe('expression');
    expect(modeFor(1001, items)).toBe('list');
    expect(modeFor(undefined, items)).toBe('list');
    expect(modeFor(42, items)).toBe('text');
    expect(modeFor('1001', items)).toBe('text');
    expect(modeFor(42, null)).toBe('list');
  });

  it('filterOptions за підписом і значенням без регістру; acceptsSearch — за query у схемі', () => {
    const items = [
      { value: 'c-1', label: 'ТОВ Агро' },
      { value: 'c-2', label: 'ФОП Петренко' },
    ];
    expect(filterOptions(items, 'агро').map((o) => o.value)).toEqual(['c-1']);
    expect(filterOptions(items, 'C-2').map((o) => o.value)).toEqual(['c-2']);
    expect(filterOptions(items, ' ')).toHaveLength(2);
    expect(acceptsSearch({ type: 'object', properties: { query: { type: 'string' } } })).toBe(true);
    expect(acceptsSearch({ type: 'object', properties: {} })).toBe(false);
    expect(acceptsSearch(undefined)).toBe(false);
  });
});
