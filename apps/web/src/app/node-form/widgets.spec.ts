import { KEYBOARD_FIELD, COMMANDS_FIELD, MULTI_CHOICE_FIELD, VALUE_FIELD, schemaFields } from './schema-fields';
import { FAKE_COMMANDS_TOOL, FAKE_MESSAGE_TOOL } from './test-schemas';
import {
  KeyboardModel,
  commandIssue,
  emptyKeyboard,
  keyboardIssues,
  moveItem,
  parseCommands,
  parseKeyboard,
  parseWidget,
  serializeCommands,
  serializeKeyboard,
  toggleChoice,
} from './widgets';

const INLINE = { inline_keyboard: [[{ text: 'Записатися', callback_data: 'book:s1' }, { text: 'Сайт', url: 'https://example.com' }], [{ text: 'Ні', callback_data: 'no' }]] };
const REPLY = { keyboard: [[{ text: 'Номер', request_contact: true }, { text: 'Де я', request_location: true }], [{ text: 'Меню' }]], resize_keyboard: true, one_time_keyboard: true };

describe('віджети полів (F6-30)', () => {
  it('parseWidget знає keyboard, commands, chat; решта — null', () => {
    expect(parseWidget('keyboard')).toBe('keyboard');
    expect(parseWidget('commands')).toBe('commands');
    expect(parseWidget('chat')).toBe('chat');
    expect(parseWidget('calendar')).toBeNull();
    expect(parseWidget({ type: 'keyboard' })).toBeNull();
  });

  describe('кнопки: розбір і серіалізація', () => {
    it('без значення — «без кнопок», а серіалізація нічого не пише', () => {
      for (const v of [undefined, null, '']) expect(parseKeyboard(v).mode).toBe('none');
      expect(serializeKeyboard(emptyKeyboard())).toBeUndefined();
    });

    it('інлайн: текст, callback_data і url; круг «значення → модель → значення» без втрат', () => {
      const model = parseKeyboard(INLINE);
      expect(model.mode).toBe('inline');
      expect(model.inline).toEqual([
        [{ text: 'Записатися', action: 'callback', value: 'book:s1' }, { text: 'Сайт', action: 'url', value: 'https://example.com' }],
        [{ text: 'Ні', action: 'callback', value: 'no' }],
      ]);
      expect(serializeKeyboard(model)).toEqual(INLINE);
    });

    it('клавіатура: контакт, геолокація, resize і one_time', () => {
      const model = parseKeyboard(REPLY);
      expect(model.mode).toBe('reply');
      expect(model.reply[0]).toEqual([{ text: 'Номер', request: 'contact' }, { text: 'Де я', request: 'location' }]);
      expect(model.resize).toBe(true);
      expect(model.oneTime).toBe(true);
      expect(serializeKeyboard(model)).toEqual(REPLY);
      expect(serializeKeyboard({ ...model, resize: false, oneTime: false })).toEqual({ keyboard: REPLY.keyboard });
    });

    it('кнопка-рядок клавіатури — просто текст', () => {
      expect(parseKeyboard({ keyboard: [['Так', 'Ні']] }).reply).toEqual([[{ text: 'Так', request: 'none' }, { text: 'Ні', request: 'none' }]]);
    });

    it('прибрати клавіатуру', () => {
      expect(parseKeyboard({ remove_keyboard: true }).mode).toBe('remove');
      expect(serializeKeyboard({ ...emptyKeyboard(), mode: 'remove' })).toEqual({ remove_keyboard: true });
    });

    it('рядок з JSON розбирається, вираз і невідоме лишаються «як є»', () => {
      expect(parseKeyboard(JSON.stringify(INLINE)).mode).toBe('inline');
      const expression = parseKeyboard('{{ $json.markup }}');
      expect(expression).toMatchObject({ mode: 'raw', raw: '{{ $json.markup }}' });
      expect(serializeKeyboard(expression)).toBe('{{ $json.markup }}');
      expect(parseKeyboard('не json').mode).toBe('raw');
      // Поля, яких редактор не знає, не губляться: розмітка лишається в режимі «як є».
      const webApp = { inline_keyboard: [[{ text: 'App', web_app: { url: 'https://x.y' } }]] };
      expect(parseKeyboard(webApp)).toMatchObject({ mode: 'raw', raw: webApp });
      const placeholder = { keyboard: [[{ text: 'a' }]], input_field_placeholder: 'пиши' };
      expect(parseKeyboard(placeholder).mode).toBe('raw');
      expect(parseKeyboard(5).mode).toBe('raw');
    });

    it('порожні рядки не потрапляють у значення; без кнопок — undefined', () => {
      const model: KeyboardModel = { ...emptyKeyboard(), mode: 'inline', inline: [[], [{ text: 'A', action: 'callback', value: 'a' }]] };
      expect(serializeKeyboard(model)).toEqual({ inline_keyboard: [[{ text: 'A', callback_data: 'a' }]] });
      expect(serializeKeyboard({ ...model, inline: [[]] })).toBeUndefined();
      expect(serializeKeyboard({ ...model, mode: 'reply', reply: [] })).toBeUndefined();
    });

    it('вирази в текстах і діях зберігаються рядками', () => {
      const markup = { inline_keyboard: [[{ text: '{{ $json.name }}', callback_data: 'pick:{{ $json.id }}' }]] };
      expect(serializeKeyboard(parseKeyboard(markup))).toEqual(markup);
    });
  });

  describe('перевірка кнопок', () => {
    const inline = (...buttons: [string, 'callback' | 'url', string][]): KeyboardModel => ({
      ...emptyKeyboard(),
      mode: 'inline',
      inline: [buttons.map(([text, action, value]) => ({ text, action, value }))],
    });

    it('чисті кнопки без зауважень', () => {
      expect(keyboardIssues(parseKeyboard(INLINE))).toEqual([]);
      expect(keyboardIssues(parseKeyboard(REPLY))).toEqual([]);
      expect(keyboardIssues(emptyKeyboard())).toEqual([]);
    });

    it('порожній текст, callback_data, довгий callback (байти UTF-8), поганий url', () => {
      expect(keyboardIssues(inline(['', 'callback', 'a']))).toEqual(['empty-text']);
      expect(keyboardIssues(inline(['A', 'callback', '']))).toEqual(['empty-callback']);
      expect(keyboardIssues(inline(['A', 'callback', 'я'.repeat(33)]))).toEqual(['long-callback']);
      expect(keyboardIssues(inline(['A', 'callback', 'я'.repeat(32)]))).toEqual([]);
      expect(keyboardIssues(inline(['A', 'url', 'example.com']))).toEqual(['bad-url']);
      expect(keyboardIssues(inline(['A', 'url', 'tg://resolve?domain=x']))).toEqual([]);
    });

    it('вираз у дії не міряється', () => {
      expect(keyboardIssues(inline(['A', 'callback', '{{ $json.veryLongValue.repeat(100) }}'], ['B', 'url', '{{ $json.link }}']))).toEqual([]);
    });

    it('більше 8 кнопок у інлайн-рядку і більше 100 усього', () => {
      const row = Array.from({ length: 9 }, () => ({ text: 'x', action: 'callback' as const, value: 'x' }));
      expect(keyboardIssues({ ...emptyKeyboard(), mode: 'inline', inline: [row] })).toEqual(['long-row']);
      const many: KeyboardModel = { ...emptyKeyboard(), mode: 'reply', reply: Array.from({ length: 101 }, () => [{ text: 'x', request: 'none' as const }]) };
      expect(keyboardIssues(many)).toEqual(['too-many']);
    });
  });

  it('moveItem зсуває елемент і не виходить за межі', () => {
    expect(moveItem([1, 2, 3], 0, 1)).toEqual([2, 1, 3]);
    expect(moveItem([1, 2, 3], 2, -1)).toEqual([1, 3, 2]);
    expect(moveItem([1, 2, 3], 0, -1)).toEqual([1, 2, 3]);
    expect(moveItem([1, 2, 3], 2, 1)).toEqual([1, 2, 3]);
  });

  describe('команди меню', () => {
    it('розбір, серіалізація, порожні рядки відкидаються', () => {
      expect(parseCommands([{ command: 'start', description: 'Головне меню' }])).toEqual([{ command: 'start', description: 'Головне меню' }]);
      expect(parseCommands(undefined)).toEqual([]);
      expect(parseCommands('{{ $json.commands }}')).toBeNull();
      expect(parseCommands([1])).toBeNull();
      expect(serializeCommands([{ command: '', description: '' }])).toBeUndefined();
      expect(serializeCommands([{ command: 'start', description: 'Меню' }, { command: '', description: '' }])).toEqual([{ command: 'start', description: 'Меню' }]);
    });

    it('правила Bot API: команда — малі латинські літери, цифри, _; опис — 3–256', () => {
      expect(commandIssue({ command: 'start', description: 'Меню' })).toBeNull();
      expect(commandIssue({ command: 'Start', description: 'Меню' })).toBe('command');
      expect(commandIssue({ command: '/start', description: 'Меню' })).toBe('command');
      expect(commandIssue({ command: 'a'.repeat(33), description: 'Меню' })).toBe('command');
      expect(commandIssue({ command: 'start', description: 'ab' })).toBe('description');
      expect(commandIssue({ command: '{{ $json.c }}', description: 'Меню' })).toBeNull();
    });
  });

  it('toggleChoice тримає порядок переліку і лишає невідомі значення', () => {
    const options = ['message', 'callback_query', 'channel_post'];
    expect(toggleChoice(undefined, 'callback_query', options, true)).toEqual(['callback_query']);
    expect(toggleChoice(['callback_query'], 'message', options, true)).toEqual(['message', 'callback_query']);
    expect(toggleChoice(['message', 'legacy'], 'message', options, false)).toEqual(['legacy']);
    expect(toggleChoice(['message'], 'message', options, false)).toBeUndefined();
  });

  describe('схема tool → поля', () => {
    it('x-openbp-widget: keyboard дає поле кнопок, chat лишає список чатів', () => {
      const fields = schemaFields(FAKE_MESSAGE_TOOL.inputSchema);
      expect(fields.map((f) => [f.key, f.type])).toEqual([
        ['chat_id', VALUE_FIELD],
        ['text', VALUE_FIELD],
        ['parse_mode', VALUE_FIELD],
        ['reply_markup', KEYBOARD_FIELD],
      ]);
      expect(fields[3].props?.['label']).toBe('reply_markup');
      expect(fields[3].props?.['description']).toBe('Кнопки');
    });

    it('maxLength дає лічильник; довгий текст — багаторядковий, parse_mode — список', () => {
      const [, text, parse] = schemaFields(FAKE_MESSAGE_TOOL.inputSchema);
      expect(text.props).toMatchObject({ maxChars: 4096, multiline: true, required: true });
      expect((parse.props?.['rules'] as { kind: string; options: string[] }).options).toEqual(['HTML', 'MarkdownV2', 'Markdown']);
      const short = schemaFields({ type: 'object', properties: { caption: { type: 'string', maxLength: 100 } } })[0];
      expect(short.props).toMatchObject({ maxChars: 100, multiline: false });
    });

    it('x-openbp-widget: commands — таблиця; масив з items.enum — множинний вибір', () => {
      const fields = schemaFields(FAKE_COMMANDS_TOOL.inputSchema);
      expect(fields.map((f) => [f.key, f.type])).toEqual([
        ['commands', COMMANDS_FIELD],
        ['allowed_updates', MULTI_CHOICE_FIELD],
      ]);
      expect((fields[1].props?.['rules'] as { options: string[] }).options).toEqual(['message', 'callback_query', 'channel_post']);
    });

    it('невідомий віджет ігнорується: поле редагується звичайним способом', () => {
      const [field] = schemaFields({ type: 'object', properties: { x: { type: 'string', 'x-openbp-widget': 'calendar' } } });
      expect(field.type).toBe(VALUE_FIELD);
    });
  });
});
