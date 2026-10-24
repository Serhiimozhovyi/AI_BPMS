import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { WorkflowValidationError } from '../workflows/workflows-api.service';
import { WorkflowSettingsDialogComponent } from './workflow-settings-dialog.component';
import { WorkflowSettings, applySettings, limitErrors, settingsChanged, settingsFormValue, timeoutError, timezones } from './workflow-settings';

const settings: WorkflowSettings = { timezone: 'Europe/Kyiv', executionTimeoutSec: 300, errorWorkflowId: null, saveSuccessfulData: false, extra: 1 };

describe('workflow-settings', () => {
  it('часові пояси: UTC першим, поточне значення завжди в списку', () => {
    expect(timezones('Europe/Kyiv', ['Europe/Kyiv', 'UTC', 'America/New_York'])).toEqual(['UTC', 'Europe/Kyiv', 'America/New_York']);
    expect(timezones('Mars/Olympus', ['Europe/Kyiv'])).toEqual(['UTC', 'Mars/Olympus', 'Europe/Kyiv']);
    expect(timezones('UTC').length).toBeGreaterThan(100);
  });

  it('таймаут: порожньо — 0, ціле ≥ 0 — число, інше — помилка; невідомі поля settings зберігаються', () => {
    expect(settingsFormValue({ ...settings, executionTimeoutSec: 0 }).executionTimeoutSec).toBe('');
    expect(timeoutError('')).toBeNull();
    expect(timeoutError(' 600 ')).toBeNull();
    expect(timeoutError('1.5')).toBe('integer');
    expect(timeoutError('-1')).toBe('integer');
    const next = applySettings(settings, { ...settingsFormValue(settings), executionTimeoutSec: '' });
    expect(next).toEqual({ ...settings, executionTimeoutSec: 0 });
    expect(settingsChanged(settings, next)).toBe(true);
    expect(settingsChanged(settings, { ...settings })).toBe(false);
  });

  it('ліміти: порожні поля прибирають ліміт, значення — в межах схеми, невідомі поля ліміту зберігаються', () => {
    const limited: WorkflowSettings = {
      ...settings,
      concurrency: { max: 2, onLimit: 'drop', note: 'x' },
      throttle: { limit: 5, periodSec: 10, onLimit: 'queue' },
      disableAfterErrors: 3,
    };
    const form = settingsFormValue(limited);
    expect(form).toMatchObject({ concurrencyMax: '2', concurrencyOnLimit: 'drop', throttleLimit: '5', throttlePeriodSec: '10', disableAfterErrors: '3' });
    expect(applySettings(limited, form)).toEqual(limited);
    expect(settingsChanged(limited, applySettings(limited, form))).toBe(false);

    const cleared = applySettings(limited, { ...form, concurrencyMax: '', throttleLimit: ' ', disableAfterErrors: '0' });
    expect(cleared).toEqual(settings);
    expect(settingsChanged(limited, cleared)).toBe(true);

    const added = applySettings(settings, { ...settingsFormValue(settings), throttleLimit: '2', throttleOnLimit: 'drop' });
    expect(added.throttle).toEqual({ limit: 2, periodSec: 60, onLimit: 'drop' });

    expect(limitErrors({ ...form, concurrencyMax: '0', throttlePeriodSec: '90000', disableAfterErrors: '1.5' })).toEqual({
      concurrencyMax: [1, 1000],
      throttlePeriodSec: [1, 86400],
      disableAfterErrors: [0, 1000],
    });
    expect(limitErrors({ ...form, throttleLimit: '', throttlePeriodSec: 'abc' })).toEqual({});
  });

  it('кореляція: перемикач додає correlation.skipIfActive, вимкнений прибирає, невідомі поля зберігаються', () => {
    expect(settingsFormValue(settings).skipIfActive).toBe(false);
    const on = applySettings(settings, { ...settingsFormValue(settings), skipIfActive: true });
    expect(on.correlation).toEqual({ skipIfActive: true });
    expect(settingsChanged(settings, on)).toBe(true);
    expect(settingsFormValue(on).skipIfActive).toBe(true);
    expect(applySettings(on, { ...settingsFormValue(on), skipIfActive: false })).toEqual(settings);

    const extra: WorkflowSettings = { ...settings, correlation: { skipIfActive: true, note: 'x' } };
    expect(applySettings(extra, { ...settingsFormValue(extra), skipIfActive: false }).correlation).toEqual({ note: 'x', skipIfActive: false });
  });
});

