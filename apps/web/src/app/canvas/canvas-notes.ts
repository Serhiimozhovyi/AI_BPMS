import { NoteColor, WorkflowDefinition, WorkflowNote } from '../workflows/workflows-api.service';
import { NodePosition, parsePosition } from './canvas-model';

/**
 * Нотатки на канвасі (F3-62, docs/workflow-format.md §«Нотатки»): чисті функції `визначення → нове визначення`
 * і рендер Markdown-тексту. Нотатки не пов'язані з вузлами, тож решта визначення лишається тими самими об'єктами.
 */

/** Розмір нової нотатки на канвасі. */
export const DEFAULT_NOTE_SIZE: [number, number] = [240, 120];

/** Менше нотатку не розтягнути — інакше ні текст, ні кут для розтягування не видно. */
export const MIN_NOTE_SIZE: [number, number] = [120, 60];

export const DEFAULT_NOTE_COLOR: NoteColor = 'yellow';

/** Кольори нотаток — Highlight-палітра AC-Kit (`--ac-*` у styles.scss) у порядку показу в палітрі нотатки. */
export const NOTE_COLORS: readonly { color: NoteColor; token: string; label: string }[] = [
  { color: 'yellow', token: '--ac-yellow', label: $localize`:@@canvas.note.color.yellow:Жовтий` },
  { color: 'green', token: '--ac-green', label: $localize`:@@canvas.note.color.green:Зелений` },
  { color: 'blue', token: '--ac-blue', label: $localize`:@@canvas.note.color.blue:Синій` },
  { color: 'teal', token: '--ac-teal', label: $localize`:@@canvas.note.color.teal:Бірюзовий` },
  { color: 'orange', token: '--ac-orange', label: $localize`:@@canvas.note.color.orange:Помаранчевий` },
  { color: 'red', token: '--ac-red', label: $localize`:@@canvas.note.color.red:Червоний` },
  { color: 'violet', token: '--ac-violet', label: $localize`:@@canvas.note.color.violet:Фіолетовий` },
];

/** CSS-змінна кольору нотатки; невідомий колір (з файлу) — як типовий. */
export function noteColorToken(color: unknown): string {
  return (NOTE_COLORS.find((c) => c.color === color) ?? NOTE_COLORS[0]).token;
}

/** `[ширина, висота]` нотатки не менші за мінімум; некоректне значення — типовий розмір. */
export function noteSize(note: Pick<WorkflowNote, 'size'>): [number, number] {
  const size = note.size;
  if (Array.isArray(size) && typeof size[0] === 'number' && typeof size[1] === 'number' && size[0] > 0 && size[1] > 0) {
    return [Math.max(MIN_NOTE_SIZE[0], size[0]), Math.max(MIN_NOTE_SIZE[1], size[1])];
  }
  return DEFAULT_NOTE_SIZE;
}

/** `note_` + 12 hex — як `randomNodeId`, але з власним префіксом, щоб не плутати з вузлами. */
export function randomNoteId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(6));
  return `note_${Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('')}`;
}

/** Результат додавання: нове визначення і id нотатки (щоб виділити її й відкрити редагування). */
export interface AddNoteResult {
  definition: WorkflowDefinition;
  id: string;
}

/** Додає порожню нотатку типового розміру й кольору з лівим верхнім кутом у `at`. */
export function addNote(definition: WorkflowDefinition, at: [number, number], newId: () => string): AddNoteResult {
  const ids = new Set(notesOf(definition).map((n) => n.id));
  let id = newId();
  while (ids.has(id)) id = newId();
  const note: WorkflowNote = { id, text: '', position: [at[0], at[1]], size: [...DEFAULT_NOTE_SIZE], color: DEFAULT_NOTE_COLOR };
  return { definition: { ...definition, notes: [...notesOf(definition), note] }, id };
}

/** Що можна змінити в нотатці з канвасу. */
export type NotePatch = Partial<Pick<WorkflowNote, 'text' | 'color' | 'size'>>;

/** Змінює поля нотатки; невідомий id або та сама суть — те саме визначення. Решта полів нотатки (зокрема невідомі) лишаються. */
export function updateNote(definition: WorkflowDefinition, id: string, patch: NotePatch): WorkflowDefinition {
  const notes = notesOf(definition);
  const index = notes.findIndex((n) => n.id === id);
  if (index < 0) return definition;
  const note = notes[index];
  const next: WorkflowNote = { ...note };
  let changed = false;
  if (patch.text !== undefined && patch.text !== note.text) {
    next.text = patch.text;
    changed = true;
  }
  if (patch.color !== undefined && patch.color !== note.color) {
    next.color = patch.color;
    changed = true;
  }
  if (patch.size !== undefined) {
    const current = noteSize(note);
    const size: [number, number] = [Math.max(MIN_NOTE_SIZE[0], patch.size[0]), Math.max(MIN_NOTE_SIZE[1], patch.size[1])];
    if (size[0] !== current[0] || size[1] !== current[1] || !note.size) {
      next.size = size;
      changed = true;
    }
  }
  if (!changed) return definition;
  return { ...definition, notes: notes.map((n, i) => (i === index ? next : n)) };
}

