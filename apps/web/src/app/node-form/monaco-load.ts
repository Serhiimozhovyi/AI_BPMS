import { expressionSegments } from './expression-tools';
import type { CodeEditorFactory, CodeEditorHandle, CodeEditorOptions } from './monaco-loader';

/**
 * Завантаження Monaco для `core.code` (F2-19) і редактора виразів (F3-56) окремим lazy-чанком. Окремий файл — щоб збірка `<openbp-canvas>`
 * (F4-13, angular.json `element`) підміняла його простим полем без Monaco (`fileReplacements`).
 */

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

/** Мова рядка параметра: текст як є, усередині `{{ }}` — JavaScript (підсвітка вбудованою мовою). */
const EXPRESSION_LANGUAGE = 'openbp-expression';

/** Клас фону `{{ … }}` у редакторі виразів (styles.scss). */
const EXPRESSION_CLASS = 'openbp-expr-hl';

type Monaco = typeof import('monaco-editor/editor');
type Model = ReturnType<Monaco['editor']['createModel']>;

/** Підказки виразу за моделлю редактора: провайдер підказок мови один на всі редактори. */
const completions = new WeakMap<Model, NonNullable<CodeEditorOptions['complete']>>();

function registerExpressionLanguage(monaco: Monaco): void {
  monaco.languages.register({ id: EXPRESSION_LANGUAGE });
  monaco.languages.setMonarchTokensProvider(EXPRESSION_LANGUAGE, {
    tokenizer: {
      root: [
        [/\{\{/, { token: 'delimiter.bracket', next: '@expression', nextEmbedded: 'javascript' }],
        [/[^{]+/, ''],
        [/\{/, ''],
      ],
      expression: [[/\}\}/, { token: 'delimiter.bracket', next: '@pop', nextEmbedded: '@pop' }]],
    },
  });
  monaco.languages.registerCompletionItemProvider(EXPRESSION_LANGUAGE, {
    triggerCharacters: ['$', '.', '[', '"', "'"],
    provideCompletionItems: (model, position) => {
      const complete = completions.get(model);
      const result = complete?.(model.getValue(), model.getOffsetAt(position));
      if (!result) return { suggestions: [] };
      const start = model.getPositionAt(result.from);
      const range = new monaco.Range(start.lineNumber, start.column, position.lineNumber, position.column);
      return {
        suggestions: result.items.map((s, i) => ({
          label: s.label,
          insertText: s.insert,
          detail: s.detail,
          kind: s.kind === 'method' ? monaco.languages.CompletionItemKind.Method : s.kind === 'field' ? monaco.languages.CompletionItemKind.Field : monaco.languages.CompletionItemKind.Variable,
          sortText: String(i).padStart(3, '0'),
          filterText: s.insert,
          range,
        })),
      };
    },
  });
}

let pending: Promise<CodeEditorFactory> | null = null;

export function loadMonaco(): Promise<CodeEditorFactory> {
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
    registerExpressionLanguage(monaco);
    const factory: CodeEditorFactory = (host, { complete, ...options }) => {
      const expression = options.language === EXPRESSION_LANGUAGE;
      const editor = monaco.editor.create(host, {
        ...options,
        automaticLayout: true,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        fontSize: 13,
        tabSize: 2,
        ...(expression ? { wordWrap: 'on' as const, lineNumbers: 'off' as const, quickSuggestions: { other: true, strings: true, comments: false } } : {}),
      });
      const model = editor.getModel();
      const disposables: { dispose(): void }[] = [];
      if (expression && model) {
        if (complete) completions.set(model, complete);
        // Фон частин `{{ … }}` — як у звичайному полі.
        const decorations = editor.createDecorationsCollection();
        const highlight = () => {
          let offset = 0;
          const ranges = [];
          for (const segment of expressionSegments(model.getValue())) {
            if (segment.expression) {
              const start = model.getPositionAt(offset);
              const end = model.getPositionAt(offset + segment.text.length);
              ranges.push({ range: new monaco.Range(start.lineNumber, start.column, end.lineNumber, end.column), options: { inlineClassName: EXPRESSION_CLASS } });
            }
            offset += segment.text.length;
          }
          decorations.set(ranges);
        };
        highlight();
        disposables.push(editor.onDidChangeModelContent(highlight));
      }
      const handle: CodeEditorHandle = {
        getValue: () => editor.getValue(),
        setValue: (value) => editor.setValue(value),
        onDidChangeModelContent: (listener) => editor.onDidChangeModelContent(listener),
        dispose: () => {
          for (const d of disposables) d.dispose();
          editor.dispose();
        },
        getCursor: () => {
          const position = editor.getPosition();
          return model && position ? model.getOffsetAt(position) : 0;
        },
        setCursor: (offset) => {
          if (model) editor.setPosition(model.getPositionAt(offset));
        },
        focus: () => editor.focus(),
      };
      return handle;
    };
    return factory;
  })().catch((error: unknown) => {
    pending = null;
    throw error;
  });
  return pending;
}
