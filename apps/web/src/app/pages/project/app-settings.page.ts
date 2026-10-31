import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { apiErrorMessage } from '../../connectors/connectors-api.service';
import { ProjectContextService } from '../../projects/project-context.service';
import { ProjectPage, ProjectPagesApiService } from '../../projects/project-pages-api.service';
import { ProjectAppNavGroup, WorkflowsApiService } from '../../workflows/workflows-api.service';
import { BpButtonDirective } from '../../shared/button.directive';

/**
 * `/projects/:id/app/_app` — налаштування оболонки застосунку (F8-19, `docs/projects.md` §3.9): назва, значок, головна
 * сторінка, часовий пояс і групи меню. Без оболонки застосунок показується простим списком сторінок.
 */
@Component({
  selector: 'app-app-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BpButtonDirective, MatButtonModule, RouterLink],
  template: `
    <section class="max-w-3xl px-6 pb-8 pt-2" data-testid="app-settings">
      <h2 class="mb-3 text-lg font-semibold" i18n="@@appset.title">Вигляд застосунку</h2>
      @if (error()) {
        <p role="alert" class="mb-2 text-red-700" data-testid="appset-error">{{ error() }}</p>
      }
      @if (saved()) {
        <p role="status" class="mb-2" data-testid="appset-saved" i18n="@@appset.saved">Збережено.</p>
      }
      <div class="mb-4 grid grid-cols-[minmax(9rem,auto)_1fr] items-center gap-x-4 gap-y-2">
        <label for="as-title" i18n="@@appset.name">Назва застосунку</label>
        <input id="as-title" class="border p-1" data-testid="as-title" [value]="title()" (input)="title.set($any($event.target).value)" />
        <label for="as-icon" i18n="@@appset.icon">Значок (Material Symbols)</label>
        <input id="as-icon" class="border p-1" data-testid="as-icon" placeholder="calendar_month" [value]="icon()" (input)="icon.set($any($event.target).value)" />
        <label for="as-home" i18n="@@appset.home">Головна сторінка</label>
        <select id="as-home" class="border p-1" data-testid="as-home" [value]="home()" (change)="home.set($any($event.target).value)">
          <option value="" [selected]="!home()" i18n="@@appset.homeFirst">перша в меню</option>
          @for (p of pages(); track p.key) {
            <option [value]="p.key" [selected]="p.key === home()">{{ p.name }}</option>
          }
        </select>
        <label for="as-tz" i18n="@@appset.tz">Часовий пояс</label>
        <input id="as-tz" class="border p-1" data-testid="as-tz" placeholder="Europe/Kyiv" [value]="timezone()" (input)="timezone.set($any($event.target).value)" />
      </div>

      <h3 class="mb-1 font-medium" i18n="@@appset.menu">Меню</h3>
      <p class="mb-2 text-sm opacity-70" i18n="@@appset.menuHint">Без груп меню — усі сторінки за порядком. Сторінки, яких немає в жодній групі, у меню не показуються.</p>
      @for (g of nav(); track $index; let i = $index) {
        <fieldset class="mb-2 rounded border border-bp-line p-2" [attr.data-nav-group]="i">
          <div class="mb-1 flex items-center gap-2">
            <input class="w-56 border p-1" placeholder="Назва групи" i18n-placeholder="@@appset.group" [value]="g.label" (input)="setGroup(i, { label: $any($event.target).value })" />
            <button mat-button type="button" [disabled]="i === 0" (click)="move(i, -1)" aria-label="Вище" i18n-aria-label="@@bd.up">↑</button>
            <button mat-button type="button" [disabled]="i === nav().length - 1" (click)="move(i, 1)" aria-label="Нижче" i18n-aria-label="@@bd.down">↓</button>
            <button mat-button type="button" (click)="nav.set(removeAt(i))" i18n="@@builder.remove" bpVariant="danger">Прибрати</button>
          </div>
          @for (p of pages(); track p.key) {
            <label class="mr-4 inline-flex items-center gap-1">
              <input type="checkbox" [attr.data-nav-page]="p.key" [checked]="g.pages.includes(p.key)" (change)="togglePage(i, p.key, $any($event.target).checked)" />
              {{ p.name }}
            </label>
          }
        </fieldset>
      }
      <button mat-button type="button" data-testid="as-add-group" (click)="nav.set([...nav(), { label: '', pages: [] }])" i18n="@@appset.addGroup">Додати групу</button>

      <div class="mt-6 flex flex-wrap gap-2">
        <button mat-flat-button type="button" data-testid="as-save" [disabled]="saving() || !title().trim()" (click)="save()" i18n="@@appset.save">Зберегти</button>
        @if (hasShell()) {
          <button mat-button type="button" data-testid="as-clear" [disabled]="saving()" (click)="clear()" i18n="@@appset.clear" bpVariant="danger">Прибрати оболонку</button>
        }
        <a mat-button [routerLink]="['/projects', projectId, 'app']" i18n="@@builder.cancel">Скасувати</a>
      </div>
    </section>
  `,
})
export class AppSettingsPage implements OnInit {
  private readonly workflows = inject(WorkflowsApiService);
  private readonly pagesApi = inject(ProjectPagesApiService);
  private readonly context = inject(ProjectContextService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  readonly projectId = this.route.parent?.snapshot.paramMap.get('id') ?? '';
  readonly pages = signal<ProjectPage[]>([]);
  readonly title = signal('');
  readonly icon = signal('');
  readonly home = signal('');
  readonly timezone = signal('');
  readonly nav = signal<ProjectAppNavGroup[]>([]);
  readonly saving = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);
  readonly hasShell = computed(() => !!this.context.projects().find((p) => p.id === this.projectId)?.app);

