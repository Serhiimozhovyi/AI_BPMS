import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AuthService } from '../auth/auth.service';
import { GlobalAssistantComponent } from './global-assistant.component';
import { GlobalAssistantService } from './global-assistant.service';

const BASE = '/api/v1/assistant/conversations';

describe('GlobalAssistantComponent', () => {
  function create(role: 'admin' | 'editor' | 'viewer' | null) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    TestBed.inject(AuthService).user.set(role ? { id: 'u1', email: 'a@b.ua', role } : null);
    const fixture = TestBed.createComponent(GlobalAssistantComponent);
    fixture.detectChanges();
    return { fixture, host: fixture.nativeElement as HTMLElement, http: TestBed.inject(HttpTestingController), service: TestBed.inject(GlobalAssistantService) };
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('панель доступна editor і admin, для viewer і гостя — ні', () => {
    for (const role of ['editor', 'admin'] as const) {
      TestBed.resetTestingModule();
      const { fixture, host, service } = create(role);
      expect(service.available()).toBe(true);
      expect(host.querySelector('[data-testid="global-assistant"]')).toBeNull();
      service.toggle();
      fixture.detectChanges();
      expect(host.querySelector('[data-testid="global-assistant"]')).not.toBeNull();
    }
    for (const role of ['viewer', null] as const) {
      TestBed.resetTestingModule();
      const { fixture, host, service } = create(role);
      expect(service.available()).toBe(false);
      service.show();
      fixture.detectChanges();
      expect(host.querySelector('[data-testid="global-assistant"]')).toBeNull();
    }
  });

  it('панель показує контекст сторінки; «Закрити» ховає її', () => {
    const { fixture, host, service } = create('editor');
    service.toggle();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="global-assistant-context"]')?.textContent).toContain('Контекст:');
    (host.querySelector('[data-testid="global-assistant-close"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(service.open()).toBe(false);
    expect(host.querySelector('[data-testid="global-assistant"]')).toBeNull();
  });

  it('надсилання: рядки розмови з\'являються, поле блокується, відповідь додається', () => {
    const { fixture, host, http, service } = create('editor');
    service.show();
    fixture.detectChanges();
    const input = host.querySelector('[data-testid="global-assistant-input"]') as HTMLTextAreaElement;
    input.value = 'Привіт';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (host.querySelector('[data-testid="global-assistant-send"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne({ method: 'POST', url: BASE }).flush({ id: 'c1', title: 'Нова', messages: [], proposals: [] });
    expect(host.querySelector('[data-testid="global-assistant-busy"]')).not.toBeNull();
    http
      .expectOne(`${BASE}/c1/messages`)
      .flush(`event: message\ndata: ${JSON.stringify({ type: 'message', text: 'Вітаю!' })}\n\nevent: done\ndata: {"type":"done"}\n\n`);
    http.expectOne({ method: 'GET', url: BASE }).flush([]);
    fixture.detectChanges();
    const lines = Array.from(host.querySelectorAll('[data-kind]')).map((l) => [l.getAttribute('data-kind'), l.textContent?.trim()]);
    expect(lines).toEqual([
      ['user', 'Привіт'],
      ['assistant', 'Вітаю!'],
    ]);
    expect(host.querySelector('[data-testid="global-assistant-busy"]')).toBeNull();
  });

  it('історія показує розмови, вибір відкриває розмову, нова розмова очищає чат', () => {
    const { fixture, host, http, service } = create('editor');
    service.show();
    fixture.detectChanges();
    (host.querySelector('[data-testid="global-assistant-history"]') as HTMLButtonElement).click();
    http.expectOne({ method: 'GET', url: BASE }).flush([{ id: 'c1', title: 'Таблиця заявок', userId: 'u', messageCount: 2, createdAt: '', updatedAt: '2026-10-28T10:00:00Z' }]);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="global-assistant-list"]')?.textContent).toContain('Таблиця заявок');

    (host.querySelector('[data-conversation="c1"]') as HTMLButtonElement).click();
    http.expectOne(`${BASE}/c1`).flush({
      id: 'c1',
      title: 'Таблиця заявок',
      userId: 'u',
      createdAt: '',
      updatedAt: '',
      messages: [{ role: 'user', text: 'Зроби таблицю', tool: null, ok: null, at: '' }],
      proposals: [],
    });
    fixture.detectChanges();
    expect(host.querySelector('[data-kind="user"]')?.textContent?.trim()).toBe('Зроби таблицю');

    (host.querySelector('[data-testid="global-assistant-new"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector('[data-kind]')).toBeNull();
  });

  it('модель не вибрана — підказка з посиланням на налаштування', () => {
    const { fixture, host, service } = create('admin');
    service.show();
    service.notConfigured.set(true);
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="global-assistant-not-configured"]')?.textContent).toContain('не вибрано модель');
  });
});
