import type { JsonSchema } from '../form-fields';
import { FormCell, FormLayout, FormSection, UI_KEY, readLayout, writeLayout } from '../form-layout';

/**
 * Модель конструктора форми (F11-02, docs/forms-designer.md §3.2): JSON Schema форми → «секції → рядки → поля» +
 * властивості полів і назад. Усі операції — чисті функції, що повертають новий стан; історія undo/redo — `DesignerHistory`.
 * Невідомі ключі схеми й властивостей (зокрема `visibleIf`, `x-openbp-*`) переносяться без втрат.
 */

/** Тип поля в палітрі конструктора. */
export type FieldKind =
  | 'text'
  | 'textarea'
  | 'number'
  | 'email'
  | 'phone'
  | 'address'
  | 'date'
  | 'datetime'
  | 'boolean'
  | 'select'
  | 'multiselect'
  | 'file';

export interface FieldKindInfo {
  kind: FieldKind;
  label: string;
  icon: string;
}

/** Палітра типів полів — у порядку показу. */
export const FIELD_KINDS: readonly FieldKindInfo[] = [
  { kind: 'text', label: $localize`:@@fd.kind.text:Текст`, icon: 'short_text' },
  { kind: 'textarea', label: $localize`:@@fd.kind.textarea:Довгий текст`, icon: 'notes' },
  { kind: 'number', label: $localize`:@@fd.kind.number:Число`, icon: 'pin' },
  { kind: 'email', label: $localize`:@@fd.kind.email:Пошта`, icon: 'alternate_email' },
  { kind: 'phone', label: $localize`:@@fd.kind.phone:Телефон`, icon: 'call' },
  { kind: 'address', label: $localize`:@@fd.kind.address:Адреса`, icon: 'home_pin' },
  { kind: 'date', label: $localize`:@@fd.kind.date:Дата`, icon: 'calendar_today' },
  { kind: 'datetime', label: $localize`:@@fd.kind.datetime:Дата й час`, icon: 'schedule' },
  { kind: 'boolean', label: $localize`:@@fd.kind.boolean:Так / Ні`, icon: 'check_box' },
  { kind: 'select', label: $localize`:@@fd.kind.select:Вибір`, icon: 'arrow_drop_down_circle' },
  { kind: 'multiselect', label: $localize`:@@fd.kind.multiselect:Кілька варіантів`, icon: 'checklist' },
  { kind: 'file', label: $localize`:@@fd.kind.file:Файл`, icon: 'attach_file' },
];

/** Шаблон телефону: цифри, пробіли, дужки, дефіси, необов'язковий «+» (F11-07 замінить маскою). */
export const PHONE_PATTERN = '^\\+?[0-9 ()-]{7,20}$';

export interface DesignerState {
  /** Ключі кореня схеми, крім `properties`, `required` і розмітки (`title`, `$defs`…). */
  rest: JsonSchema;
  properties: Record<string, JsonSchema>;
  required: string[];
  layout: FormLayout;
}

