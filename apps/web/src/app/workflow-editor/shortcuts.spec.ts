import { TestBed } from '@angular/core/testing';
import { ShortcutsDialogComponent } from './shortcuts-dialog.component';
import { keyLabel, shortcutGroups, zoomKey } from './shortcuts';

const key = (k: string, extra: Partial<KeyboardEvent> = {}) => ({ key: k, ctrlKey: false, metaKey: false, altKey: false, ...extra });

describe('shortcuts (F3-90)', () => {
  it('клавіші масштабу: +/= ближче, -/_ далі, 0 — 100 %, 1 — вписати; з Ctrl/⌘/Alt — ні', () => {
    expect(zoomKey(key('+'))).toBe('in');
    expect(zoomKey(key('='))).toBe('in');
    expect(zoomKey(key('-'))).toBe('out');
    expect(zoomKey(key('_'))).toBe('out');
    expect(zoomKey(key('0'))).toBe('reset');
    expect(zoomKey(key('1'))).toBe('fit');
    expect(zoomKey(key('2'))).toBeNull();
    expect(zoomKey(key('0', { ctrlKey: true }))).toBeNull();
    expect(zoomKey(key('=', { metaKey: true }))).toBeNull();
  });

  it('довідка не повторює клавіші й описує всі групи дій', () => {
    const groups = shortcutGroups();
    expect(groups.map((g) => g.id)).toEqual(['add', 'select', 'edit', 'run', 'canvas']);
    const all = groups.flatMap((g) => g.shortcuts.flatMap((s) => s.keys));
    expect(new Set(all).size).toBe(all.length);
    for (const k of ['Tab', 'Shift+S', 'Mod+A', 'Esc', 'Mod+Z', 'F2', 'P', 'Mod+Enter', '0', '1', '?']) expect(all).toContain(k);
  });

  it('Mod — Ctrl, на Mac — ⌘', () => {
    expect(keyLabel('Mod+Shift+Z', false)).toBe('Ctrl+Shift+Z');
    expect(keyLabel('Mod+Shift+Z', true)).toBe('⌘+Shift+Z');
    expect(keyLabel('Esc', false)).toBe('Esc');
  });

  it('діалог показує групи й клавіші', () => {
    const fixture = TestBed.createComponent(ShortcutsDialogComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('[data-group]')).toHaveLength(5);
    expect(host.querySelector('[data-group="canvas"]')?.textContent).toContain('Масштаб 100 %');
    expect(host.querySelectorAll('kbd').length).toBeGreaterThan(15);
  });
});