/** Видаляє нотатки за id; жодної не знайдено — те саме визначення. Без нотаток поле `notes` зникає з документа. */
export function removeNotes(definition: WorkflowDefinition, ids: readonly string[]): WorkflowDefinition {
  const set = new Set(ids);
  const notes = notesOf(definition);
  const kept = notes.filter((n) => !set.has(n.id));
  if (kept.length === notes.length) return definition;
  if (kept.length === 0) {
    const rest = { ...definition };
    delete rest.notes;
    return rest;
  }
  return { ...definition, notes: kept };
}

/** Переносить нові позиції нотаток (після перетягування); нотатки без змін — ті самі об'єкти. */
export function moveNotes(definition: WorkflowDefinition, positions: readonly NodePosition[]): WorkflowDefinition {
  if (positions.length === 0) return definition;
  const byId = new Map(positions.map((p) => [p.id, p]));
  let changed = false;
  const notes = notesOf(definition).map((n) => {
    const p = byId.get(n.id);
    if (!p) return n;
    const { x, y } = parsePosition(n.position);
    if (x === p.x && y === p.y) return n;
    changed = true;
    return { ...n, position: [p.x, p.y] as [number, number] };
  });
  return changed ? { ...definition, notes } : definition;
}

/** Нотатки визначення як масив; поле може бути відсутнє або (з чужого файлу) не масивом. */
export function notesOf(definition: WorkflowDefinition): WorkflowNote[] {
  return Array.isArray(definition.notes) ? definition.notes : [];
}

/**
 * Markdown нотатки → HTML. Підмножина, якої вистачає для пояснень на канвасі: заголовки `#`–`###`, абзаци, списки
 * `-`/`*`/`1.` (з чекбоксами `[ ]`/`[x]`), `**жирний**`, `*курсив*`, `` `код` ``, посилання `[текст](https://…)`.
 * Увесь текст спочатку екранується, тож HTML із нотатки не виконується; посилання — лише http(s), у новій вкладці.
 */
export function renderNoteMarkdown(text: string): string {
  const lines = text.replace(/\r\n?/g, '\n').split('\n');
  const html: string[] = [];
  let paragraph: string[] = [];
  let list: { tag: 'ul' | 'ol'; items: string[] } | null = null;

  const flushParagraph = () => {
    if (paragraph.length) html.push(`<p>${paragraph.join('<br>')}</p>`);
    paragraph = [];
  };
  const flushList = () => {
    if (list) html.push(`<${list.tag}>${list.items.map((i) => `<li>${i}</li>`).join('')}</${list.tag}>`);
    list = null;
  };

  for (const raw of lines) {
    const line = raw.trimEnd();
    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }
    const heading = /^(#{1,3})\s+(.*)$/.exec(line);
    if (heading) {
      flushParagraph();
      flushList();
      html.push(`<h${heading[1].length}>${inline(heading[2])}</h${heading[1].length}>`);
      continue;
    }
    const bullet = /^\s*[-*]\s+(.*)$/.exec(line);
    const numbered = /^\s*\d+[.)]\s+(.*)$/.exec(line);
    if (bullet || numbered) {
      flushParagraph();
      const tag = bullet ? 'ul' : 'ol';
      if (!list || list.tag !== tag) {
        flushList();
        list = { tag, items: [] };
      }
      list.items.push(inline(checkbox((bullet ?? numbered)![1])));
      continue;
    }
    flushList();
    paragraph.push(inline(line));
  }
  flushParagraph();
  flushList();
  return html.join('');
}

/** `[ ] текст` → `☐ текст`, `[x] текст` → `☑ текст` (до екранування, це лише символи). */
function checkbox(item: string): string {
  return item.replace(/^\[([ xX])\]\s+/, (_, mark: string) => (mark === ' ' ? '☐ ' : '☑ '));
}

function inline(text: string): string {
  // Спочатку екрануємо все, далі працюємо лише з безпечним текстом.
  let s = escapeHtml(text);
  s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
  s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?!\w)/g, '$1<em>$2</em>');
  s = s.replace(/(^|[^\w])_([^_\s][^_]*?)_(?!\w)/g, '$1<em>$2</em>');
  s = s.replace(/\[([^\]]+)\]\((https?:\/\/[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noopener noreferrer">$1</a>');
  return s;
}

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
