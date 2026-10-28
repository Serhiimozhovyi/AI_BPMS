import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from '../auth/auth.service';
import { checkMessage } from './credential-input';
import { CredentialFormComponent } from './credential-form.component';
import { CredentialTypeInfo } from './credentials-api.service';

const types: CredentialTypeInfo[] = [
  {
    type: 'bearer',
    displayName: 'Bearer-токен',
    description: 'Токен у Authorization.',
    fields: [{ name: 'token', title: 'Токен', kind: 'secret', required: true, description: 'Персональний токен.', placeholder: 'ghp_…' }],
    schema: {},
  },
  {
    type: 'apiKey',
    displayName: 'API-ключ',
    description: 'Ключ сервісу.',
    fields: [
      { name: 'apiKey', title: 'API-ключ', kind: 'secret', required: true },
      { name: 'name', title: 'Назва заголовка', kind: 'text', required: false, description: 'Порожньо — X-API-Key.', placeholder: 'X-API-Key' },
      { name: 'in', title: 'Де передавати', kind: 'text', required: false, options: ['header', 'query'] },
    ],
    schema: {},
  },
  {
    type: 'oauth2.clientCredentials',
    displayName: 'OAuth2: сервісний клієнт',
    description: 'Без входу користувача.',
    fields: [
      { name: 'tokenUrl', title: 'Адреса токена', kind: 'url', required: true },
      { name: 'clientId', title: 'Client ID', kind: 'text', required: true },
      { name: 'clientSecret', title: 'Client secret', kind: 'secret', required: true },
      { name: 'accessToken', title: 'accessToken', kind: 'managed', required: false },
    ],
    schema: {},
  },
];

describe('CredentialFormComponent: поля за типом (F3-94)', () => {
  function setup(type: string) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@openbp.test', role: 'admin' } as never);
    const fixture = TestBed.createComponent(CredentialFormComponent);
    fixture.componentRef.setInput('types', types);
    fixture.componentRef.setInput('allowedTypes', [type]);
    fixture.componentRef.setInput('fixedProjectId', null);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    const http = TestBed.inject(HttpTestingController);
    const fill = (testid: string, value: string) => {
      const input = host.querySelector<HTMLInputElement>(`[data-testid="${testid}"]`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    return { fixture, host, http, fill };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('підпис, підказка, приклад; секрет — поле пароля з «показати», яке перемикається', () => {
    const { fixture, host } = setup('bearer');

    const field = host.querySelector('[data-field="token"]')!;
    expect(field.textContent).toContain('Токен');
    expect(field.textContent).toContain('Персональний токен.');
    const input = host.querySelector<HTMLInputElement>('[data-testid="field-token"]')!;
    expect(input.placeholder).toBe('ghp_…');
    expect(input.type).toBe('password');

    (host.querySelector('[data-testid="reveal-token"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector<HTMLInputElement>('[data-testid="field-token"]')!.type).toBe('text');
    (host.querySelector('[data-testid="reveal-token"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector<HTMLInputElement>('[data-testid="field-token"]')!.type).toBe('password');
    expect(host.querySelector('[data-testid="credential-check"]')).toBeNull();
  });

  it('необов\'язкові поля позначені, значення зі списку — вибір, а не поле вводу', () => {
    const { host } = setup('apiKey');

    expect(host.querySelector('[data-field="apiKey"] mat-label')?.textContent).toBe('API-ключ');
    expect(host.querySelector('[data-field="name"] mat-label')?.textContent).toContain("необов'язково");
    expect(host.querySelector('[data-field="in"] mat-select')).not.toBeNull();
    expect(host.querySelector('input[data-testid="field-in"]')).toBeNull();
  });

  it('«Перевірити» для client credentials: POST /credentials/check без збереження, результат людським текстом', () => {
    const { fixture, host, http, fill } = setup('oauth2.clientCredentials');
    // Службові поля OpenBP у формі не показуються.
    expect(host.querySelector('[data-field="accessToken"]')).toBeNull();

    // Без обов'язкових полів запиту немає — лише помилки форми.
    (host.querySelector('[data-testid="credential-check"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelectorAll('[data-testid="form-error"]').length).toBe(3);

    fill('field-tokenUrl', 'https://sso.example.com/token');
    fill('field-clientId', 'svc');
    fill('field-clientSecret', 'wrong');
    (host.querySelector('[data-testid="credential-check"]') as HTMLButtonElement).click();
    const bad = http.expectOne({ method: 'POST', url: '/api/v1/credentials/check' });
    expect(bad.request.body).toEqual({
      type: 'oauth2.clientCredentials',
      data: { tokenUrl: 'https://sso.example.com/token', clientId: 'svc', clientSecret: 'wrong' },
    });
    bad.flush({ ok: false, error: 'invalid_client' });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="check-result"]')?.textContent).toContain('не прийняв Client ID або Client secret');

    (host.querySelector('[data-testid="credential-check"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/credentials/check').flush({ ok: true });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="check-result"]')?.textContent).toContain('токен отримано');
  });

  it('кнопка збереження: власний підпис і блокування батьківською формою', () => {
    const { fixture, host } = setup('bearer');
    fixture.componentRef.setInput('saveLabel', 'Підключити');
    fixture.componentRef.setInput('saveDisabled', true);
    fixture.detectChanges();

    const save = host.querySelector<HTMLButtonElement>('[data-testid="credential-save"]')!;
    expect(save.textContent).toContain('Підключити');
    expect(save.disabled).toBe(true);
  });
});

describe('checkMessage', () => {
  it('код помилки провайдера → текст без технічних деталей', () => {
    expect(checkMessage('invalid_client')).toContain('Client ID');
    expect(checkMessage('invalid_scope')).toContain('scopes');
    expect(checkMessage('http_404')).toContain('404');
    expect(checkMessage('OAuth-сервер недоступний: ConnectionError.')).toBe('OAuth-сервер недоступний: ConnectionError.');
    expect(checkMessage('weird')).toContain('weird');
  });

  it('типи надійшли після створення форми: єдиний дозволений тип обирається сам (діалог «Нове підключення»)', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'a@openbp.test', role: 'admin' } as never);
    const fixture = TestBed.createComponent(CredentialFormComponent);
    fixture.componentRef.setInput('types', []);
    fixture.componentRef.setInput('allowedTypes', ['bearer']);
    fixture.componentRef.setInput('fixedProjectId', null);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-testid="field-token"]')).toBeNull();

    fixture.componentRef.setInput('types', types);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="field-token"]')).not.toBeNull();
  });
});
