import { OPEN_MIN_ZOOM, contentBounds, fitViewport, loadViewport, openViewport, revealBox, saveViewport, viewportShowsContent } from './viewport';

function memoryStorage() {
  const data = new Map<string, string>();
  return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
}

describe('viewport', () => {
  it('contentBounds: охоплює всі прямокутники, порожньо → null', () => {
    expect(contentBounds([])).toBeNull();
    expect(
      contentBounds([
        { x: 0, y: 0, width: 220, height: 80 },
        { x: 1000, y: 200, width: 220, height: 80 },
      ]),
    ).toEqual({ x: 0, y: 0, width: 1220, height: 280 });
  });

  it('fitViewport: довгий сценарій зменшується, щоб вміститися з відступом 48 px', () => {
    const v = fitViewport({ x: 0, y: 0, width: 1800, height: 200 }, { width: 1000, height: 600 });
    expect(v.zoom).toBeCloseTo((1000 - 96) / 1800, 5);
    // По центру по вертикалі, відступ зліва = 48.
    expect(v.x).toBeCloseTo(48, 5);
    expect(v.y).toBeCloseTo((600 - 200 * v.zoom) / 2, 5);
  });

  it('fitViewport: короткий сценарій не збільшується понад 100 % і стоїть по центру', () => {
    const v = fitViewport({ x: 100, y: 100, width: 220, height: 80 }, { width: 1000, height: 600 });
    expect(v.zoom).toBe(1);
    expect(v.x).toBeCloseTo((1000 - 220) / 2 - 100, 5);
    expect(v.y).toBeCloseTo((600 - 80) / 2 - 100, 5);
  });

  it('fitViewport: масштаб не менший за мінімальний', () => {
    expect(fitViewport({ x: 0, y: 0, width: 100000, height: 100 }, { width: 800, height: 600 }).zoom).toBe(0.25);
  });

  it('save/load: круговий обхід, пошкоджене значення → null', () => {
    const storage = memoryStorage();
    saveViewport('w1', { x: 10, y: -20, zoom: 0.8 }, storage);
    expect(loadViewport('w1', storage)).toEqual({ x: 10, y: -20, zoom: 0.8 });
    expect(loadViewport('other', storage)).toBeNull();
    storage.setItem('openbp.viewport.bad', '{"x":"a"}');
    expect(loadViewport('bad', storage)).toBeNull();
    storage.setItem('openbp.viewport.far', JSON.stringify({ x: 0, y: 0, zoom: 50 }));
    expect(loadViewport('far', storage)).toBeNull();
  });

  it('save/load: недоступне сховище не кидає', () => {
    const broken = {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    };
    expect(() => saveViewport('w', { x: 0, y: 0, zoom: 1 }, broken)).not.toThrow();
    expect(loadViewport('w', broken)).toBeNull();
  });

  it('viewportShowsContent: вміст за межами екрана → false', () => {
    const bounds = { x: 0, y: 0, width: 400, height: 200 };
    const size = { width: 1000, height: 600 };
    expect(viewportShowsContent({ x: 50, y: 50, zoom: 1 }, bounds, size)).toBe(true);
    expect(viewportShowsContent({ x: -5000, y: 50, zoom: 1 }, bounds, size)).toBe(false);
  });

  describe('revealBox (F3-88)', () => {
    const size = { width: 600, height: 500 };
    const view = { x: 0, y: 0, zoom: 1 };

    it('вузол, що вже видно, не зсуває полотно', () => {
      expect(revealBox(view, { x: 100, y: 100, width: 220, height: 80 }, size, 32)).toBeNull();
    });

    it('вузол за правим краєм (панель звузила полотно) підсувається ліворуч з відступом, масштаб не змінюється', () => {
      const next = revealBox(view, { x: 700, y: 100, width: 220, height: 80 }, size, 32)!;
      expect(next.zoom).toBe(1);
      expect(next.y).toBe(0);
      // Праву межу вузла (920) ставимо на 600 − 32.
      expect(next.x).toBe(568 - 920);
    });

    it('вузол за лівим і нижнім краєм підсувається праворуч і вгору; масштаб враховується', () => {
      const zoomed = { x: -300, y: 0, zoom: 0.5 };
      const next = revealBox(zoomed, { x: 100, y: 1200, width: 220, height: 80 }, size, 32)!;
      // Лівий край вузла стоїть на екрані в −250: підсуваємо на 282 до відступу 32.
      expect(next.x).toBe(-300 + 282);
      expect(next.y).toBe(500 - 32 - 1280 * 0.5);
    });

    it('вузол ширший за видиму зону притискається лівим краєм', () => {
      const next = revealBox(view, { x: 400, y: 100, width: 800, height: 80 }, { width: 500, height: 500 }, 32)!;
      expect(next.x).toBe(32 - 400);
    });
  });

  describe('openViewport (F3-91)', () => {
    const size = { width: 1200, height: 700 };

    it('короткий сценарій вписується повністю', () => {
      const bounds = { x: 0, y: 0, width: 800, height: 200 };
      expect(openViewport(bounds, { x: 0, y: 0, width: 220, height: 80 }, size)).toEqual(fitViewport(bounds, size));
    });

    it('довгий сценарій відкривається з початку на масштабі не менше 0,7: тригер зліва по центру висоти', () => {
      const bounds = { x: 0, y: 0, width: 4000, height: 200 };
      expect(fitViewport(bounds, size).zoom).toBeLessThan(OPEN_MIN_ZOOM);
      const view = openViewport(bounds, { x: 100, y: 40, width: 220, height: 80 }, size);
      expect(view.zoom).toBe(OPEN_MIN_ZOOM);
      expect(view.x).toBe(48 - 100 * OPEN_MIN_ZOOM);
      expect(view.y).toBe(350 - 80 * OPEN_MIN_ZOOM);
    });
  });
});
