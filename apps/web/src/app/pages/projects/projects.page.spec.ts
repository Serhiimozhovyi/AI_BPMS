import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthService, UserInfo } from '../../auth/auth.service';
import { ProjectsPage } from './projects.page';

const base = '/api/v1/workflow-groups';

describe('ProjectsPage', () => {
  async function setup(role: UserInfo['role']) {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])] });
    TestBed.inject(AuthService).user.set({ id: 'u1', email: 'u@openbp.test', role });
    const fixture = TestBed.createComponent(ProjectsPage);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne({ method: 'GET', url: base }).flush([
      { id: 'p2', name: 'Продажі', sortOrder: 1, workflowCount: 0, role: 'viewer', description: '', icon: '' },
      { id: 'p1', name: 'Психологи', sortOrder: 0, workflowCount: 4, role: 'editor', description: 'Бот підтримки', icon: 'psychology' },
    ]);
    await render(fixture);
    return { fixture, http, host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => TestBed.inject(HttpTestingController).verify());

  it('показує картки проєктів з описом, кількістю сценаріїв і роллю', async () => {
    const { host } = await setup('editor');
    const cards = [...host.querySelectorAll('[data-testid="project-card"]')];
    expect(cards.map((c) => c.getAttribute('data-project'))).toEqual(['p1', 'p2']);
    expect(cards[0].textContent).toContain('Бот підтримки');
    expect(cards[0].textContent).toContain('psychology');
    expect(cards[0].textContent).toContain('4');
    expect(cards[0].getAttribute('href')).toBe('/projects/p1');
    expect(cards[1].textContent).toContain('Viewer');
  });

  it('створює проєкт з картки й відкриває його', async () => {
    const { fixture, http, host } = await setup('editor');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    host.querySelector<HTMLButtonElement>('[data-testid="project-new"]')!.click();
    await render(fixture);
    const name = host.querySelector<HTMLInputElement>('[data-testid="project-create-name"]')!;
    name.value = 'HR';
    name.dispatchEvent(new Event('input'));
    host.querySelector<HTMLFormElement>('[data-testid="project-create-form"]')!.dispatchEvent(new Event('submit'));
    const post = http.expectOne({ method: 'POST', url: base });
    expect(post.request.body).toEqual({ name: 'HR' });
    post.flush({ id: 'p3', name: 'HR', sortOrder: 2, workflowCount: 0 });
    http.expectOne({ method: 'GET', url: base }).flush([]);
    await render(fixture);
    expect(navigate).toHaveBeenCalledWith(['/projects', 'p3']);
  });

  it('viewer не бачить «Створити проєкт»', async () => {
    const { host } = await setup('viewer');
    expect(host.querySelector('[data-testid="project-new"]')).toBeNull();
  });
});