  ngOnInit(): void {
    const shell = this.context.projects().find((p) => p.id === this.projectId)?.app;
    if (shell) {
      this.title.set(shell.title);
      this.icon.set(shell.icon ?? '');
      this.home.set(shell.home ?? '');
      this.timezone.set(shell.timezone ?? '');
      this.nav.set((shell.nav ?? []).map((g) => ({ label: g.label, pages: [...g.pages] })));
    } else {
      this.title.set(this.context.projects().find((p) => p.id === this.projectId)?.name ?? '');
    }
    this.pagesApi
      .list(this.projectId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({ next: (pages) => this.pages.set(pages), error: (e: unknown) => this.error.set(apiErrorMessage(e)) });
  }

  setGroup(index: number, patch: Partial<ProjectAppNavGroup>): void {
    this.nav.update((all) => all.map((g, i) => (i === index ? { ...g, ...patch } : g)));
  }

  togglePage(index: number, key: string, on: boolean): void {
    const order = this.pages().map((p) => p.key);
    const current = this.nav()[index].pages.filter((k) => k !== key);
    this.setGroup(index, { pages: (on ? [...current, key] : current).sort((a, b) => order.indexOf(a) - order.indexOf(b)) });
  }

  removeAt(index: number): ProjectAppNavGroup[] {
    return this.nav().filter((_, i) => i !== index);
  }

  move(index: number, delta: number): void {
    const next = [...this.nav()];
    const [item] = next.splice(index, 1);
    next.splice(index + delta, 0, item);
    this.nav.set(next);
  }

  save(): void {
    this.error.set(null);
    this.saved.set(false);
    this.saving.set(true);
    const nav = this.nav().filter((g) => g.label.trim() && g.pages.length);
    this.workflows
      .setApp(this.projectId, {
        title: this.title().trim(),
        icon: this.icon().trim() || null,
        home: this.home() || null,
        timezone: this.timezone().trim() || null,
        nav: nav.length ? nav : null,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.saved.set(true);
          this.context.reload();
        },
        error: (e: unknown) => {
          this.saving.set(false);
          this.error.set(apiErrorMessage(e));
        },
      });
  }

  clear(): void {
    if (!window.confirm($localize`:@@appset.clear.confirm:Прибрати оболонку? Сторінки й дані не зачіпаються.`)) return;
    this.workflows.clearApp(this.projectId).subscribe({
      next: () => {
        this.context.reload();
        void this.router.navigate(['/projects', this.projectId, 'app']);
      },
      error: (e: unknown) => this.error.set(apiErrorMessage(e)),
    });
  }
}
