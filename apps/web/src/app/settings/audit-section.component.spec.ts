import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuditEntry, auditActionLabel, auditSummary } from './audit-api.service';
import { AuditSection } from './audit-section.component';

const saved: AuditEntry = {
  id: 'a1',
  at: '2026-10-24T10:00:00Z',
  userId: 'u1',
  userEmail: 'olena@openbp.test',
  apiKeyId: 'k1',
  apiKeyName: 'Claude Code',
  action: 'workflow.saved',
  entityType: 'workflow',
  entityId: 'w1',
  details: { name: 'Звіт', version: 3 },
};
const breaker: AuditEntry = {
  ...saved,
  id: 'a2',
  userId: null,
  userEmail: null,
  apiKeyId: null,
  apiKeyName: null,
  action: 'workflow.deactivated',
  details: { name: 'Звіт', reason: 'disableAfterErrors', errors: 5 },
};
const page = (items: AuditEntry[], total = items.length) => ({ items, page: 1, pageSize: 50, total });

describe('auditSummary і auditActionLabel', () => {
  it('описують запис без значень секретів', () => {
    expect(auditSummary(saved)).toBe('«Звіт» · версія 3');
    expect(auditSummary({ details: { name: 'Ключ', fields: ['apiKey'] } })).toBe('«Ключ» · поля: apiKey');
    expect(auditSummary(breaker)).toContain('5');
    expect(auditActionLabel('credential.replaced')).toBe('значення замінено');
    expect(auditActionLabel('custom')).toBe('custom');
  });
});

describe('AuditSection', () => {
  async function setup(items: AuditEntry[] = [saved, breaker], total = items.length) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(AuditSection);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne((r) => r.url === '/api/v1/audit').flush(page(items, total));
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує хто, що й подробиці; дія системи і ключ видно окремо', async () => {
    const { host } = await setup();
    const rows = host.querySelectorAll('[data-audit]');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('olena@openbp.test');
    expect(rows[0].querySelector('[data-testid="audit-key"]')?.textContent).toContain('Claude Code');
    expect(rows[0].textContent).toContain('Сценарій — збережено');
    expect(rows[0].textContent).toContain('версія 3');
    expect(rows[1].textContent).toContain('система');
  });

  it('порожній журнал — підказка', async () => {
    const { host } = await setup([]);
    expect(host.querySelector('[data-testid="audit-empty"]')).not.toBeNull();
  });

  it('фільтр за типом сутності перечитує першу сторінку', async () => {
    const { fixture, http, host } = await setup();
    const select = host.querySelector<HTMLSelectElement>('[data-testid="audit-entity"]')!;
    select.value = 'credential';
    select.dispatchEvent(new Event('change'));
    const req = http.expectOne((r) => r.url === '/api/v1/audit');
    expect(req.request.params.get('entityType')).toBe('credential');
    expect(req.request.params.get('page')).toBe('1');
    req.flush(page([]));
    await render(fixture);
    expect(host.querySelector('[data-testid="audit-empty"]')).not.toBeNull();
  });

  it('сторінки: «Старіші» просить наступну', async () => {
    const { http, host } = await setup([saved], 120);
    host.querySelector<HTMLButtonElement>('[data-testid="audit-next"]')!.click();
    const req = http.expectOne((r) => r.url === '/api/v1/audit');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.has('entityType')).toBe(false);
    req.flush({ items: [breaker], page: 2, pageSize: 50, total: 120 });
  });
});
