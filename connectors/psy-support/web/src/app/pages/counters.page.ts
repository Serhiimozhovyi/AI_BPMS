import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { Api, Counter } from '../core/api';

const LABELS: Record<string, string> = {
    psy_section_opened: $localize`:@@counters.menu:Відкриття меню`,
    help_opened: $localize`:@@counters.help:«Мені потрібна допомога»`,
    sos_opened: $localize`:@@counters.sos:SOS`,
    materials_viewed: $localize`:@@counters.materials:Перегляди матеріалів`,
    consult_bookings_total: $localize`:@@counters.bookings:Записи на консультації`,
    consult_moves: $localize`:@@counters.moves:Перенесення консультацій`,
    cancellations_total: $localize`:@@counters.cancellations:Скасування`,
    event_registrations: $localize`:@@counters.events:Реєстрації на події`,
    bst_bookings_total: $localize`:@@counters.bst:Записи на БСТ`,
};

/** Лічильники використання бота. */
@Component({
    selector: 'app-counters-page',
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@counters.title">Лічильники</h1>
            <table class="grid">
                <tbody>
                    @for (c of counters(); track c.metric) {
                        <tr>
                            <td>{{ label(c.metric) }}</td>
                            <td>
                                <b>{{ c.value }}</b>
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td class="muted" i18n="@@counters.empty">Даних ще немає.</td>
                        </tr>
                    }
                </tbody>
            </table>
        </section>
    `,
})
export class CountersPage implements OnInit {
    private readonly api = inject(Api);
    protected readonly counters = signal<Counter[]>([]);

    ngOnInit(): void {
        this.api.counters().subscribe((list) => this.counters.set(list));
    }

    protected label(metric: string): string {
        return LABELS[metric] ?? metric;
    }
}
