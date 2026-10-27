import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { API_BASE } from '../connectors/connectors-api.service';

/** Статуси, коли Api (або проксі перед ним) недоступний. */
const DOWN_STATUSES = [502, 503, 504];

/** Чи помилка означає «сервер недоступний» (мережа лягла або шлюз не дістався Api). */
export function isServerDown(status: number): boolean {
  return status === 0 || DOWN_STATUSES.includes(status);
}

/** Стан зв'язку з Api (F3-84): `offline` вмикає банер угорі, першу ж успішну (чи будь-яку не-шлюзову) відповідь гасить. */
@Injectable({ providedIn: 'root' })
export class ConnectivityService {
  private readonly http = inject(HttpClient);
  readonly offline = signal(false);
  readonly checking = signal(false);

  /** «Повторити»: легкий запит до Api; якщо відповів — сторінку перезавантажуємо, щоб порожні списки наповнились. */
  retry(): void {
    this.checking.set(true);
    this.http.get(`${API_BASE}/auth/providers`).subscribe({
      next: () => this.recovered(),
      // Будь-яка відповідь, крім «шлюз недоступний», означає, що Api живий (інтерсептор уже зняв банер).
      error: () => {
        this.checking.set(false);
        if (!this.offline()) this.reload();
      },
    });
  }

  /** Окремий метод, щоб у тестах не перезавантажувати вікно. */
  reload(): void {
    location.reload();
  }

  private recovered(): void {
    this.checking.set(false);
    this.reload();
  }
}
