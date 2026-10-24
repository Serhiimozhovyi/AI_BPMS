import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/errors';
import { Session } from '../core/session';

/** Встановлення пароля за одноразовим посиланням від координатора (24 год). */
@Component({
    selector: 'app-invite-page',
    imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './auth-card.scss',
    template: `
        <section class="page auth-card">
            <h1 class="page-title" i18n="@@invite.title">Встановлення пароля</h1>
            <form (ngSubmit)="submit()">
                <mat-form-field appearance="outline">
                    <mat-label i18n="@@invite.password"
                        >Новий пароль (щонайменше 10 символів)</mat-label
                    >
                    <input
                        matInput
                        type="password"
                        name="password"
                        autocomplete="new-password"
                        data-testid="invite-password"
                        [(ngModel)]="password"
                        required
                    />
                </mat-form-field>
                <mat-form-field appearance="outline">
                    <mat-label i18n="@@invite.repeat">Повторіть пароль</mat-label>
                    <input
                        matInput
                        type="password"
                        name="repeat"
                        autocomplete="new-password"
                        data-testid="invite-repeat"
                        [(ngModel)]="repeat"
                        required
                    />
                </mat-form-field>
                @if (error()) {
                    <p class="error" role="alert" data-testid="invite-error">{{ error() }}</p>
                }
                <button
                    mat-flat-button
                    type="submit"
                    data-testid="invite-submit"
                    [disabled]="busy()"
                    i18n="@@invite.submit"
                >
                    Зберегти й увійти
                </button>
            </form>
        </section>
    `,
})
export class InvitePage {
    private readonly api = inject(Api);
    private readonly session = inject(Session);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);

    protected password = '';
    protected repeat = '';
    protected readonly error = signal<string | null>(null);
    protected readonly busy = signal(false);

    protected submit(): void {
        if (this.password !== this.repeat) {
            this.error.set($localize`:@@invite.mismatch:Паролі не збігаються.`);
            return;
        }
        if (this.password.length < 10) {
            this.error.set($localize`:@@error.weakPassword:Пароль — щонайменше 10 символів.`);
            return;
        }
        this.busy.set(true);
        this.api
            .acceptInvite(this.route.snapshot.queryParamMap.get('token') ?? '', this.password)
            .subscribe({
                next: (me) => {
                    this.session.set(me);
                    void this.router.navigateByUrl('/');
                },
                error: (e: unknown) => {
                    this.error.set(errorText(e));
                    this.busy.set(false);
                },
            });
    }
}
