import { EditHistory } from './edit-history';

describe('EditHistory', () => {
  it('undo/redo проходять знімки в обидва боки; на краях — undefined', () => {
    const h = new EditHistory('a');
    h.push('b');
    h.push('c');

    expect(h.canUndo()).toBe(true);
    expect(h.canRedo()).toBe(false);
    expect(h.undo()).toBe('b');
    expect(h.undo()).toBe('a');
    expect(h.undo()).toBeUndefined();
    expect(h.current()).toBe('a');
    expect(h.canUndo()).toBe(false);
    expect(h.canRedo()).toBe(true);

    expect(h.redo()).toBe('b');
    expect(h.redo()).toBe('c');
    expect(h.redo()).toBeUndefined();
    expect(h.current()).toBe('c');
  });

  it('нова зміна після undo відкидає гілку redo', () => {
    const h = new EditHistory('a');
    h.push('b');
    h.push('c');
    h.undo();
    h.push('d');

    expect(h.canRedo()).toBe(false);
    expect(h.redo()).toBeUndefined();
    expect(h.undo()).toBe('b');
    expect(h.undo()).toBe('a');
  });

  it('той самий об’єкт — не крок історії', () => {
    const value = { n: 1 };
    const h = new EditHistory(value);
    h.push(value);
    expect(h.canUndo()).toBe(false);
  });

  it('пам’ятає не більше limit кроків назад', () => {
    const h = new EditHistory(0, 3);
    for (let i = 1; i <= 10; i++) h.push(i);

    const undone: number[] = [];
    for (let v = h.undo(); v !== undefined; v = h.undo()) undone.push(v);
    expect(undone).toEqual([9, 8, 7]);
  });

  it('reset починає історію заново', () => {
    const h = new EditHistory('a');
    h.push('b');
    h.undo();
    h.reset('x');

    expect(h.current()).toBe('x');
    expect(h.canUndo()).toBe(false);
    expect(h.canRedo()).toBe(false);
  });
});
