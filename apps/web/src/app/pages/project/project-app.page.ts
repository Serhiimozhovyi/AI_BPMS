import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectPage, ProjectPagesApiService } from '../../projects/project-pages-api.service';

/**
 * `/projects/:id/app` — вкладка «Застосунок» (F8-04): меню сторінок застосунку, які бачить користувач (за ролями
 * застосунку), і сама сторінка (`app/<key>[/<rowKey>]`). Без вибраної сторінки відкриває першу.
 */
@Component({
  selector: 'app-project-app-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
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
        <a class="shell-nav__link mb-2 inline-block" data-testid="app-new-page" routerLink="_builder" i18n="@@project.app.newPage">+ Нова сторінка</a>
      }
      @if (pages().length > 1) {
        <nav class="mb-2 flex flex-wrap gap-1" aria-label="Сторінки застосунку" i18n-aria-label="@@project.app.nav" data-testid="app-nav">
          @for (p of pages(); track p.id) {
            <a class="shell-nav__link" [routerLink]="[p.key]" routerLinkActive="active" [attr.data-page]="p.key">{{ p.name }}</a>
          }
        </nav>
      }
    </section>
    <router-outlet />
  `,
})
export class ProjectAppPage implements OnInit {
  private readonly api = inject(ProjectPagesApiService);
  private readonly context = inject(ProjectContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = this.route.parent?.snapshot.paramMap.get('id') ?? '';
  readonly pages = signal<ProjectPage[]>([]);
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  /** Відкрито конструктор: меню сторінок і посилання на нього зайві. */
  readonly building = signal(false);
  readonly canEdit = computed(() => {
    const role = this.context.projects().find((p) => p.id === this.projectId)?.role;
    return role === 'editor' || role === 'admin';
  });

  ngOnInit(): void {
    const sync = () => this.building.set(this.route.firstChild?.snapshot.url[0]?.path === '_builder');
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
          if (!this.route.firstChild && pages.length > 0) {
            void this.router.navigate([pages[0].key], { relativeTo: this.route, replaceUrl: true });
          }
        },
        error: (e: unknown) => {
          this.error.set(apiErrorMessage(e));
          this.loaded.set(true);
        },
      });
  }
}
