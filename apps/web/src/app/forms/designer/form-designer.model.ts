import { RADIO_MAX_OPTIONS, type JsonSchema } from '../form-fields';
import { FormCell, FormLayout, FormSection, GRID_COLUMNS, MAX_ROW_CELLS, UI_KEY, VisibleIf, readLayout, rowWidths, writeLayout } from '../form-layout';

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
      // Маска й нормалізація до цифр — за `format: "phone"` (F11-07); старі поля з PHONE_PATTERN теж розпізнаються.
      return { type: 'string', title, format: 'phone', [UI_KEY]: { placeholder: '+380 67 123 45 67' } };
    case 'address':
      return { type: 'string', title, format: 'textarea', [UI_KEY]: { rows: 2 } };
    case 'date':
      return { type: 'string', title, format: 'date' };
    case 'datetime':
      return { type: 'string', title, format: 'date-time' };
    case 'boolean':
      return { type: 'boolean', title };
    case 'select':
      // До 3 варіантів — кнопками (F11-07); варіантів стане більше 6 — віджет прибирається (`setOptions`).
      return {
        type: 'string',
        title,
        oneOf: [{ const: 'option_1', title: $localize`:@@fd.option1:Варіант 1` }, { const: 'option_2', title: $localize`:@@fd.option2:Варіант 2` }],
        [UI_KEY]: { widget: 'radio' },
      };
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
  else normalizeRow(row);
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

/**
 * Видаляє поле: властивість, обов'язковість, місце в розмітці й умови «Показувати, якщо», що на нього посилались
 * (F11-06; хто залежав — `rulesUsing` до видалення, щоб попередити).
 */
export function removeField(state: DesignerState, key: string): DesignerState {
  if (!(key in state.properties)) return state;
  const { sections } = takeOut(state, key);
  for (const section of sections) {
    if (section.visibleIf?.field === key) delete section.visibleIf;
    for (const row of section.rows) for (const cell of row) if (cell.visibleIf?.field === key) delete cell.visibleIf;
  }
  const properties = { ...state.properties };
  delete properties[key];
  return { ...state, properties, required: state.required.filter((r) => r !== key), layout: { sections } };
}

/** Хто залежить від поля умовою «Показувати, якщо»: ключі полів і індекси секцій (F11-06). */
export interface RuleUsers {
  fields: string[];
  sections: number[];
}

export function rulesUsing(state: DesignerState, key: string): RuleUsers {
  const users: RuleUsers = { fields: [], sections: [] };
  state.layout.sections.forEach((section, index) => {
    if (section.visibleIf?.field === key) users.sections.push(index);
    for (const row of section.rows) for (const cell of row) if (cell.visibleIf?.field === key) users.fields.push(cell.field);
  });
  return users;
}

/** Умова «Показувати, якщо» поля (`undefined` прибирає). Умова на саме поле не ставиться (F11-06). */
export function setFieldVisibleIf(state: DesignerState, key: string, rule: VisibleIf | undefined): DesignerState {
  const place = placeOf(state, key);
  if (!place || (rule && (rule.field === key || !(rule.field in state.properties)))) return state;
  const sections = copySections(state);
  const cell = sections[place.section].rows[place.row][place.cell];
  if (rule) cell.visibleIf = { ...rule };
  else delete cell.visibleIf;
  return withSections(state, sections);
}

