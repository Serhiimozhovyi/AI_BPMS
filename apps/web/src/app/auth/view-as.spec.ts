import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { NotifyService } from '../shared/notify.service';
import { WorkflowGroup } from '../workflows/workflows-api.service';
import { viewAsModeFor, viewAsOptions } from './view-as-dialog.component';
import { viewAsInterceptor } from './view-as.interceptor';
import { VIEW_AS_HEADER, VIEW_AS_KEY, ViewAsService, viewAsHeader } from './view-as.service';

const project: WorkflowGroup = {
  id: 'p1',
  name: 'Психологічна підтримка',
  sortOrder: 0,
  workflowCount: 2,
  role: 'editor',
  appRoles: [
    { key: 'coordinator', name: 'Координатор' },
    { key: 'psychologist', name: 'Психолог' },
  ],
};

describe('ViewAsService (F12-02)', () => {
  afterEach(() => sessionStorage.removeItem(VIEW_AS_KEY));

  it('заголовок: роль і роль застосунку або конкретний учасник', () => {
    expect(viewAsHeader({ projectId: 'p1', projectName: 'P', role: 'viewer', appRole: 'coordinator', label: 'К' })).toBe(
      'project=p1&role=viewer&appRole=coordinator',
    );
    expect(viewAsHeader({ projectId: 'p1', projectName: 'P', role: 'editor', label: 'Р' })).toBe('project=p1&role=editor');
    expect(viewAsHeader({ projectId: 'p1', projectName: 'P', memberId: 'u7', label: 'u7@firma.ua' })).toBe('project=p1&member=u7');
  });

  it('режим живе в sessionStorage вкладки й знімається clear()', () => {
    const service = TestBed.inject(ViewAsService);
    expect(service.active()).toBe(false);
    service.set({ projectId: 'p1', projectName: 'P', role: 'viewer', appRole: 'psychologist', label: 'Психолог' });
    expect(service.header()).toBe('project=p1&role=viewer&appRole=psychologist');
    expect(JSON.parse(sessionStorage.getItem(VIEW_AS_KEY)!).label).toBe('Психолог');
    service.clear();
    expect(service.active()).toBe(false);
    expect(sessionStorage.getItem(VIEW_AS_KEY)).toBeNull();
  });
});

describe('viewAsInterceptor (F12-02)', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([viewAsInterceptor])), provideHttpClientTesting()],
    });
    const notify = TestBed.inject(NotifyService);
    const error = vi.spyOn(notify, 'error').mockImplementation(() => undefined);
    return { http: TestBed.inject(HttpClient), controller: TestBed.inject(HttpTestingController), viewAs: TestBed.inject(ViewAsService), error };
  }

  afterEach(() => sessionStorage.removeItem(VIEW_AS_KEY));

  it('поза режимом заголовка немає; у режимі — лише до запитів Api', async () => {
    const { http, controller, viewAs } = setup();
    void firstValueFrom(http.get('/api/v1/workflows'));
    expect(controller.expectOne('/api/v1/workflows').request.headers.has(VIEW_AS_HEADER)).toBe(false);

    viewAs.set({ projectId: 'p1', projectName: 'P', role: 'viewer', appRole: 'coordinator', label: 'К' });
    void firstValueFrom(http.get('/api/v1/workflow-groups'));
    expect(controller.expectOne('/api/v1/workflow-groups').request.headers.get(VIEW_AS_HEADER)).toBe('project=p1&role=viewer&appRole=coordinator');
    void firstValueFrom(http.get('/assets/i18n.json'));
    expect(controller.expectOne('/assets/i18n.json').request.headers.has(VIEW_AS_HEADER)).toBe(false);
  });

  it('403 VIEW_AS_READ_ONLY — пояснення в снекбарі, режим лишається', async () => {
    const { http, controller, viewAs, error } = setup();
    viewAs.set({ projectId: 'p1', projectName: 'P', role: 'editor', label: 'Р' });
    const request = firstValueFrom(http.post('/api/v1/workflows', {}));
    controller.expectOne('/api/v1/workflows').flush({ code: 'VIEW_AS_READ_ONLY' }, { status: 403, statusText: 'Forbidden' });
    await expect(request).rejects.toMatchObject({ status: 403 });
    expect(error).toHaveBeenCalledWith(expect.stringContaining('У режимі перегляду дані не змінюються'));
    expect(viewAs.active()).toBe(true);
  });

  it('права на перегляд зникли — вихід із режиму', async () => {
    const { http, controller, viewAs, error } = setup();
    viewAs.set({ projectId: 'p1', projectName: 'P', role: 'editor', label: 'Р' });
    const request = firstValueFrom(http.get('/api/v1/auth/me'));
    controller.expectOne('/api/v1/auth/me').flush({ code: 'VIEW_AS_FORBIDDEN' }, { status: 403, statusText: 'Forbidden' });
    await expect(request).rejects.toMatchObject({ status: 403 });
    expect(viewAs.active()).toBe(false);
    expect(error).toHaveBeenCalled();
  });
});

describe('Варіанти «Переглянути як» (F12-02)', () => {
  it('editor проєкту: редактор, учасник без ролі й кожна роль застосунку; admin — ще конкретний учасник', () => {
    expect(viewAsOptions(project, false).map((o) => o.value)).toEqual(['editor', 'viewer', 'app:coordinator', 'app:psychologist']);
    expect(viewAsOptions(project, true).map((o) => o.label)).toEqual([
      'Редактор проєкту',
      'Учасник без ролі застосунку',
      'Координатор',
      'Психолог',
      'Конкретний учасник…',
    ]);
  });

  it('вибір перетворюється на режим із назвою для смуги', () => {
    expect(viewAsModeFor(project, 'app:psychologist', null)).toEqual({
      projectId: 'p1',
      projectName: 'Психологічна підтримка',
      role: 'viewer',
      appRole: 'psychologist',
      label: 'Психолог',
    });
    expect(viewAsModeFor(project, 'editor', null)?.role).toBe('editor');
    expect(viewAsModeFor(project, 'app:missing', null)).toBeNull();
    expect(viewAsModeFor(project, 'member', null)).toBeNull();
    expect(viewAsModeFor(project, 'member', { userId: 'u7', email: 'psy@firma.ua', role: 'viewer', appRole: 'psychologist' })).toEqual({
      projectId: 'p1',
      projectName: 'Психологічна підтримка',
      memberId: 'u7',
      label: 'psy@firma.ua',
    });
  });
});
