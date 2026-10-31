import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { PageCalendarEvent, PageCalendarEvents, ProjectPageView } from '../../projects/project-pages-api.service';
import { ExpandOpen } from './page-expand.component';
import { PageCalendarComponent } from './page-calendar.component';

const base = '/api/v1/projects/p1/pages/schedule/calendar';

const view = (extra: Partial<ProjectPageView['page']['definition']> = {}): ProjectPageView => ({
  page: {
    id: 'pg1',
    key: 'schedule',
    name: 'Розклад',
    kind: 'calendar',
    definition: {
      table: 'slots',
      calendar: {
        start: 'starts_at',
        duration: 'duration_min',
        title: { column: 'note' },
        subtitle: { column: 'format', format: 'enum', labels: { online: 'Онлайн' } },
        color: { column: 'status', format: 'badge', colors: { open: 'green', booked: 'blue' } },
        fromHour: 8,
        toHour: 20,
        open: { page: 'slot-card', mode: 'drawer' },
      },
      create: { page: 'slot-form', mode: 'drawer' },
      filters: [{ column: 'status', kind: 'chips', label: 'Стан' }],
      ...extra,
    },
    roles: [],
    sortOrder: 0,
    updatedAt: '2026-11-04T00:00:00Z',
  },
  tableId: 't1',
  columns: [
    { column: 'note', label: 'Нотатка', type: 'string' },
    { column: 'format', label: 'Формат', type: 'enum' },
    { column: 'status', label: 'Стан', type: 'enum' },
  ],
  schema: null,
  canEdit: true,
  timezone: 'Europe/Kyiv',
  filterOptions: { status: [{ value: 'open', label: 'Вільно' }, { value: 'booked', label: 'Зайнято' }] },
});

const event = (key: string, startUtc: string, minutes: number, note: string, status = 'open', actions: number[] = []): PageCalendarEvent => ({
  start: startUtc,
  end: new Date(new Date(startUtc).getTime() + minutes * 60_000).toISOString(),
  row: { key, data: { note, format: 'online', status }, display: {}, actions },
});

const result = (events: PageCalendarEvent[], extra: Partial<PageCalendarEvents> = {}): PageCalendarEvents => ({
  from: '',
  to: '',
  events,
  truncated: false,
  ...extra,
});

