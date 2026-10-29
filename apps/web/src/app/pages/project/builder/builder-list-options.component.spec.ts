import { TestBed } from '@angular/core/testing';
import { PageColumn } from '../../../projects/project-pages-api.service';
import { DataTableSummary } from '../../../tables/data-tables-api.service';
import { BuilderListOptionsComponent, ListExtras } from './builder-list-options.component';

const tables = [
  { id: 't1', name: 'slots', columns: [{ name: 'starts_at', type: 'datetime' }, { name: 'status', type: 'enum' }] },
  { id: 't2', name: 'bookings', columns: [{ name: 'slot_id', type: 'string' }, { name: 'minutes', type: 'number' }] },
] as unknown as DataTableSummary[];

describe('BuilderListOptionsComponent (F8-19)', () => {
  async function setup(extras: ListExtras, selected: PageColumn[] = [{ column: 'status' }]) {
    TestBed.configureTestingModule({});
    const fixture = TestBed.createComponent(BuilderListOptionsComponent);
    fixture.componentRef.setInput('extras', extras);
    fixture.componentRef.setInput('columnNames', ['starts_at', 'status']);
    fixture.componentRef.setInput('selected', selected);
    fixture.componentRef.setInput('tables', tables);
    fixture.componentRef.setInput('pageKeys', ['booking-form']);
    const extrasOut: ListExtras[] = [];
    const columnsOut: PageColumn[][] = [];
    fixture.componentInstance.extrasChange.subscribe((e) => extrasOut.push(e));
    fixture.componentInstance.columnsChange.subscribe((c) => columnsOut.push(c));
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return { fixture, extrasOut, columnsOut, host: fixture.nativeElement as HTMLElement };
  }

  const change = (el: Element, value: string, event = 'change') => {
    (el as HTMLInputElement).value = value;
    el.dispatchEvent(new Event(event));
  };

  it('формат і підписи значень колонки віддає наверх, невідомий колір відкидає', async () => {
    const { host, columnsOut } = await setup({});
    const row = host.querySelector('[data-column-format="status"]')!;
    change(row.querySelectorAll('select')[0], 'badge');
    expect(columnsOut.at(-1)).toEqual([{ column: 'status', format: 'badge' }]);
    change(row.querySelector('textarea')!, 'open=Вільно|green\nbusy=Зайнято|pink');
    expect(columnsOut.at(-1)).toEqual([{ column: 'status', labels: { open: 'Вільно', busy: 'Зайнято' }, colors: { open: 'green' } }]);
  });

  it('обчислювана колонка: додається, зв’язок береться з колонок підлеглої таблиці', async () => {
    const { host, extrasOut, fixture } = await setup({});
    host.querySelector<HTMLButtonElement>('[data-testid="b-add-computed"]')!.click();
    expect(extrasOut.at(-1)!.computed).toEqual([{ key: '', label: '', table: '', by: '', aggregate: 'count' }]);
    fixture.componentRef.setInput('extras', { computed: [{ key: 'booked', label: 'Записано', table: 'bookings', by: '', aggregate: 'count' }] });
    fixture.detectChanges();
    const by = host.querySelector('[data-computed="0"]')!.querySelectorAll('select')[1] as HTMLSelectElement;
    expect([...by.options].map((o) => o.value)).toEqual(['', 'slot_id', 'minutes']);
  });

  it('картки: вибір показу, порожні розділи не потрапляють у результат', async () => {
    const { host, extrasOut, fixture } = await setup({ empty: 'Нічого' });
    change(host.querySelector('[data-testid="b-layout"]')!, 'cards');
    expect(extrasOut.at(-1)).toEqual({ empty: 'Нічого', layout: 'cards' });
    fixture.componentRef.setInput('extras', { layout: 'cards', empty: 'Нічого' });
    fixture.detectChanges();
    change(host.querySelector('[data-testid="b-layout"]')!, 'table');
    expect(extrasOut.at(-1)).toEqual({ empty: 'Нічого' });
  });

  it('підлеглий список: колонки впорядковуються як у таблиці, кнопка «Додати» відкриває форму панеллю', async () => {
    const { host, extrasOut, fixture } = await setup({ expand: [{ table: 'bookings', by: 'slot_id' }] });
    const boxes = host.querySelectorAll<HTMLInputElement>('[data-expand="0"] input[type="checkbox"]');
    boxes[1].click();
    fixture.componentRef.setInput('extras', extrasOut.at(-1));
    fixture.detectChanges();
    boxes[0].click();
    expect(extrasOut.at(-1)!.expand![0].columns).toEqual([{ column: 'slot_id' }, { column: 'minutes' }]);
    fixture.componentRef.setInput('extras', extrasOut.at(-1));
    fixture.detectChanges();
    const create = host.querySelector('[data-expand="0"]')!.querySelectorAll('select')[2] as HTMLSelectElement;
    change(create, 'booking-form');
    expect(extrasOut.at(-1)!.expand![0].create).toEqual({ page: 'booking-form', mode: 'drawer' });
  });
});
