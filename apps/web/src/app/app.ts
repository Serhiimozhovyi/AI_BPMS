import { BreakpointObserver } from '@angular/cdk/layout';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, afterRenderEffect, computed, effect, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatSelectModule } from '@angular/material/select';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { GlobalAssistantComponent } from './assistant/global-assistant.component';
import { GlobalAssistantService } from './assistant/global-assistant.service';
import { AuthService } from './auth/auth.service';
import { ViewAsDialogComponent, ViewAsDialogData } from './auth/view-as-dialog.component';
import { ViewAsMode, ViewAsService } from './auth/view-as.service';
import { ConnectivityService } from './connectivity/connectivity.service';
import { AppBarService } from './layout/app-bar.service';
import { MOBILE_QUERY } from './layout/breakpoints';
import { ProjectContextService } from './projects/project-context.service';
import { NO_GROUP } from './workflows/workflow-groups';
import { SelectSearchComponent } from './shared/select-search/select-search.component';

@Component({
  imports: [GlobalAssistantComponent, RouterOutlet, RouterLink, RouterLinkActive, NgTemplateOutlet, MatButtonModule, MatSelectModule, SelectSearchComponent],
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App {
  private readonly router = inject(Router);
  private readonly auth = inject(AuthService);
  protected readonly user = this.auth.user;
  protected readonly projects = inject(ProjectContextService);
  protected readonly connectivity = inject(ConnectivityService);
  protected readonly assistant = inject(GlobalAssistantService);
  protected readonly viewAs = inject(ViewAsService);
  private readonly dialog = inject(MatDialog);
  protected readonly sharedSpace = NO_GROUP;
  /** Телефон (< 768 px): навігація, проєкт і користувач — у drawer за кнопкою-бургером (F3-84). */
  protected readonly mobile = toSignal(inject(BreakpointObserver).observe(MOBILE_QUERY).pipe(map((s) => s.matches)), {
    initialValue: false,
  });
  protected readonly drawerOpen = signal(false);
  /** Користувач лише з роллю застосунку (F8-04): меню «Застосунок» і «Огляд» його проєкту. */
  protected readonly appOnly = computed(() => this.projects.appOnlyProject(this.user()?.role));
  /** Оболонка застосунку проєкту (F8-18) у верхній смузі замість меню AI_BPMS — для користувача лише застосунку (F9-04). */
  private readonly appBarService = inject(AppBarService);
  protected readonly appBar = computed(() => {
    const project = this.appOnly();
    const bar = this.appBarService.bar();
    return project && bar?.projectId === project.id ? bar : null;
  });
  /** Ініціали для аватара: «olena.bondar@…» → «OB», «admin@…» → «A». */
  protected readonly initials = computed(() => userInitials(this.user()?.email));
  protected readonly userMenuOpen = signal(false);
  /**
   * Проєкт, який можна переглянути «як користувач» (F12-02): з адреси `/projects/:id…` або вибраний у шапці; лише для
   * admin і editor проєкту й поза режимом перегляду.
   */
  protected readonly viewAsProject = computed(() => {
    if (this.viewAs.active()) return null;
    const id = /^\/projects\/([^/?#]+)/.exec(this.url() || this.router.url)?.[1] ?? this.projects.current();
    const project = this.projects.projects().find((p) => p.id === id) ?? null;
    if (!project) return null;
    return this.user()?.role === 'admin' || project.role === 'editor' || project.role === 'admin' ? project : null;
  });
  /** Стрілки прокрутки меню, коли пункти не вміщаються (Figma «Molecules / Menu», прокрутка). */
  protected readonly canScrollStart = signal(false);
  protected readonly canScrollEnd = signal(false);
  private readonly navEl = viewChild<ElementRef<HTMLElement>>('navEl');
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: '' },
  );
  /** Публічна форма (`/form/…`, F6-03) — без шапки й меню застосунку. */
  protected readonly bare = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => isBareUrl(e.urlAfterRedirects)),
    ),
    { initialValue: isBareUrl(typeof location === 'undefined' ? '' : location.pathname) },
  );

  constructor() {
    // Перехід за пунктом drawer закриває його й меню користувача; активний пункт прокрутного меню лишається видимим.
    this.router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd), takeUntilDestroyed()).subscribe(() => {
      this.drawerOpen.set(false);
      this.userMenuOpen.set(false);
      setTimeout(() => {
        document.querySelector('.shell-nav .shell-nav__link.active')?.scrollIntoView?.({ inline: 'nearest', block: 'nearest' });
        this.updateScroll();
      });
    });
    // Стрілки прокрутки — за шириною меню: після рендеру й при зміні розміру.
    const destroyRef = inject(DestroyRef);
    let observed: HTMLElement | null = null;
    const resize = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(() => this.updateScroll());
    destroyRef.onDestroy(() => resize?.disconnect());
    afterRenderEffect(() => {
      const el = this.navEl()?.nativeElement ?? null;
      this.appBar();
      if (el !== observed) {
        resize?.disconnect();
        if (el) resize?.observe(el);
        observed = el;
      }
      this.updateScroll();
    });
    // Розширили вікно до десктопа — drawer не лишається відкритим «позаду».
    effect(() => {
      if (!this.mobile()) this.drawerOpen.set(false);
    });
    // Проєкти (F6-14) залежать від користувача: після входу — перечитати, після виходу — забути.
    effect(() => {
      if (this.user()) this.projects.reload();
      else this.projects.projects.set([]);
    });
    // Режим перегляду змінився — користувач і проєкти тепер інші: перечитати (перший запуск — це вже зробив guard).
    let lastHeader = this.viewAs.header();
    effect(() => {
      const header = this.viewAs.header();
      if (header === lastHeader) return;
      lastHeader = header;
      this.auth.me().subscribe({ next: () => this.projects.reload(), error: () => this.projects.reload() });
    });
    // Користувач лише застосунку з головної — одразу в застосунок свого проєкту (F8-04).
    effect(() => {
      const project = this.appOnly();
      if (project && this.url() === '/') void this.router.navigate(['/projects', project.id, 'app'], { replaceUrl: true });
    });
  }

  protected toggleDrawer(): void {
    this.drawerOpen.update((open) => !open);
  }

  @HostListener('document:keydown.escape')
  protected closeDrawer(): void {
    this.drawerOpen.set(false);
    if (this.userMenuOpen()) {
      this.userMenuOpen.set(false);
      this.host.nativeElement.querySelector<HTMLElement>('[data-testid="user-menu"]')?.focus();
    }
  }

  protected toggleUserMenu(): void {
    this.userMenuOpen.update((open) => !open);
    if (this.userMenuOpen()) setTimeout(() => this.menuItems()[0]?.focus());
  }

  protected closeUserMenu(): void {
    this.userMenuOpen.set(false);
  }

  /** Клік поза меню користувача закриває його. */
  @HostListener('document:click', ['$event'])
  protected outsideClick(event: MouseEvent): void {
    if (this.userMenuOpen() && !(event.target as HTMLElement | null)?.closest?.('.shell-user-menu')) this.userMenuOpen.set(false);
  }

  /** Стрілки ↑/↓, Home/End — між пунктами меню користувача. */
  protected menuKeydown(event: KeyboardEvent): void {
    const items = this.menuItems();
    const at = items.indexOf(document.activeElement as HTMLElement);
    const next = { ArrowDown: at + 1, ArrowUp: at - 1, Home: 0, End: items.length - 1 }[event.key];
    if (next === undefined || items.length === 0) return;
    event.preventDefault();
    items[(next + items.length) % items.length].focus();
  }

  private menuItems(): HTMLElement[] {
    return [...this.host.nativeElement.querySelectorAll<HTMLElement>('#shell-user-menu [role="menuitem"]')];
  }

  /** Чи є що прокручувати вліво/вправо в меню. */
  protected updateScroll(): void {
    const el = this.navEl()?.nativeElement;
    if (!el) {
      this.canScrollStart.set(false);
      this.canScrollEnd.set(false);
      return;
    }
    this.canScrollStart.set(el.scrollLeft > 1);
    this.canScrollEnd.set(el.scrollLeft + el.clientWidth < el.scrollWidth - 1);
  }

  protected scrollNav(direction: 1 | -1): void {
    const el = this.navEl()?.nativeElement;
    el?.scrollBy?.({ left: direction * Math.max(120, el.clientWidth * 0.7), behavior: 'smooth' });
  }

  /**
   * Вибір у шапці: порожнє значення — усі проєкти. На сторінці проєкту вибір іншого проєкту відкриває ту саму вкладку
   * в ньому (сторінки застосунку — його типову вкладку), а «усі» чи спільний простір — список проєктів: інакше заголовок
   * і «Учасники» лишалися б від попереднього проєкту, а вкладки фільтрувалися б уже за новим.
   */
  protected selectProject(value: string): void {
    const id = value === '' ? null : value;
    this.projects.select(id);
    const target = projectSwitchTarget(this.router.url, id);
    if (target) void this.router.navigate(target);
  }

  /** «Переглянути як…» (F12-02): вибір ролі в діалозі → режим перегляду і перехід у проєкт. */
  protected openViewAs(): void {
    const project = this.viewAsProject();
    this.userMenuOpen.set(false);
    if (!project) return;
    this.dialog
      .open<ViewAsDialogComponent, ViewAsDialogData, ViewAsMode>(ViewAsDialogComponent, {
        data: { project, isAdmin: this.user()?.role === 'admin' },
        autoFocus: 'first-tabbable',
      })
      .afterClosed()
      .subscribe((mode) => {
        if (!mode) return;
        this.viewAs.set(mode);
        const app = !!mode.appRole || !!mode.memberId;
        void this.router.navigate(app ? ['/projects', mode.projectId, 'app'] : ['/projects', mode.projectId]);
      });
  }

  /** Вийти з перегляду: знову власні права, сторінка проєкту, який переглядали. */
  protected exitViewAs(): void {
    const mode = this.viewAs.mode();
    this.userMenuOpen.set(false);
    this.viewAs.clear();
    if (mode) void this.router.navigate(['/projects', mode.projectId]);
  }

  /** Вихід: сесію закрито на Api — на сторінку входу; якщо Api недоступний, користувача все одно забуваємо. */
  protected logout(): void {
    this.userMenuOpen.set(false);
    this.viewAs.clear();
    this.auth.logout().subscribe({
      next: () => void this.router.navigate(['/login']),
      error: () => {
        this.auth.forget();
        void this.router.navigate(['/login']);
      },
    });
  }
}

