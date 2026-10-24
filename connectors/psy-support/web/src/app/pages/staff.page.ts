import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { Observable } from 'rxjs';
import { Api, Invite, Psychologist, Role, StaffAccount } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';
import { formatDateTime } from '../core/time';

/**
 * Психологи (картки, телефон для прив'язки Telegram) і облікові записи адмінки. Пароль психолог задає сам за
 * одноразовим посиланням: застосунок пошти не надсилає, тож посилання показується тут.
 */
@Component({
    selector: 'app-staff-page',
    imports: [FormsModule, MatButtonModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './editor.scss',
    template: `
        <section class="page p-6">
            <h1 class="page-title" i18n="@@staff.title">Психологи й доступ</h1>
            @if (error()) {
                <p class="error" role="alert">{{ error() }}</p>
            }
            <h2 class="page-subtitle" i18n="@@staff.psychologists">Психологи</h2>
            <table class="grid">
                <thead>
                    <tr>
                        <th i18n="@@staff.name">Ім'я</th>
                        <th i18n="@@staff.phone">Телефон</th>
                        <th>Email</th>
                        <th i18n="@@staff.telegram">Telegram</th>
                        <th i18n="@@staff.active">Активний</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (p of psychologists(); track p.id) {
                        <tr>
                            <td>{{ p.name }}</td>
                            <td>{{ p.phone ? '+' + p.phone : '—' }}</td>
                            <td>{{ p.email ?? '—' }}</td>
                            <td>{{ p.linked_to_telegram ? linkedLabel : notLinkedLabel }}</td>
                            <td>
                                <span
                                    class="badge"
                                    [class.badge--ok]="p.active"
                                    [class.badge--off]="!p.active"
                                    >{{ p.active ? yesLabel : noLabel }}</span
                                >
                            </td>
                            <td>
                                <button
                                    mat-button
                                    type="button"
                                    (click)="editPsychologist(p)"
                                    i18n="@@staff.edit"
                                >
                                    Змінити
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
            <form class="editor" style="margin-top: 12px" (ngSubmit)="savePsychologist()">
                <label
                    ><span i18n="@@staff.name">Ім'я</span
                    ><input class="border" name="pname" [(ngModel)]="psy.name" required
                /></label>
                <label
                    ><span i18n="@@staff.phoneHint">Телефон (для прив'язки Telegram)</span
                    ><input class="border" name="pphone" [(ngModel)]="psy.phone"
                /></label>
                <label
                    ><span>Email</span
                    ><input class="border" type="email" name="pemail" [(ngModel)]="psy.email"
                /></label>
                <label
                    ><span i18n="@@staff.active">Активний</span
                    ><input type="checkbox" name="pactive" [(ngModel)]="psy.active"
                /></label>
                <div class="form-row wide">
                    <button mat-flat-button type="submit">
                        {{ psy.id ? saveLabel : addPsychologistLabel }}
                    </button>
                    @if (psy.id) {
                        <button
                            mat-button
                            type="button"
                            (click)="psy = newPsychologist()"
                            i18n="@@staff.cancelEdit"
                        >
                            Скасувати
                        </button>
                    }
                </div>
            </form>

            <h2 class="page-subtitle" i18n="@@staff.accounts">Облікові записи адмінки</h2>
            <table class="grid" data-testid="accounts">
                <thead>
                    <tr>
                        <th>Email</th>
                        <th i18n="@@staff.role">Роль</th>
                        <th i18n="@@staff.psychologist">Психолог</th>
                        <th i18n="@@staff.lastLogin">Останній вхід</th>
                        <th></th>
                    </tr>
                </thead>
                <tbody>
                    @for (a of accounts(); track a.id) {
                        <tr>
                            <td>
                                {{ a.email }}
                                @if (a.disabled) {
                                    <span class="badge badge--off" i18n="@@staff.disabled"
                                        >вимкнено</span
                                    >
                                }
                                @if (!a.password_set) {
                                    <span class="badge" i18n="@@staff.noPassword"
                                        >пароль не задано</span
                                    >
                                }
                            </td>
                            <td>
                                {{
                                    a.role === 'coordinator' ? coordinatorLabel : psychologistLabel
                                }}
                            </td>
                            <td>{{ psychologistName(a.psychologist_id) }}</td>
                            <td>{{ a.last_login_at ? when(a.last_login_at) : '—' }}</td>
                            <td>
                                <button
                                    mat-button
                                    type="button"
                                    (click)="invite(a)"
                                    i18n="@@staff.newInvite"
                                >
                                    Нове посилання
                                </button>
                                @if (a.id !== session.me()?.id) {
                                    <button mat-button type="button" (click)="toggle(a)">
                                        {{ a.disabled ? enableLabel : disableLabel }}
                                    </button>
                                }
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
            <form class="editor" style="margin-top: 12px" (ngSubmit)="createAccount()">
                <label
                    ><span>Email</span
                    ><input
                        class="border"
                        type="email"
                        name="aemail"
                        data-testid="account-email"
                        [(ngModel)]="account.email"
                        required
                /></label>
                <label
                    ><span i18n="@@staff.role">Роль</span>
                    <select
                        class="border"
                        name="arole"
                        data-testid="account-role"
                        [(ngModel)]="account.role"
                    >
                        <option value="psychologist">{{ psychologistLabel }}</option>
                        <option value="coordinator">{{ coordinatorLabel }}</option>
                    </select>
                </label>
                @if (account.role === 'psychologist') {
                    <label
                        ><span i18n="@@staff.psychologist">Психолог</span>
                        <select
                            class="border"
                            name="apsy"
                            data-testid="account-psychologist"
                            [(ngModel)]="account.psychologistId"
                        >
                            @for (p of psychologists(); track p.id) {
                                <option [ngValue]="p.id">{{ p.name }}</option>
                            }
                        </select>
                    </label>
                }
                <div class="form-row wide">
                    <button
                        mat-flat-button
                        type="submit"
                        data-testid="account-create"
                        i18n="@@staff.createAccount"
                    >
                        Створити обліковий запис
                    </button>
                </div>
            </form>
            @if (inviteLink(); as link) {
                <p class="notice" data-testid="invite-link">
                    <span i18n="@@staff.inviteHint"
                        >Передайте це посилання (діє 24 год, одноразове):</span
                    ><br />
                    <code>{{ link }}</code>
                </p>
            }
        </section>
    `,
})
export class StaffPage implements OnInit {
    protected readonly session = inject(Session);
    private readonly api = inject(Api);

    protected readonly coordinatorLabel = $localize`:@@role.coordinator:координатор`;
    protected readonly psychologistLabel = $localize`:@@role.psychologist:психолог`;
    protected readonly linkedLabel = $localize`:@@staff.linked:прив'язано`;
    protected readonly notLinkedLabel = $localize`:@@staff.notLinked:ні`;
    protected readonly yesLabel = $localize`:@@common.yes:так`;
    protected readonly noLabel = $localize`:@@common.no:ні`;
    protected readonly saveLabel = $localize`:@@staff.save:Зберегти`;
    protected readonly addPsychologistLabel = $localize`:@@staff.addPsychologist:Додати психолога`;
    protected readonly enableLabel = $localize`:@@staff.enable:Увімкнути`;
    protected readonly disableLabel = $localize`:@@staff.disable:Вимкнути`;
    protected readonly psychologists = signal<Psychologist[]>([]);
    protected readonly accounts = signal<StaffAccount[]>([]);
    protected readonly error = signal<string | null>(null);
    protected readonly inviteLink = signal<string | null>(null);
    protected readonly newPsychologist = () => ({
        id: null as number | null,
        name: '',
        phone: '',
        email: '',
        active: true,
    });
    protected psy = this.newPsychologist();
    protected account: { email: string; role: Role; psychologistId: number | null } = {
        email: '',
        role: 'psychologist',
        psychologistId: null,
    };

    ngOnInit(): void {
        this.reload();
    }

    protected when(iso: string): string {
        return formatDateTime(iso, this.session.timezone());
    }

    protected psychologistName(id: number | null | undefined): string {
        return id ? (this.psychologists().find((p) => p.id === id)?.name ?? '—') : '—';
    }

    protected editPsychologist(p: Psychologist): void {
        this.psy = {
            id: p.id,
            name: p.name,
            phone: p.phone ?? '',
            email: p.email ?? '',
            active: p.active,
        };
    }

    protected savePsychologist(): void {
        const body = {
            name: this.psy.name,
            phone: this.psy.phone || null,
            email: this.psy.email || null,
            active: this.psy.active,
        };
        const request: Observable<unknown> = this.psy.id
            ? this.api.updatePsychologist(this.psy.id, body)
            : this.api.createPsychologist(body);
        request.subscribe({
            next: () => {
                this.psy = this.newPsychologist();
                this.reload();
            },
            error: (e: unknown) => this.error.set(errorText(e)),
        });
    }

    protected createAccount(): void {
        this.error.set(null);
        this.api
            .createStaff({
                email: this.account.email.trim(),
                role: this.account.role,
                psychologist_id:
                    this.account.role === 'psychologist' ? this.account.psychologistId : null,
            })
            .subscribe({
                next: (r) => {
                    this.showInvite(r.invite);
                    this.account = { email: '', role: 'psychologist', psychologistId: null };
                    this.reload();
                },
                error: (e: unknown) => this.error.set(errorText(e)),
            });
    }

    protected invite(a: StaffAccount): void {
        this.api
            .newInvite(a.id)
            .subscribe({
                next: (i) => this.showInvite(i),
                error: (e: unknown) => this.error.set(errorText(e)),
            });
    }

    protected toggle(a: StaffAccount): void {
        this.api
            .setStaffDisabled(a.id, !a.disabled)
            .subscribe({
                next: () => this.reload(),
                error: (e: unknown) => this.error.set(errorText(e)),
            });
    }

    private showInvite(invite: Invite): void {
        this.inviteLink.set(`${location.origin}/invite?token=${encodeURIComponent(invite.token)}`);
    }

    private reload(): void {
        this.api.psychologists().subscribe((list) => this.psychologists.set(list));
        this.api.staff().subscribe((list) => this.accounts.set(list));
    }
}
