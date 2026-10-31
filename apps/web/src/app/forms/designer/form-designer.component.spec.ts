import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormDesignerComponent } from './form-designer.component';
import type { JsonSchema } from '../form-fields';

@Component({
  imports: [FormDesignerComponent],
  template: `<bp-form-designer [schema]="schema()" (schemaChange)="changed($event)" />`,
})
class HostComponent {
  readonly schema = signal<JsonSchema | null>(null);
  readonly emitted: JsonSchema[] = [];
  changed(schema: JsonSchema): void {
    this.emitted.push(schema);
    this.schema.set(schema);
  }
}

const leave: JsonSchema = {
  type: 'object',
  required: ['name'],
  properties: {
    name: { type: 'string', title: 'ПІБ' },
    from: { type: 'string', format: 'date', title: 'З' },
  },
};

describe('FormDesignerComponent (F11-02)', () => {
  async function setup(schema: JsonSchema | null = leave) {
    TestBed.configureTestingModule({ imports: [HostComponent] });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.schema.set(schema);
    fixture.detectChanges();
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const q = <T extends HTMLElement = HTMLElement>(id: string) => el.querySelector<T>(`[data-testid="${id}"]`);
    const render = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    const type = async (id: string, value: string) => {
      const input = q<HTMLInputElement>(id)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      await render();
    };
    const last = () => fixture.componentInstance.emitted.at(-1)!;
    return { fixture, el, q, render, type, last };
  }

  it('показує поля схеми картками на канвасі з позначкою обов’язковості', async () => {
    const { q } = await setup();
    expect(q('fd-card-name')?.textContent).toContain('ПІБ');
    expect(q('fd-card-name')?.textContent).toContain('*');
    expect(q('fd-card-from')?.textContent).toContain('Дата');
  });

  it('клік по типу в палітрі додає поле, вибирає його і видає схему', async () => {
    const { q, render, last } = await setup();
    q('fd-kind-email')!.click();
    await render();
    const props = last()['properties'] as Record<string, JsonSchema>;
    expect(props['poshta']).toEqual({ type: 'string', format: 'email', title: 'Пошта' });
    expect(q('fd-card-poshta')?.getAttribute('aria-pressed')).toBe('true');
    expect(q<HTMLInputElement>('fd-prop-title')?.value).toBe('Пошта');
  });

  it('зміна підпису, обов’язковості й ключа потрапляє в схему', async () => {
    const { q, render, type, last } = await setup();
    q('fd-card-from')!.click();
    await render();
    await type('fd-prop-title', 'Дата початку');
    expect((last()['properties'] as Record<string, JsonSchema>)['from']['title']).toBe('Дата початку');
    (q('fd-prop-required')!.querySelector('input') as HTMLInputElement).click();
    await render();
    expect(last()['required']).toEqual(['name', 'from']);
    await type('fd-prop-key', 'name');
    expect(q('fd-key-error')).not.toBeNull();
    await type('fd-prop-key', 'start');
    q('fd-prop-key')!.dispatchEvent(new Event('change'));
    await render();
    expect(Object.keys(last()['properties'] as object)).toEqual(['name', 'start']);
    expect(last()['required']).toEqual(['name', 'start']);
  });

  it('Delete на картці видаляє поле, Ctrl+Z повертає, Ctrl+Shift+Z — знову видаляє', async () => {
    const { q, el, render, last } = await setup();
    q('fd-card-name')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }));
    await render();
    expect(Object.keys(last()['properties'] as object)).toEqual(['from']);
    expect(last()['required']).toBeUndefined();
    const host = el.querySelector('bp-form-designer')!;
    host.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }));
    await render();
    expect(last()).toEqual(leave);
    host.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, shiftKey: true, bubbles: true }));
    await render();
    expect(Object.keys(last()['properties'] as object)).toEqual(['from']);
  });

  it('набір підпису — один крок історії', async () => {
    const { q, render, type, last } = await setup();
    q('fd-card-name')!.click();
    await render();
    await type('fd-prop-title', 'П');
    await type('fd-prop-title', 'Пр');
    await type('fd-prop-title', 'Прізвище');
    q('fd-undo')!.click();
    await render();
    expect(last()).toEqual(leave);
    expect(q<HTMLButtonElement>('fd-undo')!.disabled).toBe(true);
  });

  it('Alt+↓ переносить поле нижче, Ctrl+D дублює', async () => {
    const { q, render, last } = await setup();
    q('fd-card-name')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true }));
    await render();
    expect(Object.keys(last()['properties'] as object)).toEqual(['from', 'name']);
    q('fd-card-from')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'd', ctrlKey: true, bubbles: true }));
    await render();
    expect(Object.keys(last()['properties'] as object)).toEqual(['from', 'from_2', 'name']);
  });

  it('варіанти списку: два для початку, додати, підписати, прибрати', async () => {
    const { q, render, type, last } = await setup({ type: 'object', properties: {} });
    q('fd-kind-select')!.click();
    await render();
    q('fd-option-add')!.click();
    await render();
    await type('fd-option-label-0', 'Щорічна');
    await type('fd-option-value-0', 'annual');
    const vybir = () => (last()['properties'] as Record<string, JsonSchema>)['vybir'];
    expect(vybir()['oneOf']).toEqual([
      { const: 'annual', title: 'Щорічна' },
      { const: 'option_2', title: 'Варіант 2' },
      { const: 'option_3', title: 'Варіант 3' },
    ]);
    q('fd-option-remove-1')!.click();
    await render();
    expect((vybir()['oneOf'] as { const: string }[]).map((o) => o.const)).toEqual(['annual', 'option_3']);
  });

  it('секції: додати, назвати, згорнути', async () => {
    const { q, render, type, last, el } = await setup();
    q('fd-add-section')!.click();
    await render();
    await type('fd-section-prop-title', 'Документи');
    (q('fd-section-prop-collapsible')!.querySelector('input') as HTMLInputElement).click();
    await render();
    expect(el.querySelectorAll('[data-testid="fd-section"]').length).toBe(2);
    expect(last()['x-openbp-layout']).toEqual({
      sections: [{ rows: [[{ field: 'name' }], [{ field: 'from' }]] }, { title: 'Документи', collapsible: true, rows: [] }],
    });
  });

  it('JSON: показує схему, приймає правку, показує помилку розбору', async () => {
    const { q, render, last } = await setup();
    q('fd-mode-json')!.click();
    await render();
    const area = q<HTMLTextAreaElement>('fd-json')!;
    expect(JSON.parse(area.value)).toEqual(leave);
    area.value = '{ "type": "object", ';
    area.dispatchEvent(new Event('input'));
    await render();
    expect(q('fd-json-error')?.textContent).toContain('Некоректний JSON');
    area.value = JSON.stringify({ type: 'object', properties: { x: { type: 'number', title: 'Ікс' } } });
    area.dispatchEvent(new Event('input'));
    await render();
    expect(q('fd-json-error')).toBeNull();
    expect(last()['properties']).toEqual({ x: { type: 'number', title: 'Ікс' } });
    q('fd-mode-design')!.click();
    await render();
    expect(q('fd-card-x')?.textContent).toContain('Ікс');
  });

  it('нова схема ззовні перечитує конструктор і очищає історію', async () => {
    const { fixture, q, render } = await setup();
    q('fd-kind-text')!.click();
    await render();
    expect(q<HTMLButtonElement>('fd-undo')!.disabled).toBe(false);
    fixture.componentInstance.schema.set({ type: 'object', properties: { a: { type: 'string', title: 'А' } } });
    await render();
    expect(q('fd-card-a')).not.toBeNull();
    expect(q('fd-card-name')).toBeNull();
    expect(q<HTMLButtonElement>('fd-undo')!.disabled).toBe(true);
  });
});