/** Ініціали з email: частини імені до «@», розділені «.», «_» чи «-» — до двох літер. */
export function userInitials(email: string | null | undefined): string {
  const name = (email ?? '').split('@')[0];
  const parts = name.split(/[._-]+/).filter(Boolean);
  return parts
    .slice(0, 2)
    .map((p) => p[0].toUpperCase())
    .join('');
}

/** Сторінки без шапки й меню: публічна форма. */
export function isBareUrl(url: string): boolean {
  return url.startsWith('/form/');
}

/** Вкладки проєкту, що мають сенс в іншому проєкті; сторінки застосунку (`app/<ключ>`) у кожного свої. */
const PORTABLE_TABS = new Set(['overview', 'workflows', 'tables', 'connections', 'members']);

/**
 * Куди перейти після вибору проєкту в шапці: `null` — лишитися (сторінка не проєкту або той самий проєкт). `id` — новий
 * вибір: `null` — усі, {@link NO_GROUP} — спільний простір.
 */
export function projectSwitchTarget(url: string, id: string | null): string[] | null {
  const match = /^\/projects\/([^/?#]+)(?:\/([^/?#]+))?/.exec(url);
  if (!match || match[1] === id) return null;
  if (id === null || id === NO_GROUP) return ['/projects'];
  const tab = match[2];
  return tab && PORTABLE_TABS.has(tab) ? ['/projects', id, tab] : ['/projects', id];
}
