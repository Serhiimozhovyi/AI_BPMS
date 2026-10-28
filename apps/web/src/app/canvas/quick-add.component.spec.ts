import { TestBed } from '@angular/core/testing';
import { PaletteItem, buildPalette } from './palette';
import { QuickAddComponent } from './quick-add.component';
import { quickAddSections } from './quick-add';
import { SAMPLE_DESCRIPTORS } from './sample-workflows';

describe('QuickAddComponent', () => {
  function create(recent: string[] = []) {
    const fixture = TestBed.createComponent(QuickAddComponent);
    const sections = buildPalette(SAMPLE_DESCRIPTORS, [{ id: 'c1', name: '1С', tools: [{ name: 'create_order', title: 'Створити замовлення', inputSchema: {} }] }]);
    fixture.componentRef.setInput('sections', quickAddSections(sections, 'step', recent));
    fixture.componentRef.setInput('title', 'Наступний крок');
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const picked: PaletteItem[] = [];
    let closed = 0;
    fixture.componentInstance.pick.subscribe((i) => picked.push(i));
    fixture.componentInstance.closed.subscribe(() => closed++);
    const search = host.querySelector('[data-testid="quick-add-search"]') as HTMLInputElement;
    const key = (k: string) => {
      search.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
      fixture.detectChanges();
    };
    const type = (text: string) => {
      search.value = text;
      search.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    return { fixture, host, picked, closed: () => closed, search, key, type };
  }

  it('фокус у пошуку; Enter вставляє перший результат пошуку', () => {
    const { host, picked, search, key, type } = create();
    expect(document.activeElement).toBe(search);

    type('замовлення');
    expect(host.querySelectorAll('.quick__item')).toHaveLength(1);
    key('Enter');
    expect(picked.map((i) => i.key)).toEqual(['c1/create_order']);
  });

  it('стрілки змінюють вибраний елемент; Esc закриває', () => {
    const { host, picked, key, closed } = create();
    const items = () => Array.from(host.querySelectorAll<HTMLElement>('.quick__item'));

    key('ArrowDown');
    expect(items()[1].classList).toContain('quick__item--active');
    key('ArrowUp');
    key('ArrowUp');
    expect(items().at(-1)!.classList).toContain('quick__item--active');
    // Enter на звичайному елементі вставляє його (конектор відкриває власну сторінку).
    key('ArrowDown');
    key('Enter');
    expect(picked).toHaveLength(1);
    expect(picked[0].key).toBe(items()[0].dataset['item']);

    key('Escape');
    expect(closed()).toBe(1);
  });

  it('«Останні» першими; тригерів у режимі кроку немає; порожній пошук — повідомлення', () => {
    const { host, type } = create(['core.set']);
    expect(host.querySelector('.quick__heading')!.textContent).toContain('Останні');
    expect(host.querySelector<HTMLElement>('.quick__item')!.dataset['item']).toBe('core.set');
    expect(host.querySelector('[data-item="core.manualTrigger"]')).toBeNull();

    type('нема такого');
    expect(host.querySelector('[data-testid="quick-add-empty"]')).not.toBeNull();
  });

  it('клавіші не спливають до гарячих клавіш редактора', () => {
    const { host, key } = create();
    const seen: string[] = [];
    const listener = (e: KeyboardEvent) => seen.push(e.key);
    document.addEventListener('keydown', listener);
    document.body.appendChild(host);
    key('Escape');
    key('ArrowDown');
    document.removeEventListener('keydown', listener);
    expect(seen).toEqual([]);
  });

  it('конектор відкриває сторінку з діями; Esc повертає назад, другий Esc закриває (F3-96)', () => {
    const fixture = TestBed.createComponent(QuickAddComponent);
    const sections = buildPalette(SAMPLE_DESCRIPTORS, [], [], [
      { manifest: { id: 'tg', name: 'Telegram', description: '', transport: 'stdio', actions: [{ name: 'send_photo', title: 'Надіслати фото' }], triggers: [] }, source: 'builtin', installed: [] },
    ]);
    fixture.componentRef.setInput('sections', quickAddSections(sections, 'step'));
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const picked: PaletteItem[] = [];
    let closed = 0;
    fixture.componentInstance.pick.subscribe((i) => picked.push(i));
    fixture.componentInstance.closed.subscribe(() => closed++);
    const search = host.querySelector('[data-testid="quick-add-search"]') as HTMLInputElement;
    const key = (k: string) => {
      search.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
      fixture.detectChanges();
    };

    (host.querySelector('[data-item="connector:tg"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(picked).toEqual([]);
    expect(host.querySelector('[data-testid="quick-add-page"]')?.textContent).toContain('Telegram');
    expect(Array.from(host.querySelectorAll('.quick__item')).map((e) => (e as HTMLElement).dataset['item'])).toEqual(['tg/send_photo']);

    key('Escape');
    expect(host.querySelector('[data-testid="quick-add-page"]')).toBeNull();
    expect(closed).toBe(0);

    (host.querySelector('[data-item="connector:tg"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    key('Enter');
    expect(picked.map((i) => i.key)).toEqual(['tg/send_photo']);
    key('Escape');
    key('Escape');
    expect(closed).toBe(1);
  });
});
