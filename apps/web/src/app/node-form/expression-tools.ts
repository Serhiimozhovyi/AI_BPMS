/**
 * Редактор виразів (F3-56): розбір рядка параметра на літерали й вирази `{{ … }}` (як `OpenBP.Expressions.ExpressionTemplate`),
 * перевірка синтаксису для підказки під полем, каталог функцій і вставка фрагмента в позицію курсора. Без Angular — чисті функції.
 */

/** Частина рядка параметра: літерал або вираз разом із маркерами `{{ }}`. */
export interface ExpressionSegment {
  text: string;
  expression: boolean;
}

/**
 * Розбиває рядок: кожен `{{` закривається найближчим `}}`; незакритий `{{` лишається літералом
 * (так само, як рушій виразів на сервері).
 */
export function expressionSegments(value: string): ExpressionSegment[] {
  const segments: ExpressionSegment[] = [];
  let position = 0;
  while (position < value.length) {
    const open = value.indexOf('{{', position);
    const close = open < 0 ? -1 : value.indexOf('}}', open + 2);
    if (close < 0) {
      segments.push({ text: value.slice(position), expression: false });
      break;
    }
    if (open > position) segments.push({ text: value.slice(position, open), expression: false });
    segments.push({ text: value.slice(open, close + 2), expression: true });
    position = close + 2;
  }
  return segments;
}

/** Чи є в рядку хоч один вираз `{{ … }}`. */
export function hasExpression(value: string): boolean {
  return expressionSegments(value).some((s) => s.expression);
}

/**
 * Синтаксична помилка виразів рядка для підказки під полем; `null` — помилок немає або перевірити неможливо.
 * Код не виконується: JavaScript лише розбирається (`new Function` без виклику).
 */
export function expressionSyntaxError(value: string): string | null {
  for (const segment of expressionSegments(value)) {
    if (!segment.expression) {
      if (segment.text.includes('{{')) return $localize`:@@expr.error.unclosed:Незакритий вираз: додайте }}.`;
      continue;
    }
    const body = segment.text.slice(2, -2);
    if (body.trim() === '') return $localize`:@@expr.error.empty:Порожній вираз {{ }}.`;
    try {
      // Дужки — як на сервері: `{ a: 1 }` є об'єктом; перенос рядка — щоб `// коментар` не з'їв дужку.
      new Function(`return (${body}\n);`);
    } catch (e) {
      if (e instanceof SyntaxError) return $localize`:@@expr.error.syntax:Синтаксична помилка: ${e.message}:message:`;
      // Політика безпеки сторінки забороняє розбір — перевірку пропускаємо, помилку покаже попередній перегляд.
      return null;
    }
  }
  return null;
}

/** Текст після вставки фрагмента в `from..to`; поза `{{ }}` фрагмент обгортається у `{{ … }}`. */
export function insertSnippet(text: string, from: number, to: number, snippet: string): { text: string; cursor: number } {
  const before = text.slice(0, from);
  const inside = before.lastIndexOf('{{') > before.lastIndexOf('}}');
  const insert = inside ? snippet : `{{ ${snippet} }}`;
  return { text: before + insert + text.slice(to), cursor: from + (inside ? insert.length : insert.length - 3) };
}

/** Функція каталогу: що вставити, приклад і його результат. */
export interface ExpressionFunction {
  /** Підпис у каталозі. */
  label: string;
  /** Фрагмент, що вставляється в позицію курсора. */
  insert: string;
  description: string;
  /** Приклад виразу і його результат. */
  example: string;
  result: string;
}

export interface ExpressionFunctionGroup {
  id: string;
  label: string;
  functions: readonly ExpressionFunction[];
}

/**
 * Каталог функцій — лише стандартний JavaScript, який підтримує Jint у пісочниці (без `Intl`, `require`, CLR):
 * дата й час, рядки, числа, масиви, об'єкти.
 */
