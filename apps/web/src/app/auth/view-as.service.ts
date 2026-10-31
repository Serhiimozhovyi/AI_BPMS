import { Injectable, computed, signal } from '@angular/core';

/** Ключ `sessionStorage`: режим перегляду живе у вкладці й не переживає закриття браузера. */
export const VIEW_AS_KEY = 'openbp.viewAs';

/** Заголовок запиту, що вмикає режим на сервері (F12-01, `docs/projects.md` §4.1). */
export const VIEW_AS_HEADER = 'X-OpenBP-View-As';

/**
 * Режим «переглянути як» (F12-02): чиїми очима дивимось на проєкт. Або роль (`role` + необов'язкова `appRole`), або
 * конкретний учасник (`memberId`, лише admin). Назви — для смуги режиму: сервер повертає лише ключі.
 */
export interface ViewAsMode {
  projectId: string;
  projectName: string;
  role?: 'editor' | 'viewer';
  appRole?: string | null;
  memberId?: string;
  /** Куди повернутись після виходу з перегляду (сторінка, з якої його ввімкнули). */
  returnUrl?: string;
  /** Що показати в смузі: «Координатор», «Редактор проєкту», «olena@firma.ua». */
  label: string;
}

/** Значення заголовка `X-OpenBP-View-As` для режиму. */
export function viewAsHeader(mode: ViewAsMode): string {
  const parts = [`project=${encodeURIComponent(mode.projectId)}`];
  if (mode.memberId) {
    parts.push(`member=${encodeURIComponent(mode.memberId)}`);
  } else {
    parts.push(`role=${mode.role ?? 'viewer'}`);
    if (mode.appRole) parts.push(`appRole=${encodeURIComponent(mode.appRole)}`);
  }
  return parts.join('&');
}

/**
 * Стан режиму перегляду (F12-02). Права звужує сервер за заголовком, який додає {@link viewAsInterceptor}; тут лише
 * вибір і його збереження в `sessionStorage`. Після зміни режиму shell перечитує користувача й проєкти.
 */
@Injectable({ providedIn: 'root' })
export class ViewAsService {
  readonly mode = signal<ViewAsMode | null>(readMode());
  readonly active = computed(() => this.mode() !== null);
  /** Значення заголовка або `null` поза режимом. */
  readonly header = computed(() => {
    const mode = this.mode();
    return mode ? viewAsHeader(mode) : null;
  });

  set(mode: ViewAsMode | null): void {
    this.mode.set(mode);
    try {
      if (mode) sessionStorage.setItem(VIEW_AS_KEY, JSON.stringify(mode));
      else sessionStorage.removeItem(VIEW_AS_KEY);
    } catch {
      // Сховище недоступне — режим живе до перезавантаження.
    }
  }

  clear(): void {
    this.set(null);
  }
}

function readMode(): ViewAsMode | null {
  try {
    const raw = sessionStorage.getItem(VIEW_AS_KEY);
    if (!raw) return null;
    const mode = JSON.parse(raw) as Partial<ViewAsMode>;
    return typeof mode.projectId === 'string' && typeof mode.label === 'string' ? (mode as ViewAsMode) : null;
  } catch {
    return null;
  }
}
