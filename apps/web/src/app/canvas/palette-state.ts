/** Ключ `localStorage` з вибором користувача: `1` — палітра розгорнута, `0` — згорнута в смугу (F3-89). */
export const PALETTE_OPEN_KEY = 'openbp.editor.palette';

/** Вибір користувача; `null` — не обирав (або сховище недоступне): тоді діє типове значення. */
export function readPaletteChoice(storage: Pick<Storage, 'getItem'> | null = safeStorage()): boolean | null {
  try {
    const value = storage?.getItem(PALETTE_OPEN_KEY);
    return value === '1' ? true : value === '0' ? false : null;
  } catch {
    return null;
  }
}

export function writePaletteChoice(open: boolean, storage: Pick<Storage, 'setItem'> | null = safeStorage()): void {
  try {
    storage?.setItem(PALETTE_OPEN_KEY, open ? '1' : '0');
  } catch {
    // Сховище недоступне — вибір діє до перезавантаження.
  }
}

/**
 * Чи розгорнута палітра: вибір користувача, а без нього — типове значення, яке редактор визначає один раз при відкритті
 * сценарію (розгорнута в порожньому, згорнута, коли вузли вже є), щоб палітра не згорталася, коли додається перший вузол.
 */
export function paletteExpanded(choice: boolean | null, defaultOpen: boolean): boolean {
  return choice ?? defaultOpen;
}

function safeStorage(): Storage | null {
  try {
    return localStorage;
  } catch {
    return null;
  }
}
