import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Api } from './core/api';
import { Session } from './core/session';

interface NavLink {
    path: string;
    icon: string;
    label: string;
    coordinator?: boolean;
}

@Component({
    selector: 'app-root',
    imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './app.scss',
    template: `
        <mat-toolbar class="shell-top">
            <span class="shell-brand" i18n="@@app.brand">Психологічна підтримка</span>
            @if (session.me(); as me) {
                <span class="shell-user">
                    <span data-testid="current-user">{{ me.email }}</span>
                    <span class="shell-role" data-testid="current-role">{{
                        me.role === 'coordinator' ? coordinatorLabel : psychologistLabel
                    }}</span>
                    <button
                        type="button"
                        class="shell-logout"
                        data-testid="logout"
                        (click)="logout()"
                    >
                        <span class="material-symbols-outlined" aria-hidden="true">logout</span>
                        <span i18n="@@app.logout">Вийти</span>
                    </button>
                </span>
            }
        </mat-toolbar>
        <div class="shell-body">
            @if (session.me()) {
                <nav
                    class="shell-nav"
                    aria-label="Розділи"
                    i18n-aria-label="@@app.nav.label"
                    data-testid="nav"
                >
                    @for (link of links; track link.path) {
                        @if (!link.coordinator || session.isCoordinator()) {
                            <a
                                class="shell-nav__link"
                                [routerLink]="link.path"
                                routerLinkActive="active"
                                [attr.data-testid]="'nav-' + link.path"
                            >
                                <span class="material-symbols-outlined" aria-hidden="true">{{
                                    link.icon
                                }}</span>
                                <span>{{ link.label }}</span>
                            </a>
                        }
                    }
                </nav>
            }
            <main>
                <router-outlet />
            </main>
        </div>
    `,
})
export class App {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);
    private readonly router = inject(Router);

    protected readonly coordinatorLabel = $localize`:@@role.coordinator:координатор`;
    protected readonly psychologistLabel = $localize`:@@role.psychologist:психолог`;

    protected readonly links: NavLink[] = [
        { path: 'schedule', icon: 'calendar_month', label: $localize`:@@nav.schedule:Розклад` },
        { path: 'bookings', icon: 'event_available', label: $localize`:@@nav.bookings:Записи` },
        { path: 'events', icon: 'campaign', label: $localize`:@@nav.events:Події` },
        { path: 'bst', icon: 'groups', label: $localize`:@@nav.bst:Сеанси БСТ` },
        { path: 'materials', icon: 'menu_book', label: $localize`:@@nav.materials:Матеріали` },
        {
            path: 'texts',
            icon: 'edit_note',
            label: $localize`:@@nav.texts:Тексти бота`,
            coordinator: true,
        },
        {
            path: 'staff',
            icon: 'badge',
            label: $localize`:@@nav.staff:Психологи й доступ`,
            coordinator: true,
        },
        {
            path: 'counters',
            icon: 'bar_chart',
            label: $localize`:@@nav.counters:Лічильники`,
            coordinator: true,
        },
        {
            path: 'audit',
            icon: 'history',
            label: $localize`:@@nav.audit:Журнал`,
            coordinator: true,
        },
        {
            path: 'users',
            icon: 'person_remove',
            label: $localize`:@@nav.users:Видалення даних`,
            coordinator: true,
        },
    ];

    protected logout(): void {
        const done = () => {
            this.session.forget();
            void this.router.navigate(['/login']);
        };
        this.api.logout().subscribe({ next: done, error: done });
    }
}
