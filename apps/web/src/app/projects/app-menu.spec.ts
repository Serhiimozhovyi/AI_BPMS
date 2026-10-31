import { isMenuPage, linkedPageKeys, menuPages } from './app-menu';
import { PageDefinition, PageKind, ProjectPage } from './project-pages-api.service';

const page = (key: string, kind: PageKind, definition: Partial<PageDefinition> = {}): ProjectPage => ({
  id: key,
  key,
  name: key,
  kind,
  definition: { table: 't', ...definition },
  roles: [],
  sortOrder: 0,
  updatedAt: '',
});

describe('меню застосунку: службові сторінки', () => {
  const pages = [
    page('today', 'dashboard', { blocks: [{ kind: 'actions', actions: [{ label: 'Додати слот', page: 'slot-new' } as never] }] }),
    page('schedule', 'calendar', { calendar: { start: 'starts_at', open: { page: 'slot-edit', mode: 'drawer' } } }),
    page('bookings', 'list', { create: { page: 'booking-new' } }),
    page('texts', 'form', { onSubmit: { operation: 'upsert' } }),
    page('leave', 'form', { fields: [], onSubmit: { workflow: 'leave' } }),
    page('slot-new', 'form', { fields: [], onSubmit: { operation: 'insert' }, then: { page: 'schedule' } }),
    page('slot-edit', 'form', { fields: [], onSubmit: { operation: 'update' } }),
    page('booking-new', 'form', { fields: [], onSubmit: { operation: 'insert' } }),
    page('booking', 'detail'),
  ];

  it('збирає ключі сторінок, на які посилаються інші', () => {
    expect([...linkedPageKeys(pages)].sort()).toEqual(['booking-new', 'schedule', 'slot-edit', 'slot-new']);
  });

  it('у меню — розділи й самостійні форми, без карток, форм зміни й форм, які відкриває інша сторінка', () => {
    expect(menuPages(pages).map((p) => p.key)).toEqual(['today', 'schedule', 'bookings', 'texts', 'leave']);
  });

  it('форма зміни рядка — службова, навіть якщо на неї ніхто не посилається', () => {
    expect(isMenuPage(page('x-edit', 'form', { onSubmit: { operation: 'update' } }), new Set())).toBe(false);
    expect(isMenuPage(page('x-new', 'form', { onSubmit: { operation: 'insert' } }), new Set())).toBe(true);
  });
});
