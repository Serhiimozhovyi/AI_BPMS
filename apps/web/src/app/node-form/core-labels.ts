import type { JsonSchema } from './schema-fields';

/**
 * Людські підписи полів і значень списків вузлів `core.*` (F3-87). Схеми вузлів — мова машини (`list`, `batchSize`, `eachItem`),
 * тож тут, поруч із формою, — підписи для бізнес-користувача. Ключ — шлях властивості в схемі (`form.title`), `$ref` розкриваються.
 * Підпис із самої схеми (`title`) має пріоритет; поля, які малюють власні віджети (умови, присвоєння, таблиця рішень), тут не потрібні.
 * Підписи полів конекторів (`mcp.tool`) дає схема tool і сюди не потрапляє.
 */
export interface FieldLabel {
  label?: string;
  /** Підписи значень `enum`, за значенням. */
  values?: Readonly<Record<string, string>>;
}

type Labels = Readonly<Record<string, FieldLabel>>;

const ROLE_VALUES = (): Record<string, string> => ({
  viewer: $localize`:@@enum.role.viewer:Переглядач (і вище)`,
  editor: $localize`:@@enum.role.editor:Редактор (і вище)`,
  admin: $localize`:@@enum.role.admin:Адміністратор`,
});

const ASSIGNEE = (): Labels => ({
  assignee: { label: $localize`:@@field.approval.assignee:Кому задача` },
  'assignee.userId': { label: $localize`:@@field.approval.userId:Користувач` },
  'assignee.role': { label: $localize`:@@field.approval.role:Роль`, values: ROLE_VALUES() },
  'assignee.expression': { label: $localize`:@@field.approval.expression:Email або зовнішній id` },
});

const ESCALATE = (): Labels => ({
  escalateTo: { label: $localize`:@@field.approval.escalateTo:Кому передати, якщо ніхто не вирішив` },
  'escalateTo.userId': { label: $localize`:@@field.approval.userId:Користувач` },
  'escalateTo.role': { label: $localize`:@@field.approval.role:Роль`, values: ROLE_VALUES() },
  'escalateTo.expression': { label: $localize`:@@field.approval.expression:Email або зовнішній id` },
});

