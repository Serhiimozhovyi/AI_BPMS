import { BreakpointObserver } from '@angular/cdk/layout';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, HostListener, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toSignal } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatSelectModule } from '@angular/material/select';
import { MatToolbarModule } from '@angular/material/toolbar';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter, map } from 'rxjs';
import { AuthService } from './auth/auth.service';
import { ConnectivityService } from './connectivity/connectivity.service';
import { MOBILE_QUERY } from './layout/breakpoints';
import { ProjectContextService } from './projects/project-context.service';
import { NO_GROUP } from './workflows/workflow-groups';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, NgTemplateOutlet, MatToolbarModule, MatButtonModule, MatSelectModule],
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
  protected readonly sharedSpace = NO_GROUP;
  /** Телефон (< 768 px): навігація, проєкт і користувач — у drawer за кнопкою-бургером (F3-84). */
  protected readonly mobile = toSignal(inject(BreakpointObserver).observe(MOBILE_QUERY).pipe(map((s) => s.matches)), {
    initialValue: false,
  });
  protected readonly drawerOpen = signal(false);
  /** Користувач лише з роллю застосунку (F8-04): меню «Застосунок» і «Огляд» його проєкту. */
  protected readonly appOnly = computed(() => this.projects.appOnlyProject(this.user()?.role));
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
    // Перехід за пунктом drawer закриває його; на планшеті активний пункт прокрутної навігації лишається видимим.
    this.router.events.pipe(filter((e): e is NavigationEnd => e instanceof NavigationEnd), takeUntilDestroyed()).subscribe(() => {
      this.drawerOpen.set(false);
      setTimeout(() => document.querySelector('.shell-nav__link.active')?.scrollIntoView?.({ inline: 'nearest', block: 'nearest' }));
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
  }

  /** Вибір у шапці: порожнє значення — усі проєкти. */
  protected selectProject(value: string): void {
    this.projects.select(value === '' ? null : value);
  }

  /** Вихід: сесію закрито на Api — на сторінку входу; якщо Api недоступний, користувача все одно забуваємо. */
  protected logout(): void {
    this.auth.logout().subscribe({
      next: () => void this.router.navigate(['/login']),
      error: () => {
        this.auth.forget();
        void this.router.navigate(['/login']);
      },
    });
  }
}

/** Сторінки без шапки й меню: публічна форма. */
export function isBareUrl(url: string): boolean {
  return url.startsWith('/form/');
}
