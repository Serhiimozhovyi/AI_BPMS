import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { PageBlock, PageCalendar, PagePreview, ProjectPageView } from '../../../projects/project-pages-api.service';
import { BuilderCalendarComponent } from './builder-calendar.component';
import { BuilderDashboardComponent } from './builder-dashboard.component';
import { PagePreviewComponent } from './page-preview.component';

async function render(fixture: ComponentFixture<unknown>) {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function change(el: Element, value: string, event = 'change') {
  (el as HTMLInputElement).value = value;
  el.dispatchEvent(new Event(event));
}

describe('BuilderCalendarComponent (F8-19)', () => {
  async function setup(calendar: PageCalendar) {
    TestBed.configureTestingModule({});
    const fixture = TestBed.createComponent(BuilderCalendarComponent);
    fixture.componentRef.setInput('calendar', calendar);
    fixture.componentRef.setInput('columns', ['starts_at', 'ends_at', 'minutes', 'note', 'status']);
    fixture.componentRef.setInput('pageKeys', ['slot']);
    const emitted: PageCalendar[] = [];
    fixture.componentInstance.calendarChange.subscribe((c) => emitted.push(c));
    await render(fixture);
    return { fixture, emitted, host: fixture.nativeElement as HTMLElement };
  }

  it('перемикає кінець події між однаковою тривалістю, колонкою тривалості й колонкою кінця', async () => {
    const { host, emitted } = await setup({ start: 'starts_at', defaultMinutes: 45 });
    change(host.querySelector('[data-testid="bc-end-mode"]')!, 'end');
    expect(emitted.at(-1)).toEqual({ start: 'starts_at' });
  });

  it('колір події: колонка, формат і мітки «значення=Підпис|колір»', async () => {
    const { host, emitted, fixture } = await setup({ start: 'starts_at' });
    change(host.querySelector('[data-testid="bc-color"]')!, 'status');
    expect(emitted.at(-1)).toEqual({ start: 'starts_at', color: { column: 'status' } });
    fixture.componentRef.setInput('calendar', emitted.at(-1));
    await render(fixture);
    const labels = host.querySelector('textarea')!;
    change(labels, 'open=Вільно|green\nbooked=Зайнято|blue\nbad=Дивне|pink');
    expect(emitted.at(-1)!.color).toEqual({
      column: 'status',
      labels: { open: 'Вільно', booked: 'Зайнято', bad: 'Дивне' },
      colors: { open: 'green', booked: 'blue' },
    });
  });

  it('режими: не можна зняти останній, стандартний набір не зберігається', async () => {
    const { host, emitted, fixture } = await setup({ start: 'starts_at', views: ['week'], default: 'week' });
    host.querySelector<HTMLInputElement>('[data-view="week"]')!.click();
    expect(emitted).toHaveLength(1);
    expect(emitted[0].views).toEqual(['week']);
    host.querySelector<HTMLInputElement>('[data-view="day"]')!.click();
    expect(emitted.at(-1)!.views).toEqual(['week', 'day']);
    fixture.componentRef.setInput('calendar', emitted.at(-1));
    await render(fixture);
    host.querySelector<HTMLInputElement>('[data-view="month"]')!.click();
    expect(emitted.at(-1)!.views).toBeUndefined();
  });
});

describe('BuilderDashboardComponent (F8-19)', () => {
  async function setup(blocks: PageBlock[]) {
    TestBed.configureTestingModule({});
    const fixture = TestBed.createComponent(BuilderDashboardComponent);
    fixture.componentRef.setInput('blocks', blocks);
    fixture.componentRef.setInput('pageKeys', ['today', 'slot']);
    fixture.componentRef.setInput('workflows', ['cancel']);
    fixture.componentRef.setInput('appRoles', [{ key: 'psychologist', name: 'Психолог' }]);
    const emitted: PageBlock[][] = [];
    fixture.componentInstance.blocksChange.subscribe((b) => emitted.push(b));
    await render(fixture);
    return { fixture, emitted, host: fixture.nativeElement as HTMLElement };
  }

  it('переставляє блоки, і вбудоване визначення блоку зберігається', async () => {
    const inline: PageBlock = { kind: 'list', definition: { table: 'slots' }, width: 6 };
    const { host, emitted } = await setup([{ kind: 'text', text: 'Привіт', width: 12 }, inline]);
    expect(host.querySelector('[data-block="1"]')!.textContent).toContain('вбудоване визначення');
    host.querySelector<HTMLButtonElement>('[data-block="1"] button[aria-label="Вище"]')!.click();
    expect(emitted.at(-1)).toEqual([inline, { kind: 'text', text: 'Привіт', width: 12 }]);
  });

  it('зміна виду блоку лишає підпис, ширину й умову, а решту скидає', async () => {
    const { host, emitted } = await setup([{ kind: 'list', page: 'today', label: 'Сьогодні', width: 6, limit: 5, when: "{{ $user.id }}" }]);
    change(host.querySelector('[data-block="0"] select')!, 'calendar');
    expect(emitted.at(-1)).toEqual([{ kind: 'calendar', label: 'Сьогодні', width: 6, when: '{{ $user.id }}' }]);
  });

  it('кнопка блоку: сценарій, підтвердження й ролі', async () => {
    const { host, emitted, fixture } = await setup([{ kind: 'actions', width: 12, actions: [{ label: 'Скасувати', page: '' }] }]);
    change(host.querySelector('[data-block-action="0"]')!.querySelectorAll('select')[0], 'workflow');
    fixture.componentRef.setInput('blocks', emitted.at(-1));
    await render(fixture);
    const selects = host.querySelector('[data-block-action="0"]')!.querySelectorAll('select');
    change(selects[1], 'cancel');
    fixture.componentRef.setInput('blocks', emitted.at(-1));
    await render(fixture);
    host.querySelector<HTMLInputElement>('[data-block-action="0"] input[type="checkbox"]')!.click();
    expect(emitted.at(-1)![0].actions).toEqual([{ label: 'Скасувати', workflow: 'cancel', roles: ['psychologist'] }]);
  });
});

describe('PagePreviewComponent (F8-19)', () => {
  const view = (kind: ProjectPageView['page']['kind'], definition: ProjectPageView['page']['definition']): ProjectPageView => ({
    page: { id: 'x', key: 'preview', name: 'Перегляд', kind, definition, roles: [], sortOrder: 0, updatedAt: '2026-10-29T00:00:00Z' },
    tableId: 't1',
    columns: [
      { column: 'name', label: 'Імʼя', type: 'string' },
      { column: 'status', label: 'Стан', type: 'enum' },
    ],
    schema: { type: 'object', properties: { name: { type: 'string', title: 'Імʼя' } } },
    canEdit: true,
    timezone: 'Europe/Kyiv',
  });
  const rows = { items: [{ key: '1', data: { name: 'Оля', status: 'open' }, display: {}, actions: [] }], page: 1, pageSize: 10, total: 1 };

  async function show(data: PagePreview) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(PagePreviewComponent);
    fixture.componentRef.setInput('data', data);
    await render(fixture);
    return { fixture, host: fixture.nativeElement as HTMLElement, http: TestBed.inject(HttpTestingController) };
  }

  it('список: мітка зі статусом і підписом колонки з визначення', async () => {
    const { host } = await show({
      view: view('list', { table: 't', columns: [{ column: 'name', label: 'Клієнт' }, { column: 'status', format: 'badge', labels: { open: 'Вільно' }, colors: { open: 'green' } }] }),
      rows,
    });
    expect(host.querySelector('th')!.textContent).toContain('Клієнт');
    expect(host.querySelector('tbody')!.textContent).toContain('Вільно');
    expect(host.querySelector('tbody span.rounded')).not.toBeNull();
  });

  it('картки й текст порожнього списку', async () => {
    const def = { table: 't', columns: [{ column: 'name' }, { column: 'status' }], layout: 'cards' as const, card: { title: 'name' }, empty: 'Нікого' };
    const cards = await show({ view: view('list', def), rows });
    expect(cards.host.querySelector('[data-testid="preview-cards"]')!.textContent).toContain('Оля');
    TestBed.resetTestingModule();
    const empty = await show({ view: view('list', def), rows: { ...rows, items: [], total: 0 } });
    expect(empty.host.querySelector('[data-testid="preview-empty"]')!.textContent).toContain('Нікого');
  });

  it('календар, головна й форма показуються без запитів до Api', async () => {
    const events = { from: '', to: '', truncated: false, events: [] };
    const cal = await show({ view: view('calendar', { table: 't', calendar: { start: 'starts_at' } }), events });
    expect(cal.host.querySelector('app-page-calendar')).not.toBeNull();
    cal.http.verify();
    TestBed.resetTestingModule();
    const dash = await show({ view: view('dashboard', { table: '', blocks: [{ kind: 'text', text: 'Привіт' }] }), dashboard: { blocks: [{ index: 0, kind: 'text', width: 12, text: 'Привіт' }] } });
    expect(dash.host.textContent).toContain('Привіт');
    dash.http.verify();
    TestBed.resetTestingModule();
    const form = await show({ view: view('form', { table: 't', fields: [{ column: 'name' }] }) });
    expect(form.host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.disabled).toBe(true);
    form.http.verify();
  });
});
