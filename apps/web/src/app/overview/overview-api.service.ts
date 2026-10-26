import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Періоди сторінки «Огляд» — як `OverviewService.Ranges` в Api. */
export const OVERVIEW_RANGES = ['24h', '7d', '30d'] as const;
export type OverviewRange = (typeof OVERVIEW_RANGES)[number];

/** Сценарій із топу за помилками. */
export interface OverviewFailingWorkflow {
  workflowId: string;
  name: string | null;
  failed: number;
  total: number;
}

/** `GET /api/v1/overview` (F6-07): з погодинних агрегатів `execution_stats`, «зараз» — з черги. */
export interface Overview {
  range: OverviewRange;
  from: string;
  total: number;
  success: number;
  failed: number;
  cancelled: number;
  /** Від 0 до 1; `null` без виконань. */
  successRate: number | null;
  p50Ms: number | null;
  p95Ms: number | null;
  topFailing: OverviewFailingWorkflow[];
  queued: number;
  running: number;
  waiting: number;
  /** Виконань з відкритими порушеннями правил переходу (F7-04). */
  openViolations?: number;
}

/** Назва періоду для людей. */
export function overviewRangeLabel(range: OverviewRange): string {
  switch (range) {
    case '24h':
      return $localize`:@@overview.range.24h:24 години`;
    case '7d':
      return $localize`:@@overview.range.7d:7 днів`;
    case '30d':
      return $localize`:@@overview.range.30d:30 днів`;
  }
}

/** Тривалість для людей: `850 мс`, `2,5 с`, `4 хв`; `null` — тире. Значення — верхня межа кошика, тому «до». */
export function formatDuration(ms: number | null): string {
  if (ms === null) return '—';
  if (ms < 1000) return $localize`:@@overview.duration.ms:до ${ms}:ms: мс`;
  if (ms < 60_000) return $localize`:@@overview.duration.s:до ${(ms / 1000).toLocaleString('uk-UA', { maximumFractionDigits: 1 })}:s: с`;
  if (ms < 3_600_000) return $localize`:@@overview.duration.min:до ${Math.round(ms / 60_000)}:min: хв`;
  return $localize`:@@overview.duration.h:понад ${Math.round(ms / 3_600_000)}:h: год`;
}

/** Частка успішних у відсотках; `null` — тире. */
export function formatRate(rate: number | null): string {
  return rate === null ? '—' : `${(rate * 100).toLocaleString('uk-UA', { maximumFractionDigits: 1 })} %`;
}

/** Клієнт `/api/v1/overview`: усі ролі. */
@Injectable({ providedIn: 'root' })
export class OverviewApiService {
  private readonly http = inject(HttpClient);

  get(range: OverviewRange): Observable<Overview> {
    return this.http.get<Overview>(`${API_BASE}/overview`, { params: { range } });
  }
}
