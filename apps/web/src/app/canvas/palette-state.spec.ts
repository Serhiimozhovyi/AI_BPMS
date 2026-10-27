import { PALETTE_OPEN_KEY, paletteExpanded, readPaletteChoice, writePaletteChoice } from './palette-state';

function memoryStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial));
  return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
}

describe('palette-state (F3-89)', () => {
  it('вибір запам’ятовується; без запису чи зі сміттям — null', () => {
    const storage = memoryStorage();
    expect(readPaletteChoice(storage)).toBeNull();
    writePaletteChoice(true, storage);
    expect(readPaletteChoice(storage)).toBe(true);
    writePaletteChoice(false, storage);
    expect(readPaletteChoice(storage)).toBe(false);
    expect(readPaletteChoice(memoryStorage({ [PALETTE_OPEN_KEY]: 'абракадабра' }))).toBeNull();
  });

  it('недоступне сховище не ламає редактор', () => {
    const broken = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      },
    };
    expect(readPaletteChoice(broken)).toBeNull();
    expect(() => writePaletteChoice(true, broken)).not.toThrow();
  });

  it('типове значення діє, лише поки користувач не обирав', () => {
    expect(paletteExpanded(null, true)).toBe(true);
    expect(paletteExpanded(null, false)).toBe(false);
    expect(paletteExpanded(false, true)).toBe(false);
    expect(paletteExpanded(true, false)).toBe(true);
  });
});