describe('PageCalendarComponent (F8-17)', () => {
  let http: HttpTestingController;
  let fixture: ComponentFixture<PageCalendarComponent>;
  let host: HTMLElement;
  const opened: ExpandOpen[] = [];
  const acted: number[] = [];

  async function render() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** Створює календар і відповідає на перший запит подій. */
  async function setup(v: ProjectPageView, first: PageCalendarEvents = result([])) {
    fixture = TestBed.createComponent(PageCalendarComponent);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.componentRef.setInput('view', v);
    fixture.componentInstance.open.subscribe((o) => opened.push(o));
    fixture.componentInstance.act.subscribe((a) => acted.push(a.index));
    host = fixture.nativeElement as HTMLElement;
    await render();
    const req = http.expectOne((r) => r.url === base);
    req.flush(first);
    await render();
    return req.request;
  }

  const q = (selector: string) => host.querySelector<HTMLElement>(selector);
  const all = (selector: string) => Array.from(host.querySelectorAll<HTMLElement>(selector));

  async function nextRequest(flush: PageCalendarEvents = result([])) {
    await render();
    const req = http.expectOne((r) => r.url === base);
    req.flush(flush);
    await render();
    return req.request;
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'], now: new Date('2026-11-04T10:00:00Z') });
    opened.length = 0;
    acted.length = 0;
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.useRealTimers();
    http.verify();
  });

  it('читає події тижня від понеділка до понеділка в поясі застосунку', async () => {
    const request = await setup(view());
    expect(request.params.get('from')).toBe('2026-11-01T22:00:00.000Z');
    expect(request.params.get('to')).toBe('2026-11-08T22:00:00.000Z');
    expect(q('[data-testid="cal-range"]')?.textContent).toContain('2 лист.');
    expect(all('[data-day-head]')).toHaveLength(7);
  });

  it('показує подію в потрібному дні й годині з міткою кольору', async () => {
    await setup(view(), result([event('s1', '2026-11-04T08:00:00Z', 60, 'Консультація', 'booked')]));
    const day = q('[data-day="2026-11-04"]')!;
    const el = day.querySelector<HTMLElement>('[data-event="s1"]')!;
    expect(el.textContent).toContain('Консультація');
    expect(el.textContent).toContain('Онлайн');
    expect(el.style.top).toBe('96px');
    expect(el.className).toContain('bg-bp-info');
    expect(el.getAttribute('aria-label')).toBe('10:00–11:00: Консультація');
  });

  it('клік по події без дій відкриває її картку в панелі', async () => {
    await setup(view(), result([event('s1', '2026-11-04T08:00:00Z', 60, 'Консультація')]));
    q('[data-event="s1"]')!.click();
    expect(opened).toEqual([{ page: 'slot-card', rowKey: 's1', mode: 'drawer' }]);
  });

  it('подія з діями спершу показує панель, а дія повідомляє хост', async () => {
    const withActions = view({ actions: [{ label: 'Закрити слот', workflow: 'close-slot' }] });
    await setup(withActions, result([event('s1', '2026-11-04T08:00:00Z', 60, 'Консультація', 'open', [0])]));
    q('[data-event="s1"]')!.click();
    await render();
    expect(opened).toEqual([]);
    expect(q('[data-testid="cal-selected-title"]')?.textContent).toBe('Консультація');
    q('[data-testid="cal-open"]')!.click();
    expect(opened).toEqual([{ page: 'slot-card', rowKey: 's1', mode: 'drawer' }]);
    q('[data-action="0"]')!.click();
    expect(acted).toEqual([0]);
    q('[data-testid="cal-selected-close"]')!.click();
    await render();
    expect(q('[data-testid="cal-selected"]')).toBeNull();
  });

  it('клік по вільній клітинці відкриває форму створення з часом початку', async () => {
    await setup(view());
    q('[data-slot="2026-11-05T14:00"]')!.click();
    expect(opened).toEqual([{ page: 'slot-form', mode: 'drawer', prefill: { starts_at: '2026-11-05T14:00' } }]);
  });

  it('без create клітинок для створення немає', async () => {
    await setup(view({ create: undefined }));
    expect(all('[data-slot]')).toHaveLength(0);
  });

  it('стрілки переходять між клітинками', async () => {
    await setup(view());
    const first = q('[data-slot="2026-11-04T10:00"]')!;
    first.focus();
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(q('[data-slot="2026-11-05T10:00"]'));
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(q('[data-slot="2026-11-05T11:00"]'));
    // Поза сіткою рух зупиняється.
    q('[data-slot="2026-11-05T19:00"]')!.focus();
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(q('[data-slot="2026-11-05T19:00"]'));
  });

  it('події, що перетинаються, ділять день на смуги', async () => {
    await setup(
      view(),
      result([event('a', '2026-11-04T08:00:00Z', 60, 'Перша'), event('b', '2026-11-04T08:30:00Z', 60, 'Друга'), event('c', '2026-11-04T12:00:00Z', 30, 'Окрема')]),
    );
    expect(q('[data-event="a"]')!.style.width).toBe('50%');
    expect(q('[data-event="b"]')!.style.left).toBe('50%');
    expect(q('[data-event="c"]')!.style.width).toBe('100%');
  });

  it('події поза годинами сітки не губляться', async () => {
    await setup(view(), result([event('late', '2026-11-04T19:00:00Z', 30, 'Пізня')]));
    expect(q('[data-testid="cal-outside"] [data-event="late"]')?.textContent).toContain('Пізня');
  });

  it('вигляд «День» запитує одну добу, «Місяць» — повні тижні', async () => {
    await setup(view());
    q('[data-cal-view="day"]')!.click();
    const day = await nextRequest();
    expect(day.params.get('from')).toBe('2026-11-03T22:00:00.000Z');
    expect(day.params.get('to')).toBe('2026-11-04T22:00:00.000Z');
    expect(all('[data-day-head]')).toHaveLength(1);

    q('[data-cal-view="month"]')!.click();
    const month = await nextRequest();
    expect(month.params.get('from')).toBe('2026-10-25T22:00:00.000Z');
    expect(all('[data-day]')).toHaveLength(42);
    expect(q('[data-testid="cal-range"]')?.textContent).toContain('листопад 2026');
  });

  it('місяць: найбільше три події в дні, решта — «ще N», клік по дню відкриває його', async () => {
    await setup(view(), result([]));
    q('[data-cal-view="month"]')!.click();
    await nextRequest(
      result([1, 2, 3, 4, 5].map((n) => event(`m${n}`, `2026-11-04T0${n}:00:00Z`, 30, `Подія ${n}`))),
    );
    expect(all('[data-day="2026-11-04"] [data-event]')).toHaveLength(3);
    expect(q('[data-more="2026-11-04"]')?.textContent).toContain('ще 2');
    q('[data-more="2026-11-04"]')!.click();
    await nextRequest();
    expect(q('[data-testid="cal-grid"]')?.getAttribute('data-view')).toBe('day');
  });

  it('кнопки «раніше», «пізніше» і «сьогодні» зсувають діапазон', async () => {
    await setup(view());
    q('[data-testid="cal-next"]')!.click();
    const next = await nextRequest();
    expect(next.params.get('from')).toBe('2026-11-08T22:00:00.000Z');
    q('[data-testid="cal-prev"]')!.click();
    q('[data-testid="cal-prev"]')!.click();
    const prev = await nextRequest();
    expect(prev.params.get('from')).toBe('2026-10-25T22:00:00.000Z');
    q('[data-testid="cal-today"]')!.click();
    const today = await nextRequest();
    expect(today.params.get('from')).toBe('2026-11-01T22:00:00.000Z');
  });

  it('вигляд за замовчуванням і перелік виглядів беруться з визначення', async () => {
    const v = view();
    v.page.definition.calendar = { ...v.page.definition.calendar!, views: ['day', 'month'], default: 'day' };
    await setup(v);
    expect(all('[data-cal-view]').map((b) => b.getAttribute('data-cal-view'))).toEqual(['day', 'month']);
    expect(all('[data-day-head]')).toHaveLength(1);
  });

  it('фільтри: значення за замовчуванням з Api видно, зміна перечитує події', async () => {
    await setup(view(), result([], { filters: { status: { values: ['open'] } } }));
    expect(q('[data-chip="open"]')?.getAttribute('aria-pressed')).toBe('true');
    q('[data-chip="booked"]')!.click();
    const request = await nextRequest();
    expect(request.params.getAll('filters[status]')).toEqual(['open', 'booked']);
  });

  it('порожній календар, ліміт подій і помилка', async () => {
    await setup(view({ empty: 'Слотів немає' }), result([], { truncated: true }));
    expect(q('[data-testid="cal-empty"]')?.textContent).toBe('Слотів немає');
    expect(q('[data-testid="cal-truncated"]')).not.toBeNull();

    q('[data-testid="cal-next"]')!.click();
    await render();
    http.expectOne((r) => r.url === base).flush({ title: 'Помилка', detail: 'Не вдалося' }, { status: 500, statusText: 'Server Error' });
    await render();
    expect(q('[data-testid="cal-error"]')).not.toBeNull();
  });

  it('нова версія даних перечитує події', async () => {
    await setup(view(), result([event('s1', '2026-11-04T08:00:00Z', 60, 'Консультація')]));
    fixture.componentRef.setInput('version', 1);
    await nextRequest(result([]));
    expect(q('[data-event="s1"]')).toBeNull();
  });
});