export const EXPRESSION_FUNCTIONS: readonly ExpressionFunctionGroup[] = [
  {
    id: 'date',
    label: $localize`:@@expr.fn.group.date:Дата й час`,
    functions: [
      { label: '$now.toISOString()', insert: '$now.toISOString()', description: $localize`:@@expr.fn.iso:Поточний момент у форматі ISO 8601 (UTC)`, example: '{{ $now.toISOString() }}', result: '"2026-10-24T09:30:00.000Z"' },
      { label: '$now.getFullYear()', insert: '$now.getFullYear()', description: $localize`:@@expr.fn.year:Рік у часовому поясі сценарію`, example: '{{ $now.getFullYear() }}', result: '2026' },
      { label: '$now.getMonth() + 1', insert: '($now.getMonth() + 1)', description: $localize`:@@expr.fn.month:Місяць 1–12 (getMonth рахує з 0)`, example: '{{ $now.getMonth() + 1 }}', result: '10' },
      { label: '$now.getDay()', insert: '$now.getDay()', description: $localize`:@@expr.fn.day:День тижня: 0 — неділя, 6 — субота`, example: '{{ $now.getDay() }}', result: '6' },
      { label: 'new Date(…)', insert: 'new Date($json.date)', description: $localize`:@@expr.fn.parse:Дата з рядка ISO або мілісекунд`, example: '{{ new Date("2026-01-31").getDate() }}', result: '31' },
      { label: 'Date.now()', insert: 'Date.now()', description: $localize`:@@expr.fn.ms:Поточний момент у мілісекундах`, example: '{{ Date.now() > 0 }}', result: 'true' },
      { label: '+ днів', insert: 'new Date($now.getTime() + 7 * 864e5)', description: $localize`:@@expr.fn.add:Дата через N днів (864e5 — мілісекунд у добі)`, example: '{{ new Date($now.getTime() + 7 * 864e5).toISOString().slice(0, 10) }}', result: '"2026-10-31"' },
    ],
  },
  {
    id: 'string',
    label: $localize`:@@expr.fn.group.string:Рядки`,
    functions: [
      { label: '.trim()', insert: '.trim()', description: $localize`:@@expr.fn.trim:Без пробілів на початку й у кінці`, example: '{{ "  Привіт ".trim() }}', result: '"Привіт"' },
      { label: '.toUpperCase()', insert: '.toUpperCase()', description: $localize`:@@expr.fn.upper:Великими літерами (.toLowerCase() — малими)`, example: '{{ "тов агро".toUpperCase() }}', result: '"ТОВ АГРО"' },
      { label: '.includes(…)', insert: ".includes('')", description: $localize`:@@expr.fn.includes:Чи містить підрядок`, example: '{{ "Замовлення №12".includes("№") }}', result: 'true' },
      { label: '.replace(…)', insert: ".replace('', '')", description: $localize`:@@expr.fn.replace:Заміна першого входження (.replaceAll — усіх)`, example: '{{ "a-b-c".replaceAll("-", "/") }}', result: '"a/b/c"' },
      { label: '.split(…)', insert: ".split(',')", description: $localize`:@@expr.fn.split:Рядок на масив за роздільником`, example: '{{ "a,b".split(",") }}', result: '["a","b"]' },
      { label: '.slice(…)', insert: '.slice(0, 10)', description: $localize`:@@expr.fn.slice:Частина рядка за індексами`, example: '{{ "2026-10-24T09:30".slice(0, 10) }}', result: '"2026-10-24"' },
      { label: '.padStart(…)', insert: ".padStart(6, '0')", description: $localize`:@@expr.fn.pad:Доповнити зліва до довжини`, example: '{{ String(42).padStart(6, "0") }}', result: '"000042"' },
      { label: '`…${…}`', insert: '`${$json.name}`', description: $localize`:@@expr.fn.template:Шаблонний рядок`, example: '{{ `№${1 + 1}` }}', result: '"№2"' },
    ],
  },
  {
    id: 'number',
    label: $localize`:@@expr.fn.group.number:Числа`,
    functions: [
      { label: 'Math.round(…)', insert: 'Math.round()', description: $localize`:@@expr.fn.round:Округлення до цілого (Math.floor / Math.ceil — вниз / вгору)`, example: '{{ Math.round(12.5) }}', result: '13' },
      { label: '.toFixed(2)', insert: '.toFixed(2)', description: $localize`:@@expr.fn.fixed:Рядок із двома знаками після коми`, example: '{{ (12.345).toFixed(2) }}', result: '"12.35"' },
      { label: 'Number(…)', insert: 'Number()', description: $localize`:@@expr.fn.number:Число з рядка (NaN, якщо не число)`, example: '{{ Number("42") + 1 }}', result: '43' },
      { label: 'Math.max(…)', insert: 'Math.max()', description: $localize`:@@expr.fn.max:Найбільше з чисел (Math.min — найменше)`, example: '{{ Math.max(3, 7, 5) }}', result: '7' },
      { label: 'Math.abs(…)', insert: 'Math.abs()', description: $localize`:@@expr.fn.abs:Модуль числа`, example: '{{ Math.abs(-5) }}', result: '5' },
    ],
  },
  {
    id: 'array',
    label: $localize`:@@expr.fn.group.array:Масиви й об'єкти`,
    functions: [
      { label: '.length', insert: '.length', description: $localize`:@@expr.fn.length:Кількість елементів (або символів рядка)`, example: '{{ [1, 2, 3].length }}', result: '3' },
      { label: '.map(…)', insert: '.map(x => x)', description: $localize`:@@expr.fn.map:Перетворити кожен елемент`, example: '{{ [1, 2].map(x => x * 10) }}', result: '[10,20]' },
      { label: '.filter(…)', insert: '.filter(x => x)', description: $localize`:@@expr.fn.filter:Лише елементи, що задовольняють умову`, example: '{{ [1, 2, 3].filter(x => x > 1) }}', result: '[2,3]' },
      { label: '.find(…)', insert: '.find(x => x)', description: $localize`:@@expr.fn.find:Перший елемент за умовою`, example: '{{ [{ id: 1 }, { id: 2 }].find(x => x.id === 2) }}', result: '{"id":2}' },
      { label: '.reduce(…)', insert: '.reduce((sum, x) => sum + x, 0)', description: $localize`:@@expr.fn.reduce:Згорнути в одне значення (напр. суму)`, example: '{{ [1, 2, 3].reduce((s, x) => s + x, 0) }}', result: '6' },
      { label: '.join(…)', insert: ".join(', ')", description: $localize`:@@expr.fn.join:Масив у рядок через роздільник`, example: '{{ ["a", "b"].join(", ") }}', result: '"a, b"' },
      { label: '$input.all()', insert: '$input.all()', description: $localize`:@@expr.fn.all:Усі вхідні items вузла`, example: '{{ $input.all().length }}', result: '1' },
      { label: 'Object.keys(…)', insert: 'Object.keys($json)', description: $localize`:@@expr.fn.keys:Назви полів об'єкта (Object.values — значення)`, example: '{{ Object.keys({ a: 1, b: 2 }) }}', result: '["a","b"]' },
      { label: 'JSON.stringify(…)', insert: 'JSON.stringify($json)', description: $localize`:@@expr.fn.json:Значення в JSON-рядок (JSON.parse — назад)`, example: '{{ JSON.stringify({ a: 1 }) }}', result: '"{\\"a\\":1}"' },
    ],
  },
];

/** Пошук у каталозі: кожне слово — у підписі, описі чи прикладі. */
export function filterFunctions(groups: readonly ExpressionFunctionGroup[], query: string): ExpressionFunctionGroup[] {
  const words = query.trim().toLocaleLowerCase('uk').split(/\s+/).filter(Boolean);
  if (words.length === 0) return [...groups];
  return groups
    .map((g) => ({
      ...g,
      functions: g.functions.filter((f) => {
        const text = [f.label, f.description, f.example, g.label].join('\n').toLocaleLowerCase('uk');
        return words.every((w) => text.includes(w));
      }),
    }))
    .filter((g) => g.functions.length > 0);
}

/** Значення результату для показу: рядок — як є, решта — JSON з відступами. */
export function formatPreview(value: unknown): string {
  if (typeof value === 'string') return value;
  if (value === undefined) return 'undefined';
  return JSON.stringify(value, null, 2);
}

/** Тип значення результату для підпису. */
export function previewType(value: unknown): string {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}