/** Підписи по типу вузла; функція — щоб `$localize` викликався вже після завантаження перекладів. */
const BY_TYPE: Readonly<Record<string, () => Labels>> = {
  'core.approval': () => ({
    title: { label: $localize`:@@field.approval.title:Заголовок задачі` },
    description: { label: $localize`:@@field.approval.description:Що треба вирішити` },
    decisions: { label: $localize`:@@field.approval.decisions:Варіанти рішення` },
    'decisions.key': { label: $localize`:@@field.approval.decisionKey:Код рішення` },
    'decisions.label': { label: $localize`:@@field.approval.decisionLabel:Підпис кнопки` },
    fields: { label: $localize`:@@field.approval.fields:Поля до рішення (JSON Schema)` },
    maxWaitSec: { label: $localize`:@@field.approval.maxWaitSec:Скільки секунд чекати рішення` },
    ...ASSIGNEE(),
    ...ESCALATE(),
  }),
  'core.code': () => ({
    code: { label: $localize`:@@field.code.code:Код (JavaScript)` },
    mode: {
      label: $localize`:@@field.code.mode:Як виконувати`,
      values: {
        eachItem: $localize`:@@enum.code.mode.eachItem:Для кожного елемента окремо`,
        allItems: $localize`:@@enum.code.mode.allItems:Для всіх елементів разом`,
      },
    },
  }),
  'core.dataTable': () => ({
    table: { label: $localize`:@@field.dataTable.table:Таблиця` },
    operation: {
      label: $localize`:@@field.dataTable.operation:Дія`,
      values: {
        get: $localize`:@@enum.dataTable.get:Отримати рядок`,
        list: $localize`:@@enum.dataTable.list:Отримати список`,
        count: $localize`:@@enum.dataTable.count:Порахувати рядки`,
        insert: $localize`:@@enum.dataTable.insert:Додати рядок`,
        insertIf: $localize`:@@enum.dataTable.insertIf:Додати, якщо є місце`,
        upsert: $localize`:@@enum.dataTable.upsert:Додати або оновити`,
        update: $localize`:@@enum.dataTable.update:Оновити рядки`,
        increment: $localize`:@@enum.dataTable.increment:Збільшити число`,
        delete: $localize`:@@enum.dataTable.delete:Видалити рядки`,
      },
    },
    key: { label: $localize`:@@field.dataTable.key:Ключ рядка` },
    where: { label: $localize`:@@field.dataTable.where:Умова відбору` },
    'where.column': { label: $localize`:@@field.dataTable.column:Колонка` },
    'where.operator': {
      label: $localize`:@@field.dataTable.operator:Оператор`,
      values: {
        eq: $localize`:@@enum.dataTable.eq:дорівнює`,
        ne: $localize`:@@enum.dataTable.ne:не дорівнює`,
        gt: $localize`:@@enum.dataTable.gt:більше`,
        gte: $localize`:@@enum.dataTable.gte:більше або дорівнює`,
        lt: $localize`:@@enum.dataTable.lt:менше`,
        lte: $localize`:@@enum.dataTable.lte:менше або дорівнює`,
        contains: $localize`:@@enum.dataTable.contains:містить`,
        isEmpty: $localize`:@@enum.dataTable.isEmpty:порожнє`,
        isNotEmpty: $localize`:@@enum.dataTable.isNotEmpty:не порожнє`,
      },
    },
    'where.value': { label: $localize`:@@field.dataTable.value:Значення` },
    values: { label: $localize`:@@field.dataTable.values:Значення колонок` },
    limit: { label: $localize`:@@field.dataTable.limit:Не більше рядків` },
    maxCount: { label: $localize`:@@field.dataTable.maxCount:Місткість (рядків за умовою)` },
    column: { label: $localize`:@@field.dataTable.incrementColumn:Числова колонка` },
    by: { label: $localize`:@@field.dataTable.by:На скільки` },
  }),
  'core.decisionTable': () => ({
    hitPolicy: {
      values: {
        first: $localize`:@@enum.decisionTable.first:Перше правило, що підійшло`,
        all: $localize`:@@enum.decisionTable.all:Усі правила, що підійшли`,
        collect: $localize`:@@enum.decisionTable.collect:Зібрати результати всіх правил`,
      },
    },
  }),
  'core.executeWorkflow': () => ({
    mode: {
      values: {
        wait: $localize`:@@enum.executeWorkflow.wait:Чекати завершення підсценарію`,
        fireAndForget: $localize`:@@enum.executeWorkflow.fireAndForget:Запустити й не чекати`,
      },
    },
  }),
  'core.formTrigger': () => ({
    auth: {
      values: {
        none: $localize`:@@enum.formTrigger.none:Будь-хто за посиланням`,
        login: $localize`:@@enum.formTrigger.login:Користувачі AI_BPMS`,
        token: $localize`:@@enum.formTrigger.token:За токеном`,
      },
    },
  }),
  'core.loop': () => ({
    list: { label: $localize`:@@field.loop.list:Список для перебору` },
    batchSize: { label: $localize`:@@field.loop.batchSize:Скільки елементів за раз` },
  }),
  'core.merge': () => ({
    mode: {
      label: $localize`:@@field.merge.mode:Коли об'єднувати`,
      values: {
        all: $localize`:@@enum.merge.all:Коли прийдуть усі гілки`,
        any: $localize`:@@enum.merge.any:Коли прийде будь-яка гілка`,
        count: $localize`:@@enum.merge.count:Коли прийде задана кількість гілок`,
        append: $localize`:@@enum.merge.append:Дописати одне за одним`,
        combineByPosition: $localize`:@@enum.merge.combineByPosition:Скласти за порядковим номером`,
      },
    },
    count: { label: $localize`:@@field.merge.count:Скільки гілок чекати` },
    branches: { label: $localize`:@@field.merge.branches:Кількість гілок` },
    combine: {
      label: $localize`:@@field.merge.combine:Як скласти дані`,
      values: {
        append: $localize`:@@enum.merge.append:Дописати одне за одним`,
        combineByPosition: $localize`:@@enum.merge.combineByPosition:Скласти за порядковим номером`,
      },
    },
  }),
  'core.set': () => ({
    keepOnlySet: { label: $localize`:@@field.set.keepOnlySet:Залишити лише ці поля` },
  }),
  'core.wait': () => ({
    mode: {
      label: $localize`:@@field.wait.mode:Чого чекати`,
      values: {
        duration: $localize`:@@enum.wait.duration:Заданий час`,
        until: $localize`:@@enum.wait.until:Дати й часу`,
        webhook: $localize`:@@enum.wait.webhook:Виклику за посиланням`,
        form: $localize`:@@enum.wait.form:Заповнення форми`,
      },
    },
    amount: { label: $localize`:@@field.wait.amount:Скільки` },
    unit: {
      label: $localize`:@@field.wait.unit:Одиниця`,
      values: {
        milliseconds: $localize`:@@enum.wait.milliseconds:мілісекунди`,
        seconds: $localize`:@@enum.wait.seconds:секунди`,
        minutes: $localize`:@@enum.wait.minutes:хвилини`,
        hours: $localize`:@@enum.wait.hours:години`,
        days: $localize`:@@enum.wait.days:дні`,
      },
    },
    until: { label: $localize`:@@field.wait.until:Дата й час` },
    maxWaitSec: { label: $localize`:@@field.wait.maxWaitSec:Скільки секунд чекати щонайбільше` },
    form: { label: $localize`:@@field.wait.form:Форма` },
    'form.title': { label: $localize`:@@field.wait.formTitle:Заголовок форми` },
    'form.description': { label: $localize`:@@field.wait.formDescription:Текст над полями` },
    'form.schema': { label: $localize`:@@field.wait.formSchema:Поля форми (JSON Schema)` },
    'form.submitLabel': { label: $localize`:@@field.wait.formSubmit:Текст кнопки` },
    'form.message': { label: $localize`:@@field.wait.formMessage:Текст після надсилання` },
  }),
  'core.webhook': () => ({
    respond: {
      values: {
        immediately: $localize`:@@enum.webhook.immediately:Одразу, не чекаючи сценарію`,
        whenFinished: $localize`:@@enum.webhook.whenFinished:Коли сценарій завершиться`,
        fromNode: $localize`:@@enum.webhook.fromNode:З вузла «Відповідь webhook-у»`,
      },
    },
    auth: { label: $localize`:@@field.webhook.auth:Автентифікація` },
    'auth.type': {
      label: $localize`:@@field.webhook.authType:Спосіб`,
      values: {
        header: $localize`:@@enum.webhook.header:Секрет у заголовку`,
        basic: $localize`:@@enum.webhook.basic:Логін і пароль (Basic)`,
      },
    },
    'auth.credentialId': { label: $localize`:@@field.webhook.credentialId:Облікові дані` },
  }),
};

