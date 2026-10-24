import { InjectionToken } from '@angular/core';

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

/** Змінні, доступні в коді `core.code` (`OpenBP.Nodes.Core/Nodes/CodeNode.cs`): для підказок Monaco. */
const CODE_GLOBALS = `
/** Поточний item (режим eachItem). */
declare const $json: any;
/** Індекс поточного item (режим eachItem). */
declare const $index: number;
/** Усі вхідні items (режим allItems). */
declare const items: any[];
/** Виходи вже виконаних вузлів за назвою: \`json\` — item з тим самим індексом (або перший), \`all\` — усі items. */
declare const $node: Record<string, { json: any; all: any[] }>;
`;

let pending: Promise<CodeEditorFactory> | null = null;

function loadMonaco(): Promise<CodeEditorFactory> {
  pending ??= (async () => {
    const scope = globalThis as unknown as { MonacoEnvironment?: { getWorker(id: string, label: string): Worker } };
    scope.MonacoEnvironment ??= {
      getWorker: (_id, label) =>
        label === 'typescript' || label === 'javascript'
          ? new Worker(new URL('./workers/ts.worker', import.meta.url), { type: 'module' })
          : new Worker(new URL('./workers/editor.worker', import.meta.url), { type: 'module' }),
    };
    const [monaco, , , typescript] = await Promise.all([
      import('monaco-editor/editor'),
      import('monaco-editor/features/register.all'),
      import('monaco-editor/languages/definitions/javascript/register'),
      import('monaco-editor/languages/features/typescript/register'),
    ]);
    typescript.javascriptDefaults.addExtraLib(CODE_GLOBALS, 'ts:openbp-code-globals.d.ts');
    const factory: CodeEditorFactory = (host, options) =>
      monaco.editor.create(host, {
        ...options,
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        fontSize: 13,
        tabSize: 2,
      });
    return factory;
  })().catch((error: unknown) => {
    pending = null;
    throw error;
  });
  return pending;
}
