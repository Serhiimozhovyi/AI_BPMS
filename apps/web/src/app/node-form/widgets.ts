import { isExpression, isPlainObject } from './node-value';

/**
 * Віджети полів з конекторів (F6-30): анотація `x-openbp-widget` властивості `inputSchema` tool'а
 * (docs/connectors.md §2) підказує, яким редактором показати значення. Продукт не знає конкретних конекторів:
 * схожий аргумент у будь-якого конектора отримує той самий редактор. Без Angular — чисті функції.
 */

/** Назва анотації в JSON Schema властивості. */
export const WIDGET_ANNOTATION = 'x-openbp-widget';

/** Відомі віджети: кнопки (`reply_markup`), таблиця команд, вибір чату (це `x-openbp-options`, окремого редактора немає), поля форми (F11-04, конструктор у діалозі). */
export type Widget = 'keyboard' | 'commands' | 'chat' | 'form-schema';

/** Віджет з анотації або `null`, якщо її немає чи значення невідоме (поле тоді редагується звичайним способом). */
export function parseWidget(annotation: unknown): Widget | null {
  return annotation === 'keyboard' || annotation === 'commands' || annotation === 'chat' || annotation === 'form-schema' ? annotation : null;
}

/** Не більше цього кнопок у рядку інлайн-клавіатури Telegram і не більше стільки кнопок усього. */
export const MAX_INLINE_ROW = 8;
export const MAX_BUTTONS = 100;
/** Ліміт `callback_data` Telegram — 64 байти UTF-8. */
export const MAX_CALLBACK_BYTES = 64;

export type KeyboardMode = 'none' | 'inline' | 'reply' | 'remove' | 'raw';
export type InlineAction = 'callback' | 'url';
export type ReplyRequest = 'none' | 'contact' | 'location';

export interface InlineButton {
  text: string;
  action: InlineAction;
  /** `callback_data` або `url` залежно від `action`. */
  value: string;
}

export interface ReplyButton {
  text: string;
  request: ReplyRequest;
}

/** Стан редактора кнопок: активний режим і дані кожного, щоб перемикання не губило набране. */
export interface KeyboardModel {
  mode: KeyboardMode;
  inline: InlineButton[][];
  reply: ReplyButton[][];
  resize: boolean;
  oneTime: boolean;
  /** Режим `raw`: значення як є (вираз, рядок з JSON чи розмітка з полями, яких редактор не знає). */
  raw: unknown;
}

export function emptyKeyboard(): KeyboardModel {
  return { mode: 'none', inline: [], reply: [], resize: true, oneTime: false, raw: undefined };
}

const INLINE_KEYS = new Set(['text', 'callback_data', 'url']);
const REPLY_KEYS = new Set(['text', 'request_contact', 'request_location']);
const REPLY_MARKUP_KEYS = new Set(['keyboard', 'resize_keyboard', 'one_time_keyboard']);

/** Стан редактора за значенням `reply_markup`; що не вкладається в редактор без втрат — режим `raw`. */
export function parseKeyboard(value: unknown): KeyboardModel {
  const model = emptyKeyboard();
  if (value === undefined || value === null || value === '') return model;
  let markup = value;
  if (typeof value === 'string') {
    if (isExpression(value)) return { ...model, mode: 'raw', raw: value };
    try {
      markup = JSON.parse(value);
    } catch {
      return { ...model, mode: 'raw', raw: value };
    }
  }
  const raw = (): KeyboardModel => ({ ...model, mode: 'raw', raw: value });
  if (!isPlainObject(markup)) return raw();
  const keys = Object.keys(markup);

  if (keys.length === 1 && markup['remove_keyboard'] === true) return { ...model, mode: 'remove' };

  if (keys.length === 1 && Array.isArray(markup['inline_keyboard'])) {
    const rows = rowsOf(markup['inline_keyboard'], (b) => {
      if (!isPlainObject(b) || typeof b['text'] !== 'string' || !Object.keys(b).every((k) => INLINE_KEYS.has(k))) return null;
      const url = typeof b['url'] === 'string' ? b['url'] : undefined;
      const callback = b['callback_data'];
      if (url === undefined && callback !== undefined && typeof callback !== 'string') return null;
      return url !== undefined
        ? { text: b['text'], action: 'url' as const, value: url }
        : { text: b['text'], action: 'callback' as const, value: typeof callback === 'string' ? callback : '' };
    });
    return rows ? { ...model, mode: 'inline', inline: rows } : raw();
  }

  if (Array.isArray(markup['keyboard']) && keys.every((k) => REPLY_MARKUP_KEYS.has(k))) {
    const rows = rowsOf(markup['keyboard'], (b) => {
      if (typeof b === 'string') return { text: b, request: 'none' as const };
      if (!isPlainObject(b) || typeof b['text'] !== 'string' || !Object.keys(b).every((k) => REPLY_KEYS.has(k))) return null;
      return { text: b['text'], request: b['request_contact'] === true ? ('contact' as const) : b['request_location'] === true ? ('location' as const) : ('none' as const) };
    });
    return rows
      ? { ...model, mode: 'reply', reply: rows, resize: markup['resize_keyboard'] === true, oneTime: markup['one_time_keyboard'] === true }
      : raw();
  }
  return raw();
}

