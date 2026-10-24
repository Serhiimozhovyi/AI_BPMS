import { isExpression, isPlainObject } from './node-value';

/**
 * Динамічні списки з конекторів (F3-54): анотація `x-openbp-options` властивості `inputSchema` tool'а
 * (docs/connectors.md §2) — інший tool того самого конектора повертає варіанти значення. Без Angular — чисті функції:
 * розбір анотації, підстановка `{{ $params.<param> }}`, варіанти з `structuredContent` і режим поля за значенням.
 */

/** Назва анотації в JSON Schema властивості. */
export const OPTIONS_ANNOTATION = 'x-openbp-options';

/** Більше варіантів не показується: далі — пошук на сервері (якщо tool приймає `query`) або уточнення. */
export const MAX_OPTIONS = 500;

/** Аргумент tool'а списку для пошуку на сервері. */
export const SEARCH_ARGUMENT = 'query';

/** Розібрана анотація `x-openbp-options`. */
export interface OptionsSource {
  tool: string;
  arguments: Record<string, unknown>;
  itemsPath?: string;
  valuePath: string;
  /** Шляхи до підпису: береться перший непорожній; порожньо — підпис = значення. */
  labelPaths: string[];
  dependsOn: string[];
}

/** Варіант списку: значення, що записується в параметр, і підпис. */
export interface OptionItem {
  value: unknown;
  label: string;
}

/** Анотація зі схеми або `null`, якщо її немає чи вона не за схемою (`tools/connector-template/schemas`). */
export function parseOptionsSource(annotation: unknown): OptionsSource | null {
  if (!isPlainObject(annotation)) return null;
  const { tool, valuePath, itemsPath, labelPath, dependsOn } = annotation;
  const args = annotation['arguments'];
  if (typeof tool !== 'string' || !tool || typeof valuePath !== 'string' || !valuePath) return null;
  if (args !== undefined && !isPlainObject(args)) return null;
  if (itemsPath !== undefined && typeof itemsPath !== 'string') return null;
  const labels = typeof labelPath === 'string' ? [labelPath] : Array.isArray(labelPath) ? labelPath : labelPath === undefined ? [] : null;
  if (!labels || !labels.every((l): l is string => typeof l === 'string' && l !== '')) return null;
  const depends = Array.isArray(dependsOn) ? dependsOn : dependsOn === undefined ? [] : null;
  if (!depends || !depends.every((d): d is string => typeof d === 'string' && d !== '')) return null;
  return { tool, arguments: args ?? {}, itemsPath: itemsPath || undefined, valuePath, labelPaths: labels, dependsOn: depends };
}

const PARAM = /\{\{\s*\$params\.([A-Za-z_$][\w$]*)\s*\}\}/g;
const WHOLE_PARAM = /^\{\{\s*\$params\.([A-Za-z_$][\w$]*)\s*\}\}$/;

/** Аргументи виклику з підставленими параметрами або перелік параметрів, яких ще немає. */
export type ResolvedArguments = { ok: true; arguments: Record<string, unknown> } | { ok: false; missing: string[] };

/**
 * Підставляє `{{ $params.<param> }}` у рядки `arguments` (вкладені об'єкти й масиви — теж): рядок цілком з одного
 * посилання стає значенням параметра як є, частина рядка — текстом. Параметр не заданий або сам є виразом —
 * список завантажити не можна (`missing`): значення стане відомим лише під час виконання.
 */
export function resolveArguments(args: Record<string, unknown>, params: Record<string, unknown>): ResolvedArguments {
  const missing = new Set<string>();
  const valueOf = (name: string): unknown => {
    const value = params[name];
    if (value === undefined || value === null || value === '' || isExpression(value)) missing.add(name);
    return value;
  };
  const visit = (node: unknown): unknown => {
    if (typeof node === 'string') {
      const whole = WHOLE_PARAM.exec(node);
      if (whole) return valueOf(whole[1]);
      return node.replace(PARAM, (_, name: string) => {
        const value = valueOf(name);
        return typeof value === 'string' ? value : JSON.stringify(value ?? '');
      });
    }
    if (Array.isArray(node)) return node.map(visit);
    if (isPlainObject(node)) return Object.fromEntries(Object.entries(node).map(([k, v]) => [k, visit(v)]));
    return node;
  };
  const resolved = visit(args) as Record<string, unknown>;
  return missing.size > 0 ? { ok: false, missing: [...missing] } : { ok: true, arguments: resolved };
}

