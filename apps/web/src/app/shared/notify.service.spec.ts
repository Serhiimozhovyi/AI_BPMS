import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject } from 'rxjs';
import { NOTIFY_ERROR_MS, NOTIFY_SUCCESS_MS, NotifyService } from './notify.service';

describe('NotifyService', () => {
  function setup() {
    const action = new Subject<void>();
    const open = vi.fn().mockReturnValue({ onAction: () => action });
    TestBed.configureTestingModule({ providers: [{ provide: MatSnackBar, useValue: { open } }] });
    return { service: TestBed.inject(NotifyService), open, action };
  }

  it('успіх — коротке сповіщення без кнопки', () => {
    const { service, open } = setup();
    service.success('Готово');
    expect(open).toHaveBeenCalledWith('Готово', undefined, expect.objectContaining({ duration: NOTIFY_SUCCESS_MS }));
  });

  it('помилка дописує причину й тримається довше', () => {
    const { service, open } = setup();
    service.error('Не вийшло.', 'Немає доступу.');
    expect(open).toHaveBeenCalledWith('Не вийшло. Немає доступу.', 'Закрити', expect.objectContaining({ duration: NOTIFY_ERROR_MS }));
  });

  it('«Скасувати» викликає undo', () => {
    const { service, action } = setup();
    const undo = vi.fn();
    service.undoable('Видалено', undo);
    action.next();
    expect(undo).toHaveBeenCalledTimes(1);
  });
});
