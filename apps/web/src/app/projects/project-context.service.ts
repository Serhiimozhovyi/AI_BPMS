import { Injectable, computed, inject, signal } from '@angular/core';
import { NO_GROUP } from '../workflows/workflow-groups';
import { WorkflowGroup, WorkflowsApiService } from '../workflows/workflows-api.service';

/** Ключ `localStorage` з вибраним у шапці проєктом. */
export const PROJECT_KEY = 'openbp.project';

/**
 * Вибраний у шапці проєкт (F6-14): `null` — усі доступні, {@link NO_GROUP} — спільний простір, інакше id проєкту
 * (групи F3-63). Сторінка сценаріїв фільтрує за ним, а нові сценарії, облікові дані й конектори за замовчуванням
 * створюються в ньому. Вибір пам'ятається в `localStorage`.
 */
@Injectable({ providedIn: 'root' })
export class ProjectContextService {
  private readonly api = inject(WorkflowsApiService);

  /** Проєкти, які бачить користувач, у порядку списку. */
  readonly projects = signal<WorkflowGroup[]>([]);
  private readonly selected = signal<string | null>(readSelected());

  /** Вибір, що існує серед доступних проєктів (зниклий проєкт — «усі»). */
  readonly current = computed(() => {
    const id = this.selected();
    return id === null || id === NO_GROUP || this.projects().some((p) => p.id === id) ? id : null;
  });

  /** Вибраний конкретний проєкт; для «усі» і спільного простору — `null`. */
  readonly currentProject = computed(() => this.projects().find((p) => p.id === this.current()) ?? null);

  /** Проєкти, де можна створювати облікові дані й конектори (editor або admin). */
  readonly manageable = computed(() => this.projects().filter((p) => p.role === 'editor' || p.role === 'admin'));

  /** Перечитує список проєктів (після входу, створення чи видалення групи). */
  reload(): void {
    this.api.listGroups().subscribe({
      next: (groups) => this.projects.set([...groups].sort((a, b) => a.sortOrder - b.sortOrder)),
      error: () => this.projects.set([]),
    });
  }

  select(id: string | null): void {
    this.selected.set(id);
    try {
      if (id === null) localStorage.removeItem(PROJECT_KEY);
      else localStorage.setItem(PROJECT_KEY, id);
    } catch {
      // Сховище недоступне — вибір живе до перезавантаження.
    }
  }

  /** Проєкт для нового ресурсу: вибраний у шапці конкретний проєкт, інакше `null` (спільний). */
  defaultProjectId(): string | null {
    return this.currentProject()?.id ?? null;
  }

  /**
   * Куди можна створити credential чи конектор: спільний простір (`id: null`) — лише admin, далі проєкти, де користувач
   * editor (або всі — для admin).
   */
  targets(isAdmin: boolean): ProjectTarget[] {
    const shared: ProjectTarget[] = isAdmin ? [{ id: null, name: $localize`:@@projects.target.shared:Спільні (для всіх проєктів)` }] : [];
    return [...shared, ...this.manageable().map((p) => ({ id: p.id, name: p.name }))];
  }

  /** Початковий вибір для нового ресурсу: проєкт із шапки, якщо в ньому можна створювати, інакше перший доступний. */
  initialTarget(isAdmin: boolean): string | null {
    const targets = this.targets(isAdmin);
    const preferred = this.defaultProjectId();
    return targets.some((t) => t.id === preferred) ? preferred : (targets[0]?.id ?? null);
  }
}

/** Варіант вибору проєкту для нового ресурсу; `id: null` — спільний. */
export interface ProjectTarget {
  id: string | null;
  name: string;
}

function readSelected(): string | null {
  try {
    return localStorage.getItem(PROJECT_KEY);
  } catch {
    return null;
  }
}
