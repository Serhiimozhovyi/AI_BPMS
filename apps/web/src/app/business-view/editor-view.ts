/** Вигляд редактора сценарію (F3-66): бізнес-схема або технічний канвас. */
export type EditorView = 'business' | 'technical';

const VIEW_KEY = 'openbp.editor.view';
const VIEW_PARAM = 'view';

/** `?view=business|technical` у адресі, інакше — останній вибраний (`localStorage`), інакше — технічний. */
export function readEditorView(search: string = location.search): EditorView {
  const fromUrl = new URLSearchParams(search).get(VIEW_PARAM);
  if (isView(fromUrl)) return fromUrl;
  try {
    const stored = localStorage.getItem(VIEW_KEY);
    if (isView(stored)) return stored;
  } catch {
    // Сховище недоступне — лишаємо типовий вигляд.
  }
  return 'technical';
}

/** Запам'ятовує вибір; у SPA ще й оновлює `?view=` без нового запису в історії (у веб-компоненті адреса хоста не наша). */
export function writeEditorView(view: EditorView, updateUrl: boolean): void {
  try {
    localStorage.setItem(VIEW_KEY, view);
  } catch {
    // Немає сховища — вибір живе до перезавантаження.
  }
  if (!updateUrl) return;
  const url = new URL(location.href);
  if (view === 'business') url.searchParams.set(VIEW_PARAM, view);
  else url.searchParams.delete(VIEW_PARAM);
  history.replaceState(history.state, '', url);
}

function isView(value: string | null): value is EditorView {
  return value === 'business' || value === 'technical';
}
