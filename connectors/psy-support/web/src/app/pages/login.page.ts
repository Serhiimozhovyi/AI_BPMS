import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { Api } from '../core/api';
import { errorText } from '../core/errors';
import { Session, safeReturnUrl } from '../core/session';

@Component({
    selector: 'app-login-page',
    imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule],
    changeDetection: ChangeDetectionStrategy.OnPush,
    styleUrl: './auth-card.scss',
    template: `
        <section class="page auth-card">
            <h1 class="page-title" i18n="@@login.title">Вхід в адміністрування</h1>
            <form (ngSubmit)="submit()">
                <mat-form-field appearance="outline">
                    <mat-label i18n="@@login.email">Email</mat-label>
                    <input
                        matInput
                        type="email"
                        name="email"
                        autocomplete="username"
                        data-testid="login-email"
                        [(ngModel)]="email"
                        required
                    />
                </mat-form-field>
                <mat-form-field appearance="outline">
                    <mat-label i18n="@@login.password">Пароль</mat-label>
                    <input
                        matInput
                        type="password"
                        name="password"
                        autocomplete="current-password"
                        data-testid="login-password"
                        [(ngModel)]="password"
                        required
                    />
                </mat-form-field>
                @if (error()) {
                    <p class="error" role="alert" data-testid="login-error">{{ error() }}</p>
                }
                <button
                    mat-flat-button
                    type="submit"
                    data-testid="login-submit"
                    [disabled]="busy()"
                    i18n="@@login.submit"
                >
                    Увійти
                </button>
            </form>
        </section>
    `,
})
export class LoginPage {
    private readonly api = inject(Api);
    private readonly session = inject(Session);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);

    protected email = '';
    protected password = '';
    protected readonly error = signal<string | null>(null);
    protected readonly busy = signal(false);

    protected submit(): void {
        this.busy.set(true);
        this.error.set(null);
        this.api.login(this.email.trim(), this.password).subscribe({
            next: (me) => {
                this.session.set(me);
                void this.router.navigateByUrl(
                    safeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl')),
                );
            },
            error: (e: unknown) => {
                this.password = '';
                this.error.set(errorText(e));
                this.busy.set(false);
            },
        });
    }
}
