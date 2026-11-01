import { CdkDropList } from '@angular/cdk/drag-drop';
import { Component, signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { TestBed } from '@angular/core/testing';
import { FormDesignerComponent } from './form-designer.component';
import type { JsonSchema } from '../form-fields';
import { chooseOption, selectOptionValues, selectValue } from '../../shared/select-search/select-testing';

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

/** Схема `leave` з конструктора: проста розмітка лишається, бо тримає порядок полів у jsonb (F11-08). */
const leaveOut: JsonSchema = { ...leave, 'x-openbp-layout': { sections: [{ rows: [[{ field: 'name' }], [{ field: 'from' }]] }] } };

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
    expect(last()).toEqual(leaveOut);
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
    expect(last()).toEqual(leaveOut);
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
    expect(JSON.parse(area.value)).toEqual(leaveOut);
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

  describe('рядки з кількох полів (F11-03)', () => {
    const layoutOf = (schema: JsonSchema) =>
      ((schema['x-openbp-layout'] as { sections: { rows: { field: string; width?: number }[][] }[] }) ?? { sections: [] }).sections.map((s) =>
        s.rows.map((r) => r.map((c) => (c.width ? `${c.field}:${c.width}` : c.field))),
      );

    async function drop(ctx: Awaited<ReturnType<typeof setup>>, selector: string, data: unknown, from: { id: string; index: number }, currentIndex: number) {
      const target = ctx.fixture.debugElement.query(By.css(selector)).injector.get(CdkDropList);
      const previous = from.id === 'fd-palette' ? { id: 'fd-palette', data: null } : ctx.fixture.debugElement.query(By.css(`#${from.id}`)).injector.get(CdkDropList);
      target.dropped.emit({
        item: { data },
        container: target,
        previousContainer: previous,
        previousIndex: from.index,
        currentIndex,
        isPointerOverContainer: true,
        distance: { x: 0, y: 0 },
        dropPoint: { x: 0, y: 0 },
        event: new MouseEvent('mouseup'),
      } as never);
      await ctx.render();
    }

    it('поле, скинуте в рядок іншого поля, стає поруч; у проміжок — знову окремим рядком', async () => {
      const ctx = await setup();
      await drop(ctx, '[data-row="0:0"]', 'from', { id: 'fd-row-0-1', index: 0 }, 1);
      expect(layoutOf(ctx.last())).toEqual([[['name', 'from']]]);
      expect(ctx.q('fd-card-from')?.getAttribute('data-width')).toBe('6');
      await drop(ctx, '[data-gap="0:1"]', 'from', { id: 'fd-row-0-0', index: 1 }, 0);
      expect(layoutOf(ctx.last())).toEqual([[['name'], ['from']]]);
      expect(Object.keys(ctx.last()['properties'] as object)).toEqual(['name', 'from']);
    });

    it('тип з палітри, скинутий у рядок, додає поле поруч', async () => {
      const ctx = await setup();
      await drop(ctx, '[data-row="0:1"]', { kind: 'date' }, { id: 'fd-palette', index: 6 }, 1);
      expect(layoutOf(ctx.last())).toEqual([[['name'], ['from', 'data']]]);
      expect(ctx.q('fd-card-data')?.getAttribute('aria-pressed')).toBe('true');
    });

    it('у рядку з 4 полів місця немає', async () => {
      const ctx = await setup({
        type: 'object',
        properties: { a: { type: 'string' }, b: { type: 'string' }, c: { type: 'string' }, d: { type: 'string' }, e: { type: 'string' } },
        'x-openbp-layout': { sections: [{ rows: [[{ field: 'a' }, { field: 'b' }, { field: 'c' }, { field: 'd' }], [{ field: 'e' }]] }] },
      });
      const row = ctx.fixture.debugElement.query(By.css('[data-row="0:0"]')).injector.get(CdkDropList);
      expect(row.enterPredicate({ data: 'e' } as never, row)).toBe(false);
      expect(row.enterPredicate({ data: 'a' } as never, row)).toBe(true);
    });

    it('пресети ширин, Alt+←/→, «Окремим рядком» і «В один рядок із попереднім»', async () => {
      const ctx = await setup();
      ctx.q('fd-card-from')!.click();
      await ctx.render();
      ctx.q('fd-join')!.click();
      await ctx.render();
      expect(layoutOf(ctx.last())).toEqual([[['name', 'from']]]);
      ctx.q('fd-row-preset-4-8')!.click();
      await ctx.render();
      expect(layoutOf(ctx.last())).toEqual([[['name:4', 'from:8']]]);
      expect(ctx.q('fd-row-preset-4-8')?.getAttribute('aria-pressed')).toBe('true');
      ctx.q('fd-card-name')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', altKey: true, bubbles: true }));
      await ctx.render();
      expect(layoutOf(ctx.last())).toEqual([[['name:5', 'from:7']]]);
      ctx.q('fd-card-from')!.click();
      await ctx.render();
      ctx.q('fd-split')!.click();
      await ctx.render();
      expect(layoutOf(ctx.last())).toEqual([[['name'], ['from']]]);
      ctx.q('fd-undo')!.click();
      await ctx.render();
      expect(layoutOf(ctx.last())).toEqual([[['name:5', 'from:7']]]);
    });
  });

  describe('режим таблиці (F11-05)', () => {
    const columns = [
      { name: 'name', type: 'string' },
      { name: 'starts_at', type: 'datetime' },
      { name: 'status', type: 'enum', values: ['active', 'cancelled'] },
      { name: 'minutes', type: 'number' },
    ];

    const layoutOf = (schema: JsonSchema) =>
      ((schema['x-openbp-layout'] as { sections: { rows: { field: string }[][] }[] }) ?? { sections: [] }).sections.map((sec) => sec.rows.map((r) => r.map((c) => c.field)));

    async function tableSetup(schema: JsonSchema | null = null) {
      TestBed.configureTestingModule({ imports: [TableHostComponent] });
      const fixture = TestBed.createComponent(TableHostComponent);
      fixture.componentInstance.schema.set(schema);
      fixture.detectChanges();
      await fixture.whenStable();
      const el = fixture.nativeElement as HTMLElement;
      const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
      const render = async () => {
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
      };
      return { fixture, el, q, render, last: () => fixture.componentInstance.emitted.at(-1)! };
    }

    @Component({
      imports: [FormDesignerComponent],
      template: `<bp-form-designer [schema]="schema()" [columns]="columns" (schemaChange)="changed($event)" />`,
    })
    class TableHostComponent {
      readonly schema = signal<JsonSchema | null>(null);
      readonly emitted: JsonSchema[] = [];
      readonly columns = columns;
      changed(schema: JsonSchema): void {
        this.emitted.push(schema);
        this.schema.set(schema);
      }
    }

    it('палітра — колонки таблиці, без типів і вкладок JSON; клік ставить колонку з типом колонки', async () => {
      const { q, render, last, el } = await tableSetup();
      expect(q('fd-kind-text')).toBeNull();
      expect(q('fd-mode-json')).toBeNull();
      expect(el.querySelectorAll('[data-column]').length).toBe(4);
      q('fd-col-starts_at')!.click();
      await render();
      expect((last()['properties'] as Record<string, JsonSchema>)['starts_at']).toEqual({ type: 'string', title: 'starts_at', format: 'date-time' });
      expect(q('fd-col-starts_at')).toBeNull();
      expect(q('fd-prop-column')?.textContent).toContain('starts_at');
      expect(q('fd-prop-key')).toBeNull();
      expect(q('fd-duplicate')).toBeNull();
      expect(q('fd-delete')?.getAttribute('aria-label')).toBe('Прибрати з форми');
    });

    it('«Додати всі» і повернення колонки в палітру після «Прибрати з форми»', async () => {
      const { q, render, last } = await tableSetup();
      q('fd-add-all')!.click();
      await render();
      expect(Object.keys(last()['properties'] as object)).toEqual(['name', 'starts_at', 'status', 'minutes']);
      expect((last()['properties'] as Record<string, JsonSchema>)['status']['oneOf']).toEqual([
        { const: 'active', title: 'active' },
        { const: 'cancelled', title: 'cancelled' },
      ]);
      expect(q('fd-columns-empty')).not.toBeNull();
      q('fd-card-minutes')!.click();
      await render();
      q('fd-delete')!.click();
      await render();
      expect(q('fd-col-minutes')).not.toBeNull();
      expect(Object.keys(last()['properties'] as object)).not.toContain('minutes');
    });

    it('підпис, підказка, плейсхолдер, обов’язкове й ширини працюють, обмежень типу немає', async () => {
      const { q, render, last } = await tableSetup({ type: 'object', properties: { name: { type: 'string', title: 'name' }, minutes: { type: 'number', title: 'minutes' } } });
      q('fd-card-name')!.click();
      await render();
      expect(q('fd-prop-minLength')).toBeNull();
      expect(q('fd-prop-pattern')).toBeNull();
      const title = q('fd-prop-title') as HTMLInputElement;
      title.value = 'Ім’я клієнта';
      title.dispatchEvent(new Event('input'));
      (q('fd-prop-required')!.querySelector('input') as HTMLInputElement).click();
      await render();
      expect(last()['required']).toEqual(['name']);
      expect((last()['properties'] as Record<string, JsonSchema>)['name']['title']).toBe('Ім’я клієнта');
      q('fd-card-minutes')!.click();
      await render();
      q('fd-join')!.click();
      await render();
      expect(layoutOf(last())).toEqual([[['name', 'minutes']]]);
      expect(q('fd-row-preset-6-6')).not.toBeNull();
    });
  });

  describe('«Показувати, якщо» (F11-06)', () => {
    const withKind: JsonSchema = {
      type: 'object',
      properties: {
        kind: { type: 'string', title: 'Вид', oneOf: [{ const: 'annual', title: 'Щорічна' }, { const: 'other', title: 'Інше' }] },
        reason: { type: 'string', title: 'Причина' },
        agree: { type: 'boolean', title: 'Згода' },
      },
    };
    const layout = (schema: JsonSchema) => schema['x-openbp-layout'] as { sections: { visibleIf?: unknown; rows: { field: string; visibleIf?: unknown }[][] }[] };

    it('умова поля: джерело — інші поля, значення — варіанти списку; «Завжди» прибирає умову', async () => {
      const { q, render, last } = await setup(withKind);
      q('fd-card-reason')!.click();
      await render();
      expect(selectOptionValues(q('fd-if-field')!)).toEqual(['', 'kind', 'agree']);

      chooseOption(q('fd-if-field')!, 'kind');
      await render();
      expect(layout(last()).sections[0].rows[1][0].visibleIf).toEqual({ field: 'kind', operator: 'eq', value: 'annual' });
      expect(selectOptionValues(q('fd-if-value')!)).toEqual(['annual', 'other']);
      chooseOption(q('fd-if-value')!, 'other');
      await render();
      expect(layout(last()).sections[0].rows[1][0].visibleIf).toEqual({ field: 'kind', operator: 'eq', value: 'other' });

      chooseOption(q('fd-if-operator')!, 'isEmpty');
      await render();
      expect(layout(last()).sections[0].rows[1][0].visibleIf).toEqual({ field: 'kind', operator: 'isEmpty' });
      expect(q('fd-if-value')).toBeNull();

      chooseOption(q('fd-if-field')!, '');
      await render();
      expect(layout(last()).sections[0].rows[1][0]).toEqual({ field: 'reason' });
    });

    it('умова секції: джерело — поля поза секцією, «Так/Ні» для прапорця', async () => {
      const { q, render, last } = await setup({
        ...withKind,
        'x-openbp-layout': { sections: [{ rows: [[{ field: 'agree' }]] }, { title: 'Деталі', rows: [[{ field: 'kind' }], [{ field: 'reason' }]] }] },
      });
      q('fd-section-title')!.click();
      await render();
      const titles = Array.from(document.querySelectorAll<HTMLElement>('[data-testid="fd-section-title"]'));
      titles[1].click();
      await render();
      expect(selectOptionValues(q('fd-section-if-field')!)).toEqual(['', 'agree']);
      chooseOption(q('fd-section-if-field')!, 'agree');
      await render();
      expect(layout(last()).sections[1].visibleIf).toEqual({ field: 'agree', operator: 'eq', value: true });
      expect(selectOptionValues(q('fd-section-if-value')!)).toEqual([true, false]);
    });

    it('видалення поля-джерела прибирає умови з попередженням', async () => {
      const { q, render, last } = await setup({
        ...withKind,
        'x-openbp-layout': {
          sections: [
            { rows: [[{ field: 'kind' }], [{ field: 'reason', visibleIf: { field: 'kind', operator: 'eq', value: 'other' } }]] },
            { title: 'Згода', visibleIf: { field: 'kind', operator: 'isNotEmpty' }, rows: [[{ field: 'agree' }]] },
          ],
        },
      });
      q('fd-card-kind')!.click();
      await render();
      q('fd-delete')!.click();
      await render();
      const sections = layout(last()).sections;
      expect(sections[0].rows[0][0]).toEqual({ field: 'reason' });
      expect(sections[1].visibleIf).toBeUndefined();
      expect(q('fd-notice')?.textContent).toContain('«Вид»');
      expect(q('fd-notice')?.textContent).toContain('«Причина»');
      expect(q('fd-notice')?.textContent).toContain('«Згода»');
    });
  });

  it('«Як показати» (F11-07): «Вибір» — кнопки або список, «Кілька варіантів» — чипи; висота багаторядкового', async () => {
    const { q, render, type, last } = await setup(null);
    q('fd-kind-select')!.click();
    await render();
    const key = Object.keys(last()['properties'] as object)[0];
    const props = () => (last()['properties'] as Record<string, JsonSchema>);
    expect(selectValue(q('fd-prop-widget')!)).toBe('radio');
    chooseOption(q('fd-prop-widget')!, '');
    await render();
    expect(props()[key]['x-openbp-ui']).toBeUndefined();

    q('fd-kind-multiselect')!.click();
    await render();
    const multi = Object.keys(props())[1];
    expect(selectOptionValues(q('fd-prop-widget')!)).toEqual(['', 'chips']);
    chooseOption(q('fd-prop-widget')!, 'chips');
    await render();
    expect(props()[multi]['x-openbp-ui']).toEqual({ widget: 'chips' });

    q('fd-kind-textarea')!.click();
    await render();
    expect(q('fd-prop-widget')).toBeNull();
    await type('fd-prop-rows', '3');
    expect(props()[Object.keys(props())[2]]['x-openbp-ui']).toEqual({ rows: 3 });
  });
});