function rowsOf<T>(rows: unknown[], button: (b: unknown) => T | null): T[][] | null {
  const result: T[][] = [];
  for (const row of rows) {
    if (!Array.isArray(row)) return null;
    const parsed = row.map(button);
    if (parsed.some((b) => b === null)) return null;
    result.push(parsed as T[]);
  }
  return result;
}

/** Значення `reply_markup` за станом редактора; `undefined` — без кнопок (поле не пишеться у вузол). */
export function serializeKeyboard(model: KeyboardModel): unknown {
  switch (model.mode) {
    case 'remove':
      return { remove_keyboard: true };
    case 'raw':
      return model.raw === '' ? undefined : model.raw;
    case 'inline': {
      const rows = model.inline.filter((r) => r.length > 0);
      return rows.length === 0
        ? undefined
        : { inline_keyboard: rows.map((r) => r.map((b) => (b.action === 'url' ? { text: b.text, url: b.value } : { text: b.text, callback_data: b.value }))) };
    }
    case 'reply': {
      const rows = model.reply.filter((r) => r.length > 0);
      if (rows.length === 0) return undefined;
      return {
        keyboard: rows.map((r) =>
          r.map((b) => (b.request === 'contact' ? { text: b.text, request_contact: true } : b.request === 'location' ? { text: b.text, request_location: true } : { text: b.text })),
        ),
        ...(model.resize ? { resize_keyboard: true } : {}),
        ...(model.oneTime ? { one_time_keyboard: true } : {}),
      };
    }
    default:
      return undefined;
  }
}

/** Код помилки перевірки кнопок. */
export type KeyboardIssue = 'empty-text' | 'empty-callback' | 'long-callback' | 'bad-url' | 'long-row' | 'too-many';

/** Що не так з кнопками активного режиму; тексти з виразами `{{ }}` не міряються — їхній розмір відомий лише під час виконання. */
export function keyboardIssues(model: KeyboardModel): KeyboardIssue[] {
  const issues = new Set<KeyboardIssue>();
  const buttons = model.mode === 'inline' ? model.inline.flat() : model.mode === 'reply' ? model.reply.flat() : [];
  if (buttons.length > MAX_BUTTONS) issues.add('too-many');
  if (model.mode === 'inline' && model.inline.some((r) => r.length > MAX_INLINE_ROW)) issues.add('long-row');
  for (const b of buttons) if (!b.text.trim()) issues.add('empty-text');
  if (model.mode === 'inline') {
    for (const b of model.inline.flat()) {
      if (isExpression(b.value)) continue;
      if (b.action === 'callback') {
        if (!b.value) issues.add('empty-callback');
        else if (new TextEncoder().encode(b.value).length > MAX_CALLBACK_BYTES) issues.add('long-callback');
      } else if (!/^(https?|tg):\/\/\S+$/i.test(b.value)) {
        issues.add('bad-url');
      }
    }
  }
  return [...issues];
}

/** Копія списку з елементом `index`, зсунутим на `delta`; поза межами — без змін. */
export function moveItem<T>(list: readonly T[], index: number, delta: number): T[] {
  const target = index + delta;
  const copy = [...list];
  if (index < 0 || index >= copy.length || target < 0 || target >= copy.length) return copy;
  [copy[index], copy[target]] = [copy[target], copy[index]];
  return copy;
}

/** Команда меню бота `set_my_commands`. */
export interface BotCommandRow {
  command: string;
  description: string;
}

/** Рядки таблиці команд; не масив (вираз) — `null`: редагується як є. */
export function parseCommands(value: unknown): BotCommandRow[] | null {
  if (value === undefined || value === null || value === '') return [];
  if (!Array.isArray(value)) return null;
  const rows: BotCommandRow[] = [];
  for (const entry of value) {
    if (!isPlainObject(entry)) return null;
    rows.push({ command: String(entry['command'] ?? ''), description: String(entry['description'] ?? '') });
  }
  return rows;
}

/** Значення параметра; цілком порожні рядки відкидаються, без рядків — `undefined`. */
export function serializeCommands(rows: readonly BotCommandRow[]): BotCommandRow[] | undefined {
  const kept = rows.filter((r) => r.command !== '' || r.description !== '').map((r) => ({ command: r.command, description: r.description }));
  return kept.length > 0 ? kept : undefined;
}

/** Помилка команди: порожня, не за правилами Bot API (1–32 малі латинські літери, цифри, `_`) чи без опису (3–256). */
export function commandIssue(row: BotCommandRow): 'command' | 'description' | null {
  const dynamic = (text: string): boolean => text.includes('{{');
  if (!dynamic(row.command) && !/^[a-z0-9_]{1,32}$/.test(row.command)) return 'command';
  if (!dynamic(row.description) && (row.description.length < 3 || row.description.length > 256)) return 'description';
  return null;
}

/** Масив рядків з переліку варіантів: значення в порядку переліку, невідомі лишаються в кінці. */
export function toggleChoice(current: unknown, option: string, options: readonly string[], on: boolean): string[] | undefined {
  const have = new Set(Array.isArray(current) ? current.filter((x): x is string => typeof x === 'string') : []);
  if (on) have.add(option);
  else have.delete(option);
  const ordered = [...options.filter((o) => have.has(o)), ...[...have].filter((o) => !options.includes(o))];
  return ordered.length > 0 ? ordered : undefined;
}
