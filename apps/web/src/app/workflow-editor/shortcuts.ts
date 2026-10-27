/** Гарячі клавіші редактора для довідки (F3-90). Джерело правди — `keydown` у `workflow-editor.component.ts`: змінюєте там — змінюйте тут. */
export interface Shortcut {
  /** Клавіші; кілька варіантів одного дії — окремі елементи («Tab», «N»). `Mod` — Ctrl, на Mac — ⌘. */
  keys: readonly string[];
  action: string;
}

export interface ShortcutGroup {
  id: string;
  title: string;
  shortcuts: readonly Shortcut[];
}

/** Групи за діями; функція — щоб `$localize` викликався вже з перекладами. */
export function shortcutGroups(): readonly ShortcutGroup[] {
  return [
    {
      id: 'add',
      title: $localize`:@@shortcuts.group.add:Додавання`,
      shortcuts: [
        { keys: ['Tab', 'N'], action: $localize`:@@shortcuts.add.step:Додати крок (після виділеного вузла)` },
        { keys: ['Shift+S'], action: $localize`:@@shortcuts.add.note:Додати нотатку` },
        { keys: ['Mod+V'], action: $localize`:@@shortcuts.add.paste:Вставити скопійовані вузли` },
      ],
    },
    {
      id: 'select',
      title: $localize`:@@shortcuts.group.select:Виділення`,
      shortcuts: [
        { keys: ['Mod+A'], action: $localize`:@@shortcuts.select.all:Виділити все` },
        { keys: ['←', '→', '↑', '↓'], action: $localize`:@@shortcuts.select.move:Перейти на сусідній вузол` },
        { keys: ['Esc'], action: $localize`:@@shortcuts.select.close:Закрити панель вузла` },
      ],
    },
    {
      id: 'edit',
      title: $localize`:@@shortcuts.group.edit:Редагування`,
      shortcuts: [
        { keys: ['Mod+Z'], action: $localize`:@@shortcuts.edit.undo:Скасувати` },
        { keys: ['Mod+Shift+Z', 'Mod+Y'], action: $localize`:@@shortcuts.edit.redo:Повторити` },
        { keys: ['Mod+C'], action: $localize`:@@shortcuts.edit.copy:Копіювати вузли` },
        { keys: ['Mod+D'], action: $localize`:@@shortcuts.edit.duplicate:Дублювати вузли` },
        { keys: ['D'], action: $localize`:@@shortcuts.edit.disable:Вимкнути або увімкнути вузол` },
        { keys: ['F2'], action: $localize`:@@shortcuts.edit.rename:Перейменувати вузол` },
        { keys: ['P'], action: $localize`:@@shortcuts.edit.pin:Закріпити або відкріпити output вузла` },
        { keys: ['Delete', 'Backspace'], action: $localize`:@@shortcuts.edit.delete:Видалити виділене` },
      ],
    },
    {
      id: 'run',
      title: $localize`:@@shortcuts.group.run:Запуск`,
      shortcuts: [{ keys: ['Mod+Enter'], action: $localize`:@@shortcuts.run.run:Виконати сценарій` }],
    },
    {
      id: 'canvas',
      title: $localize`:@@shortcuts.group.canvas:Полотно`,
      shortcuts: [
        { keys: ['1'], action: $localize`:@@shortcuts.canvas.fit:Показати весь сценарій` },
        { keys: ['0'], action: $localize`:@@shortcuts.canvas.reset:Масштаб 100 %` },
        { keys: ['+', '−'], action: $localize`:@@shortcuts.canvas.zoom:Збільшити або зменшити` },
        { keys: ['?'], action: $localize`:@@shortcuts.canvas.help:Ця довідка` },
      ],
    },
  ];
}

/** Підпис клавіші для екрана: `Mod` → ⌘ на Mac, інакше Ctrl. */
export function keyLabel(key: string, mac: boolean): string {
  return key.split('+').map((part) => (part === 'Mod' ? (mac ? '⌘' : 'Ctrl') : part)).join('+');
}

/** Клавіші масштабу: `+`/`=` — ближче, `-`/`_` — далі, `0` — 100 %, `1` — вписати. Інше — `null`. */
export type ZoomKey = 'in' | 'out' | 'reset' | 'fit';

export function zoomKey(event: Pick<KeyboardEvent, 'key' | 'ctrlKey' | 'metaKey' | 'altKey'>): ZoomKey | null {
  if (event.ctrlKey || event.metaKey || event.altKey) return null;
  switch (event.key) {
    case '+':
    case '=':
      return 'in';
    case '-':
    case '_':
      return 'out';
    case '0':
      return 'reset';
    case '1':
      return 'fit';
    default:
      return null;
  }
}
