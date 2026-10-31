import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { FormDesignerDialogComponent } from './form-designer-dialog.component';

const SCHEMA = { type: 'object', properties: { name: { type: 'string', title: 'ПІБ' } } };

describe('діалог конструктора форми (F11-04)', () => {
  const close = vi.fn();

  function setup() {
    close.mockClear();
    TestBed.configureTestingModule({
      imports: [FormDesignerDialogComponent],
      providers: [
        { provide: MAT_DIALOG_DATA, useValue: { schema: SCHEMA, label: 'Поля форми', title: 'Заява', description: 'Опис форми', submitLabel: 'Подати' } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(FormDesignerDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => el.querySelector<HTMLElement>(`[data-testid="${id}"]`);
    const render = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    return { q, render };
  }

  it('«Зберегти» неактивна без змін; після правки віддає нову схему', async () => {
    const { q, render } = setup();
    expect((q('fdd-save') as HTMLButtonElement).disabled).toBe(true);
    q('fd-kind-email')!.click();
    await render();
    expect((q('fdd-save') as HTMLButtonElement).disabled).toBe(false);
    q('fdd-save')!.click();
    const saved = close.mock.calls[0][0] as { properties: Record<string, unknown> };
    expect(Object.keys(saved.properties)).toEqual(['name', 'poshta']);
  });

  it('скасування без змін закриває одразу, зі змінами — питає підтвердження', async () => {
    const { q, render } = setup();
    q('fdd-cancel')!.click();
    expect(close).toHaveBeenCalledWith();
    close.mockClear();
    q('fd-kind-email')!.click();
    await render();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    q('fdd-close')!.click();
    expect(close).not.toHaveBeenCalled();
    confirm.mockReturnValue(true);
    q('fdd-close')!.click();
    expect(close).toHaveBeenCalledWith();
    confirm.mockRestore();
  });

  it('вкладка «Перегляд» показує справжній рендер із заголовком, описом і кнопкою вузла, ширина перемикається', async () => {
    const { q, render } = setup();
    q('fd-mode-preview')!.click();
    await render();
    expect(q('fp-title')?.textContent).toContain('Заява');
    expect(q('fp-description')?.textContent).toContain('Опис форми');
    expect(q('fp-submit')?.textContent).toContain('Подати');
    expect(q('fp-page')?.getAttribute('data-width')).toBe('1024');
    expect(document.body.querySelector('formly-field, .mat-mdc-form-field')).not.toBeNull();
    q('fp-width-390')!.click();
    await render();
    expect(q('fp-page')?.getAttribute('data-width')).toBe('390');
  });

  it('у перегляді секції й рядки з розмітки рендеряться як на /form', async () => {
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [FormDesignerDialogComponent],
      providers: [
        {
          provide: MAT_DIALOG_DATA,
          useValue: {
            label: 'x',
            schema: {
              type: 'object',
              properties: { a: { type: 'string', title: 'А' }, b: { type: 'string', title: 'Б' } },
              'x-openbp-layout': { sections: [{ title: 'Раз', rows: [[{ field: 'a' }, { field: 'b' }]] }] },
            },
          },
        },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(FormDesignerDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLElement>('[data-testid="fd-mode-preview"]')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="form-section"]')).not.toBeNull();
    expect(el.querySelectorAll('.pf-row .pf-col').length).toBe(2);
  });
});
