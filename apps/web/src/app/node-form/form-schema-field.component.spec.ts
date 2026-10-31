import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FormGroup } from '@angular/forms';
import { MatDialog } from '@angular/material/dialog';
import { FormlyFieldConfig, FormlyForm, provideFormlyCore } from '@ngx-formly/core';
import { of } from 'rxjs';
import { FORM_SCHEMA_FIELD, schemaFields } from './schema-fields';
import { NodeFormSchemaFieldComponent } from './form-schema-field.component';

const NODE_SCHEMA = {
  type: 'object',
  properties: {
    title: { type: 'string' },
    submitLabel: { type: 'string' },
    schema: { type: 'object', title: 'Поля форми', 'x-openbp-widget': 'form-schema' },
  },
};

@Component({ imports: [FormlyForm], template: `<formly-form [form]="form" [fields]="fields" [model]="model" />` })
class HostComponent {
  form = new FormGroup({});
  model: Record<string, unknown> = { title: 'Відпустка', submitLabel: 'Подати', schema: { type: 'object', properties: { a: { type: 'string', title: 'ПІБ' } } } };
  /** Лише поле схеми: решта полів вузла рендеряться іншими типами, яких тут не реєструємо. */
  fields: FormlyFieldConfig[] = schemaFields(NODE_SCHEMA).filter((f) => f.key === 'schema');
}

describe('поля форми в панелі вузла (F11-04)', () => {
  const opened: { data: unknown }[] = [];
  let result: unknown;

  function setup(model?: Record<string, unknown>) {
    opened.length = 0;
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideFormlyCore({ types: [{ name: FORM_SCHEMA_FIELD, component: NodeFormSchemaFieldComponent }] }),
        { provide: MatDialog, useValue: { open: (_c: unknown, config: { data: unknown }) => (opened.push(config), { afterClosed: () => of(result) }) } },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    if (model) fixture.componentInstance.model = model;
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, q: (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`) };
  }

  it('схема з анотацією x-openbp-widget: form-schema — власний тип поля', () => {
    const fields = schemaFields(NODE_SCHEMA);
    expect(fields.find((f) => f.key === 'schema')?.type).toBe(FORM_SCHEMA_FIELD);
  });

  it('кнопка з лічильником і перелік підписів', () => {
    const { q } = setup();
    expect(q('fs-open')?.textContent).toContain('(1)');
    expect(q('fs-list')?.textContent).toContain('ПІБ');
  });

  it('порожня схема — «Полів ще немає»', () => {
    const { q } = setup({ title: 'T', schema: {} });
    expect(q('fs-empty')).not.toBeNull();
    expect(q('fs-open')?.textContent).toContain('(0)');
  });

  it('більше п’яти полів — «ще N»', () => {
    const properties = Object.fromEntries(['a', 'b', 'c', 'd', 'e', 'f', 'g'].map((k) => [k, { type: 'string', title: k }]));
    const { q } = setup({ schema: { type: 'object', properties } });
    expect(q('fs-more')?.textContent).toContain('2');
  });

  it('вираз у параметрі — конструктор недоступний', () => {
    const { q } = setup({ schema: '{{ $json.schema }}' });
    expect(q('fs-raw')).not.toBeNull();
    expect(q('fs-open')).toBeNull();
  });

  it('діалог отримує схему й заголовок з вузла; збереження пишеться в параметр, скасування — ні', () => {
    const next = { type: 'object', properties: { x: { type: 'string', title: 'Ікс' } } };
    const { fixture, q } = setup();
    result = next;
    q('fs-open')!.click();
    fixture.detectChanges();
    const data = opened[0].data as { schema: unknown; title: string; submitLabel: string };
    expect(data.title).toBe('Відпустка');
    expect(data.submitLabel).toBe('Подати');
    expect(fixture.componentInstance.model['schema']).toEqual(next);
    expect(q('fs-list')?.textContent).toContain('Ікс');

    result = undefined;
    q('fs-open')!.click();
    expect(fixture.componentInstance.model['schema']).toEqual(next);
  });
});
