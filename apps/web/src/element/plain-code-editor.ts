import type { CodeEditorFactory } from '../app/node-form/monaco-loader';

/**
 * Заміна `src/app/node-form/monaco-load.ts` у збірці `<openbp-canvas>` (angular.json, `fileReplacements`):
 * код `core.code` і вирази (F3-56) редагуються звичайним `<textarea>` — без Monaco, його воркерів і глобальних стилів,
 * яким не місце в одному JS-файлі й у Shadow DOM.
 */
export function loadMonaco(): Promise<CodeEditorFactory> {
  return Promise.resolve((host, options) => {
    const area = host.ownerDocument.createElement('textarea');
    area.value = options.value;
    area.readOnly = !!options.readOnly;
    area.spellcheck = false;
    if (options.ariaLabel) area.setAttribute('aria-label', options.ariaLabel);
    area.className = 'openbp-code';
    Object.assign(area.style, { width: '100%', height: '100%', boxSizing: 'border-box', font: '13px/18px monospace', resize: 'none' });
    host.appendChild(area);
    return {
      getValue: () => area.value,
      setValue: (value: string) => (area.value = value),
      onDidChangeModelContent: (listener: () => void) => {
        area.addEventListener('input', listener);
        return { dispose: () => area.removeEventListener('input', listener) };
      },
      dispose: () => area.remove(),
      getCursor: () => area.selectionStart,
      setCursor: (offset: number) => area.setSelectionRange(offset, offset),
      focus: () => area.focus(),
    };
  });
}
