import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API, Booking, Me } from '../core/api';
import { Session } from '../core/session';
import { BookingsPage } from './bookings.page';

const coordinator: Me = {
    id: 1,
    email: 'coord@psy.ua',
    role: 'coordinator',
    timezone: 'Europe/Kyiv',
};

function booking(id: number, startsAt: string): Booking {
    return {
        id,
        slot_id: id,
        psychologist_id: 7,
        psychologist_name: 'Марія',
        starts_at: startsAt,
        duration_min: 50,
        format: 'онлайн',
        status: 'active',
        person_name: 'Олена',
        phone: '380501112233',
    };
}

describe('BookingsPage', () => {
    it('показує всі записи без обмеження в 14 днів, скасувати можна лише майбутні', async () => {
        TestBed.configureTestingModule({
            imports: [BookingsPage],
            providers: [provideHttpClient(), provideHttpClientTesting()],
        });
        TestBed.inject(Session).set(coordinator);
        const http = TestBed.inject(HttpTestingController);
        const fixture = TestBed.createComponent(BookingsPage);
        fixture.detectChanges();

        const req = http.expectOne((r) => r.url === `${API}/bookings`);
        const from = Date.parse(req.request.params.get('from') ?? '');
        const to = Date.parse(req.request.params.get('to') ?? '');
        expect(from).toBeLessThan(Date.parse('2001-01-01T00:00:00Z'));
        expect(to).toBeGreaterThan(Date.now() + 365 * 24 * 3600 * 1000);

        const past = new Date(Date.now() - 3 * 24 * 3600 * 1000).toISOString();
        const far = new Date(Date.now() + 60 * 24 * 3600 * 1000).toISOString();
        req.flush([booking(1, past), booking(2, far)]);
        fixture.detectChanges();
        await fixture.whenStable();

        const host = fixture.nativeElement as HTMLElement;
        expect(host.querySelector('[data-testid="booking-1"]')).not.toBeNull();
        expect(host.querySelector('[data-testid="booking-2"]')).not.toBeNull();
        expect(host.querySelector('[data-testid="booking-1"] button')).toBeNull();
        expect(host.querySelector('[data-testid="booking-2"] button')).not.toBeNull();
        http.verify();
    });
});
