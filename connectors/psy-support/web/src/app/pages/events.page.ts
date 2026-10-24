import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Api, EventItem, Participant } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { dayStartIso, formatDateTime, isoToZoned, zonedToIso } from '../core/time';

interface EventForm {
    id: number | null;
    title: string;
    description: string;
    startsAt: string;
    format: string;
    host: string;
    contact: string;
    capacity: number;
    status: 'draft' | 'open' | 'closed';
    notice: string;
}

const empty = (): EventForm => ({
    id: null,
    title: '',
    description: '',
    startsAt: '',
    format: 'онлайн',
    host: '',
    contact: '',
    capacity: 0,
    status: 'draft',
    notice: '',
});

/** Події: створення й редагування (координатор), учасники й оголошення (обидві ролі). */
@Component({
    selector: 'app-events-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './editor.scss',
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@events.title">Події</h1>
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <table class="grid" data-testid="events">
                <thead>
                    <tr>
                        <th i18n="@@events.when">Коли</th>
                        <th i18n="@@events.name">Назва</th>
                        <th i18n="@@events.places">Місця</th>
                        <th i18n="@@events.status">Стан</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (e of events(); track e.id) {
                        <tr>
                            <td>{{ when(e.starts_at) }}</td>
                            <td>{{ e.title }}</td>
                            <td>{{ e.taken }}{{ e.capacity ? ' / ' + e.capacity : '' }}</td>
                            <td>
                                <span class="badge">{{ statusLabel[e.status] }}</span>
                            </td>
                            <td>
                                <button
                                    mat-button
                                    type="button"
                                    (click)="open(e)"
                                    i18n="@@events.participants"
                                >
                                    Учасники
                                </button>
                                @if (session.isCoordinator()) {
                                    <button
                                        mat-button
                                        type="button"
                                        (click)="edit(e)"
                                        i18n="@@events.edit"
                                    >
                                        Змінити
                                    </button>
                                }
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="5" class="muted" i18n="@@events.empty">Подій немає.</td>
                        </tr>
                    }
                </tbody>
            </table>

            @if (selected(); as e) {
                <h2 class="page-subtitle">{{ e.title }} · {{ when(e.starts_at) }}</h2>
                <ol data-testid="participants">
                    @for (p of participants(); track p.id) {
                        <li>
                            {{ p.name ?? '—' }}{{ p.phone ? ', +' + p.phone : '' }}
                            <span class="muted"
                                >{{ p.status
                                }}{{ p.feedback ? ' · ' + p.feedback + '/5' : '' }}</span
                            >
                        </li>
                    } @empty {
                        <li class="muted" i18n="@@events.noParticipants">Учасників немає.</li>
                    }
                </ol>
                <p class="notice">
                    <b i18n="@@events.notice">Оголошення:</b> {{ e.notice || '—' }}
                    @if (e.notice_sent_at) {
                        <span class="muted" i18n="@@events.noticeSent"
                            >(надіслано {{ when(e.notice_sent_at) }})</span
                        >
                    }
                </p>
                <button
                    mat-stroked-button
                    type="button"
                    data-testid="send-notice"
                    (click)="sendNotice(e)"
                    i18n="@@events.sendNotice"
                >
                    Надіслати оголошення учасникам
                </button>
                @if (sent()) {
                    <span class="muted">{{ sent() }}</span>
                }
            }

            @if (session.isCoordinator()) {
                <h2 class="page-subtitle">{{ form.id ? editTitle : newTitle }}</h2>
                <form class="editor" (ngSubmit)="save()">
                    <label
                        ><span i18n="@@events.name">Назва</span
                        ><input class="border" name="title" [(ngModel)]="form.title" required
                    /></label>
                    <label
                        ><span i18n="@@events.startsAt">Початок</span
                        ><input
                            class="border"
                            type="datetime-local"
                            name="startsAt"
                            [(ngModel)]="form.startsAt"
                            required
                    /></label>
                    <label
                        ><span i18n="@@events.format">Формат</span
                        ><input class="border" name="format" [(ngModel)]="form.format"
                    /></label>
                    <label
                        ><span i18n="@@events.host">Веде</span
                        ><input class="border" name="host" [(ngModel)]="form.host"
                    /></label>
                    <label
                        ><span i18n="@@events.contact">Контакт</span
                        ><input class="border" name="contact" [(ngModel)]="form.contact"
                    /></label>
                    <label
                        ><span i18n="@@events.capacity">Місткість (0 — без ліміту)</span
                        ><input
                            class="border"
                            type="number"
                            min="0"
                            name="capacity"
                            [(ngModel)]="form.capacity"
                    /></label>
                    <label
                        ><span i18n="@@events.status">Стан</span>
                        <select class="border" name="status" [(ngModel)]="form.status">
                            <option value="draft">{{ statusLabel.draft }}</option>
                            <option value="open">{{ statusLabel.open }}</option>
                            <option value="closed">{{ statusLabel.closed }}</option>
                        </select>
                    </label>
                    <label class="wide"
                        ><span i18n="@@events.description">Опис</span
                        ><textarea
                            class="border"
                            rows="3"
                            name="description"
                            [(ngModel)]="form.description"
                        ></textarea>
                    </label>
                    <label class="wide"
                        ><span i18n="@@events.noticeField">Оголошення учасникам</span
                        ><textarea
                            class="border"
                            rows="3"
                            name="notice"
                            [(ngModel)]="form.notice"
                        ></textarea>
                    </label>
                    <div class="form-row wide">
                        <button mat-flat-button type="submit" i18n="@@events.save">Зберегти</button>
                        @if (form.id) {
                            <button
                                mat-button
                                type="button"
                                (click)="reset()"
                                i18n="@@events.cancelEdit"
                            >
                                Скасувати
                            </button>
                        }
                    </div>
                </form>
            }
        </section>
    `,
})
export class EventsPage implements OnInit {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);

    protected readonly statusLabel = {
        draft: $localize`:@@status.draft:чернетка`,
        open: $localize`:@@status.open:відкрито`,
        closed: $localize`:@@status.closed:закрито`,
    };
    protected readonly newTitle = $localize`:@@events.new:Нова подія`;
    protected readonly editTitle = $localize`:@@events.editTitle:Редагування події`;
    protected readonly events = signal<EventItem[]>([]);
    protected readonly selected = signal<EventItem | null>(null);
    protected readonly participants = signal<Participant[]>([]);
    protected readonly error = signal<string | null>(null);
    protected readonly sent = signal<string | null>(null);
    protected form = empty();

    ngOnInit(): void {
        this.reload();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected open(e: EventItem): void {
        this.selected.set(e);
        this.sent.set(null);
        this.api.registrations(e.id).subscribe((list) => this.participants.set(list));
    }

    protected edit(e: EventItem): void {
        this.form = {
            id: e.id,
            title: e.title,
            description: e.description ?? '',
            startsAt: isoToZoned(e.starts_at, this.session.timezone()),
            format: e.format ?? '',
            host: e.host ?? '',
            contact: e.contact ?? '',
            capacity: e.capacity,
            status: e.status,
            notice: e.notice ?? '',
        };
    }

    protected reset(): void {
        this.form = empty();
    }

    protected save(): void {
        const f = this.form;
        this.error.set(null);
        this.api
            .saveEvent(f.id, {
                title: f.title,
                description: f.description || null,
                starts_at: zonedToIso(f.startsAt, this.session.timezone()),
                format: f.format || null,
                host: f.host || null,
                contact: f.contact || null,
                capacity: Number(f.capacity) || 0,
                status: f.status,
                notice: f.notice || null,
            })
            .subscribe({
                next: () => {
                    this.reset();
                    this.reload();
                },
                error: (e: unknown) => this.error.set(errorText(e)),
            });
    }

    protected sendNotice(e: EventItem): void {
        this.api.sendEventNotice(e.id).subscribe({
            next: (r) => {
                this.sent.set($localize`:@@notice.queued:Поставлено в чергу: ${r.queued}:count:`);
                this.reload();
            },
            error: (err: unknown) => this.sent.set(errorText(err)),
        });
    }

    private reload(): void {
        this.api.events(dayStartIso(this.session.timezone(), -30)).subscribe((list) => {
            this.events.set(list);
            const current = this.selected();
            if (current) {
                this.selected.set(list.find((e) => e.id === current.id) ?? null);
            }
        });
    }
}
