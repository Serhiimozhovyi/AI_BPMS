import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { ImportWorkflowDialog, isN8nWorkflow, parseExport } from './import-workflow-dialog.component';
import { N8nConversion, WorkflowExport, WorkflowImportResult, emptyWorkflowDefinition } from './workflows-api.service';

const exported: WorkflowExport = {
  definition: emptyWorkflowDefinition('Заявки'),
  manifest: {
    connectors: [{ ref: 'srv-src', name: '1С', transport: 'http' }],
    credentials: [{ ref: 'cred-src', name: 'Токен вебхука', type: 'webhook.header' }],
    variables: ['region'],
  },
};

function result(missing: string[] = []): WorkflowImportResult {
  return {
    workflow: { id: 'w-new', name: 'Заявки', active: false, version: 1, versionId: 'v1', definition: exported.definition, updatedAt: '', groupId: 'g1' },
    mapping: {},
    missingVariables: missing,
  };
}

describe('parseExport', () => {
  it('приймає файл експорту і саме визначення, відкидає інше', () => {
    expect(parseExport(JSON.stringify(exported))).toEqual({ definition: exported.definition, manifest: exported.manifest });
    expect(parseExport(JSON.stringify(exported.definition))).toEqual({ definition: exported.definition, manifest: null });
    expect(parseExport('{ "name": "без вузлів" }')).toBeNull();
    expect(parseExport('не json')).toBeNull();
  });

  it('розпізнає сценарій n8n за connections-об\'єктом або типами вузлів', () => {
    const n8n = { name: 'n8n', nodes: [{ name: 'A', type: 'n8n-nodes-base.manualTrigger' }], connections: {} };
    expect(parseExport(JSON.stringify(n8n))).toEqual({ definition: null, manifest: null, n8n });
    expect(isN8nWorkflow({ nodes: [{ type: '@n8n/n8n-nodes-langchain.ollama' }] })).toBe(true);
    expect(isN8nWorkflow(exported.definition)).toBe(false);
  });
});

