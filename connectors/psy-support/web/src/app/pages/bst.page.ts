import {
    ChangeDetectionStrategy,
    Component,
    OnInit,
    computed,
    inject,
    signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Api, BstSession, Participant, Psychologist } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { dayStartIso, formatDateTime, isoToZoned, zonedToIso } from '../core/time';

interface BstForm {
    id: number | null;
    psychologistId: number | null;
    startsAt: string;
    format: string;
    capacity: number | null;
    status: 'open' | 'closed';
    notice: string;
}

/** Групові сеанси БСТ: психолог — свої сеанси, учасники, черга й оголошення; координатор — усі. */
@Component({
    selector: 'app-bst-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './editor.scss',
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@bst.title">Групові сеанси БСТ</h1>
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <table class="grid" data-testid="bst">
                <thead>
                    <tr>
                        <th i18n="@@bst.when">Коли</th>
                        <th i18n="@@bst.psychologist">Психолог</th>
                        <th i18n="@@bst.places">Місця</th>
                        <th i18n="@@bst.status">Стан</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (s of sessions(); track s.id) {
                        <tr>
                            <td>{{ when(s.starts_at) }} · {{ s.format ?? '' }}</td>
                            <td>{{ s.psychologist_name }}</td>
                            <td>{{ s.taken }}{{ s.capacity ? ' / ' + s.capacity : '' }}</td>
                            <td>
                                <span class="badge">{{
                                    s.status === 'open' ? openLabel : closedLabel
                                }}</span>
                            </td>
                            <td>
                                <button
                                    mat-button
                                    type="button"
                                    (click)="open(s)"
                                    i18n="@@bst.participants"
                                >
                                    Учасники
                                </button>
                                <button
                                    mat-button
                                    type="button"
                                    (click)="edit(s)"
                                    i18n="@@bst.edit"
                                >
                                    Змінити
                                </button>
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="5" class="muted" i18n="@@bst.empty">Сеансів немає.</td>
                        </tr>
                    }
                </tbody>
            </table>

            @if (selected(); as s) {
                <h2 class="page-subtitle">{{ when(s.starts_at) }} · {{ s.psychologist_name }}</h2>
                <ol>
                    @for (p of participants(); track p.id) {
                        <li>
                            {{ p.name ?? '—' }}{{ p.phone ? ', +' + p.phone : '' }}
                            <span class="muted">{{ p.status }}</span>
                        </li>
                    } @empty {
                        <li class="muted" i18n="@@bst.noParticipants">Учасників немає.</li>
                    }
                </ol>
                <h3 class="muted" i18n="@@bst.waitlist">Лист очікування</h3>
                <ol>
                    @for (p of waitlist(); track p.id) {
                        <li>
                            {{ p.name ?? '—' }}
                            <span class="muted">{{
                                p.status === 'offered' ? offeredLabel : waitingLabel
                            }}</span>
                        </li>
                    } @empty {
                        <li class="muted" i18n="@@bst.noWaitlist">Черга порожня.</li>
                    }
                </ol>
                <p class="notice"><b i18n="@@bst.notice">Оголошення:</b> {{ s.notice || '—' }}</p>
                <button
                    mat-stroked-button
                    type="button"
                    (click)="sendNotice(s)"
                    i18n="@@bst.sendNotice"
                >
                    Надіслати оголошення учасникам
                </button>
                @if (sent()) {
                    <span class="muted">{{ sent() }}</span>
                }
            }

            <h2 class="page-subtitle">{{ form.id ? editTitle : newTitle }}</h2>
            <form class="editor" (ngSubmit)="save()">
                @if (session.isCoordinator()) {
                    <label
                        ><span i18n="@@bst.psychologist">Психолог</span>
                        <select
                            class="border"
                            name="psychologist"
                            [(ngModel)]="form.psychologistId"
                            required
                        >
                            @for (p of psychologists(); track p.id) {
                                <option [ngValue]="p.id">{{ p.name }}</option>
                            }
                        </select>
                    </label>
                }
                <label
                    ><span i18n="@@bst.startsAt">Початок</span
                    ><input
                        class="border"
                        type="datetime-local"
                        name="startsAt"
                        [(ngModel)]="form.startsAt"
                        required
                /></label>
                <label
                    ><span i18n="@@bst.format">Формат</span
                    ><input class="border" name="format" [(ngModel)]="form.format"
                /></label>
                <label
                    ><span i18n="@@bst.capacity">Місткість</span
                    ><input
                        class="border"
                        type="number"
                        min="0"
                        name="capacity"
                        [(ngModel)]="form.capacity"
                /></label>
                <label
                    ><span i18n="@@bst.status">Стан</span>
                    <select class="border" name="status" [(ngModel)]="form.status">
                        <option value="open">{{ openLabel }}</option>
                        <option value="closed">{{ closedLabel }}</option>
                    </select>
                </label>
                <label class="wide"
                    ><span i18n="@@bst.noticeField">Оголошення учасникам</span
                    ><textarea
                        class="border"
                        rows="3"
                        name="notice"
                        [(ngModel)]="form.notice"
                    ></textarea>
                </label>
                <div class="form-row wide">
                    <button mat-flat-button type="submit" [disabled]="!ownerId()" i18n="@@bst.save">
                        Зберегти
                    </button>
                    @if (form.id) {
                        <button mat-button type="button" (click)="reset()" i18n="@@bst.cancelEdit">
                            Скасувати
                        </button>
                    }
                </div>
            </form>
        </section>
    `,
})
export class BstPage implements OnInit {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);

    protected readonly openLabel = $localize`:@@status.open:відкрито`;
    protected readonly closedLabel = $localize`:@@status.closed:закрито`;
    protected readonly offeredLabel = $localize`:@@bst.offered:запропоновано місце`;
    protected readonly waitingLabel = $localize`:@@bst.waiting:чекає`;
    protected readonly newTitle = $localize`:@@bst.new:Новий сеанс`;
    protected readonly editTitle = $localize`:@@bst.editTitle:Редагування сеансу`;
    protected readonly sessions = signal<BstSession[]>([]);
    protected readonly psychologists = signal<Psychologist[]>([]);
    protected readonly selected = signal<BstSession | null>(null);
    protected readonly participants = signal<Participant[]>([]);
    protected readonly waitlist = signal<Participant[]>([]);
    protected readonly error = signal<string | null>(null);
    protected readonly sent = signal<string | null>(null);
    protected form = this.empty();
    protected readonly ownerId = computed(() =>
        this.session.isCoordinator() ? true : this.session.me()?.psychologist_id,
    );

    ngOnInit(): void {
        if (this.session.isCoordinator()) {
            this.api.psychologists().subscribe((list) => this.psychologists.set(list));
        }
        this.reload();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected open(s: BstSession): void {
        this.selected.set(s);
        this.sent.set(null);
        this.api.bstParticipants(s.id).subscribe((r) => {
            this.participants.set(r.participants);
            this.waitlist.set(r.waitlist);
        });
    }

    protected edit(s: BstSession): void {
        this.form = {
            id: s.id,
            psychologistId: s.psychologist_id,
            startsAt: isoToZoned(s.starts_at, this.session.timezone()),
            format: s.format ?? '',
            capacity: s.capacity,
            status: s.status,
            notice: s.notice ?? '',
        };
    }

    protected reset(): void {
        this.form = this.empty();
    }

    protected save(): void {
        const f = this.form;
        const psychologistId = this.session.isCoordinator()
            ? f.psychologistId
            : (this.session.me()?.psychologist_id ?? null);
        if (!psychologistId || !f.startsAt) {
            return;
        }
        this.api
            .saveBst(f.id, {
                psychologist_id: psychologistId,
                starts_at: zonedToIso(f.startsAt, this.session.timezone()),
                format: f.format || null,
                capacity: f.capacity,
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

    protected sendNotice(s: BstSession): void {
        this.api.sendBstNotice(s.id).subscribe({
            next: (r) =>
                this.sent.set($localize`:@@notice.queued:Поставлено в чергу: ${r.queued}:count:`),
            error: (e: unknown) => this.sent.set(errorText(e)),
        });
    }

    private empty(): BstForm {
        return {
            id: null,
            psychologistId: null,
            startsAt: '',
            format: 'онлайн',
            capacity: null,
            status: 'open',
            notice: '',
        };
    }

    private reload(): void {
        this.api
            .bst(dayStartIso(this.session.timezone(), -7))
            .subscribe((list) => this.sessions.set(list));
    }
}