/** Параметри, від яких залежить список: `dependsOn` і всі `{{ $params.x }}` в `arguments`. */
export function dependencies(source: OptionsSource): string[] {
  const names = new Set(source.dependsOn);
  for (const match of JSON.stringify(source.arguments).matchAll(PARAM)) names.add(match[1]);
  return [...names];
}

/** Значення за шляхом через крапки (числові сегменти — індекси масивів); порожній шлях — сам об'єкт. */
export function valueAt(value: unknown, path: string | undefined): unknown {
  if (!path) return value;
  let current = value;
  for (const segment of path.split('.')) {
    if (Array.isArray(current) && /^\d+$/.test(segment)) current = current[Number(segment)];
    else if (isPlainObject(current)) current = current[segment];
    else return undefined;
  }
  return current;
}

/** Варіанти з `structuredContent` і чи їх більше за {@link MAX_OPTIONS}; однакові значення — один раз. */
export function extractOptions(content: unknown, source: OptionsSource): { items: OptionItem[]; truncated: boolean } | null {
  const list = source.itemsPath ? valueAt(content, source.itemsPath) : Array.isArray(content) ? content : valueAt(content, 'result');
  if (!Array.isArray(list)) return null;
  const seen = new Set<string>();
  const items: OptionItem[] = [];
  let truncated = false;
  for (const element of list) {
    const value = valueAt(element, source.valuePath);
    if (value === undefined || value === null || typeof value === 'object') continue;
    const key = JSON.stringify(value);
    if (seen.has(key)) continue;
    if (items.length === MAX_OPTIONS) {
      truncated = true;
      break;
    }
    seen.add(key);
    items.push({ value, label: labelOf(element, source.labelPaths) ?? String(value) });
  }
  return { items, truncated };
}

function labelOf(element: unknown, paths: readonly string[]): string | null {
  for (const path of paths) {
    const label = valueAt(element, path);
    if (label !== undefined && label !== null && label !== '' && typeof label !== 'object') return String(label);
  }
  return null;
}

/** Режим поля зі списком: значення зі списку, інше введене значення або вираз. */
export type OptionsMode = 'list' | 'text' | 'expression';

/**
 * Режим за значенням: `{{` — вираз; порожнє або є серед варіантів — список; інше — введене значення.
 * Поки варіантів немає (`null`: ще вантажаться), непорожнє значення лишається у списку.
 */
export function modeFor(value: unknown, items: readonly OptionItem[] | null): OptionsMode {
  if (isExpression(value)) return 'expression';
  if (value === undefined || value === null || value === '' || items === null) return 'list';
  return findOption(value, items) ? 'list' : 'text';
}

/** Варіант зі значенням `value` (порівняння як JSON: `42` і `"42"` — різні). */
export function findOption(value: unknown, items: readonly OptionItem[]): OptionItem | undefined {
  const key = JSON.stringify(value);
  return items.find((o) => JSON.stringify(o.value) === key);
}

/** Пошук у завантаженому списку: підпис або значення містить текст (без регістру). */
export function filterOptions(items: readonly OptionItem[], text: string): OptionItem[] {
  const query = text.trim().toLowerCase();
  if (!query) return [...items];
  return items.filter((o) => o.label.toLowerCase().includes(query) || String(o.value).toLowerCase().includes(query));
}

/** Tool приймає аргумент пошуку `query` (його `inputSchema.properties.query`). */
export function acceptsSearch(inputSchema: unknown): boolean {
  return isPlainObject(inputSchema) && isPlainObject(inputSchema['properties']) && SEARCH_ARGUMENT in inputSchema['properties'];
}
