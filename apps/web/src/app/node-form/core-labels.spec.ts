import { NodeTypeInfo } from '../nodes/nodes-api.service';
import { WorkflowNode } from '../workflows/workflows-api.service';
import { LABELLED_TYPES, withCoreLabels } from './core-labels';
import { parameterFields } from './node-form';
import { JsonSchema } from './schema-fields';

// Спека читає справжні схеми бекенду; типів node у tsconfig.spec немає, тому мінімальні оголошення.
declare const process: { cwd(): string };
interface Fs {
  readdirSync(path: string): string[];
  readFileSync(path: string, encoding: string): string;
}
// @ts-expect-error — `node:fs` без типів node
const fs: Fs = await import('node:fs');
const { readFileSync, readdirSync } = fs;

/** Тести Angular запускаються з `apps/web`. */
const SCHEMAS = '../../src/OpenBP.Nodes.Core/Schemas';
/** Поля, які малюють власні віджети (умови, присвоєння, таблиця рішень, «Як поєднати») — підписи там свої. */
const OWN_WIDGET = [/^core\.(if|switch)$/, /^core\.set$/, /^core\.decisionTable$/];
/** Значення, що самі є назвою (HTTP-методи). */
const SELF_EXPLAINING = new Set(['core.webhook:method']);
/** Вкладені шляхи, які малює той самий віджет: `conditions.*`, `rules.*`, `fields.*`, `combine`. */
const WIDGET_PATH = /^(conditions|rules|fields|combine)(\.|$)/;

interface Entry {
  type: string;
  file: string;
  schema: JsonSchema;
}

function schemas(): Entry[] {
  const dir = `${process.cwd()}/${SCHEMAS}`;
  return readdirSync(dir)
    .filter((f: string) => f.endsWith('.schema.json'))
    .map((file: string) => {
      const schema = JSON.parse(readFileSync(`${dir}/${file}`, 'utf8')) as JsonSchema;
      return { type: String(schema['title']), file, schema };
    });
}

/** Усі шляхи властивостей схеми (з розкриттям `$ref`) з ознакою «є title» і значеннями enum. */
function walk(schema: JsonSchema): { path: string; titled: boolean; values: unknown[] | null }[] {
  const out: { path: string; titled: boolean; values: unknown[] | null }[] = [];
  const deref = (n: JsonSchema): JsonSchema => {
    const ref = n['$ref'];
    if (typeof ref !== 'string') return n;
    let t: unknown = schema;
    for (const p of ref.slice(2).split('/')) t = (t as Record<string, unknown>)[p];
    return { ...(t as JsonSchema), ...n, $ref: undefined };
  };
  const visit = (node: JsonSchema, prefix: string): void => {
    const n = deref(node);
    for (const [k, v] of Object.entries((n['properties'] ?? {}) as Record<string, JsonSchema>)) {
      const d = deref(v);
      out.push({ path: prefix + k, titled: typeof d['title'] === 'string', values: Array.isArray(d['enum']) ? d['enum'] : null });
      visit(d, `${prefix}${k}.`);
    }
    if (n['items']) visit(n['items'] as JsonSchema, prefix);
  };
  visit(schema, '');
  return out;
}

describe('core-labels (F3-87)', () => {
  it('кожне поле й кожне значення списку кожної схеми core.* має людський підпис', () => {
    const missing: string[] = [];
    for (const { type, file, schema } of schemas()) {
      const labelled = withCoreLabels(type, schema);
      const byPath = new Map(walk(labelled).map((e) => [e.path, e]));
      for (const e of walk(schema)) {
        if (OWN_WIDGET.some((r) => r.test(type)) && WIDGET_PATH.test(e.path)) continue;
        const after = byPath.get(e.path)!;
        if (!after.titled) missing.push(`${file}: ${e.path} без підпису`);
        const raw = readLabels(labelled, e.path);
        if (e.values && !SELF_EXPLAINING.has(`${type}:${e.path}`) && raw?.length !== e.values.length) missing.push(`${file}: ${e.path} без підписів значень`);
      }
    }
    expect(missing).toEqual([]);
  });

  it('словник є для вузлів із полями, які схема не підписує', () => {
    expect(LABELLED_TYPES).toEqual(expect.arrayContaining(['core.loop', 'core.code', 'core.wait', 'core.merge', 'core.approval', 'core.dataTable']));
  });

  it('форма core.loop і core.code показує людські підписи без технічних ключів', () => {
    const forms = (type: string): ReturnType<typeof parameterFields> => {
      const { schema } = schemas().find((s) => s.type === type)!;
      const descriptor: NodeTypeInfo = { type, version: 1, displayName: type, group: 'logic', inputs: ['main'], outputs: ['main'], parametersSchema: schema, isTrigger: false };
      return parameterFields(type, descriptor, null);
    };
    expect(forms('core.loop').map((f) => f.props?.label)).toEqual(['Список для перебору', 'Скільки елементів за раз']);
    const mode = forms('core.code').find((f) => f.key === 'mode')!;
    expect(mode.props?.label).toBe('Як виконувати');
    expect((mode.props?.['rules'] as { labels: string[] }).labels).toEqual(['Для кожного елемента окремо', 'Для всіх елементів разом']);
  });

  it('підпис із самої схеми (title) має пріоритет', () => {
    const labelled = withCoreLabels('core.loop', { type: 'object', properties: { list: { type: 'array', title: 'Мій список' } } });
    expect((labelled['properties'] as Record<string, JsonSchema>)['list']['title']).toBe('Мій список');
  });

  it('вузол без словника лишається як є', () => {
    const schema = { type: 'object', properties: { a: { type: 'string' } } };
    expect(withCoreLabels('mcp.tool', schema)).toBe(schema);
    void ({} as WorkflowNode);
  });
});

function readLabels(schema: JsonSchema, path: string): string[] | undefined {
  let node: JsonSchema | undefined = schema;
  const deref = (n: JsonSchema): JsonSchema => n;
  for (const part of path.split('.')) {
    node = deref(node as JsonSchema);
    while (node['items'] && !(node['properties'] as object | undefined)) node = node['items'] as JsonSchema;
    node = (node['properties'] as Record<string, JsonSchema> | undefined)?.[part];
    if (!node) return undefined;
  }
  return node['x-openbp-enum-labels'] as string[] | undefined;
}
