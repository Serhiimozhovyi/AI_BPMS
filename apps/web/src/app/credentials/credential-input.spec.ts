import { buildData, initialValue, usageLabel } from './credential-input';
import { CredentialSummary, CredentialTypeInfo } from './credentials-api.service';

const http: CredentialTypeInfo = {
  type: 'mcp.http',
  displayName: 'Конектор HTTP: заголовки',
  description: '',
  fields: [{ name: 'headers', title: 'Заголовки', kind: 'secretMap', required: true }],
  schema: {},
};
const compat: CredentialTypeInfo = {
  type: 'openai-compatible',
  displayName: 'LLM',
  description: '',
  fields: [
    { name: 'baseUrl', title: 'Адреса API', kind: 'url', required: true },
    { name: 'apiKey', title: 'API-ключ', kind: 'secret', required: false },
  ],
  schema: {},
};
const generic: CredentialTypeInfo = { type: 'generic', displayName: 'Інше', description: '', fields: [], schema: {} };

function summary(type: string, fields: string[]): CredentialSummary {
  return { id: 'c1', name: 'x', type, fields, createdBy: null, usedBy: { connectors: [], workflows: [] }, error: null };
}

describe('credential-input', () => {
  it('створення: поля і пари в дані', () => {
    const v = initialValue(compat);
    v.fields['baseUrl'] = ' http://ollama:11434/v1 ';
    v.fields['apiKey'] = 'k';
    expect(buildData(compat, v)).toEqual({ data: { baseUrl: 'http://ollama:11434/v1', apiKey: 'k' }, errors: [] });

    const h = initialValue(http);
    h.maps['headers'] = [{ key: 'Authorization', value: 'Bearer t', existing: false, removed: false }];
    expect(buildData(http, h).data).toEqual({ headers: { Authorization: 'Bearer t' } });
  });

  it('створення: обов\'язкові поля, адреса, порожня мапа', () => {
    const v = initialValue(compat);
    v.fields['baseUrl'] = 'ftp://x';
    expect(buildData(compat, v).errors).toHaveLength(1);
    expect(buildData(compat, initialValue(compat)).errors[0]).toContain('Адреса API');
    expect(buildData(http, initialValue(http)).errors[0]).toContain('хоча б одна пара');
  });

  it('редагування: порожнє поле не змінюється, вилучена пара — null', () => {
    const existing = summary('mcp.http', ['headers.Authorization', 'headers.X-Tenant']);
    const v = initialValue(http, existing);
    expect(v.maps['headers'].map((p) => p.key)).toEqual(['Authorization', 'X-Tenant']);
    expect(buildData(http, v, existing)).toEqual({ data: null, errors: [] });

    v.maps['headers'][1].removed = true;
    v.maps['headers'].push({ key: 'X-New', value: 'n', existing: false, removed: false });
    expect(buildData(http, v, existing).data).toEqual({ headers: { 'X-Tenant': null, 'X-New': 'n' } });

    const c = summary('openai-compatible', ['baseUrl']);
    const cv = initialValue(compat, c);
    expect(buildData(compat, cv, c)).toEqual({ data: null, errors: [] });
  });

  it('generic: пари на верхньому рівні', () => {
    const v = initialValue(generic);
    v.maps[''] = [{ key: 'token', value: 's', existing: false, removed: false }];
    expect(buildData(generic, v).data).toEqual({ token: 's' });
    const existing = summary('generic', ['token']);
    expect(initialValue(generic, existing).maps[''][0]).toMatchObject({ key: 'token', existing: true });
  });

  it('дублікати й пари без назви — помилки', () => {
    const h = initialValue(http);
    h.maps['headers'] = [
      { key: 'A', value: '1', existing: false, removed: false },
      { key: 'A', value: '2', existing: false, removed: false },
      { key: '', value: '3', existing: false, removed: false },
    ];
    expect(buildData(http, h).errors).toHaveLength(2);
  });

  it('опис використання', () => {
    const c = summary('generic', []);
    expect(usageLabel(c)).toBe('—');
    c.usedBy = { connectors: [{ id: '1', name: 'CRM' }], workflows: [{ id: '2', name: 'Звіт' }] };
    expect(usageLabel(c)).toBe('конектор «CRM», сценарій «Звіт»');
  });
});
