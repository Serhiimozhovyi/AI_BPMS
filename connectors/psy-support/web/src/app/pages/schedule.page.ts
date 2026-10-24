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
import { Observable } from 'rxjs';
import { Api, Psychologist, Slot, SlotTemplate } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { WEEKDAYS, dayStartIso, formatDateTime, zonedToIso } from '../core/time';

/** Розклад: слоти на 14 днів і шаблони слотів (психолог — лише свої). */
@Component({
    selector: 'app-schedule-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@schedule.title">Розклад</h1>
            <div class="form-row">
                @if (session.isCoordinator()) {
                    <label>
                        <span class="muted" i18n="@@schedule.psychologist">Психолог</span>
                        <select
                            class="border"
                            name="psychologist"
                            data-testid="schedule-psychologist"
                            [ngModel]="psychologistId()"
                            (ngModelChange)="selectPsychologist($event)"
                        >
                            <option [ngValue]="null" i18n="@@schedule.all">Усі</option>
                            @for (p of psychologists(); track p.id) {
                                <option [ngValue]="p.id">{{ p.name }}</option>
                            }
                        </select>
                    </label>
                }
            </div>

            <h2 class="page-subtitle" i18n="@@schedule.slots">Слоти на 14 днів</h2>
            @if (canEdit()) {
                <form class="form-row" (ngSubmit)="addSlot()">
                    <input
                        class="border"
                        type="datetime-local"
                        name="startsAt"
                        aria-label="Початок"
                        i18n-aria-label="@@schedule.startsAt"
                        data-testid="slot-starts"
                        [(ngModel)]="newSlot.startsAt"
                        required
                    />
                    <input
                        class="border"
                        type="number"
                        name="duration"
                        min="10"
                        max="240"
                        aria-label="Тривалість, хв"
                        i18n-aria-label="@@schedule.duration"
                        [(ngModel)]="newSlot.duration"
                    />
                    <input
                        class="border"
                        type="text"
                        name="format"
                        aria-label="Формат"
                        i18n-aria-label="@@schedule.format"
                        [(ngModel)]="newSlot.format"
                    />
                    <button
                        mat-flat-button
                        type="submit"
                        data-testid="slot-add"
                        i18n="@@schedule.addSlot"
                    >
                        Додати слот
                    </button>
                </form>
            }
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <table class="grid" data-testid="slots">
                <thead>
                    <tr>
                        <th i18n="@@schedule.when">Коли</th>
                        <th i18n="@@schedule.psychologist">Психолог</th>
                        <th i18n="@@schedule.format">Формат</th>
                        <th i18n="@@schedule.state">Стан</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (s of slots(); track s.id) {
                        <tr>
                            <td>{{ when(s.starts_at) }} · {{ s.duration_min }} хв</td>
                            <td>{{ s.psychologist_name }}</td>
                            <td>{{ s.format }}</td>
                            <td>
                                @if (s.booking_id) {
                                    <span class="badge badge--ok" i18n="@@schedule.booked"
                                        >зайнято</span
                                    >
                                } @else if (s.status === 'closed') {
                                    <span class="badge badge--off" i18n="@@schedule.closed"
                                        >закрито</span
                                    >
                                } @else {
                                    <span class="badge" i18n="@@schedule.free">вільно</span>
                                }
                            </td>
                            <td>
                                @if (canManage(s.psychologist_id)) {
                                    <button mat-button type="button" (click)="toggle(s)">
                                        {{ s.status === 'open' ? closeLabel : openLabel }}
                                    </button>
                                    @if (!s.booking_id) {
                                        <button
                                            mat-button
                                            type="button"
                                            (click)="remove(s)"
                                            i18n="@@schedule.delete"
                                        >
                                            Видалити
                                        </button>
                                    }
                                }
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="5" class="muted" i18n="@@schedule.noSlots">
                                Слотів немає.
                            </td>
                        </tr>
                    }
                </tbody>
            </table>

            <h2 class="page-subtitle" i18n="@@schedule.templates">Шаблони слотів</h2>
            <p class="muted" i18n="@@schedule.templatesHint">
                З шаблонів «Шедулер» щоночі створює слоти на горизонт запису.
            </p>
            @if (canEdit()) {
                <form class="form-row" (ngSubmit)="addTemplate()">
                    <select
                        class="border"
                        name="weekday"
                        aria-label="День тижня"
                        i18n-aria-label="@@schedule.weekday"
                        [(ngModel)]="newTemplate.weekday"
                    >
                        @for (d of weekdays; track $index) {
                            @if ($index > 0) {
                                <option [ngValue]="$index">{{ d }}</option>
                            }
                        }
                    </select>
                    <input
                        class="border"
                        type="time"
                        name="time"
                        aria-label="Час"
                        i18n-aria-label="@@schedule.time"
                        [(ngModel)]="newTemplate.time"
                        required
                    />
                    <input
                        class="border"
                        type="number"
                        name="tduration"
                        min="10"
                        max="240"
                        aria-label="Тривалість, хв"
                        i18n-aria-label="@@schedule.duration"
                        [(ngModel)]="newTemplate.duration"
                    />
                    <input
                        class="border"
                        type="text"
                        name="tformat"
                        aria-label="Формат"
                        i18n-aria-label="@@schedule.format"
                        [(ngModel)]="newTemplate.format"
                    />
                    <button mat-stroked-button type="submit" i18n="@@schedule.addTemplate">
                        Додати шаблон
                    </button>
                </form>
            }
            <table class="grid">
                <thead>
                    <tr>
                        <th i18n="@@schedule.psychologist">Психолог</th>
                        <th i18n="@@schedule.weekday">День тижня</th>
                        <th i18n="@@schedule.time">Час</th>
                        <th i18n="@@schedule.format">Формат</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (t of templates(); track t.id) {
                        <tr>
                            <td>{{ psychologistName(t.psychologist_id) }}</td>
                            <td>{{ weekdays[t.weekday] }}</td>
                            <td>{{ t.time.slice(0, 5) }} · {{ t.duration_min }} хв</td>
                            <td>{{ t.format }}</td>
                            <td>
                                @if (canManage(t.psychologist_id)) {
                                    <button
                                        mat-button
                                        type="button"
                                        (click)="removeTemplate(t)"
                                        i18n="@@schedule.delete"
                                    >
                                        Видалити
                                    </button>
                                }
                            </td>
                        </tr>
                    } @empty {
                        <tr>
                            <td colspan="5" class="muted" i18n="@@schedule.noTemplates">
                                Шаблонів немає.
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
            @if (session.isCoordinator()) {
                <div class="form-row" style="margin-top: 12px">
                    <button
                        mat-stroked-button
                        type="button"
                        (click)="generate()"
                        i18n="@@schedule.generate"
                    >
                        Створити слоти з шаблонів на 21 день
                    </button>
                    @if (generated() !== null) {
                        <span class="muted" i18n="@@schedule.generated"
                            >Створено: {{ generated() }}</span
                        >
                    }
                </div>
            }
        </section>
    `,
})
export class SchedulePage implements OnInit {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);

    protected readonly weekdays = WEEKDAYS;
    protected readonly closeLabel = $localize`:@@schedule.close:Закрити`;
    protected readonly openLabel = $localize`:@@schedule.open:Відкрити`;
    protected readonly psychologists = signal<Psychologist[]>([]);
    protected readonly psychologistId = signal<number | null>(null);
    protected readonly slots = signal<Slot[]>([]);
    protected readonly templates = signal<SlotTemplate[]>([]);
    protected readonly error = signal<string | null>(null);
    protected readonly generated = signal<number | null>(null);
    protected newSlot = { startsAt: '', duration: 50, format: 'онлайн' };
    protected newTemplate = { weekday: 1, time: '10:00', duration: 50, format: 'онлайн' };

    /** Чий розклад редагуємо: психолог — свій, координатор — обраного психолога. */
    private readonly target = computed(() =>
        this.session.isCoordinator()
            ? this.psychologistId()
            : (this.session.me()?.psychologist_id ?? null),
    );
    protected readonly canEdit = computed(() => this.target() !== null);

    ngOnInit(): void {
        this.api.psychologists().subscribe((list) => this.psychologists.set(list));
        this.reload();
    }

    protected selectPsychologist(id: number | null): void {
        this.psychologistId.set(id);
        this.reload();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected psychologistName(id: number): string {
        return this.psychologists().find((p) => p.id === id)?.name ?? '—';
    }

    protected canManage(psychologistId: number): boolean {
        return (
            this.session.isCoordinator() || this.session.me()?.psychologist_id === psychologistId
        );
    }

    protected addSlot(): void {
        const target = this.target();
        if (target === null || !this.newSlot.startsAt) {
            return;
        }
        this.run(
            this.api.createSlot({
                psychologist_id: target,
                starts_at: zonedToIso(this.newSlot.startsAt, this.session.timezone()),
                duration_min: this.newSlot.duration,
                format: this.newSlot.format,
            }),
        );
    }

    protected toggle(slot: Slot): void {
        this.run(this.api.setSlotStatus(slot.id, slot.status === 'open' ? 'closed' : 'open'));
    }

    protected remove(slot: Slot): void {
        this.run(this.api.deleteSlot(slot.id));
    }

    protected addTemplate(): void {
        const target = this.target();
        if (target === null) {
            return;
        }
        this.run(
            this.api.createTemplate({
                psychologist_id: target,
                weekday: this.newTemplate.weekday,
                time: `${this.newTemplate.time}:00`,
                duration_min: this.newTemplate.duration,
                format: this.newTemplate.format,
            }),
        );
    }

    protected removeTemplate(t: SlotTemplate): void {
        this.run(this.api.deleteTemplate(t.id));
    }

    protected generate(): void {
        this.api.generateSlots(21).subscribe({
            next: (r) => {
                this.generated.set(r.created);
                this.reload();
            },
            error: (e: unknown) => this.error.set(errorText(e)),
        });
    }

    private run(action: Observable<unknown>): void {
        this.error.set(null);
        action.subscribe({
            next: () => this.reload(),
            error: (e: unknown) => this.error.set(errorText(e)),
        });
    }

    private reload(): void {
        const tz = this.session.timezone();
        this.api
            .slots(dayStartIso(tz), dayStartIso(tz, 14), this.psychologistId())
            .subscribe((list) => this.slots.set(list));
        this.api.templates(this.psychologistId()).subscribe((list) => this.templates.set(list));
    }
}
