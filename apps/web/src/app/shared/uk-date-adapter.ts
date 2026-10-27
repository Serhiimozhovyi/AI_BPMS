import { Injectable } from '@angular/core';
import { NativeDateAdapter } from '@angular/material/core';

/**
 * Адаптер дат для `mat-datepicker` (F3-81): показує `дд.мм.рррр` (локаль `uk-UA`) і розбирає введене в цьому форматі.
 * Рідний адаптер розбирав би `27.10.2026` через `Date.parse` і отримував би невалідну дату.
 */
@Injectable()
export class UkDateAdapter extends NativeDateAdapter {
  override parse(value: unknown, parseFormat?: unknown): Date | null {
    if (typeof value === 'string') {
      const match = /^\s*(\d{1,2})\.(\d{1,2})\.(\d{4})\s*$/.exec(value);
      if (match) {
        const [day, month, year] = [Number(match[1]), Number(match[2]), Number(match[3])];
        const date = new Date(year, month - 1, day);
        return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day ? date : this.invalid();
      }
    }
    return super.parse(value, parseFormat);
  }
}