/** Поля, від яких може залежати поле `key` (усі інші) чи секція `section` (поля поза нею) — у порядку розмітки. */
export function ruleSources(state: DesignerState, target: { field: string } | { section: number }): string[] {
  const own = 'field' in target ? new Set([target.field]) : new Set(state.layout.sections[target.section]?.rows.flat().map((c) => c.field) ?? []);
  return fieldOrder(state).filter((k) => !own.has(k));
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

// ---------- Режим таблиці: поля форми сторінки застосунку = колонки (F11-05) ----------

/** Колонка таблиці даних, яку можна поставити на форму сторінки; тип колонки визначає тип поля. */
export interface TableColumn {
  name: string;
  /** Тип колонки таблиці: `string`, `number`, `boolean`, `date`, `datetime`, `enum`, `ref`, `user`, `json`. */
  type: string;
  /** Варіанти колонки `enum`. */
  values?: readonly string[] | null;
}

/** Властивість схеми для колонки таблиці: тип поля задає колонка, підпис — назва колонки, поки її не змінили. */
export function propertyForColumn(column: TableColumn, title = column.name): JsonSchema {
  switch (column.type) {
    case 'number':
      return { type: 'number', title };
    case 'boolean':
      return { type: 'boolean', title };
    case 'date':
      return { type: 'string', title, format: 'date' };
    case 'datetime':
      return { type: 'string', title, format: 'date-time' };
    case 'enum':
      return { type: 'string', title, oneOf: (column.values ?? []).map((v) => ({ const: v, title: v })) };
    case 'ref':
    case 'user':
      // Варіанти бере сервер зі зв'язаної таблиці; конструктору важливо лише, що це вибір.
      return { type: 'string', title, enum: [] };
    case 'json':
      return { type: 'string', title, format: 'textarea' };
    default:
      return { type: 'string', title };
  }
}

/** Колонки, яких ще немає на формі, у порядку таблиці. */
export function columnsNotOnForm(state: DesignerState, columns: readonly TableColumn[]): TableColumn[] {
  return columns.filter((c) => !(c.name in state.properties));
}

/** Колонка стає полем форми окремим рядком у секції `section` на місці `row` (за замовчуванням — у кінці). */
export function addColumn(state: DesignerState, column: TableColumn, section = state.layout.sections.length - 1, row?: number): DesignerState {
  if (column.name in state.properties) return state;
  const sections = copySections(state);
  if (!sections.length) sections.push({ rows: [] });
  const s = Math.max(0, Math.min(section, sections.length - 1));
  sections[s].rows.splice(clampIndex(row ?? sections[s].rows.length, sections[s].rows.length), 0, [{ field: column.name }]);
  return { ...state, properties: { ...state.properties, [column.name]: propertyForColumn(column) }, layout: { sections } };
}

/** Усі колонки, яких ще немає на формі, — окремими рядками в кінці секції `section`. */
export function addAllColumns(state: DesignerState, columns: readonly TableColumn[], section = state.layout.sections.length - 1): DesignerState {
  return columnsNotOnForm(state, columns).reduce((acc, column) => addColumn(acc, column, section), state);
}

/** Колонка як поле в наявному рядку (якщо там уже 4 поля — окремим рядком під ним). */
export function addColumnToRow(state: DesignerState, column: TableColumn, section: number, row: number, index: number): DesignerState {
  const added = addColumn(state, column, section, row + 1);
  const target = state.layout.sections[section]?.rows[row];
  if (added === state || !target || target.length >= MAX_ROW_CELLS) return added;
  return placeInRow(added, column.name, section, row, index);
}

// ---------- Рядки з кількох полів і ширини (F11-03) ----------

/** Найменша ширина клітинки при зміні ширин перетягуванням чи клавіатурою. */
export const MIN_CELL_WIDTH = 2;

/** Пресети ширин для рядка з `n` полів (docs/forms-designer.md §3.2). */
export function widthPresets(n: number): number[][] {
  switch (n) {
    case 2:
      return [[6, 6], [4, 8], [8, 4]];
    case 3:
      return [[4, 4, 4], [3, 6, 3]];
    case 4:
      return [[3, 3, 3, 3]];
    default:
      return [];
  }
}

/**
 * Ширини, приведені до суми 12: пропорційно, кожна не менша за `MIN_CELL_WIDTH` (для 1 поля — 12), залишок від
 * округлення — клітинкам з найбільшою дробовою частиною.
 */
export function normalizeWidths(widths: readonly number[]): number[] {
  const n = widths.length;
  if (n <= 1) return n ? [GRID_COLUMNS] : [];
  const safe = widths.map((w) => (Number.isFinite(w) && w > 0 ? w : 1));
  const sum = safe.reduce((a, b) => a + b, 0);
  const raw = safe.map((w) => (w * GRID_COLUMNS) / sum);
  const out = raw.map((r) => Math.max(MIN_CELL_WIDTH, Math.floor(r)));
  let diff = GRID_COLUMNS - out.reduce((a, b) => a + b, 0);
  const byFraction = raw.map((r, i) => ({ i, f: r - Math.floor(r) })).sort((a, b) => b.f - a.f || a.i - b.i);
  for (let k = 0; diff > 0; k = (k + 1) % n, diff--) out[byFraction[k].i]++;
  while (diff < 0) {
    const i = out.indexOf(Math.max(...out));
    out[i]--;
    diff++;
  }
  return out;
}

/** Записує ширини в клітинки рядка; рівні ширини (або одне поле) — без явного `width`. */
function applyWidths(row: FormCell[], widths: readonly number[]): void {
  const equal = widths.every((w) => w === widths[0]) && widths[0] * widths.length === GRID_COLUMNS;
  row.forEach((cell, i) => {
    if (row.length === 1 || equal) delete cell.width;
    else cell.width = widths[i];
  });
}

/** Після зміни складу рядка: ширини знову в сумі 12 (рівні, якщо явних не було). */
function normalizeRow(row: FormCell[]): void {
  if (row.every((c) => !c.width)) return applyWidths(row, []);
  applyWidths(row, normalizeWidths(rowWidths(row)));
}

/**
 * Ставить поле `key` у наявний рядок `row` секції `section` на позицію `index` («об'єднати»). У тому самому рядку —
 * лише змінює порядок (ширини йдуть разом з полями); в інший рядок — до 4 полів, ширини стають рівними. Індекси —
 * як у розмітці до зміни.
 */
export function placeInRow(state: DesignerState, key: string, section: number, row: number, index: number): DesignerState {
  const from = placeOf(state, key);
  const target = state.layout.sections[section]?.rows[row];
  if (!from || !target) return state;
  const sections = copySections(state);
  if (from.section === section && from.row === row) {
    const cells = sections[section].rows[row];
    const [cell] = cells.splice(from.cell, 1);
    cells.splice(clampIndex(index > from.cell ? index - 1 : index, cells.length), 0, cell);
    return withSections(state, sections);
  }
  if (target.length >= MAX_ROW_CELLS) return state;
  const cells = sections[section].rows[row];
  const [cell] = sections[from.section].rows[from.row].splice(from.cell, 1);
  cells.splice(clampIndex(index, cells.length), 0, cell);
  applyWidths(cells, []);
  const source = sections[from.section].rows[from.row];
  if (!source.length) sections[from.section].rows.splice(from.row, 1);
  else normalizeRow(source);
  return withSections(state, sections);
}

/** Поле `key` поруч з полем `target`: зліва (`before`) чи справа (`after`). */
export function mergeIntoRow(state: DesignerState, key: string, target: string, side: 'before' | 'after'): DesignerState {
  const place = placeOf(state, target);
  if (!place || key === target) return state;
  return placeInRow(state, key, place.section, place.row, side === 'before' ? place.cell : place.cell + 1);
}

/** Чи можна поставити поле `key` у рядок з полем `target` (ліміт 4 поля; у своєму рядку — завжди). */
export function canMerge(state: DesignerState, key: string, target: string): boolean {
  const a = placeOf(state, key);
  const b = placeOf(state, target);
  if (!a || !b || key === target) return false;
  if (a.section === b.section && a.row === b.row) return true;
  return state.layout.sections[b.section].rows[b.row].length < MAX_ROW_CELLS;
}

/** Поле з рядка кількох полів виходить окремим рядком одразу під ним. */
export function splitFromRow(state: DesignerState, key: string): DesignerState {
  const place = placeOf(state, key);
  if (!place || state.layout.sections[place.section].rows[place.row].length < 2) return state;
  return moveField(state, key, place.section, place.row + 1);
}

/** Окреме поле стає в кінець рядка над ним (у тій самій секції), якщо там є місце. */
export function joinPrevious(state: DesignerState, key: string): DesignerState {
  const place = placeOf(state, key);
  if (!place || place.row === 0) return state;
  const rows = state.layout.sections[place.section].rows;
  if (rows[place.row].length !== 1 || rows[place.row - 1].length >= MAX_ROW_CELLS) return state;
  return placeInRow(state, key, place.section, place.row - 1, rows[place.row - 1].length);
}

/** Чи доступне «В один рядок із попереднім» для поля. */
export function canJoinPrevious(state: DesignerState, key: string): boolean {
  return joinPrevious(state, key) !== state;
}

/** Нові ширини рядка (наприклад, пресет); некоректні — нормалізуються до суми 12. */
export function setRowWidths(state: DesignerState, section: number, row: number, widths: readonly number[]): DesignerState {
  const cells = state.layout.sections[section]?.rows[row];
  if (!cells || cells.length < 2 || widths.length !== cells.length) return state;
  const valid = widths.every((w) => Number.isInteger(w) && w >= 1) && widths.reduce((a, b) => a + b, 0) === GRID_COLUMNS;
  const sections = copySections(state);
  applyWidths(sections[section].rows[row], valid ? widths : normalizeWidths(widths));
  return withSections(state, sections);
}

/**
 * Зсуває межу між клітинками `boundary` і `boundary + 1` на `delta` колонок (додатне — ліва ширшає); жодна з двох не
 * стає вужчою за `MIN_CELL_WIDTH`.
 */
export function resizeBoundary(state: DesignerState, section: number, row: number, boundary: number, delta: number): DesignerState {
  const cells = state.layout.sections[section]?.rows[row];
  if (!cells || boundary < 0 || boundary >= cells.length - 1 || !delta) return state;
  const widths = rowWidths(cells);
  const d = Math.max(MIN_CELL_WIDTH - widths[boundary], Math.min(delta, widths[boundary + 1] - MIN_CELL_WIDTH));
  if (!d) return state;
  widths[boundary] += d;
  widths[boundary + 1] -= d;
  return setRowWidths(state, section, row, widths);
}

/** Alt+←/→: ширина поля −/+1 колонка за рахунок сусіда справа (для останнього в рядку — зліва). */
export function resizeField(state: DesignerState, key: string, delta: -1 | 1): DesignerState {
  const place = placeOf(state, key);
  if (!place) return state;
  const cells = state.layout.sections[place.section].rows[place.row];
  if (cells.length < 2) return state;
  const last = place.cell === cells.length - 1;
  return last
    ? resizeBoundary(state, place.section, place.row, place.cell - 1, -delta)
    : resizeBoundary(state, place.section, place.row, place.cell, delta);
}

/** Нове поле типу `kind` у наявному рядку на позиції `index` (якщо в рядку вже 4 поля — окремим рядком під ним). */
export function addFieldToRow(state: DesignerState, kind: FieldKind, section: number, row: number, index: number): { state: DesignerState; key: string } {
  const added = addField(state, kind, section, row + 1);
  const target = state.layout.sections[section]?.rows[row];
  if (!target || target.length >= MAX_ROW_CELLS) return added;
  return { state: placeInRow(added.state, added.key, section, row, index), key: added.key };
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
  const next = setFieldProps(state, key, { oneOf, enum: undefined });
  return options.length > RADIO_MAX_OPTIONS && widgetOf(property) === 'radio' ? setFieldUi(next, key, { widget: undefined }) : next;
}

/** Як показати поле (`x-openbp-ui.widget`, F11-07): `radio`, `chips` або `undefined` — типово (список). */
export function widgetOf(property: JsonSchema): 'radio' | 'chips' | undefined {
  const ui = isObject(property[UI_KEY]) ? property[UI_KEY] : {};
  return ui['widget'] === 'radio' || ui['widget'] === 'chips' ? ui['widget'] : undefined;
}

/** Які віджети доступні полю: «Вибір» — кнопки (до 6 варіантів), «Кілька варіантів» — чипи; іншим — жодного. */
export function widgetsFor(property: JsonSchema): ('radio' | 'chips')[] {
  const kind = kindOf(property);
  if (kind === 'select') return optionsOf(property).length <= RADIO_MAX_OPTIONS ? ['radio'] : [];
  if (kind === 'multiselect') return ['chips'];
  return [];
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