describe('WorkflowSettingsDialogComponent', () => {
  async function setup(errors: WorkflowValidationError[] = [], current: WorkflowSettings = settings) {
    const close = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { workflowId: 'w1', settings: current, errors } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(WorkflowSettingsDialogComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/workflows').flush({
      items: [
        { id: 'w1', name: 'Цей', active: false, version: 1, updatedAt: '' },
        { id: 'w2', name: 'Помилки', active: true, version: 3, updatedAt: '' },
      ],
      page: 1,
      pageSize: 200,
      total: 2,
    });
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, close, host: fixture.nativeElement as HTMLElement };
  }

  async function choose(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }, host: HTMLElement, testId: string, option: string) {
    host.querySelector<HTMLElement>(`[data-testid="${testId}"] .mat-mdc-select-trigger`)!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    const options = [...document.querySelectorAll<HTMLElement>('mat-option')];
    options.find((o) => o.textContent?.trim() === option)!.click();
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('сценарій помилок — серед інших сценаріїв, без себе; застосування повертає нові settings', async () => {
    const { fixture, close, host } = await setup();
    await choose(fixture, host, 'settings-error-workflow', 'Помилки');
    expect([...document.querySelectorAll('mat-option')].map((o) => o.textContent?.trim())).not.toContain('Цей');

    const timeout = host.querySelector<HTMLInputElement>('[data-testid="settings-timeout"]')!;
    expect(timeout.value).toBe('300');
    timeout.value = '900';
    timeout.dispatchEvent(new Event('input'));
    host.querySelector<HTMLElement>('[data-testid="settings-save-successful"] button')!.click();
    fixture.detectChanges();

    host.querySelector<HTMLButtonElement>('[data-testid="settings-apply"]')!.click();
    expect(close).toHaveBeenCalledWith({ ...settings, executionTimeoutSec: 900, errorWorkflowId: 'w2', saveSuccessfulData: true });
  });

  it('часовий пояс обирається зі списку IANA', async () => {
    const { fixture, close, host } = await setup();
    await choose(fixture, host, 'settings-timezone', 'UTC');
    host.querySelector<HTMLButtonElement>('[data-testid="settings-apply"]')!.click();
    expect(close).toHaveBeenCalledWith({ ...settings, timezone: 'UTC' });
  });

  it('нецілий таймаут — помилка поля і кнопка вимкнена; скасування нічого не повертає', async () => {
    const { fixture, close, host } = await setup();
    const timeout = host.querySelector<HTMLInputElement>('[data-testid="settings-timeout"]')!;
    timeout.value = 'година';
    timeout.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(host.querySelector<HTMLButtonElement>('[data-testid="settings-apply"]')!.disabled).toBe(true);

    host.querySelector<HTMLButtonElement>('[data-testid="settings-cancel"]')!.click();
    expect(close).toHaveBeenCalledWith(undefined);
  });

  it('розділ «Навантаження»: ліміти потрапляють у settings, значення поза межами вимикає кнопку', async () => {
    const { fixture, close, host } = await setup();
    const type = (testId: string, text: string) => {
      const input = host.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)!;
      input.value = text;
      input.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    };
    type('settings-concurrency-max', '1');
    await choose(fixture, host, 'settings-concurrency-on-limit', 'Відкинути');
    type('settings-throttle-limit', '2');
    type('settings-throttle-period', '10');
    type('settings-disable-after-errors', '5000');
    expect(host.querySelector('[data-testid="settings-disable-after-errors-error"]')?.textContent).toContain('від 0 до 1000');
    expect(host.querySelector<HTMLButtonElement>('[data-testid="settings-apply"]')!.disabled).toBe(true);

    type('settings-disable-after-errors', '3');
    host.querySelector<HTMLButtonElement>('[data-testid="settings-apply"]')!.click();
    expect(close).toHaveBeenCalledWith({
      ...settings,
      concurrency: { max: 1, onLimit: 'drop' },
      throttle: { limit: 2, periodSec: 10, onLimit: 'queue' },
      disableAfterErrors: 3,
    });
  });

  it('помилка межі з Api показується біля поля; видалений сценарій помилок — з позначкою', async () => {
    const error = { code: 'EXECUTION_TIMEOUT_TOO_LARGE', message: 'Таймаут 7200 с більший за дозволений сервером (3600 с).', path: '/settings/executionTimeoutSec' };
    const { fixture, host } = await setup([error], { ...settings, executionTimeoutSec: 7200, errorWorkflowId: 'gone' });
    fixture.detectChanges();
    expect(host.textContent).toContain('3600 с');
    host.querySelector<HTMLElement>('[data-testid="settings-error-workflow"] .mat-mdc-select-trigger')!.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect([...document.querySelectorAll('mat-option')].map((o) => o.textContent?.trim())).toContain('gone (не знайдено)');
  });
});
