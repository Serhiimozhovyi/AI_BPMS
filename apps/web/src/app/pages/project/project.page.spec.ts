import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ProjectMember, WorkflowGroup } from '../../workflows/workflows-api.service';
import { ProjectPage } from './project.page';

const base = '/api/v1/workflow-groups';
const alice: ProjectMember = { userId: 'u1', email: 'alice@firma.ua', role: 'editor', globalRole: 'editor' };
const bob: ProjectMember = { userId: 'u2', email: 'bob@firma.ua', role: 'viewer', globalRole: 'viewer' };

describe('ProjectPage', () => {
  async function setup(role: WorkflowGroup['role'] = 'editor', members: ProjectMember[] = [alice, bob]) {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ id: 'p1' }) } } },
      ],
    });
    const fixture = TestBed.createComponent(ProjectPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: base }).flush([{ id: 'p1', name: 'Продажі', sortOrder: 0, workflowCount: 3, role }]);
    http.expectOne({ method: 'GET', url: `${base}/p1/members` }).flush(members);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('показує проєкт, мою роль і учасників', async () => {
    const { host } = await setup();
    expect(host.querySelector('[data-testid="project-name"]')?.textContent).toContain('Продажі');
    expect(host.querySelector('[data-testid="project-role"]')?.textContent).toContain('Editor');
    expect([...host.querySelectorAll('[data-member]')].map((r) => r.getAttribute('data-member'))).toEqual(['alice@firma.ua', 'bob@firma.ua']);
  });

  it('додає учасника за email з вибраною роллю', async () => {
    const { fixture, http, host } = await setup('admin', [alice]);
    const email = host.querySelector<HTMLInputElement>('[data-testid="member-email"]')!;
    email.value = 'bob@firma.ua';
    email.dispatchEvent(new Event('input'));
    const role = host.querySelector<HTMLSelectElement>('[data-testid="member-role"]')!;
    role.value = 'viewer';
    role.dispatchEvent(new Event('change'));
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-testid="member-add"]')!.click();
    const put = http.expectOne({ method: 'PUT', url: `${base}/p1/members` });
    expect(put.request.body).toEqual({ email: 'bob@firma.ua', role: 'viewer' });
    put.flush(bob);
    http.expectOne({ method: 'GET', url: base }).flush([]);
    await render(fixture);

    expect(host.querySelectorAll('[data-member]')).toHaveLength(2);
    expect(email.value).toBe('');
  });

  it('змінює роль і прибирає учасника', async () => {
    const { fixture, http, host } = await setup();
    const select = host.querySelector<HTMLSelectElement>('[data-member="bob@firma.ua"] [data-testid="member-role-change"]')!;
    select.value = 'editor';
    select.dispatchEvent(new Event('change'));
    const put = http.expectOne({ method: 'PUT', url: `${base}/p1/members` });
    expect(put.request.body).toEqual({ email: 'bob@firma.ua', role: 'editor' });
    put.flush({ ...bob, role: 'editor' });
    http.expectOne({ method: 'GET', url: base }).flush([]);
    await render(fixture);

    host.querySelector<HTMLButtonElement>('[data-member="alice@firma.ua"] [data-testid="member-remove"]')!.click();
    http.expectOne({ method: 'DELETE', url: `${base}/p1/members/u1` }).flush(null, { status: 204, statusText: 'No Content' });
    await render(fixture);
    expect([...host.querySelectorAll('[data-member]')].map((r) => r.getAttribute('data-member'))).toEqual(['bob@firma.ua']);
  });

  it('viewer бачить учасників без форми й кнопок', async () => {
    const { host } = await setup('viewer');
    expect(host.querySelector('[data-testid="member-form"]')).toBeNull();
    expect(host.querySelector('[data-testid="member-remove"]')).toBeNull();
    expect(host.querySelector('[data-member="bob@firma.ua"]')?.textContent).toContain('Viewer');
  });

  it('помилка Api показується', async () => {
    const { fixture, http, host } = await setup();
    host.querySelector<HTMLButtonElement>('[data-member="alice@firma.ua"] [data-testid="member-remove"]')!.click();
    http
      .expectOne({ method: 'DELETE', url: `${base}/p1/members/u1` })
      .flush({ code: 'FORBIDDEN', detail: 'Потрібна роль editor.' }, { status: 403, statusText: 'Forbidden' });
    await render(fixture);
    expect(host.querySelector('[data-testid="project-error"]')).not.toBeNull();
  });
});
