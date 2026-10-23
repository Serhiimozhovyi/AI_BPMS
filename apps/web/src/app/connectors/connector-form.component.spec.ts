import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ConnectorFormComponent } from './connector-form.component';
import { ConnectorDetail } from './connectors-api.service';

describe('ConnectorFormComponent', () => {
  function setup() {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(ConnectorFormComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const host = fixture.nativeElement as HTMLElement;
    return { fixture, http, host, form: fixture.componentInstance.form };
  }

  it('stdio: без команди форма невалідна і запит не надсилається', async () => {
    const { fixture, http, form } = setup();
    form.patchValue({ name: 'fs' });
    fixture.componentInstance.submit();
    await fixture.whenStable();
    expect(form.controls.command.hasError('required')).toBe(true);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Вкажіть команду');
    http.expectNone('/api/v1/connectors');
  });

  it('перемикання на http вимагає URL замість команди', () => {
    const { form } = setup();
    form.patchValue({ name: 'x', transport: 'http' });
    expect(form.controls.command.valid).toBe(true);
    expect(form.controls.url.hasError('required')).toBe(true);
    form.patchValue({ url: 'ftp://x' });
    expect(form.controls.url.hasError('url')).toBe(true);
    form.patchValue({ url: 'https://mcp.example/mcp' });
    expect(form.valid).toBe(true);
  });

  it('неправильні змінні середовища і credential ID — помилки валідації', () => {
    const { form } = setup();
    form.patchValue({ name: 'fs', command: 'npx', environment: 'NOEQUALS' });
    expect(form.controls.environment.hasError('format')).toBe(true);
    form.patchValue({ environment: 'A=1', credentialId: 'not-a-uuid' });
    expect(form.controls.credentialId.hasError('pattern')).toBe(true);
  });

  it('успіх: POST /connectors і подія created', () => {
    const { fixture, http, form } = setup();
    const created: ConnectorDetail[] = [];
    fixture.componentInstance.created.subscribe((d) => created.push(d));
    form.patchValue({ name: 'fs', command: 'npx', arguments: '-y\npkg' });
    fixture.componentInstance.submit();

    const req = http.expectOne({ method: 'POST', url: '/api/v1/connectors' });
    expect(req.request.body.config).toEqual({ command: 'npx', arguments: ['-y', 'pkg'], environment: null, workingDirectory: null });
    const detail: ConnectorDetail = {
      summary: { id: 'c1', name: 'fs', transport: 'stdio', status: 'ok', toolCount: 0, toolsCachedAt: null },
      config: { command: 'npx' },
      credentialId: null,
      tools: [],
    };
    req.flush(detail);
    expect(created).toEqual([detail]);
    expect(form.controls.name.value).toBe('');
  });

  it('помилка Api показується у формі', async () => {
    const { fixture, http, form, host } = setup();
    form.patchValue({ name: 'fs', command: 'npx' });
    fixture.componentInstance.submit();
    http
      .expectOne('/api/v1/connectors')
      .flush({ detail: 'Credential не знайдено' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(host.querySelector('[data-testid="form-error"]')?.textContent).toContain('Credential не знайдено');
    expect(fixture.componentInstance.saving()).toBe(false);
  });
});
