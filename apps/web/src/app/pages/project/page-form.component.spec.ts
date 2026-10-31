import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { ProjectPageView } from '../../projects/project-pages-api.service';
import { PageFormComponent } from './page-form.component';
import { ViewAsService } from '../../auth/view-as.service';

const view: ProjectPageView = {
  page: {
    id: 'pg',
    key: 'slot-form',
    name: 'Слот',
    kind: 'form',
    definition: { table: 'slots', onSubmit: { operation: 'upsert' } },
    roles: [],
    sortOrder: 0,
    updatedAt: '2026-11-04T00:00:00Z',
  },
  tableId: 't1',
  columns: [],
  schema: { type: 'object', properties: { starts_at: { type: 'string', format: 'date-time', title: 'Початок' }, note: { type: 'string', title: 'Нотатка' } } },
  canEdit: true,
  timezone: 'Europe/Kyiv',
};

describe('PageFormComponent: дата й час у поясі застосунку (F8-17)', () => {
  async function setup(rowKey: string | null, prefill: Record<string, unknown> = {}) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    const fixture = TestBed.createComponent(PageFormComponent);
    fixture.componentRef.setInput('projectId', 'p1');
    fixture.componentRef.setInput('view', view);
    fixture.componentRef.setInput('rowKey', rowKey);
    fixture.componentRef.setInput('prefill', prefill);
    const render = async () => {
      fixture.detectChanges();
      await fixture.whenStable();
      fixture.detectChanges();
    };
    await render();
    return { fixture, render, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  it('показує збережений момент часу годинником пояса і зберігає його назад як ISO', async () => {
    const { render, http, host } = await setup('s1');
    http.expectOne('/api/v1/projects/p1/pages/slot-form/rows/s1').flush({
      row: { key: 's1', data: { starts_at: '2026-11-04T08:00:00Z', note: 'Консультація' }, display: {}, actions: [] },
      related: [],
    });
    await render();
    const input = host.querySelector<HTMLInputElement>('input[type="datetime-local"]')!;
    expect(input.value).toBe('2026-11-04T10:00');

    host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.click();
    await render();
    const submit = http.expectOne('/api/v1/projects/p1/pages/slot-form/submit');
    expect(submit.request.body.values.starts_at).toBe('2026-11-04T08:00:00.000Z');
    expect(submit.request.body.rowKey).toBe('s1');
    submit.flush({ row: null, then: null });
  });

  it('час із підстановки (клітинка календаря) трактується в поясі застосунку', async () => {
    const { render, http, host } = await setup(null, { starts_at: '2026-07-01T10:00' });
    const input = host.querySelector<HTMLInputElement>('input[type="datetime-local"]')!;
    expect(input.value).toBe('2026-07-01T10:00');
    host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!.click();
    await render();
    const submit = http.expectOne('/api/v1/projects/p1/pages/slot-form/submit');
    expect(submit.request.body.values.starts_at).toBe('2026-07-01T07:00:00.000Z');
    submit.flush({ row: null, then: null });
  });

  it('у перегляді «як користувач» (F12-02) кнопка «Зберегти» неактивна з поясненням', async () => {
    const { fixture, render, http, host } = await setup(null);
    http.match(() => true).forEach((r) => r.flush({}));
    TestBed.inject(ViewAsService).mode.set({ projectId: 'p1', projectName: 'P', role: 'viewer', appRole: 'psychologist', label: 'Психолог' });
    fixture.detectChanges();
    await render();
    const submit = host.querySelector<HTMLButtonElement>('[data-testid="app-submit"]')!;
    expect(submit.disabled).toBe(true);
    expect((submit.parentElement as HTMLElement).title).toContain('У режимі перегляду');
  });
});
