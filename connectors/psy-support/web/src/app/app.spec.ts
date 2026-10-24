import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from './app';
import { routes } from './app.routes';
import { Me } from './core/api';
import { Session, safeReturnUrl, sessionInterceptor } from './core/session';

const coordinator: Me = {
    id: 1,
    email: 'coord@psy.ua',
    role: 'coordinator',
    timezone: 'Europe/Kyiv',
};
const psychologist: Me = {
    id: 2,
    email: 'psy@psy.ua',
    role: 'psychologist',
    psychologist_id: 7,
    timezone: 'Europe/Kyiv',
};

function configure(me: Me | null) {
    TestBed.configureTestingModule({
        imports: [App],
        providers: [
            provideRouter(routes),
            provideHttpClient(withInterceptors([sessionInterceptor])),
            provideHttpClientTesting(),
        ],
    });
    if (me) {
        TestBed.inject(Session).set(me);
    }
}

function navLinks(host: HTMLElement): string[] {
    return Array.from(host.querySelectorAll('[data-testid^="nav-"]')).map(
        (a) => a.getAttribute('data-testid') ?? '',
    );
}

describe('App', () => {
    it('координатор бачить усі розділи', async () => {
        configure(coordinator);
        const fixture = TestBed.createComponent(App);
        await fixture.whenStable();
        const host = fixture.nativeElement as HTMLElement;
        expect(navLinks(host)).toContain('nav-staff');
        expect(navLinks(host)).toContain('nav-users');
        expect(host.querySelector('[data-testid="current-role"]')?.textContent).toContain(
            'координатор',
        );
    });

    it('психолог не бачить розділів координатора', async () => {
        configure(psychologist);
        const fixture = TestBed.createComponent(App);
        await fixture.whenStable();
        const links = navLinks(fixture.nativeElement as HTMLElement);
        expect(links).toEqual([
            'nav-schedule',
            'nav-bookings',
            'nav-events',
            'nav-bst',
            'nav-materials',
        ]);
    });

    it('без входу навігації немає', async () => {
        configure(null);
        const fixture = TestBed.createComponent(App);
        await fixture.whenStable();
        expect((fixture.nativeElement as HTMLElement).querySelector('nav')).toBeNull();
    });

    it('психолога зі сторінки координатора перенаправляє на розклад', async () => {
        configure(psychologist);
        const harness = await RouterTestingHarness.create();
        const http = TestBed.inject(HttpTestingController);
        const navigation = harness.navigateByUrl('/staff');
        await new Promise((r) => setTimeout(r));
        http.match(() => true).forEach((req) => req.flush([]));
        await navigation;
        expect(TestBed.inject(Router).url).toBe('/schedule');
    });

    it('без сесії guard веде на вхід із returnUrl', async () => {
        configure(null);
        const harness = await RouterTestingHarness.create();
        const http = TestBed.inject(HttpTestingController);
        const navigation = harness.navigateByUrl('/bookings');
        await new Promise((r) => setTimeout(r));
        http.expectOne('/api/v1/auth/me').flush({}, { status: 401, statusText: 'Unauthorized' });
        await navigation;
        expect(TestBed.inject(Router).url).toBe('/login?returnUrl=%2Fbookings');
    });
});

describe('safeReturnUrl', () => {
    it('пропускає лише внутрішні шляхи', () => {
        expect(safeReturnUrl('/events')).toBe('/events');
        expect(safeReturnUrl('//evil.example')).toBe('/');
        expect(safeReturnUrl('https://evil.example')).toBe('/');
        expect(safeReturnUrl('/\\evil')).toBe('/');
        expect(safeReturnUrl('/login')).toBe('/');
        expect(safeReturnUrl(null)).toBe('/');
    });
});