/** Підписи для типу вузла (порожньо для типів без словника — `mcp.*`, `ai.*` тощо). */
export function coreLabels(type: string): Labels {
  return BY_TYPE[type]?.() ?? {};
}

/** Типи вузлів, для яких є словник, — для тесту повноти. */
export const LABELLED_TYPES: readonly string[] = Object.keys(BY_TYPE);

/** Анотація, якою схема несе підписи значень списку до `typedField` (не частина JSON Schema). */
export const ENUM_LABELS_ANNOTATION = 'x-openbp-enum-labels';

/**
 * Копія схеми з підставленими підписами: `title` — лише там, де його немає, `x-openbp-enum-labels` — підписи значень
 * у порядку `enum`. `$ref` усередині розкриваються (схеми `core.*` без циклів).
 */
export function withCoreLabels(type: string, schema: JsonSchema): JsonSchema {
  const labels = coreLabels(type);
  if (Object.keys(labels).length === 0) return schema;
  return decorate(schema, schema, labels, '');
}

function decorate(node: unknown, root: JsonSchema, labels: Labels, path: string): JsonSchema {
  const schema = deref(node, root);
  const out: JsonSchema = { ...schema };
  const entry = path ? labels[path] : undefined;
  if (entry?.label && typeof out['title'] !== 'string') out['title'] = entry.label;
  if (entry?.values && Array.isArray(out['enum'])) out[ENUM_LABELS_ANNOTATION] = out['enum'].map((v) => entry.values?.[String(v)] ?? String(v));
  const properties = out['properties'];
  if (properties && typeof properties === 'object') {
    const prefix = path ? `${path}.` : '';
    out['properties'] = Object.fromEntries(Object.entries(properties as Record<string, unknown>).map(([k, v]) => [k, decorate(v, root, labels, prefix + k)]));
  }
  if (out['items'] && typeof out['items'] === 'object') out['items'] = decorate(out['items'], root, labels, path);
  return out;
}

/** Розкриває локальний `$ref` (`#/$defs/x`); решта ключів поруч із `$ref` перекриває розкрите. */
function deref(node: unknown, root: JsonSchema): JsonSchema {
  if (!node || typeof node !== 'object') return {};
  const schema = node as JsonSchema;
  const ref = schema['$ref'];
  if (typeof ref !== 'string' || !ref.startsWith('#/')) return schema;
  let target: unknown = root;
  for (const part of ref.slice(2).split('/')) target = (target as Record<string, unknown> | undefined)?.[part];
  const rest = { ...schema };
  delete rest['$ref'];
  return { ...(target && typeof target === 'object' ? (target as JsonSchema) : {}), ...rest };
}
