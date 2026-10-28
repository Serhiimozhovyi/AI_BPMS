import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AssistantSection } from './assistant-section.component';

const OFF = { credentialId: null, credentialName: null, provider: null, model: null, configured: false };

function setup(settings: object, options: object[]) {
  TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
  const fixture = TestBed.createComponent(AssistantSection);
  fixture.detectChanges();
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/v1/settings/assistant').flush(settings);
  http.expectOne('/api/v1/credentials/options').flush(options);
  fixture.detectChanges();
  return { fixture, http, host: fixture.nativeElement as HTMLElement };
}

function type(host: HTMLElement, testId: string, value: string): void {
  const input = host.querySelector(`[data-testid="${testId}"]`) as HTMLInputElement;
  input.value = value;
  input.dispatchEvent(new Event('input'));
}

describe('AssistantSection', () => {
  it('без моделей показує, що помічник вимкнений, і пропонує лише credentials моделей', async () => {
    const { fixture, http, host } = setup(OFF, [
      { id: 'c1', name: 'Claude', type: 'anthropic' },
      { id: 'c2', name: 'Telegram', type: 'mcp.http' },
      { id: 'c4', name: 'Gemini', type: 'gemini' },
    ]);
    expect(host.querySelector('[data-testid="assistant-off"]')).not.toBeNull();

    (host.querySelector('[data-testid="assistant-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-credential-0"] .mat-mdc-select-trigger') as HTMLElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const options = Array.from(document.querySelectorAll('mat-option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['Claude · anthropic', 'Gemini · gemini']);
    http.verify();
  });

  it('зберігає кілька моделей з основною', async () => {
    const { fixture, http, host } = setup(
      { credentialId: 'c1', credentialName: 'Claude', provider: 'anthropic', model: 'claude-sonnet-5-5', configured: true },
      [
        { id: 'c1', name: 'Claude', type: 'anthropic' },
        { id: 'c4', name: 'Gemini', type: 'gemini' },
      ],
    );
    // Старий формат без profiles показується одним рядком.
    expect(host.querySelectorAll('[data-testid="assistant-row"]').length).toBe(1);

    (host.querySelector('[data-testid="assistant-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-credential-1"] .mat-mdc-select-trigger') as HTMLElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    (Array.from(document.querySelectorAll('mat-option')).find((o) => o.textContent?.includes('Gemini')) as HTMLElement).click();
    fixture.detectChanges();
    type(host, 'assistant-model-1', ' gemini-flash ');
    (host.querySelector('[data-testid="assistant-primary-1"] input') as HTMLInputElement).click();
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-save"]') as HTMLButtonElement).click();

    const put = http.expectOne({ method: 'PUT', url: '/api/v1/settings/assistant' });
    expect(put.request.body).toEqual({
      profiles: [
        { credentialId: 'c1', model: 'claude-sonnet-5-5' },
        { credentialId: 'c4', model: 'gemini-flash' },
      ],
      primaryIndex: 1,
    });
    put.flush({ credentialId: 'c4', credentialName: 'Gemini', provider: 'gemini', model: 'gemini-flash', configured: true });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="assistant-saved"]')).not.toBeNull();
    http.verify();
  });

  it('не зберігає модель без облікових даних чи назви, порожній список вимикає помічника', () => {
    const { fixture, http, host } = setup(
      { credentialId: 'c1', credentialName: 'Claude', provider: 'anthropic', model: 'm', configured: true },
      [{ id: 'c1', name: 'Claude', type: 'anthropic' }],
    );
    (host.querySelector('[data-testid="assistant-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-save"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="assistant-error"]')).not.toBeNull();
    http.expectNone({ method: 'PUT', url: '/api/v1/settings/assistant' });

    (host.querySelector('[data-testid="assistant-remove-1"]') as HTMLButtonElement).click();
    (host.querySelector('[data-testid="assistant-remove-0"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (host.querySelector('[data-testid="assistant-save"]') as HTMLButtonElement).click();
    const put = http.expectOne({ method: 'PUT', url: '/api/v1/settings/assistant' });
    expect(put.request.body).toEqual({ credentialId: null, model: null });
    put.flush(OFF);
    http.verify();
  });

  it('для Ollama підставляє встановлені моделі зі списку', () => {
    const { fixture, http, host } = setup(OFF, [{ id: 'c3', name: 'Мій Mac', type: 'ollama' }]);
    (host.querySelector('[data-testid="assistant-add"]') as HTMLButtonElement).click();
    fixture.detectChanges();

    fixture.componentInstance['setCredential'](0, 'c3');
    const request = http.expectOne((r) => r.url === '/api/v1/settings/assistant/models');
    expect(request.request.params.get('credentialId')).toBe('c3');
    request.flush([{ name: 'qwen3:14b' }, { name: 'qwen3:8b' }]);
    fixture.detectChanges();

    const options = Array.from(host.querySelectorAll('datalist option')).map((o) => o.getAttribute('value'));
    expect(options).toEqual(['qwen3:14b', 'qwen3:8b']);
    http.verify();
  });
});
