/**
 * Глобальні заглушки для jsdom у Vitest: ngx-vflow використовує ResizeObserver,
 * якого в jsdom немає (докладніше — docs/adr/ADR-003-canvas.md).
 */
if (typeof globalThis.ResizeObserver === 'undefined') {
  class ResizeObserverStub implements ResizeObserver {
    observe(): void {
      // jsdom не змінює розміри — спостерігати нема за чим.
    }

    unobserve(): void {
      // див. observe
    }

    disconnect(): void {
      // див. observe
    }
  }

  globalThis.ResizeObserver = ResizeObserverStub;
}

// Шар віртуалізації ngx-vflow малює на <canvas>; jsdom без пакета `canvas` повертає null з getContext.
if (typeof HTMLCanvasElement !== 'undefined' && HTMLCanvasElement.prototype.getContext.call(document.createElement('canvas'), '2d') === null) {
  const noop = () => undefined;
  HTMLCanvasElement.prototype.getContext = function getContextStub() {
    return new Proxy({} as Record<string | symbol, unknown>, {
      get: (target, key) => (key in target ? target[key] : noop),
    }) as unknown as RenderingContext;
  } as typeof HTMLCanvasElement.prototype.getContext;
}
