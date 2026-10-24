import { InjectionToken } from '@angular/core';
import { loadMonaco } from './monaco-load';

/**
 * Monaco для `core.code` (F2-19): вантажиться окремим lazy-чанком лише при відкритті Code-вузла.
 * Компонент працює з вузьким інтерфейсом нижче — у тестах його підміняє фейк через {@link MONACO_LOADER}.
 */

export interface CodeEditorHandle {
  getValue(): string;
  setValue(value: string): void;
  onDidChangeModelContent(listener: () => void): { dispose(): void };
  dispose(): void;
}

export interface CodeEditorOptions {
  value: string;
  language: 'javascript';
  ariaLabel?: string;
  readOnly?: boolean;
}

/** Створює редактор у `host`. */
export type CodeEditorFactory = (host: HTMLElement, options: CodeEditorOptions) => CodeEditorHandle;

/** Завантажує Monaco і повертає фабрику редакторів. */
export const MONACO_LOADER = new InjectionToken<() => Promise<CodeEditorFactory>>('MONACO_LOADER', {
  providedIn: 'root',
  factory: () => loadMonaco,
});
