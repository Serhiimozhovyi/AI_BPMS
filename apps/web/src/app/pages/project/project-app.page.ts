import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { AppBarService } from '../../layout/app-bar.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectPage, ProjectPagesApiService } from '../../projects/project-pages-api.service';
import { BpTab, BpTabsComponent } from '../../shared/tabs/tabs.component';

/**
 * `/projects/:id/app` — вкладка «Застосунок» (F8-04): меню сторінок застосунку, які бачить користувач (за ролями
 * застосунку), і сама сторінка (`app/<key>[/<rowKey>]`). Без вибраної сторінки відкриває головну оболонки або першу.
 * Якщо проєкт задав оболонку `app` (F8-18) — назва, значок й меню розділів за групами (сторінки не з меню відкриваються
 * лише за посиланням), editor бачить кнопку «Конструктор». Користувачу лише застосунку оболонка показується у верхній
 * смузі замість меню AI_BPMS (F9-04), тут — лише сторінка.
 */
@Component({
  selector: 'app-project-app-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet, BpTabsComponent],
  template: `
    @if (inBar()) {
      <div class="px-6 pt-4" data-testid="app-shell">
        @if (error()) {
          <p role="alert" class="mb-2 text-red-700" data-testid="app-error">{{ error() }}</p>
        }
        @if (loaded() && pages().length === 0 && !error()) {
          <p class="opacity-70" data-testid="app-empty" i18n="@@project.app.empty">Для вашої ролі сторінок застосунку немає.</p>
        }
        <router-outlet />
      </div>
    } @else if (shell(); as app) {
      <div class="px-6 pt-4 md:flex md:gap-6" data-testid="app-shell">
        <aside class="mb-3 md:mb-0 md:w-56 md:shrink-0">
          <div class="mb-3 flex items-center gap-2">
            @if (iconName(); as icon) {
              <span class="material-symbols-outlined text-2xl" aria-hidden="true" data-testid="shell-icon">{{ icon }}</span>
            } @else if (app.icon) {
              <span class="text-2xl" aria-hidden="true" data-testid="shell-icon">{{ app.icon }}</span>
            }
            <h2 class="text-lg font-semibold" data-testid="shell-title">{{ app.title }}</h2>
          </div>
          @if (error()) {
            <p role="alert" class="mb-2 text-red-700" data-testid="app-error">{{ error() }}</p>
          }
          <nav class="flex flex-row flex-wrap gap-1 md:flex-col" aria-label="Розділи застосунку" i18n-aria-label="@@project.app.sections" data-testid="shell-nav">
            @for (group of menu(); track $index) {
              @if (group.label) {
                <div class="mt-2 w-full px-2 text-xs uppercase opacity-60" data-testid="shell-group">{{ group.label }}</div>
              }
              @for (p of group.pages; track p.id) {
                <a class="ac-side-link" [routerLink]="[p.key]" routerLinkActive="active" [attr.data-page]="p.key">{{ p.name }}</a>
              }
            }
          </nav>
          @if (loaded() && pages().length === 0 && !error()) {
            <p class="opacity-70" data-testid="app-empty">
              @if (canEdit()) {
                <ng-container i18n="@@project.app.emptyEditor"
                  >Сторінок застосунку ще немає. Додайте першу конструктором («Нова сторінка») або через MCP-інструмент upsert_project_page
                  (наприклад, з Claude).</ng-container
                >
              } @else {
                <ng-container i18n="@@project.app.empty">Для вашої ролі сторінок застосунку немає.</ng-container>
              }
            </p>
          }
          @if (canEdit() && !building()) {
            <a class="ac-side-link mt-3" data-testid="shell-builder" routerLink="_builder" i18n="@@project.app.builder">Конструктор</a>
            <a class="ac-side-link" data-testid="shell-settings" routerLink="_app" i18n="@@project.app.settings">Вигляд застосунку</a>
          }
        </aside>
        <div class="min-w-0 grow">
          <router-outlet />
        </div>
      </div>
    } @else {
      <section class="px-6 pt-4">
        @if (error()) {
          <p role="alert" class="mb-2 text-red-700" data-testid="app-error">{{ error() }}</p>
        }
        @if (loaded() && pages().length === 0 && !error()) {
          <p class="max-w-3xl opacity-70" data-testid="app-empty">
            @if (canEdit()) {
              <ng-container i18n="@@project.app.emptyEditor"
                >Сторінок застосунку ще немає. Додайте першу конструктором («Нова сторінка») або через MCP-інструмент upsert_project_page
                (наприклад, з Claude).</ng-container
              >
            } @else {
              <ng-container i18n="@@project.app.empty">Для вашої ролі сторінок застосунку немає.</ng-container>
            }
          </p>
        }
        @if (canEdit() && !building()) {
          <a class="mb-2 inline-block" data-testid="app-new-page" routerLink="_builder" i18n="@@project.app.newPage">+ Нова сторінка</a>
          <a class="mb-2 ml-4 inline-block" data-testid="app-settings-link" routerLink="_app" i18n="@@project.app.settings">Вигляд застосунку</a>
        }
        @if (pages().length > 1) {
          <bp-tabs class="mb-3" [tabs]="pageTabs()" label="Сторінки застосунку" i18n-label="@@project.app.nav" testId="app-nav" />
        }
      </section>
      <router-outlet />
    }
  `,
})
export class ProjectAppPage implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  private readonly context = inject(ProjectContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  private readonly auth = inject(AuthService);
  private readonly appBar = inject(AppBarService);

  readonly projectId = this.route.parent?.snapshot.paramMap.get('id') ?? '';
  readonly pages = signal<ProjectPage[]>([]);
  readonly pageTabs = computed<BpTab[]>(() => this.pages().map((p) => ({ value: p.key, label: p.name, link: [p.key] })));
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  /** Відкрито конструктор: меню сторінок і посилання на нього зайві. */
  readonly building = signal(false);
  private readonly project = computed(() => this.context.projects().find((p) => p.id === this.projectId) ?? null);
  readonly canEdit = computed(() => {
    const role = this.project()?.role;
    return role === 'editor' || role === 'admin';
  });
  /** Оболонка застосунку проєкту (F8-18): назва, меню розділів, головна; без неї — плоский список сторінок. */
  readonly shell = computed(() => this.project()?.app ?? null);
  /** Назва значка Material Symbols, якщо значок оболонки — слово, а не emoji. */
  readonly iconName = computed(() => (/^[a-z][a-z0-9_]*$/.test(this.shell()?.icon ?? '') ? this.shell()!.icon! : null));
  /** Групи меню з видимих користувачу сторінок: у `app.nav` — як задано, решта (не з меню) відкривається за посиланням. */
  readonly menu = computed(() => {
    const byKey = new Map(this.pages().map((p) => [p.key, p]));
    const nav = this.shell()?.nav;
    if (!nav?.length) return [{ label: '', pages: this.pages() }];
    return nav
      .map((g) => ({ label: g.label, pages: g.pages.flatMap((key) => byKey.get(key) ?? []) }))
      .filter((g) => g.pages.length > 0);
  });
  /** Користувач лише застосунку цього проєкту з оболонкою: меню — у верхній смузі (F9-04). */
  readonly inBar = computed(() => !!this.shell() && this.context.appOnlyProject(this.auth.user()?.role)?.id === this.projectId);
  private redirected = false;

  constructor() {
    // Оболонку у верхній смузі публікуємо, поки відкрита ця сторінка.
    effect(() => {
      const app = this.shell();
      const menu = this.menu();
      this.appBar.bar.set(
        this.inBar() && app
          ? {
              projectId: this.projectId,
              title: app.title,
              iconName: this.iconName(),
              emoji: this.iconName() ? null : (app.icon ?? null),
              menu: menu.map((g) => ({ label: g.label, pages: g.pages.map((p) => ({ key: p.key, name: p.name })) })),
            }
          : null,
      );
    });
    this.destroyRef.onDestroy(() => this.appBar.bar.set(null));
    // Застосунок відкривається на головній сторінці оболонки, а без неї — на першій сторінці меню (F8-18).
    effect(() => {
      const pages = this.pages();
      const project = this.project();
      if (!this.loaded() || this.redirected || pages.length === 0 || !project) return;
      const shell = project.app ?? null;
      untracked(() => {
        if (this.route.firstChild) return;
        this.redirected = true;
        const home = shell?.home && pages.some((p) => p.key === shell.home) ? shell.home : (this.menu()[0]?.pages[0]?.key ?? pages[0].key);
        void this.router.navigate([home], { relativeTo: this.route, replaceUrl: true });
      });
    });
  }

  ngOnInit(): void {
    const sync = () => this.building.set((this.route.firstChild?.snapshot.url[0]?.path ?? '').startsWith('_'));
    sync();
    this.router.events.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((e) => {
      if (e instanceof NavigationEnd) sync();
    });
    this.api
      .list(this.projectId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (pages) => {
          this.pages.set(pages);
          this.loaded.set(true);
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loaded.set(true);
        },
      });
  }
}
