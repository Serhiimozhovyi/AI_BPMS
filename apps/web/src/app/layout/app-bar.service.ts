import { Injectable, signal } from '@angular/core';

/** Пункт меню застосунку проєкту у верхній смузі (F9-04). */
export interface AppBarPage {
  key: string;
  name: string;
}

/** Група меню застосунку: порожня назва — пункти без заголовка. */
export interface AppBarGroup {
  label: string;
  pages: AppBarPage[];
}

/** Оболонка застосунку проєкту (F8-18) у верхній смузі замість меню AI_BPMS: назва, значок і розділи. */
export interface AppBar {
  projectId: string;
  title: string;
  /** Назва значка Material Symbols. */
  iconName: string | null;
  /** Значок-emoji, якщо значок — не слово. */
  emoji: string | null;
  menu: AppBarGroup[];
}

/**
 * Зв'язок між сторінкою застосунку проєкту та шапкою (F9-04): сторінка публікує оболонку, шапка показує її
 * в тій самій смузі, де звичайне меню AI_BPMS.
 */
@Injectable({ providedIn: 'root' })
export class AppBarService {
  readonly bar = signal<AppBar | null>(null);
}
