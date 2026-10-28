import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AssistantSection } from './assistant-section.component';

describe('AssistantSection', () => {
  it('показує лише credentials моделей, зберігає вибір і модель', async () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(AssistantSection);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/settings/assistant').flush({ credentialId: null, credentialName: null, provider: null, model: null, configured: false });
    http.expectOne('/api/v1/credentials/options').flush([
      { id: 'c1', name: 'Claude', type: 'anthropic' },
      { id: 'c2', name: 'Telegram', type: 'mcp.http' },
    ]);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    (host.querySelector('[data-testid="assistant-credential"] .mat-mdc-select-trigger') as HTMLElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const options = Array.from(document.querySelectorAll('mat-option')).map((o) => o.textContent?.trim());
    expect(options).toEqual(['Вимкнено', 'Claude · anthropic']);
    (document.querySelectorAll('mat-option')[1] as HTMLElement).click();
    const model = host.querySelector('[data-testid="assistant-model"]') as HTMLInputElement;
    model.value = ' claude-sonnet-5-5 ';
    model.dispatchEvent(new Event('input'));
    (host.querySelector('[data-testid="assistant-save"]') as HTMLButtonElement).click();

    const put = http.expectOne({ method: 'PUT', url: '/api/v1/settings/assistant' });
    expect(put.request.body).toEqual({ credentialId: 'c1', model: 'claude-sonnet-5-5' });
    put.flush({ credentialId: 'c1', credentialName: 'Claude', provider: 'anthropic', model: 'claude-sonnet-5-5', configured: true });
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="assistant-saved"]')).not.toBeNull();
    http.verify();
  });

  it('для Ollama підставляє встановлені моделі зі списку', () => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(AssistantSection);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/settings/assistant').flush({ credentialId: null, credentialName: null, provider: null, model: null, configured: false });
    http.expectOne('/api/v1/credentials/options').flush([{ id: 'c3', name: 'Мій Mac', type: 'ollama' }]);
    fixture.detectChanges();

    fixture.componentInstance['credential'].setValue('c3');
    const request = http.expectOne((r) => r.url === '/api/v1/settings/assistant/models');
    expect(request.request.params.get('credentialId')).toBe('c3');
    request.flush([{ name: 'qwen3:14b' }, { name: 'qwen3:8b' }]);
    fixture.detectChanges();

    const options = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('datalist option')).map((o) => o.getAttribute('value'));
    expect(options).toEqual(['qwen3:14b', 'qwen3:8b']);
    http.verify();
  });
});
