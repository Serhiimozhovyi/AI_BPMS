import { TestBed } from '@angular/core/testing';
import { MAT_DATE_LOCALE } from '@angular/material/core';
import { describe, expect, it } from 'vitest';
import { UkDateAdapter } from './uk-date-adapter';

describe('UkDateAdapter', () => {
  TestBed.configureTestingModule({ providers: [UkDateAdapter, { provide: MAT_DATE_LOCALE, useValue: 'uk-UA' }] });
  const adapter = TestBed.inject(UkDateAdapter);

  it('розбирає дд.мм.рррр', () => {
    const d = adapter.parse('27.10.2026', null)!;
    expect([d.getFullYear(), d.getMonth(), d.getDate()]).toEqual([2026, 9, 27]);
  });

  it('неіснуюча дата — невалідна', () => {
    expect(adapter.isValid(adapter.parse('31.02.2026', null)!)).toBe(false);
  });
});
