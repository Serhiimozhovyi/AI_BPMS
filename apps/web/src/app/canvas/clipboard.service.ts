import { Injectable, signal } from '@angular/core';
import { CanvasClipboard } from './canvas-edit';

/**
 * Буфер копіювання канвасу на час сесії вкладки: переживає перехід між сценаріями,
 * тож вузли можна скопіювати з одного сценарію і вставити в інший.
 */
@Injectable({ providedIn: 'root' })
export class CanvasClipboardService {
  private readonly content = signal<CanvasClipboard | null>(null);
  private pastes = 0;

  readonly clipboard = this.content.asReadonly();

  set(clipboard: CanvasClipboard): void {
    this.content.set(clipboard);
    this.pastes = 0;
  }

  /** Номер чергової вставки поточного вмісту (1, 2, …) — для зсуву копій, щоб вони не лягали одна на одну. */
  nextPaste(): number {
    return ++this.pastes;
  }
}
