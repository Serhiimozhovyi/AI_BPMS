import { Injectable, inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';

/** Тривалість показу, мс: успіх зникає швидко, помилку читають довше. */
export const NOTIFY_SUCCESS_MS = 4000;
export const NOTIFY_ERROR_MS = 8000;
export const NOTIFY_UNDO_MS = 6000;

/**
 * Короткі сповіщення про результат дії (F3-82): успіх, помилка з причиною, «Скасувати» для видалень.
 * Панелі мають `role="status"`/`alert` від MatSnackBar, тож їх читають і скрінрідери.
 */
@Injectable({ providedIn: 'root' })
export class NotifyService {
  private readonly snackBar = inject(MatSnackBar);

  success(message: string): void {
    this.snackBar.open(message, undefined, { duration: NOTIFY_SUCCESS_MS, panelClass: 'ob-notify-success' });
  }

  /** Помилка з причиною: `reason` додається після пояснення, дію не губимо за межами екрана. */
  error(message: string, reason?: string): void {
    const text = reason ? `${message} ${reason}` : message;
    this.snackBar.open(text, $localize`:@@notify.close:Закрити`, { duration: NOTIFY_ERROR_MS, panelClass: 'ob-notify-error' });
  }

  /** Успіх із кнопкою «Скасувати»: `undo` викликається, лише якщо її натиснули до зникнення сповіщення. */
  undoable(message: string, undo: () => void): void {
    const ref = this.snackBar.open(message, $localize`:@@notify.undo:Скасувати`, {
      duration: NOTIFY_UNDO_MS,
      panelClass: 'ob-notify-success',
    });
    ref.onAction().subscribe(undo);
  }
}
