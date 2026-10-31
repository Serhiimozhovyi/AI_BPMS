import { PageKind, ProjectPage } from './project-pages-api.service';

/** Значок Material Symbols пункту меню застосунку за типом сторінки (у формат сторінки значок не входить). */
export const PAGE_KIND_ICONS: Record<PageKind, string> = {
  dashboard: 'space_dashboard',
  calendar: 'calendar_month',
  list: 'list_alt',
  detail: 'description',
  form: 'edit_note',
  metrics: 'monitoring',
};

/** Ключі сторінок, на які посилаються інші сторінки (`create`, `open`, дії, `then`, блоки головної). */
export function linkedPageKeys(pages: readonly ProjectPage[]): Set<string> {
  const keys = new Set<string>();
  const walk = (node: unknown): void => {
    if (Array.isArray(node)) node.forEach(walk);
    else if (node && typeof node === 'object') {
      for (const [k, v] of Object.entries(node)) {
        if (k === 'page' && typeof v === 'string') keys.add(v);
        else walk(v);
      }
    }
  };
  pages.forEach((p) => walk(p.definition));
  return keys;
}

/**
 * Чи може сторінка бути пунктом меню застосунку. Службові сторінки відкриваються лише з інших: картка (`detail`) і форма
 * зміни рядка (`update`) потребують рядка, а форма, на яку веде кнопка іншої сторінки («Слот — новий запис»), — не розділ.
 */
export function isMenuPage(page: ProjectPage, linked: ReadonlySet<string>): boolean {
  if (page.kind === 'detail') return false;
  if (page.kind !== 'form') return true;
  return page.definition?.onSubmit?.operation !== 'update' && !linked.has(page.key);
}

/** Сторінки, що можуть бути пунктами меню, у тому ж порядку. */
export function menuPages(pages: readonly ProjectPage[]): ProjectPage[] {
  const linked = linkedPageKeys(pages);
  return pages.filter((p) => isMenuPage(p, linked));
}