/** Де стоїть поле: секція, рядок, клітинка. */
export interface FieldPlace {
  section: number;
  row: number;
  cell: number;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function clone<T>(value: T): T {
  return structuredClone(value);
}

/** Стан конструктора зі схеми; не об'єкт — порожня форма. */
export function fromSchema(schema: JsonSchema | null | undefined): DesignerState {
  const source = isObject(schema) ? schema : {};
  const properties: Record<string, JsonSchema> = {};
  if (isObject(source['properties'])) {
    for (const [key, value] of Object.entries(source['properties'])) properties[key] = isObject(value) ? clone(value) : {};
  }
  const required = Array.isArray(source['required']) ? source['required'].filter((r): r is string => typeof r === 'string' && r in properties) : [];
  const rest: JsonSchema = {};
  for (const [key, value] of Object.entries(source)) {
    if (key !== 'properties' && key !== 'required' && key !== 'x-openbp-layout') rest[key] = clone(value);
  }
  return { rest, properties, required, layout: readLayout({ properties, ...('x-openbp-layout' in source ? { 'x-openbp-layout': source['x-openbp-layout'] } : {}) }) };
}

/** Схема зі стану: `type: object`, властивості в порядку розмітки, `required` лише за наявними полями. */
export function toSchema(state: DesignerState): JsonSchema {
  const base: JsonSchema = { type: 'object', ...clone(state.rest), properties: clone(state.properties) };
  const required = state.required.filter((r) => r in state.properties);
  if (required.length) base['required'] = required;
  else delete base['required'];
  return writeLayout(base, state.layout);
}

/** Тип поля конструктора за властивістю схеми. */
export function kindOf(property: JsonSchema): FieldKind {
  const type = Array.isArray(property['type']) ? property['type'].find((t) => t !== 'null') : property['type'];
  const ui = isObject(property[UI_KEY]) ? property[UI_KEY] : {};
  if (property['format'] === 'binary') return 'file';
  if (type === 'array') return 'multiselect';
  if (Array.isArray(property['enum']) || Array.isArray(property['oneOf'])) return 'select';
  if (type === 'boolean') return 'boolean';
  if (type === 'number' || type === 'integer') return 'number';
  if (property['format'] === 'email') return 'email';
  if (property['format'] === 'date') return 'date';
  if (property['format'] === 'date-time') return 'datetime';
  if (property['format'] === 'phone' || property['pattern'] === PHONE_PATTERN) return 'phone';
  // «Адреса» — двохрядкове багаторядкове поле.
  if (property['format'] === 'textarea') return ui['rows'] === 2 ? 'address' : 'textarea';
  return 'text';
}

/** Нова властивість схеми для типу поля. */
export function propertyFor(kind: FieldKind, title: string): JsonSchema {
  switch (kind) {
    case 'textarea':
      return { type: 'string', title, format: 'textarea' };
    case 'number':
      return { type: 'number', title };
    case 'email':
      return { type: 'string', title, format: 'email' };
    case 'phone':
      return { type: 'string', title, pattern: PHONE_PATTERN, [UI_KEY]: { placeholder: '+380 67 123 45 67' } };
    case 'address':
      return { type: 'string', title, format: 'textarea', [UI_KEY]: { rows: 2 } };
    case 'date':
      return { type: 'string', title, format: 'date' };
    case 'datetime':
      return { type: 'string', title, format: 'date-time' };
    case 'boolean':
      return { type: 'boolean', title };
    case 'select':
      return { type: 'string', title, oneOf: [{ const: 'option_1', title: $localize`:@@fd.option1:Варіант 1` }, { const: 'option_2', title: $localize`:@@fd.option2:Варіант 2` }] };
    case 'multiselect':
      return { type: 'array', title, items: { type: 'string', oneOf: [{ const: 'option_1', title: $localize`:@@fd.option1:Варіант 1` }, { const: 'option_2', title: $localize`:@@fd.option2:Варіант 2` }] } };
    case 'file':
      return { type: 'string', title, format: 'binary' };
    default:
      return { type: 'string', title };
  }
}

const TRANSLIT: Record<string, string> = {
  а: 'a', б: 'b', в: 'v', г: 'h', ґ: 'g', д: 'd', е: 'e', є: 'ie', ж: 'zh', з: 'z', и: 'y', і: 'i', ї: 'i', й: 'i',
  к: 'k', л: 'l', м: 'm', н: 'n', о: 'o', п: 'p', р: 'r', с: 's', т: 't', у: 'u', ф: 'f', х: 'kh', ц: 'ts', ч: 'ch',
  ш: 'sh', щ: 'shch', ь: '', ю: 'iu', я: 'ia', ы: 'y', э: 'e', ё: 'io', ъ: '', "'": '', '’': '', 'ʼ': '',
};

/** Ключ поля з підпису: транслітерація, нижній регістр, `_` замість решти; порожньо — `field`. */
export function keyFromLabel(label: string): string {
  const latin = [...label.toLowerCase()].map((ch) => TRANSLIT[ch] ?? ch).join('');
  const key = latin.replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 40).replace(/_+$/, '');
  return /^[a-z]/.test(key) ? key : key ? `f_${key}` : 'field';
}

/** Ключ, якого ще немає серед полів: `name`, `name_2`, `name_3`… */
export function uniqueKey(base: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  if (!used.has(base)) return base;
  for (let i = 2; ; i++) if (!used.has(`${base}_${i}`)) return `${base}_${i}`;
}

