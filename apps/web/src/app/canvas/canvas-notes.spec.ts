import { WorkflowDefinition, emptyWorkflowDefinition } from '../workflows/workflows-api.service';
import { deleteSelection } from './canvas-edit';
import { DEFAULT_NOTE_SIZE, MIN_NOTE_SIZE, addNote, moveNotes, noteSize, removeNotes, renderNoteMarkdown, updateNote } from './canvas-notes';

function withNotes(): WorkflowDefinition {
  return {
    ...emptyWorkflowDefinition('Нотатки'),
    nodes: [{ id: 'n_start', name: 'Старт', type: 'core.manualTrigger', typeVersion: 1, position: [0, 0], parameters: {} }],
    notes: [
      { id: 'note_a', text: 'A', position: [10, 20], size: [200, 100], color: 'green', 'x-author': 'agent' },
      { id: 'note_b', text: '', position: [300, 40] },
    ],
  };
}

describe('canvas-notes (F3-62)', () => {
  it('addNote: порожня нотатка типового розміру й кольору в заданій точці; id унікальний; решта визначення — ті самі об’єкти', () => {
    const def = withNotes();
    const ids = ['note_a', 'note_new'];
    const { definition, id } = addNote(def, [50, 60], () => ids.shift()!);

    expect(id).toBe('note_new');
    expect(definition.notes!.at(-1)).toEqual({ id: 'note_new', text: '', position: [50, 60], size: DEFAULT_NOTE_SIZE, color: 'yellow' });
    expect(definition.notes!.slice(0, 2)).toEqual(def.notes);
    expect(definition.nodes).toBe(def.nodes);
    expect(def.notes).toHaveLength(2);

    const first = addNote(emptyWorkflowDefinition('Без нотаток'), [0, 0], () => 'note_1');
    expect(first.definition.notes).toHaveLength(1);
  });

  it('updateNote: текст, колір і розмір (не менший за мінімум); невідомі поля лишаються; без змін — те саме визначення', () => {
    const def = withNotes();

    const text = updateNote(def, 'note_a', { text: 'B' });
    expect(text.notes![0]).toEqual({ ...def.notes![0], text: 'B' });
    expect(text.notes![1]).toBe(def.notes![1]);

    const color = updateNote(def, 'note_b', { color: 'red' });
    expect(color.notes![1].color).toBe('red');

    const size = updateNote(def, 'note_a', { size: [10, 500] });
    expect(size.notes![0].size).toEqual([MIN_NOTE_SIZE[0], 500]);
    // Нотатка без розміру отримує явний, навіть якщо він дорівнює типовому.
    expect(updateNote(def, 'note_b', { size: DEFAULT_NOTE_SIZE }).notes![1].size).toEqual(DEFAULT_NOTE_SIZE);

    expect(updateNote(def, 'note_a', { text: 'A', color: 'green', size: [200, 100] })).toBe(def);
    expect(updateNote(def, 'ghost', { text: 'x' })).toBe(def);
  });

  it('removeNotes і deleteSelection: видаляють за id; остання видалена — поле notes зникає', () => {
    const def = withNotes();

    const one = removeNotes(def, ['note_b']);
    expect(one.notes!.map((n) => n.id)).toEqual(['note_a']);
    expect(removeNotes(def, ['ghost'])).toBe(def);

    const none = removeNotes(one, ['note_a']);
    expect('notes' in none).toBe(false);

    const viaSelection = deleteSelection(def, { nodes: [], edges: [], notes: ['note_a'] });
    expect(viaSelection.notes!.map((n) => n.id)).toEqual(['note_b']);
    expect(viaSelection.nodes).toBe(def.nodes);
    expect(deleteSelection(def, { nodes: [], edges: [] })).toBe(def);
  });

  it('moveNotes: нові позиції лише для перелічених; без змін — ті самі об’єкти', () => {
    const def = withNotes();

    const moved = moveNotes(def, [{ id: 'note_b', x: 310, y: 50 }, { id: 'n_start', x: 1, y: 1 }]);
    expect(moved.notes![1].position).toEqual([310, 50]);
    expect(moved.notes![0]).toBe(def.notes![0]);
    expect(moved.nodes).toBe(def.nodes);
    expect(moveNotes(def, [{ id: 'note_a', x: 10, y: 20 }])).toBe(def);
    expect(moveNotes(def, [])).toBe(def);
  });

  it('noteSize: типовий розмір без size чи з некоректним, мінімум — для замалого', () => {
    expect(noteSize({})).toEqual(DEFAULT_NOTE_SIZE);
    expect(noteSize({ size: [0, 10] })).toEqual(DEFAULT_NOTE_SIZE);
    expect(noteSize({ size: [10, 10] })).toEqual(MIN_NOTE_SIZE);
    expect(noteSize({ size: [300, 150] })).toEqual([300, 150]);
  });

  describe('renderNoteMarkdown', () => {
    it('заголовки, абзаци з переносами, списки з чекбоксами', () => {
      const html = renderNoteMarkdown('## Вхід\n\nПерший рядок\nдругий рядок\n\n- [ ] зробити\n- [x] готово\n1. раз\n2) два');
      expect(html).toBe(
        '<h2>Вхід</h2><p>Перший рядок<br>другий рядок</p><ul><li>☐ зробити</li><li>☑ готово</li></ul><ol><li>раз</li><li>два</li></ol>',
      );
    });

    it('жирний, курсив, код і посилання лише http(s) у новій вкладці', () => {
      expect(renderNoteMarkdown('**важливо** і *курсив* і _теж_ та `code`')).toBe(
        '<p><strong>важливо</strong> і <em>курсив</em> і <em>теж</em> та <code>code</code></p>',
      );
      expect(renderNoteMarkdown('див. [регламент](https://wiki.local/p?a=1&b=2)')).toBe(
        '<p>див. <a href="https://wiki.local/p?a=1&amp;b=2" target="_blank" rel="noopener noreferrer">регламент</a></p>',
      );
      expect(renderNoteMarkdown('[клік](javascript:alert(1))')).toBe('<p>[клік](javascript:alert(1))</p>');
      // Зірочки всередині слів і цін — не форматування.
      expect(renderNoteMarkdown('2*3*4 і snake_case_name')).toBe('<p>2*3*4 і snake_case_name</p>');
    });

    it('HTML з тексту екранується, а не вставляється', () => {
      expect(renderNoteMarkdown('<img src=x onerror=alert(1)> & <b>')).toBe('<p>&lt;img src=x onerror=alert(1)&gt; &amp; &lt;b&gt;</p>');
      expect(renderNoteMarkdown('')).toBe('');
      expect(renderNoteMarkdown('   \n\n')).toBe('');
    });
  });
});
