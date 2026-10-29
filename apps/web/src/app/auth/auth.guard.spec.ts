import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

describe('authGuard', () => {
  function run(url: string) {
    return TestBed.runInInjectionContext(() => authGuard({} as ActivatedRouteSnapshot, { url } as RouterStateSnapshot));
  }

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
  });

  it('пускає користувача з постійним паролем', () => {
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@firma.ua', role: 'viewer' });
    expect(run('/projects')).toBe(true);
  });

  it('F8-13: з тимчасовим паролем веде на зміну пароля, а туди пускає', () => {
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@firma.ua', role: 'viewer', mustChangePassword: true });
    const result = run('/projects/p1/app');
    expect(result).toBeInstanceOf(UrlTree);
    expect(TestBed.inject(Router).serializeUrl(result as UrlTree)).toBe('/account/password?returnUrl=%2Fprojects%2Fp1%2Fapp');
    expect(run('/account/password?returnUrl=%2F')).toBe(true);
  });
});
