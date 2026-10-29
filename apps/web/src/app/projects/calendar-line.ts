import { zonedParts } from './calendar-time';
import { CellView, DisplayColumn, cellView } from './page-format';
import type { PageCalendarEvent, PageColor, PageColumn, ProjectPageView } from './project-pages-api.service';

/** Підготовлений показ події календаря: заголовок, підзаголовок, колір мітки й час «10:00–11:00». */
export interface EventLine {
  title: CellView;
  subtitle: string;
  color: PageColor;
  time: string;
}

function displayColumn(view: ProjectPageView, spec: PageColumn | undefined): DisplayColumn | null {
  if (!spec) return null;
  const known = view.columns.find((c) => c.column === spec.column);
  return {
    column: spec.column,
    label: spec.label ?? known?.label ?? spec.column,
    type: known?.type ?? 'string',
    format: spec.format ?? known?.format,
    labels: spec.labels,
    colors: spec.colors,
  };
}

/** Подія календаря для людей: колонки заголовка, підзаголовка й кольору з визначення сторінки, час — у поясі застосунку. */
export function eventLine(view: ProjectPageView, event: PageCalendarEvent, timezone: string): EventLine {
  const calendar = view.page.definition.calendar;
  const title = displayColumn(view, calendar?.title);
  const subtitle = displayColumn(view, calendar?.subtitle);
  const color = displayColumn(view, calendar?.color);
  const clock = (d: Date) => {
    const p = zonedParts(d, timezone);
    return `${String(p.h).padStart(2, '0')}:${String(p.mi).padStart(2, '0')}`;
  };
  return {
    title: title ? cellView(event.row, title, timezone) : { text: event.row.key },
    subtitle: subtitle ? cellView(event.row, subtitle, timezone).text : '',
    color: (color ? cellView(event.row, color, timezone).color : undefined) ?? 'gray',
    time: `${clock(new Date(event.start))}–${clock(new Date(event.end))}`,
  };
}
