import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { ExpressionContext, ROOT_VARIABLES, SuggestResult, applySuggestion, expressionContext, suggestExpression } from './expression-suggest';

const CTX: ExpressionContext = {
  nodes: [
    { name: 'Нова заявка', sample: { body: { message: { chat: { id: 42 }, text: 'Привіт' } } } },
    { name: 'Розібрати', sample: { customer: 'Петренко', lines: [{ sku: 'A1', qty: 2 }], 'first name': 'Іван' } },
    { name: 'Без даних' },
  ],
  input: { customer: 'Петренко', total: 10, lines: [{ sku: 'A1', qty: 2 }], 'first name': 'Іван' },
  env: ['API_URL', 'REGION'],
};

/** Підказки для тексту з курсором у позиції `|`. */
function at(textWithCursor: string, ctx: ExpressionContext = CTX): SuggestResult | null {
  const cursor = textWithCursor.indexOf('|');
  return suggestExpression(textWithCursor.replace('|', ''), cursor, ctx);
}

function labels(textWithCursor: string, ctx: ExpressionContext = CTX): string[] {
  return at(textWithCursor, ctx)?.items.map((s) => s.label) ?? [];
}

/** Текст після вибору підказки з міткою `label`; `|` — курсор після вставки. */
function pick(textWithCursor: string, label: string): string {
  const text = textWithCursor.replace('|', '');
  const result = at(textWithCursor);
  const suggestion = result?.items.find((s) => s.label === label);
  if (!result || !suggestion) throw new Error(`немає підказки ${label}`);
  const applied = applySuggestion(text, result, suggestion);
  return applied.text.slice(0, applied.cursor) + '|' + applied.text.slice(applied.cursor);
}

