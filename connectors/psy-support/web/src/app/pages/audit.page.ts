import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuditEntry, Api } from '../core/api';
import { Session } from '../core/session';
import { formatDateTime } from '../core/time';

/** Журнал дій (`audit_log`): лише id і коди, без персональних даних (§8). */
@Component({
    selector: 'app-audit-page',
    imports: [MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@audit.title">Журнал дій</h1>
            <table class="grid">
                <thead>
                    <tr>
                        <th i18n="@@audit.at">Коли</th>
                        <th i18n="@@audit.actor">Хто</th>
                        <th i18n="@@audit.action">Дія</th>
                        <th i18n="@@audit.entity">Об'єкт</th>
                        <th i18n="@@audit.details">Деталі</th>
                    </tr>
                </thead>
                <tbody>
                    @for (a of entries(); track a.id) {
                        <tr>
                            <td>{{ when(a.at) }}</td>
                            <td>{{ a.actor }}</td>
                            <td>
                                <code>{{ a.action }}</code>
                            </td>
                            <td>{{ a.entity ?? '' }} {{ a.entity_id ?? '' }}</td>
                            <td class="muted">{{ a.details ?? '' }}</td>
                        </tr>
                    }
                </tbody>
            </table>
            @if (more()) {
                <button
                    mat-stroked-button
                    type="button"
                    style="margin-top: 12px"
                    (click)="load()"
                    i18n="@@audit.more"
                >
                    Показати ще
                </button>
            }
        </section>
    `,
})
export class AuditPage implements OnInit {
    private readonly api = inject(Api);
    private readonly session = inject(Session);
    protected readonly entries = signal<AuditEntry[]>([]);
    protected readonly more = signal(false);

    ngOnInit(): void {
        this.load();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected load(): void {
        const last = this.entries().at(-1);
        this.api.audit(last?.id).subscribe((list) => {
            this.entries.update((current) => [...current, ...list]);
            this.more.set(list.length === 100);
        });
    }
}
