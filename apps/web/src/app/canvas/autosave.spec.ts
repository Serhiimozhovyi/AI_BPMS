import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { Autosave } from './autosave';

describe('Autosave', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function setup() {
    const requests: { value: string; reply: Subject<string> }[] = [];
    const saved: string[] = [];
    const autosave = new Autosave<string, string>(
      (value) => {
        const reply = new Subject<string>();
        requests.push({ value, reply });
        return reply;
      },
      (result) => saved.push(result),
      500,
    );
    const respond = (i: number, result = `ok:${requests[i].value}`) => {
      requests[i].reply.next(result);
      requests[i].reply.complete();
    };
    return { autosave, requests, saved, respond };
  }

  it('вдале збереження без помилок не замінює validationErrors новим масивом (інакше канвас перебудовує всі вузли)', () => {
    const { autosave, respond } = setup();
    const before = autosave.validationErrors();

    autosave.schedule('a');
    vi.advanceTimersByTime(500);
    respond(0);

    expect(autosave.validationErrors()).toBe(before);
  });

  it('debounce: серія змін — одне збереження останнього значення після паузи', () => {
    const { autosave, requests, saved, respond } = setup();
    expect(autosave.status()).toBe('idle');
    expect(autosave.dirty()).toBe(false);

    autosave.schedule('a');
    vi.advanceTimersByTime(300);
    autosave.schedule('b');
    vi.advanceTimersByTime(300);
    autosave.schedule('c');
    expect(autosave.status()).toBe('pending');
    expect(autosave.dirty()).toBe(true);
    expect(requests).toHaveLength(0);

    vi.advanceTimersByTime(499);
    expect(requests).toHaveLength(0);
    vi.advanceTimersByTime(1);
    expect(requests.map((r) => r.value)).toEqual(['c']);
    expect(autosave.status()).toBe('saving');

    respond(0);
    expect(saved).toEqual(['ok:c']);
    expect(autosave.status()).toBe('saved');
    expect(autosave.dirty()).toBe(false);
  });

  it('зміни під час запиту йдуть наступним запитом — не паралельно і в тому ж порядку', () => {
    const { autosave, requests, respond } = setup();
    autosave.schedule('a');
    vi.advanceTimersByTime(500);
    autosave.schedule('b');
    vi.advanceTimersByTime(500);
    // Таймер спрацював, але запит «a» ще в дорозі — «b» чекає.
    expect(requests.map((r) => r.value)).toEqual(['a']);
    expect(autosave.status()).toBe('saving');

    respond(0);
    expect(requests.map((r) => r.value)).toEqual(['a', 'b']);
    expect(autosave.status()).toBe('saving');
    respond(1);
    expect(autosave.status()).toBe('saved');
  });

  it('зміна під час запиту, поки таймер ще йде, — після відповіді чекає паузи', () => {
    const { autosave, requests, respond } = setup();
    autosave.schedule('a');
    vi.advanceTimersByTime(500);
    autosave.schedule('b');
    respond(0);
    expect(autosave.status()).toBe('pending');
    expect(requests).toHaveLength(1);
    vi.advanceTimersByTime(500);
    expect(requests.map((r) => r.value)).toEqual(['a', 'b']);
  });

  it('422: помилки валідації і повідомлення; без автоповтору; успішне збереження їх очищає', () => {
    const errors = [{ code: 'GRAPH_CYCLE', message: 'Цикл', nodeId: 'n1' }];
    let fail = true;
    const autosave = new Autosave<string>(
      () =>
        fail
          ? throwError(
              () =>
                new HttpErrorResponse({
                  status: 422,
                  error: { title: 'Validation', detail: 'Сценарій не пройшов валідацію.', errors },
                }),
            )
          : of('ok'),
      undefined,
      500,
    );

    autosave.schedule('bad');
    vi.advanceTimersByTime(500);
    expect(autosave.status()).toBe('error');
    expect(autosave.dirty()).toBe(true);
    expect(autosave.validationErrors()).toEqual(errors);
    expect(autosave.errorMessage()).toBe('Сценарій не пройшов валідацію.');
    vi.advanceTimersByTime(10_000);
    expect(autosave.status()).toBe('error');

    fail = false;
    autosave.schedule('fixed');
    vi.advanceTimersByTime(500);
    expect(autosave.status()).toBe('saved');
    expect(autosave.validationErrors()).toEqual([]);
    expect(autosave.errorMessage()).toBeNull();
  });

  it('409 та інші помилки — повідомлення без помилок валідації', () => {
    const autosave = new Autosave<string>(
      () => throwError(() => new HttpErrorResponse({ status: 409, error: { detail: 'Сценарій щойно збережено іншим запитом' } })),
      undefined,
      500,
    );
    autosave.schedule('x');
    vi.advanceTimersByTime(500);
    expect(autosave.status()).toBe('error');
    expect(autosave.validationErrors()).toEqual([]);
    expect(autosave.errorMessage()).toContain('іншим запитом');
  });

  it('flush зберігає одразу, без паузи; без змін — нічого не робить', () => {
    const { autosave, requests } = setup();
    autosave.flush();
    expect(requests).toHaveLength(0);
    autosave.schedule('a');
    autosave.flush();
    expect(requests.map((r) => r.value)).toEqual(['a']);
    vi.advanceTimersByTime(1000);
    expect(requests).toHaveLength(1);
  });

  it('reset забуває відкладене; результат запиту, що вже в дорозі, стан не змінює', () => {
    const { autosave, requests, saved, respond } = setup();
    autosave.schedule('a');
    autosave.flush();
    autosave.schedule('b');
    autosave.reset();
    expect(autosave.status()).toBe('idle');

    respond(0);
    // Відповідь дійшла (onSaved викликано), але стан нового сценарію лишився чистим, «b» не відправлено.
    expect(saved).toEqual(['ok:a']);
    expect(autosave.status()).toBe('idle');
    vi.advanceTimersByTime(1000);
    expect(requests).toHaveLength(1);
  });
});