describe('suggestExpression', () => {
  it('поза {{ }} підказок немає', () => {
    expect(at('Текст $js|')).toBeNull();
    expect(at('{{ $json.a }} $js|')).toBeNull();
  });

  it('кореневі змінні: усі після $ і за префіксом', () => {
    expect(labels('{{ $| }}')).toEqual(ROOT_VARIABLES.map((v) => v.name));
    expect(labels('{{ $e| }}')).toEqual(['$env', '$execution']);
    expect(labels('{{ $j|')).toEqual(['$json']);
    expect(labels('{{ $b| }}')).toEqual(['$binary']);
    expect(pick('{{ $j| }}', '$json')).toBe('{{ $json| }}');
  });

  it('$json: поля входу з останнього тестового output, вкладені й з типом', () => {
    expect(labels('{{ $json.| }}')).toEqual(['customer', 'total', 'lines', 'first name']);
    expect(at('{{ $json.| }}')?.items.find((s) => s.label === 'lines')?.detail).toBe('array');
    expect(labels('{{ $json.cu| }}')).toEqual(['customer']);
    expect(labels('{{ $json.lines[0].| }}')).toEqual(['sku', 'qty']);
    expect(pick('{{ $json.cu| }}', 'customer')).toBe('{{ $json.customer| }}');
  });

  it('ключ, що не ідентифікатор, вставляється як ["…"]', () => {
    expect(pick('{{ $json.fi| }}', 'first name')).toBe('{{ $json["first name"]| }}');
    expect(pick('{{ $json["fi| }}', 'first name')).toBe('{{ $json["first name"]| }}');
  });

  it('$node: назви вузлів сценарію, далі json і поля output вузла', () => {
    expect(labels('{{ $node[| }}')).toEqual(['Нова заявка', 'Розібрати', 'Без даних']);
    expect(labels('{{ $node["Ро| }}')).toEqual(['Розібрати']);
    expect(labels("{{ $node['Но| }}")).toEqual(['Нова заявка']);
    expect(pick('{{ $node["Ро| }}', 'Розібрати')).toBe('{{ $node["Розібрати"]| }}');
    expect(labels('{{ $node["Нова заявка"].| }}')).toEqual(['json', 'binary']);
    expect(labels('{{ $node["Нова заявка"].json.body.message.| }}')).toEqual(['chat', 'text']);
    expect(labels('{{ $node["Без даних"].json.| }}')).toEqual([]);
    expect(labels('{{ $node["Немає"].| }}')).toEqual([]);
  });

  it('назва вузла з лапками екранується', () => {
    const ctx: ExpressionContext = { nodes: [{ name: 'Крок "1"' }] };
    const result = suggestExpression('{{ $node[ }}', 9, ctx);
    expect(result?.items[0].insert).toBe('["Крок \\"1\\""]');
  });

  it('$input: item, all(), first(), last() і поля входу', () => {
    expect(labels('{{ $input.| }}')).toEqual(['item', 'all()', 'first()', 'last()']);
    expect(pick('{{ $input.f| }}', 'first()')).toBe('{{ $input.first()| }}');
    expect(labels('{{ $input.item.| }}')).toEqual(['json', 'binary']);
    expect(labels('{{ $input.first().json.t| }}')).toEqual(['total']);
    expect(labels('{{ $input.all()[0].json.l| }}')).toEqual(['lines']);
    expect(labels('{{ $input.all().| }}')).toEqual([]);
  });

  it('$now, $execution, $env', () => {
    expect(labels('{{ $now.toI| }}')).toEqual(['toISOString()']);
    expect(labels('{{ $execution.| }}')).toEqual(['id', 'resumeUrl', 'resumeUrls']);
    expect(labels('{{ $env.| }}')).toEqual(['API_URL', 'REGION']);
    expect(labels('{{ $env.| }}', { nodes: [] })).toEqual([]);
  });

  it('у середині виразу і шаблону рядка; завершене посилання — без підказок', () => {
    expect(labels('Замовлення {{ $json.total + $json.cu| }} шт.')).toEqual(['customer']);
    expect(labels('{{ $json.lines.map(l => l.qty) + $node[| }}')).toEqual(['Нова заявка', 'Розібрати', 'Без даних']);
    expect(at('{{ $json.lines[0]| }}')).toBeNull();
    expect(at('{{ $now.getTime()| }}')).toBeNull();
    expect(at('{{ $unknown.| }}')).toBeNull();
    expect(at('{{ "$json| }}')).not.toBeNull();
  });

  it('без зразка output підказуються лише змінні', () => {
    const ctx: ExpressionContext = { nodes: [{ name: 'A' }] };
    expect(labels('{{ $json.| }}', ctx)).toEqual([]);
    expect(labels('{{ $| }}', ctx)).toHaveLength(ROOT_VARIABLES.length);
  });
});

describe('expressionContext', () => {
  function definition(): WorkflowDefinition {
    const base = emptyWorkflowDefinition('Тест');
    const node = (id: string, name: string) => ({ id, name, type: 'core.set', typeVersion: 1, position: [0, 0] as [number, number], parameters: {} });
    return {
      ...base,
      nodes: [node('a', 'Тригер'), node('b', 'Збагатити'), node('c', 'Відповідь')],
      connections: [
        { from: { node: 'a', output: 0 }, to: { node: 'c', input: 0 } },
        { from: { node: 'b', output: 0 }, to: { node: 'c', input: 1 } },
      ],
    };
  }

  it('інші вузли з першим item output, вхід — з першого вхідного ребра з даними', () => {
    const ctx = expressionContext(definition(), 'c', { b: [{ json: { x: 1 } }, { json: { x: 2 } }] }, ['ENV']);
    expect(ctx.nodes).toEqual([
      { name: 'Тригер', sample: undefined },
      { name: 'Збагатити', sample: { x: 1 } },
    ]);
    expect(ctx.input).toEqual({ x: 1 });
    expect(ctx.env).toEqual(['ENV']);

    expect(expressionContext(definition(), 'c', { a: [{ json: { y: 1 } }], b: [{ json: { x: 1 } }] }).input).toEqual({ y: 1 });
    expect(expressionContext(definition(), 'a', {}).input).toBeUndefined();
  });
});
