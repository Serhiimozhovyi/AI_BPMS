import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { FormDefinition } from '../../forms/forms-api.service';
import { FormPage } from './form.page';

describe('FormPage (F6-03)', () => {
  const leave: FormDefinition = {
    kind: 'trigger',
    title: 'Заява на відпустку',
    description: 'Заповніть за тиждень до відпустки.',
    submitLabel: 'Надіслати заяву',
    schema: {
      type: 'object',
      required: ['name', 'days'],
      properties: {
        name: { type: 'string', title: 'ПІБ' },
        days: { type: 'integer', title: 'Днів', minimum: 1 },
        scan: { type: 'string', format: 'binary', title: 'Скан заяви' },
      },
    },
  };

  async function setup(segments = ['form', 'hr', 'leave'], query: Record<string, string> = {}) {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { url: segments.map((path) => ({ path })), queryParamMap: convertToParamMap(query) } },
        },
      ],
    });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    const fixture = TestBed.createComponent(FormPage);
    fixture.detectChanges();
    return { fixture, navigate, http: TestBed.inject(HttpTestingController), host: fixture.nativeElement as HTMLElement };
  }

  async function render(fixture: { detectChanges(): void; whenStable(): Promise<unknown> }) {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function type(host: HTMLElement, key: string, value: string) {
    const input = host.querySelector<HTMLInputElement>(`[data-testid="form-field-${key}"]`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function submit(host: HTMLElement) {
    host.querySelector<HTMLButtonElement>('[data-testid="form-submit"]')!.click();
  }

  it('рендерить форму за схемою: заголовок, опис, поля, кнопка', async () => {
    const { fixture, http, host } = await setup();
    http.expectOne('/api/v1/forms/hr/leave').flush(leave);
    await render(fixture);

    expect(host.querySelector('[data-testid="form-title"]')?.textContent).toContain('Заява на відпустку');
    expect(host.querySelector('[data-testid="form-description"]')?.textContent).toContain('за тиждень');
    expect(host.querySelector('[data-testid="form-field-name"]')?.getAttribute('type')).toBe('text');
    expect(host.querySelector('[data-testid="form-field-days"]')?.getAttribute('type')).toBe('number');
    expect(host.querySelector('[data-testid="form-field-scan"]')?.getAttribute('type')).toBe('file');
    expect(host.textContent).toContain('ПІБ');
    expect(host.textContent).toContain('Скан заяви');
    expect(host.querySelector('[data-testid="form-submit"]')?.textContent).toContain('Надіслати заяву');
  });

  it('незаповнені обов’язкові поля не надсилаються', async () => {
    const { fixture, http, host } = await setup();
    http.expectOne('/api/v1/forms/hr/leave').flush(leave);
    await render(fixture);

    submit(host);
    await render(fixture);

    http.expectNone({ method: 'POST' });
    expect(host.querySelector('[data-testid="form-submit-error"]')?.textContent).toContain('Перевірте');
    expect(host.textContent).toContain('Заповніть це поле.');
  });

  it('надсилає поля JSON-ом і показує повідомлення сценарію', async () => {
    const { fixture, http, host } = await setup();
    http.expectOne('/api/v1/forms/hr/leave').flush(leave);
    await render(fixture);
    type(host, 'name', 'Олена');
    type(host, 'days', '5');

    submit(host);
    const post = http.expectOne({ method: 'POST', url: '/api/v1/forms/hr/leave' });
    expect(post.request.body).toEqual({ fields: { name: 'Олена', days: 5 } });
    post.flush({ executionId: 'e1', message: 'Дякуємо, заяву прийнято' }, { status: 202, statusText: 'Accepted' });
    await render(fixture);

    expect(host.querySelector('[data-testid="form-done"]')?.textContent).toContain('Дякуємо, заяву прийнято');
  });

  it('з файлом — multipart: fields і файл за назвою поля; токен посилання — у заголовку', async () => {
    const { fixture, http, host } = await setup(['form', 'hr', 'leave'], { token: 'link-1' });
    const get = http.expectOne('/api/v1/forms/hr/leave');
    expect(get.request.headers.get('X-Form-Token')).toBe('link-1');
    get.flush(leave);
    await render(fixture);
    type(host, 'name', 'Олена');
    type(host, 'days', '3');
    const file = new File(['%PDF'], 'заява.pdf', { type: 'application/pdf' });
    const input = host.querySelector<HTMLInputElement>('[data-testid="form-field-scan"]')!;
    Object.defineProperty(input, 'files', { value: [file] });
    input.dispatchEvent(new Event('change'));
    await render(fixture);
    expect(host.querySelector('[data-testid="form-file-name-scan"]')?.textContent).toContain('заява.pdf');

    submit(host);
    const post = http.expectOne({ method: 'POST', url: '/api/v1/forms/hr/leave' });
    const body = post.request.body as FormData;
    expect(post.request.headers.get('X-Form-Token')).toBe('link-1');
    expect(JSON.parse(body.get('fields') as string)).toEqual({ name: 'Олена', days: 3 });
    expect((body.get('scan') as File).name).toBe('заява.pdf');
    post.flush({ executionId: 'e1' }, { status: 202, statusText: 'Accepted' });
    await render(fixture);
    expect(host.querySelector('[data-testid="form-done"]')?.textContent).toContain('Форму надіслано');
  });

  it('помилки полів з Api — під полями', async () => {
    const { fixture, http, host } = await setup();
    http.expectOne('/api/v1/forms/hr/leave').flush(leave);
    await render(fixture);
    type(host, 'name', 'Олена');
    type(host, 'days', '40');

    submit(host);
    http.expectOne({ method: 'POST', url: '/api/v1/forms/hr/leave' }).flush(
      { code: 'FORM_INVALID', errors: [{ field: 'days', message: 'Значення має бути не більше 28.' }] },
      { status: 400, statusText: 'Bad Request' },
    );
    await render(fixture);

    expect(host.textContent).toContain('Значення має бути не більше 28.');
    expect(host.querySelector('[data-testid="form"]')).not.toBeNull();
  });

  it('форма для користувачів без сесії — на вхід з поверненням на форму', async () => {
    const { fixture, http, navigate } = await setup();
    http.expectOne('/api/v1/forms/hr/leave').flush(
      { code: 'FORM_LOGIN_REQUIRED', loginUrl: '/login?returnUrl=%2Fform%2Fhr%2Fleave' },
      { status: 401, statusText: 'Unauthorized' },
    );
    await render(fixture);

    expect(navigate).toHaveBeenCalledWith('/login?returnUrl=%2Fform%2Fhr%2Fleave');
  });

  it.each([
    [404, { code: 'NOT_FOUND' }, 'Форму не знайдено'],
    [401, { code: 'UNAUTHENTICATED' }, 'Посилання недійсне'],
    [409, { code: 'RESUME_TOKEN_USED' }, 'вже надіслано'],
  ])('%s — зрозуміле повідомлення замість форми', async (status, body, text) => {
    const { fixture, http, host } = await setup(['form', 'resume', 'tok']);
    http.expectOne('/api/v1/forms/resume/tok').flush(body, { status, statusText: 'Error' });
    await render(fixture);

    expect(host.querySelector('[data-testid="form-error"]')?.textContent).toContain(text);
    expect(host.querySelector('[data-testid="form"]')).toBeNull();
  });
});