/** Де стоїть поле в розмітці або `null`. */
export function placeOf(state: DesignerState, key: string): FieldPlace | null {
  for (let s = 0; s < state.layout.sections.length; s++) {
    const rows = state.layout.sections[s].rows;
    for (let r = 0; r < rows.length; r++) {
      const c = rows[r].findIndex((cell) => cell.field === key);
      if (c >= 0) return { section: s, row: r, cell: c };
    }
  }
  return null;
}

/** Ключі полів у порядку розмітки. */
export function fieldOrder(state: DesignerState): string[] {
  return state.layout.sections.flatMap((s) => s.rows.flatMap((r) => r.map((c) => c.field)));
}

function withSections(state: DesignerState, sections: FormSection[]): DesignerState {
  return { ...state, layout: { sections } };
}

function copySections(state: DesignerState): FormSection[] {
  return state.layout.sections.map((s) => ({ ...s, rows: s.rows.map((r) => r.map((c) => ({ ...c }))) }));
}

/** Прибирає поле з розмітки (порожній рядок зникає); повертає копію секцій і клітинку поля. */
function takeOut(state: DesignerState, key: string): { sections: FormSection[]; cell: FormCell | null } {
  const sections = copySections(state);
  const place = placeOf(state, key);
  if (!place) return { sections, cell: null };
  const row = sections[place.section].rows[place.row];
  const [cell] = row.splice(place.cell, 1);
  if (!row.length) sections[place.section].rows.splice(place.row, 1);
  else if (row.length === 1) delete row[0].width;
  return { sections, cell };
}

function clampIndex(index: number, length: number): number {
  return Math.max(0, Math.min(index, length));
}

/** Нове поле типу `kind` окремим рядком у секції `section` на місці `row` (за замовчуванням — у кінці). */
export function addField(state: DesignerState, kind: FieldKind, section = state.layout.sections.length - 1, row?: number): { state: DesignerState; key: string } {
  const info = FIELD_KINDS.find((k) => k.kind === kind)!;
  const key = uniqueKey(keyFromLabel(info.label), Object.keys(state.properties));
  const sections = copySections(state);
  if (!sections.length) sections.push({ rows: [] });
  const s = Math.max(0, Math.min(section, sections.length - 1));
  sections[s].rows.splice(clampIndex(row ?? sections[s].rows.length, sections[s].rows.length), 0, [{ field: key }]);
  return { state: { ...state, properties: { ...state.properties, [key]: propertyFor(kind, info.label) }, layout: { sections } }, key };
}

/** Видаляє поле: властивість, обов'язковість, місце в розмітці. */
export function removeField(state: DesignerState, key: string): DesignerState {
  if (!(key in state.properties)) return state;
  const { sections } = takeOut(state, key);
  const properties = { ...state.properties };
  delete properties[key];
  return { ...state, properties, required: state.required.filter((r) => r !== key), layout: { sections } };
}

/** Переносить поле окремим рядком у секцію `section` на місце `row` (індекс — у секції після вилучення поля). */
export function moveField(state: DesignerState, key: string, section: number, row: number): DesignerState {
  const { sections, cell } = takeOut(state, key);
  if (!cell || !sections[section]) return state;
  delete cell.width;
  sections[section].rows.splice(clampIndex(row, sections[section].rows.length), 0, [cell]);
  return withSections(state, sections);
}

/** Переносить рядок цілком (з усіма полями) у секцію `toSection` на місце `toRow`. */
export function moveRow(state: DesignerState, fromSection: number, fromRow: number, toSection: number, toRow: number): DesignerState {
  const sections = copySections(state);
  const [row] = sections[fromSection]?.rows.splice(fromRow, 1) ?? [];
  if (!row || !sections[toSection]) return state;
  sections[toSection].rows.splice(clampIndex(toRow, sections[toSection].rows.length), 0, row);
  return withSections(state, sections);
}

