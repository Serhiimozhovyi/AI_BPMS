import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { WorkflowExplanation } from './assistant-api.service';
import { EXPLANATION_POLL_MS, ExplanationCardComponent } from './explanation-card.component';

const URL = '/api/v1/workflows/w1/explanation';

function explanation(patch: Partial<WorkflowExplanation>): WorkflowExplanation {
  return {
    status: 'none',
    version: null,
    currentVersion: 2,
    text: null,
    error: null,
    updatedAt: null,
    stale: false,
    ...patch,
  };
}

describe('ExplanationCardComponent', () => {
  function create(version = 2) {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(ExplanationCardComponent);
    fixture.componentRef.setInput('workflowId', 'w1');
    fixture.componentRef.setInput('version', version);
    fixture.detectChanges();
    return {
      fixture,
      host: fixture.nativeElement as HTMLElement,
      http: TestBed.inject(HttpTestingController),
    };
  }

  const text = (host: HTMLElement, id: string) =>
    host.querySelector(`[data-testid="${id}"]`)?.textContent?.trim();

  afterEach(() => {
    vi.useRealTimers();
    TestBed.inject(HttpTestingController).verify();
  });

  it('показує готове пояснення і позначає застаріле', () => {
    const { fixture, host, http } = create();
    http.expectOne(URL).flush(
      explanation({
        status: 'ready',
        version: 1,
        text: 'Коротко: процес приймає заявки.',
        stale: true,
      }),
    );
    fixture.detectChanges();

    expect(text(host, 'explanation-text')).toBe('Коротко: процес приймає заявки.');
    expect(text(host, 'explanation-stale')).toContain('версії 1');
  });

  it('поки пояснення готується — перепитує, доки не буде готове; нова версія — перечитує', () => {
    vi.useFakeTimers();
    const { fixture, host, http } = create();
    http.expectOne(URL).flush(explanation({ status: 'pending' }));
    fixture.detectChanges();
    expect(host.querySelector('[data-testid="explanation-pending"]')).not.toBeNull();

    vi.advanceTimersByTime(EXPLANATION_POLL_MS);
    http.expectOne(URL).flush(explanation({ status: 'ready', version: 2, text: 'Готово' }));
    fixture.detectChanges();
    expect(text(host, 'explanation-text')).toBe('Готово');
    expect(host.querySelector('[data-testid="explanation-pending"]')).toBeNull();

    // Готове — більше не перепитує.
    vi.advanceTimersByTime(EXPLANATION_POLL_MS * 3);
    http.expectNone(URL);

    fixture.componentRef.setInput('version', 3);
    fixture.detectChanges();
    http.expectOne(URL).flush(
      explanation({
        status: 'pending',
        version: 2,
        currentVersion: 3,
        text: 'Готово',
        stale: true,
      }),
    );
    fixture.detectChanges();
    expect(text(host, 'explanation-text')).toBe('Готово');
    vi.advanceTimersByTime(EXPLANATION_POLL_MS);
    http
      .expectOne(URL)
      .flush(explanation({ status: 'ready', version: 3, currentVersion: 3, text: 'Нове' }));
    fixture.detectChanges();
    expect(text(host, 'explanation-text')).toBe('Нове');
  });

  it('«Пояснити заново» замовляє пояснення; не налаштовано — підказка і кнопка вимкнена', () => {
    const { fixture, host, http } = create();
    http.expectOne(URL).flush(explanation({ status: 'failed', error: 'boom' }));
    fixture.detectChanges();
    expect(text(host, 'explanation-status')).toContain('boom');

    (host.querySelector('[data-testid="explanation-refresh"]') as HTMLButtonElement).click();
    const request = http.expectOne((r) => r.method === 'POST' && r.url === URL);
    request.flush(explanation({ status: 'not_configured' }));
    http.expectOne(URL).flush(explanation({ status: 'not_configured' }));
    fixture.detectChanges();

    expect(text(host, 'explanation-status')).toContain('через MCP');
    expect(
      (host.querySelector('[data-testid="explanation-refresh"]') as HTMLButtonElement).disabled,
    ).toBe(true);
  });
});
