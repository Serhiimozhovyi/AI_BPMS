import { WorkflowGroup, WorkflowSummary } from './workflows-api.service';

/**
 * Групування списку сценаріїв (F3-64): секції груп у їхньому порядку, «Без групи» в кінці; фільтр-чіпи груп і пошук.
 * Чисті функції — сторінка тримає стан у сигналах.
 */

/** Id секції і чіпа «Без групи». */
export const NO_GROUP = 'none';

/** Ключ `localStorage` зі станом згортання секцій: `{ [sectionId]: true — згорнута }`. */
export const GROUPS_COLLAPSE_KEY = 'openbp.workflows.collapsed';

/** Секція списку: група (або `null` — «Без групи») і її сценарії після фільтрів. */
export interface WorkflowSection {
  id: string;
  group: WorkflowGroup | null;
  workflows: WorkflowSummary[];
}

/** Фільтр списку: вибрані чіпи (порожньо — усі секції) і пошук за назвою. */
export interface WorkflowSectionFilter {
  groups: ReadonlySet<string>;
  search: string;
}

/** Секція сценарію: id групи, а якщо групи немає серед відомих — «Без групи». */
export function sectionOf(workflow: WorkflowSummary, known: ReadonlySet<string>): string {
  return workflow.groupId && known.has(workflow.groupId) ? workflow.groupId : NO_GROUP;
}

/**
 * Секції для показу. Без пошуку показуються й порожні секції (у них можна перетягнути сценарій); з пошуком — лише ті,
 * де щось знайдено. Вибрані чіпи обмежують і секції, і пошук. Сценарії в секції — у порядку списку з Api.
 */
export function groupSections(groups: readonly WorkflowGroup[], workflows: readonly WorkflowSummary[], filter: WorkflowSectionFilter): WorkflowSection[] {
  const ordered = [...groups].sort((a, b) => a.sortOrder - b.sortOrder);
  const known = new Set(ordered.map((g) => g.id));
  const query = filter.search.trim().toLocaleLowerCase('uk');
  const byId = new Map<string, WorkflowSummary[]>();
  for (const w of workflows) {
    if (query && !w.name.toLocaleLowerCase('uk').includes(query)) continue;
    const id = sectionOf(w, known);
    byId.set(id, [...(byId.get(id) ?? []), w]);
  }
  const sections: WorkflowSection[] = [
    ...ordered.map((g) => ({ id: g.id, group: g, workflows: byId.get(g.id) ?? [] })),
    { id: NO_GROUP, group: null, workflows: byId.get(NO_GROUP) ?? [] },
  ];
  return sections
    .filter((s) => filter.groups.size === 0 || filter.groups.has(s.id))
    .filter((s) => !query || s.workflows.length > 0);
}

/** Групи з новою кількістю сценаріїв — після перенесення, створення чи видалення сценарію. */
export function recount(groups: readonly WorkflowGroup[], workflows: readonly WorkflowSummary[]): WorkflowGroup[] {
  const counts = new Map<string, number>();
  for (const w of workflows) if (w.groupId) counts.set(w.groupId, (counts.get(w.groupId) ?? 0) + 1);
  return groups.map((g) => (g.workflowCount === (counts.get(g.id) ?? 0) ? g : { ...g, workflowCount: counts.get(g.id) ?? 0 }));
}

/** Групи в новому порядку після перетягування секції з позиції `from` на `to` (індекси серед груп). */
export function reorder(groups: readonly WorkflowGroup[], from: number, to: number): WorkflowGroup[] {
  const ordered = [...groups].sort((a, b) => a.sortOrder - b.sortOrder);
  if (from === to || from < 0 || to < 0 || from >= ordered.length || to >= ordered.length) return ordered;
  const [moved] = ordered.splice(from, 1);
  ordered.splice(to, 0, moved);
  return ordered.map((g, i) => (g.sortOrder === i ? g : { ...g, sortOrder: i }));
}

export function readCollapsed(): Record<string, boolean> {
  try {
    const value: unknown = JSON.parse(localStorage.getItem(GROUPS_COLLAPSE_KEY) ?? '{}');
    return value && typeof value === 'object' && !Array.isArray(value)
      ? Object.fromEntries(Object.entries(value).filter(([, v]) => typeof v === 'boolean'))
      : {};
  } catch {
    return {};
  }
}

export function writeCollapsed(collapsed: Record<string, boolean>): void {
  try {
    localStorage.setItem(GROUPS_COLLAPSE_KEY, JSON.stringify(collapsed));
  } catch {
    // Сховище недоступне — стан живе до перезавантаження.
  }
}