/** Поле на позицію вище (`-1`) чи нижче (`+1`) серед рядків форми; на межі секції переходить у сусідню. */
export function nudgeField(state: DesignerState, key: string, delta: -1 | 1): DesignerState {
  const place = placeOf(state, key);
  if (!place) return state;
  const sections = state.layout.sections;
  const rowCount = sections[place.section].rows.length;
  const alone = sections[place.section].rows[place.row].length === 1;
  if (!alone) {
    // Поле з рядка кількох полів виходить окремим рядком над або під ним.
    return moveField(state, key, place.section, delta < 0 ? place.row : place.row + 1);
  }
  const target = place.row + delta;
  if (target >= 0 && target < rowCount) return moveRow(state, place.section, place.row, place.section, target);
  const next = place.section + delta;
  if (next < 0 || next >= sections.length) return state;
  return moveField(state, key, next, delta < 0 ? sections[next].rows.length : 0);
}

/** Копія поля з новим ключем — окремим рядком одразу під оригіналом. */
export function duplicateField(state: DesignerState, key: string): { state: DesignerState; key: string } {
  const place = placeOf(state, key);
  const property = state.properties[key];
  if (!place || !property) return { state, key };
  const copyKey = uniqueKey(key.replace(/_\d+$/, ''), Object.keys(state.properties));
  const sections = copySections(state);
  sections[place.section].rows.splice(place.row + 1, 0, [{ field: copyKey }]);
  const required = state.required.includes(key) ? [...state.required, copyKey] : state.required;
  return { state: { ...state, properties: { ...state.properties, [copyKey]: clone(property) }, required, layout: { sections } }, key: copyKey };
}

/** Нові значення властивості поля: `undefined` прибирає ключ. */
export function setFieldProps(state: DesignerState, key: string, patch: Record<string, unknown>): DesignerState {
  const current = state.properties[key];
  if (!current) return state;
  const next: JsonSchema = { ...current };
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined || v === '') delete next[k];
    else next[k] = v;
  }
  return { ...state, properties: { ...state.properties, [key]: next } };
}

/** Підказки показу (`x-openbp-ui`) поля: `undefined` прибирає ключ, порожній об'єкт прибирає анотацію. */
export function setFieldUi(state: DesignerState, key: string, patch: Record<string, unknown>): DesignerState {
  const current = state.properties[key];
  if (!current) return state;
  const ui: Record<string, unknown> = { ...(isObject(current[UI_KEY]) ? current[UI_KEY] : {}) };
  for (const [k, v] of Object.entries(patch)) {
    if (v === undefined || v === '') delete ui[k];
    else ui[k] = v;
  }
  return setFieldProps(state, key, { [UI_KEY]: Object.keys(ui).length ? ui : undefined });
}

export function setRequired(state: DesignerState, key: string, required: boolean): DesignerState {
  const has = state.required.includes(key);
  if (has === required || !(key in state.properties)) return state;
  return { ...state, required: required ? [...state.required, key] : state.required.filter((r) => r !== key) };
}

/** Перевірка нового ключа поля: `null` — можна. */
export function keyError(state: DesignerState, key: string, next: string): string | null {
  if (!/^[A-Za-z_][A-Za-z0-9_]{0,63}$/.test(next)) return $localize`:@@fd.key.invalid:Латиниця, цифри й «_», з літери, до 64 символів.`;
  if (next !== key && next in state.properties) return $localize`:@@fd.key.taken:Поле з таким ключем уже є.`;
  return null;
}

/** Перейменовує ключ поля: властивість (на тому ж місці), обов'язковість, розмітка, умови `visibleIf`. */
export function renameKey(state: DesignerState, key: string, next: string): DesignerState {
  if (key === next || !(key in state.properties) || keyError(state, key, next)) return state;
  const properties: Record<string, JsonSchema> = {};
  for (const [k, v] of Object.entries(state.properties)) properties[k === key ? next : k] = v;
  const rename = (field: string) => (field === key ? next : field);
  const sections = copySections(state).map((s) => ({
    ...s,
    ...(s.visibleIf ? { visibleIf: { ...s.visibleIf, field: rename(s.visibleIf.field) } } : {}),
    rows: s.rows.map((r) =>
      r.map((c) => ({ ...c, field: rename(c.field), ...(c.visibleIf ? { visibleIf: { ...c.visibleIf, field: rename(c.visibleIf.field) } } : {}) })),
    ),
  }));
  return { ...state, properties, required: state.required.map(rename), layout: { sections } };
}