describe('ImportWorkflowDialog', () => {
  async function setup() {
    const close = vi.fn();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: MAT_DIALOG_DATA, useValue: { groupId: 'g1' } },
        { provide: MatDialogRef, useValue: { close } },
      ],
    });
    const fixture = TestBed.createComponent(ImportWorkflowDialog);
    fixture.detectChanges();
    return { fixture, close, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement, dialog: fixture.componentInstance };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  function paste(host: HTMLElement, text: string) {
    const area = host.querySelector<HTMLTextAreaElement>('[data-testid="import-json"]')!;
    area.value = text;
    area.dispatchEvent(new Event('input'));
  }

  const submit = (host: HTMLElement) => host.querySelector<HTMLButtonElement>('[data-testid="import-submit"]')!;

  it('не файл експорту — помилка й кнопка вимкнена', async () => {
    const { fixture, host } = await setup();
    expect(submit(host).disabled).toBe(true);
    paste(host, '{ "foo": 1 }');
    await render(fixture);
    expect(host.querySelector('[data-testid="import-parse-error"]')).not.toBeNull();
    expect(submit(host).disabled).toBe(true);
  });

  it('сценарій n8n: перетворення, імпорт і список того, що доробити', async () => {
    const { fixture, close, http, host } = await setup();
    const n8n = { name: 'Агро', nodes: [{ name: 'RSS', type: 'n8n-nodes-base.rssFeedRead' }], connections: {} };
    paste(host, JSON.stringify(n8n));
    await render(fixture);
    expect(host.querySelector('[data-testid="import-n8n"]')).not.toBeNull();
    submit(host).click();

    const conversion: N8nConversion = {
      definition: exported.definition,
      manifest: { connectors: [{ ref: 'n8n:telegramApi:t1', name: 'radarbot', transport: '' }], credentials: [], variables: [] },
      notes: [
        { level: 'replace', node: 'RSS', message: 'Читання RSS у OpenBP немає' },
        { level: 'check', node: null, message: 'Створіть таблицю даних «agro_history»' },
      ],
    };
    const convert = http.expectOne({ method: 'POST', url: '/api/v1/workflows/convert/n8n' });
    expect(convert.request.body).toEqual(n8n);
    convert.flush(conversion);
    const req = http.expectOne({ method: 'POST', url: '/api/v1/workflows/import' });
    expect(req.request.body).toEqual({
      definition: conversion.definition,
      manifest: conversion.manifest,
      mapping: { connectors: {}, credentials: {}, workflows: {} },
      groupId: 'g1',
    });
    req.flush(result());
    await render(fixture);

    expect(close).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="import-missing-vars"]')).toBeNull();
    const items = [...host.querySelectorAll('[data-testid="import-notes"] li')];
    expect(items.map((li) => li.getAttribute('data-level'))).toEqual(['replace', 'check']);
    expect(items[0].textContent).toContain('RSS');
  });

  it('усе знайдено автоматично — закривається результатом', async () => {
    const { fixture, close, http, host } = await setup();
    paste(host, JSON.stringify(exported));
    await render(fixture);
    submit(host).click();
    const req = http.expectOne({ method: 'POST', url: '/api/v1/workflows/import' });
    expect(req.request.body).toEqual({ definition: exported.definition, manifest: exported.manifest, mapping: { connectors: {}, credentials: {}, workflows: {} }, groupId: 'g1' });
    req.flush(result());
    expect(close).toHaveBeenCalledWith(result());
  });

  it('незмаплене: вибір відповідників за типом і повторний імпорт з mapping', async () => {
    const { fixture, close, http, host, dialog } = await setup();
    paste(host, JSON.stringify(exported));
    await render(fixture);
    submit(host).click();
    http.expectOne('/api/v1/workflows/import').flush(
      {
        code: 'IMPORT_UNMAPPED',
        unmapped: [
          { kind: 'connector', ref: 'srv-src', name: '1С', type: 'http', reason: 'ambiguous' },
          { kind: 'credential', ref: 'cred-src', name: 'Токен вебхука', type: 'webhook.header', reason: 'not_found' },
        ],
      },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    http.expectOne((r) => r.url === '/api/v1/connectors').flush({
      items: [
        { id: 'srv-a', name: '1С', transport: 'http', status: 'ok', toolCount: 1, toolsCachedAt: null },
        { id: 'srv-b', name: '1С (stdio)', transport: 'stdio', status: 'ok', toolCount: 1, toolsCachedAt: null },
      ],
      page: 1,
      pageSize: 200,
      total: 2,
    });
    http.expectOne((r) => r.url === '/api/v1/credentials/options').flush([
      { id: 'cred-a', name: 'Токен', type: 'webhook.header' },
      { id: 'cred-b', name: 'Basic', type: 'webhook.basic' },
    ]);
    await render(fixture);

    expect(host.querySelector('[data-testid="import-mapping"]')).not.toBeNull();
    const [connector, credential] = dialog.unmapped();
    expect(dialog.candidatesFor(connector).map((c) => c.id)).toEqual(['srv-a']);
    expect(dialog.candidatesFor(credential).map((c) => c.id)).toEqual(['cred-a']);
    expect(submit(host).disabled).toBe(true);

    dialog.choose(connector, 'srv-a');
    dialog.choose(credential, 'cred-a');
    await render(fixture);
    expect(submit(host).disabled).toBe(false);
    submit(host).click();
    const req = http.expectOne({ method: 'POST', url: '/api/v1/workflows/import' });
    expect(req.request.body.mapping).toEqual({ connectors: { 'srv-src': 'srv-a' }, credentials: { 'cred-src': 'cred-a' }, workflows: {} });

    // Відсутні змінні — діалог лишається з попередженням до «Готово».
    req.flush(result(['region']));
    await render(fixture);
    expect(close).not.toHaveBeenCalled();
    expect(host.querySelector('[data-testid="import-missing-vars"]')?.textContent).toContain('region');
    host.querySelector<HTMLButtonElement>('[data-testid="import-close"]')!.click();
    expect(close).toHaveBeenCalledWith(result(['region']));
  });

  it('немає кандидатів — підказка; помилка валідації показується текстом', async () => {
    const { fixture, http, host } = await setup();
    paste(host, JSON.stringify(exported));
    await render(fixture);
    submit(host).click();
    http.expectOne('/api/v1/workflows/import').flush(
      { code: 'IMPORT_UNMAPPED', unmapped: [{ kind: 'credential', ref: 'cred-src', name: 'Токен вебхука', type: 'webhook.header', reason: 'not_found' }] },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    http.expectOne((r) => r.url === '/api/v1/credentials/options').flush([]);
    await render(fixture);
    expect(host.querySelector('[data-testid="import-no-candidates"]')).not.toBeNull();

    paste(host, JSON.stringify(exported.definition));
    await render(fixture);
    expect(host.querySelector('[data-testid="import-mapping"]')).toBeNull();
    submit(host).click();
    http.expectOne('/api/v1/workflows/import').flush(
      { code: 'WORKFLOW_INVALID', errors: [{ code: 'MCP_TOOL_UNKNOWN', message: 'Tool не знайдено.' }] },
      { status: 422, statusText: 'Unprocessable Entity' },
    );
    await render(fixture);
    expect(host.querySelector('[data-testid="import-error"]')?.textContent).toContain('Tool не знайдено.');
  });
});
