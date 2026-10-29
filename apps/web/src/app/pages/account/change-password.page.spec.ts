import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { AuthService } from '../../auth/auth.service';
import { ChangePasswordPage } from './change-password.page';

describe('ChangePasswordPage', () => {
  async function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ returnUrl: '/projects/p1/app' }) } } },
      ],
    });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@firma.ua', role: 'viewer', mustChangePassword: true });
    const fixture = TestBed.createComponent(ChangePasswordPage);
    fixture.detectChanges();
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const type = (testId: string, value: string) => {
      const input = host.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    const submit = () => {
      host.querySelector<HTMLFormElement>('[data-testid="password-form"]')!.dispatchEvent(new Event('submit'));
      fixture.detectChanges();
    };
    return { fixture, host, type, submit, http: TestBed.inject(HttpTestingController) };
  }

  it('пояснює тимчасовий пароль і не надсилає, якщо паролі не збігаються', async () => {
    const { host, type, submit, http } = await setup();
    expect(host.querySelector('[data-testid="password-temporary"]')).not.toBeNull();
    type('password-current', 'Temp-pass-1');
    type('password-new', 'New-pass-12');
    type('password-repeat', 'New-pass-13');
    submit();
    expect(host.querySelector('[data-testid="password-error"]')?.textContent).toContain('не збігаються');
    http.expectNone('/api/v1/auth/password');
  });

  it('змінює пароль і повертає на returnUrl', async () => {
    const { type, submit, http } = await setup();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    type('password-current', 'Temp-pass-1');
    type('password-new', 'New-pass-12');
    type('password-repeat', 'New-pass-12');
    submit();

    const req = http.expectOne({ method: 'POST', url: '/api/v1/auth/password' });
    expect(req.request.body).toEqual({ currentPassword: 'Temp-pass-1', newPassword: 'New-pass-12' });
    req.flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne('/api/v1/auth/me').flush({ id: 'u1', email: 'a@firma.ua', role: 'viewer', mustChangePassword: false });

    expect(navigate).toHaveBeenCalledWith('/projects/p1/app');
    expect(TestBed.inject(AuthService).user()?.mustChangePassword).toBe(false);
  });
});
