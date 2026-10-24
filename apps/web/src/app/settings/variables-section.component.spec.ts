import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService, UserInfo } from '../auth/auth.service';
import { VariableInfo } from './variables-api.service';
import { VariablesSection } from './variables-section.component';

const region: VariableInfo = { key: 'region', value: 'ua-central', description: 'Регіон', updatedAt: '2026-10-24T10:00:00Z' };
const apiUrl: VariableInfo = { key: 'api_url', value: 'https://erp.example', description: null, updatedAt: '2026-10-24T10:00:00Z' };

describe('VariablesSection', () => {
  async function setup(role: UserInfo['role'], items: VariableInfo[] = [apiUrl, region]) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@openbp.test', role });
    const fixture = TestBed.createComponent(VariablesSection);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: '/api/v1/variables' }).flush(items);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function type(host: HTMLElement, testid: string, value: string) {
    const input = host.querySelector<HTMLInputElement>(`[data-testid="${testid}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  it('viewer бачить змінні без форми й кнопок', async () => {
    const { host } = await setup('viewer');
    const rows = host.querySelectorAll('[data-var]');
    expect(rows).toHaveLength(2);
    expect(rows[1].textContent).toContain('ua-central');
    expect(rows[1].textContent).toContain('Регіон');
    expect(host.querySelector('[data-testid="vars-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="var-edit"]')).toBeNull();
  });

  it('порожній список — підказка', async () => {
    const { host } = await setup('admin', []);
    expect(host.querySelector('[data-testid="vars-empty"]')).not.toBeNull();
  });

  it('admin створює змінну: PUT і рядок у списку за абеткою', async () => {
    const { fixture, http, host } = await setup('admin');
    type(host, 'var-key', ' city ');
    type(host, 'var-value', 'Київ');
    type(host, 'var-description', '  ');
    host.querySelector<HTMLButtonElement>('[data-testid="var-save"]')!.click();
    const req = http.expectOne({ method: 'PUT', url: '/api/v1/variables/city' });
    expect(req.request.body).toEqual({ value: 'Київ', description: null });
    req.flush({ key: 'city', value: 'Київ', description: null, updatedAt: '2026-10-24T11:00:00Z' });
    await render(fixture);
    expect([...host.querySelectorAll('[data-var]')].map((r) => r.getAttribute('data-var'))).toEqual(['api_url', 'city', 'region']);
    expect(host.querySelector<HTMLInputElement>('[data-testid="var-key"]')!.value).toBe('');
  });

  it('некоректний ключ — помилка поля, запиту немає', async () => {
    const { fixture, http, host } = await setup('admin');
    type(host, 'var-key', '1bad-key');
    host.querySelector<HTMLButtonElement>('[data-testid="var-save"]')!.click();
    await render(fixture);
    http.expectNone({ method: 'PUT' });
    expect(host.querySelector('[data-testid="var-key-error"]')).not.toBeNull();
  });

  it('«Змінити» заповнює форму, ключ лишається незмінним', async () => {
    const { fixture, http, host } = await setup('admin');
    host.querySelector<HTMLButtonElement>('[data-var="region"] [data-testid="var-edit"]')!.click();
    await render(fixture);
    const key = host.querySelector<HTMLInputElement>('[data-testid="var-key"]')!;
    expect(key.value).toBe('region');
    expect(key.readOnly).toBe(true);
    type(host, 'var-value', 'ua-west');
    host.querySelector<HTMLButtonElement>('[data-testid="var-save"]')!.click();
    const req = http.expectOne({ method: 'PUT', url: '/api/v1/variables/region' });
    expect(req.request.body).toEqual({ value: 'ua-west', description: 'Регіон' });
    req.flush({ ...region, value: 'ua-west' });
    await render(fixture);
    expect(host.querySelector('[data-var="region"]')!.textContent).toContain('ua-west');
    expect(host.querySelector('[data-testid="var-cancel"]')).toBeNull();
  });

  it('видалення прибирає рядок; помилка Api показується', async () => {
    const { fixture, http, host } = await setup('admin');
    host.querySelector<HTMLButtonElement>('[data-var="region"] [data-testid="var-delete"]')!.click();
    http.expectOne({ method: 'DELETE', url: '/api/v1/variables/region' }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    expect(host.querySelectorAll('[data-var]')).toHaveLength(1);

    host.querySelector<HTMLButtonElement>('[data-var="api_url"] [data-testid="var-delete"]')!.click();
    http.expectOne('/api/v1/variables/api_url').flush(null, { status: 403, statusText: 'Forbidden' });
    await render(fixture);
    expect(host.querySelector('[data-testid="vars-error"]')?.textContent).toContain('прав');
    expect(host.querySelectorAll('[data-var]')).toHaveLength(1);
  });
});
