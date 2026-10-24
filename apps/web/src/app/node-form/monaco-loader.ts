import { InjectionToken } from '@angular/core';
import type { SuggestResult } from './expression-suggest';
import { loadMonaco } from './monaco-load';

/**
 * Monaco для `core.code` (F2-19) і редактора виразів (F3-56): вантажиться окремим lazy-чанком лише при відкритті.
 * Компонент працює з вузьким інтерфейсом нижче — у тестах його підміняє фейк через {@link MONACO_LOADER}.
 */

export interface CodeEditorHandle {
  getValue(): string;
  setValue(value: string): void;
  onDidChangeModelContent(listener: () => void): { dispose(): void };
  dispose(): void;
  /** Позиція курсора — зсув від початку тексту. */
  getCursor?(): number;
  setCursor?(offset: number): void;
  focus?(): void;
}

/** `javascript` — код `core.code`; `openbp-expression` — рядок параметра з `{{ }}` (JavaScript лише всередині). */
export type CodeEditorLanguage = 'javascript' | 'openbp-expression';

export interface CodeEditorOptions {
  value: string;
  language: CodeEditorLanguage;
  ariaLabel?: string;
  readOnly?: boolean;
  /** Підказки виразу для позиції курсора (`openbp-expression`). */
  complete?: (text: string, offset: number) => SuggestResult | null;
}

/** Створює редактор у `host`. */
export type CodeEditorFactory = (host: HTMLElement, options: CodeEditorOptions) => CodeEditorHandle;

/** Завантажує Monaco і повертає фабрику редакторів. */
export const MONACO_LOADER = new InjectionToken<() => Promise<CodeEditorFactory>>('MONACO_LOADER', {
  providedIn: 'root',
  factory: () => loadMonaco,
});