/** Нова секція з заголовком на місці `at` (за замовчуванням — у кінці). */
export function addSection(state: DesignerState, at = state.layout.sections.length): DesignerState {
  const sections = copySections(state);
  sections.splice(clampIndex(at, sections.length), 0, { title: $localize`:@@fd.section.new:Нова секція`, rows: [] });
  return withSections(state, sections);
}

/** Видаляє секцію; її поля переходять у попередню (для першої — у наступну). Останню секцію не видалити. */
export function removeSection(state: DesignerState, index: number): DesignerState {
  if (state.layout.sections.length <= 1 || !state.layout.sections[index]) return state;
  const sections = copySections(state);
  const [removed] = sections.splice(index, 1);
  const target = index > 0 ? sections[index - 1] : sections[0];
  if (index > 0) target.rows.push(...removed.rows);
  else target.rows.unshift(...removed.rows);
  return withSections(state, sections);
}

export function moveSection(state: DesignerState, from: number, to: number): DesignerState {
  const sections = copySections(state);
  const [section] = sections.splice(from, 1);
  if (!section) return state;
  sections.splice(clampIndex(to, sections.length), 0, section);
  return withSections(state, sections);
}

export function setSectionProps(state: DesignerState, index: number, patch: Partial<Omit<FormSection, 'rows'>>): DesignerState {
  const sections = copySections(state);
  const section = sections[index];
  if (!section) return state;
  for (const [k, v] of Object.entries(patch) as [keyof FormSection, unknown][]) {
    if (v === undefined || v === '' || v === false) delete section[k];
    else (section as unknown as Record<string, unknown>)[k] = v;
  }
  if (!section.collapsible) delete section.collapsed;
  return withSections(state, sections);
}

/** Варіант списку для редактора варіантів. */
export interface OptionRow {
  value: string;
  label: string;
}

/** Варіанти поля-списку (`enum` або `oneOf`, для масиву — з `items`). */
export function optionsOf(property: JsonSchema): OptionRow[] {
  const source = property['type'] === 'array' && isObject(property['items']) ? property['items'] : property;
  if (Array.isArray(source['oneOf'])) {
    return source['oneOf'].filter(isObject).map((o) => ({ value: String(o['const'] ?? ''), label: typeof o['title'] === 'string' ? o['title'] : String(o['const'] ?? '') }));
  }
  if (Array.isArray(source['enum'])) return source['enum'].map((v) => ({ value: String(v), label: String(v) }));
  return [];
}

/** Записує варіанти як `oneOf` з `const`/`title` (для масиву — у `items`); `enum` прибирається. */
export function setOptions(state: DesignerState, key: string, options: readonly OptionRow[]): DesignerState {
  const property = state.properties[key];
  if (!property) return state;
  const oneOf = options.map((o) => ({ const: o.value, title: o.label || o.value }));
  if (property['type'] === 'array') {
    const items: JsonSchema = { ...(isObject(property['items']) ? property['items'] : { type: 'string' }), oneOf };
    delete items['enum'];
    return setFieldProps(state, key, { items });
  }
  return setFieldProps(state, key, { oneOf, enum: undefined });
}

/** Історія станів конструктора: undo/redo до `limit` кроків. */
export class DesignerHistory<T> {
  private past: T[] = [];
  private future: T[] = [];

  constructor(private readonly limit = 50) {}

  push(previous: T): void {
    this.past.push(previous);
    if (this.past.length > this.limit) this.past.shift();
    this.future = [];
  }

  undo(current: T): T | null {
    const previous = this.past.pop();
    if (previous === undefined) return null;
    this.future.push(current);
    return previous;
  }

  redo(current: T): T | null {
    const next = this.future.pop();
    if (next === undefined) return null;
    this.past.push(current);
    return next;
  }

  get canUndo(): boolean {
    return this.past.length > 0;
  }

  get canRedo(): boolean {
    return this.future.length > 0;
  }

  clear(): void {
    this.past = [];
    this.future = [];
  }
}
