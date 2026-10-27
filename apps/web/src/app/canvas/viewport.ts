/** Положення і масштаб полотна: `x`/`y` — зсув у пікселях екрана, як у d3-zoom (`ViewportState` ngx-vflow). */
export interface Viewport {
  x: number;
  y: number;
  zoom: number;
}

/** Прямокутник у координатах сценарію. */
export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FitOptions {
  /** Відступ від країв полотна, px. */
  padding?: number;
  minZoom?: number;
  maxZoom?: number;
}

/** Відступ при відкритті сценарію (F3-76) і найбільший масштаб, щоб короткий сценарій не роздувався. */
export const FIT_PADDING = 48;
export const FIT_MAX_ZOOM = 1;
export const MIN_ZOOM = 0.25;
export const MAX_ZOOM = 2;

/** Найменший прямокутник, що вміщує всі `boxes`; `null` для порожнього списку. */
export function contentBounds(boxes: readonly Box[]): Box | null {
  if (boxes.length === 0) return null;
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const b of boxes) {
    minX = Math.min(minX, b.x);
    minY = Math.min(minY, b.y);
    maxX = Math.max(maxX, b.x + b.width);
    maxY = Math.max(maxY, b.y + b.height);
  }
  return { x: minX, y: minY, width: maxX - minX, height: maxY - minY };
}

/** Вписує `bounds` у полотно `width`×`height` з відступом, по центру; масштаб не більший за `maxZoom` (за замовчуванням 100 %). */
export function fitViewport(bounds: Box, size: { width: number; height: number }, options: FitOptions = {}): Viewport {
  const { padding = FIT_PADDING, minZoom = MIN_ZOOM, maxZoom = FIT_MAX_ZOOM } = options;
  const availableW = Math.max(1, size.width - padding * 2);
  const availableH = Math.max(1, size.height - padding * 2);
  const fit = Math.min(availableW / Math.max(1, bounds.width), availableH / Math.max(1, bounds.height));
  const zoom = Math.min(maxZoom, Math.max(minZoom, fit));
  return {
    zoom,
    x: (size.width - bounds.width * zoom) / 2 - bounds.x * zoom,
    y: (size.height - bounds.height * zoom) / 2 - bounds.y * zoom,
  };
}

const STORAGE_PREFIX = 'openbp.viewport.';

/** Збережене положення сценарію; пошкоджене чи відсутнє значення (або недоступний `localStorage`) → `null`. */
export function loadViewport(key: string, storage: Pick<Storage, 'getItem'> | null = safeStorage()): Viewport | null {
  try {
    const raw = storage?.getItem(STORAGE_PREFIX + key);
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<Viewport>;
    const { x, y, zoom } = value;
    if (![x, y, zoom].every((n) => typeof n === 'number' && Number.isFinite(n)) || zoom! < MIN_ZOOM || zoom! > MAX_ZOOM) return null;
    return { x: x!, y: y!, zoom: zoom! };
  } catch {
    return null;
  }
}

export function saveViewport(key: string, viewport: Viewport, storage: Pick<Storage, 'setItem'> | null = safeStorage()): void {
  try {
    storage?.setItem(STORAGE_PREFIX + key, JSON.stringify(viewport));
  } catch {
    // Приватне вікно чи переповнене сховище: положення просто не запам'ятається.
  }
}

/**
 * Чи видно збережений `viewport` хоча б частиною вмісту — інакше (сценарій змінили в іншій вкладці, вузли далеко)
 * краще вписати все, ніж показати порожнє полотно.
 */
export function viewportShowsContent(viewport: Viewport, bounds: Box, size: { width: number; height: number }): boolean {
  const left = bounds.x * viewport.zoom + viewport.x;
  const top = bounds.y * viewport.zoom + viewport.y;
  const right = left + bounds.width * viewport.zoom;
  const bottom = top + bounds.height * viewport.zoom;
  return right > 0 && bottom > 0 && left < size.width && top < size.height;
}

function safeStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage;
  } catch {
    return null;
  }
}
