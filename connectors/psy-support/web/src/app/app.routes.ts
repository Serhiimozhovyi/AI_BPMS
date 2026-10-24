import { Routes } from '@angular/router';
import { authGuard, coordinatorGuard } from './core/session';

/** Сторінки за docs/psy-support.md §7.1; сторінки координатора закриті `coordinatorGuard` (і ролями на бекенді). */
export const routes: Routes = [
    { path: 'login', loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage) },
    {
        path: 'invite',
        loadComponent: () => import('./pages/invite.page').then((m) => m.InvitePage),
    },
    {
        path: '',
        canActivate: [authGuard],
        children: [
            { path: '', pathMatch: 'full', redirectTo: 'schedule' },
            {
                path: 'schedule',
                loadComponent: () => import('./pages/schedule.page').then((m) => m.SchedulePage),
            },
            {
                path: 'bookings',
                loadComponent: () => import('./pages/bookings.page').then((m) => m.BookingsPage),
            },
            {
                path: 'events',
                loadComponent: () => import('./pages/events.page').then((m) => m.EventsPage),
            },
            { path: 'bst', loadComponent: () => import('./pages/bst.page').then((m) => m.BstPage) },
            {
                path: 'materials',
                loadComponent: () => import('./pages/materials.page').then((m) => m.MaterialsPage),
            },
            {
                path: '',
                canActivate: [coordinatorGuard],
                children: [
                    {
                        path: 'texts',
                        loadComponent: () => import('./pages/texts.page').then((m) => m.TextsPage),
                    },
                    {
                        path: 'staff',
                        loadComponent: () => import('./pages/staff.page').then((m) => m.StaffPage),
                    },
                    {
                        path: 'counters',
                        loadComponent: () =>
                            import('./pages/counters.page').then((m) => m.CountersPage),
                    },
                    {
                        path: 'audit',
                        loadComponent: () => import('./pages/audit.page').then((m) => m.AuditPage),
                    },
                    {
                        path: 'users',
                        loadComponent: () => import('./pages/users.page').then((m) => m.UsersPage),
                    },
                ],
            },
        ],
    },
    { path: '**', redirectTo: '' },
];
