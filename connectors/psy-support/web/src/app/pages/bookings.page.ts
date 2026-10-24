import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { Api, Booking } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { formatDateTime } from '../core/time';

/** Межі вибірки «усі записи»: бекенд вимагає from і to. */
const ALL_FROM = '2000-01-01T00:00:00Z';
const ALL_TO = '2100-01-01T00:00:00Z';

/**
 * Усі активні записи на консультації, від найраніших до найпізніших. Психолог бачить ім'я й телефон лише у своїх записах, записи колег — без даних
 * людини (це вирішує бекенд, §7.1).
 */
@Component({
    selector: 'app-bookings-page',
    imports: [MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@bookings.title">Записи на консультації</h1>
            <p class="muted" i18n="@@bookings.hint">
                Усі записи. Скасування сповіщає людину в боті.
            </p>
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <table class="grid" data-testid="bookings">
                <thead>
                    <tr>
                        <th i18n="@@bookings.when">Коли</th>
                        <th i18n="@@bookings.psychologist">Психолог</th>
                        <th i18n="@@bookings.person">Людина</th>
                        <th i18n="@@bookings.phone">Телефон</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (b of bookings(); track b.id) {
                        <tr [attr.data-testid]="'booking-' + b.id">
                            <td>{{ when(b.starts_at) }} · {{ b.format }}</td>
                            <td>{{ b.psychologist_name }}</td>
                            <td>{{ b.person_name ?? '—' }}</td>
                            <td>{{ b.phone ? '+' + b.phone : '—' }}</td>
                            <td>
                                @if (canCancel(b)) {
                                    <button
                                        mat-button
                                        type="button"
                                        (click)="cancel(b)"
                                        i18n="@@bookings.cancel"
                                    >
                                        Скасувати
                                    </button>
                                }
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="5" class="muted" i18n="@@bookings.empty">
                                Записів немає.
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    `,
})
export class BookingsPage implements OnInit {
    private readonly api = inject(Api);
    private readonly session = inject(Session);

    protected readonly bookings = signal<Booking[]>([]);
    protected readonly error = signal<string | null>(null);

    ngOnInit(): void {
        this.reload();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected canCancel(b: Booking): boolean {
        return (
            new Date(b.starts_at) > new Date() &&
            (this.session.isCoordinator() ||
                this.session.me()?.psychologist_id === b.psychologist_id)
        );
    }

    protected cancel(b: Booking): void {
        if (
            !confirm(
                $localize`:@@bookings.confirm:Скасувати запис? Людина отримає сповіщення в боті.`,
            )
        ) {
            return;
        }
        this.api.cancelBooking(b.id).subscribe({
            next: () => this.reload(),
            error: (e: unknown) => this.error.set(errorText(e)),
        });
    }

    private reload(): void {
        this.api.bookings(ALL_FROM, ALL_TO).subscribe((list) => this.bookings.set(list));
    }
}
