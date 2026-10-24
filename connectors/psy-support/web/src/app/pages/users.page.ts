import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Api, UserHit } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { formatDateTime } from '../core/time';

/** Видалення даних людини на її запит (§8): ім'я й телефон стираються, майбутні записи скасовуються. */
@Component({
    selector: 'app-users-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@users.title">Видалення даних на запит</h1>
            <p class="muted" i18n="@@users.hint">
                Знайдіть людину за телефоном або ім'ям. Пошук записується в журнал.
            </p>
            <form class="form-row" (ngSubmit)="search()">
                <input
                    class="border"
                    name="q"
                    aria-label="Телефон або ім'я"
                    i18n-aria-label="@@users.query"
                    [(ngModel)]="query"
                    required
                />
                <button mat-flat-button type="submit" i18n="@@users.search">Знайти</button>
            </form>
            @if (message()) {
                <p class="notice">{{ message() }}</p>
            }
            <table class="grid" style="margin-top: 12px">
                <tbody>
                    @for (u of hits(); track u.telegram_id) {
                        <tr>
                            <td>{{ u.name ?? '—' }}</td>
                            <td>{{ u.phone ? '+' + u.phone : '—' }}</td>
                            <td class="muted" i18n="@@users.registered">
                                зареєстровано {{ when(u.registered_at) }}
                            </td>
                            <td>
                                <button
                                    mat-stroked-button
                                    type="button"
                                    (click)="remove(u)"
                                    i18n="@@users.delete"
                                >
                                    Видалити дані
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    `,
})
export class UsersPage {
    private readonly api = inject(Api);
    private readonly session = inject(Session);
    protected query = '';
    protected readonly hits = signal<UserHit[]>([]);
    protected readonly message = signal<string | null>(null);

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected search(): void {
        this.message.set(null);
        this.api.searchUsers(this.query.trim()).subscribe({
            next: (list) => {
                this.hits.set(list);
                if (list.length === 0) {
                    this.message.set($localize`:@@users.none:Нікого не знайдено.`);
                }
            },
            error: (e: unknown) => this.message.set(errorText(e)),
        });
    }

    protected remove(u: UserHit): void {
        if (
            !confirm(
                $localize`:@@users.confirm:Видалити ім'я й телефон цієї людини та скасувати її майбутні записи? Дію не можна скасувати.`,
            )
        ) {
            return;
        }
        this.api.deleteUser(u.telegram_id).subscribe({
            next: () => {
                this.hits.update((list) => list.filter((h) => h.telegram_id !== u.telegram_id));
                this.message.set($localize`:@@users.deleted:Дані видалено.`);
            },
            error: (e: unknown) => this.message.set(errorText(e)),
        });
    }
}
