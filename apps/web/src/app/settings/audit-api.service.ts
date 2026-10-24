import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE, PageResult } from '../connectors/connectors-api.service';

/** `AuditEntry` — запис `GET /api/v1/audit` (F6-15). `details` — без значень секретів. */
export interface AuditEntry {
  id: string;
  at: string;
  userId: string | null;
  userEmail: string | null;
  apiKeyId: string | null;
  apiKeyName: string | null;
  action: string;
  entityType: string;
  entityId: string | null;
  details: Record<string, unknown>;
}

/** Фільтр журналу; порожні поля не передаються. */
export interface AuditFilter {
  entityType?: string;
  /** Точна дія або префікс із крапкою (`workflow.`). */
  action?: string;
  entityId?: string;
}

/** Типи сутностей журналу — як `AuditEntities` в Api. */
export const AUDIT_ENTITY_TYPES = ['workflow', 'credential', 'connector', 'apiKey', 'variable', 'dataTable', 'project'] as const;

/** Назва типу сутності для людей. */
export function auditEntityLabel(type: string): string {
  switch (type) {
    case 'workflow':
      return $localize`:@@audit.entity.workflow:Сценарій`;
    case 'credential':
      return $localize`:@@audit.entity.credential:Облікові дані`;
    case 'connector':
      return $localize`:@@audit.entity.connector:Конектор`;
    case 'apiKey':
      return $localize`:@@audit.entity.apiKey:API-ключ`;
    case 'variable':
      return $localize`:@@audit.entity.variable:Змінна`;
    case 'dataTable':
      return $localize`:@@audit.entity.dataTable:Таблиця даних`;
    case 'project':
      return $localize`:@@audit.entity.project:Проєкт`;
    default:
      return type;
  }
}

/** Дія для людей: «створено», «збережено» тощо; невідома — як є. */
export function auditActionLabel(action: string): string {
  switch (action.slice(action.indexOf('.') + 1)) {
    case 'created':
      return $localize`:@@audit.action.created:створено`;
    case 'saved':
      return $localize`:@@audit.action.saved:збережено`;
    case 'renamed':
      return $localize`:@@audit.action.renamed:перейменовано`;
    case 'activated':
      return $localize`:@@audit.action.activated:активовано`;
    case 'deactivated':
      return $localize`:@@audit.action.deactivated:деактивовано`;
    case 'deleted':
      return $localize`:@@audit.action.deleted:видалено`;
    case 'replaced':
      return $localize`:@@audit.action.replaced:значення замінено`;
    case 'updated':
      return $localize`:@@audit.action.updated:змінено`;
    case 'revoked':
      return $localize`:@@audit.action.revoked:відкликано`;
    case 'set':
      return $localize`:@@audit.action.set:задано`;
    default:
      return action;
  }
}

/** Коротко про запис: назва сутності, версія, поля, причина дії системи. */
export function auditSummary(entry: Pick<AuditEntry, 'details'>): string {
  const d = entry.details;
  const parts: string[] = [];
  const name = d['name'] ?? d['key'];
  if (typeof name === 'string') parts.push(`«${name}»`);
  if (typeof d['previousName'] === 'string') parts.push($localize`:@@audit.summary.previous:було «${d['previousName']}:name:»`);
  if (typeof d['version'] === 'number') parts.push($localize`:@@audit.summary.version:версія ${d['version']}:version:`);
  if (Array.isArray(d['fields']) && d['fields'].length > 0) parts.push($localize`:@@audit.summary.fields:поля: ${d['fields'].join(', ')}:fields:`);
  if (d['reason'] === 'disableAfterErrors') parts.push($localize`:@@audit.summary.breaker:запобіжник після ${d['errors']}:errors: помилок поспіль`);
  return parts.join(' · ');
}

/** Клієнт `/api/v1/audit`: лише admin. */
@Injectable({ providedIn: 'root' })
export class AuditApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/audit`;

  list(filter: AuditFilter, page = 1, pageSize = 50): Observable<PageResult<AuditEntry>> {
    const params: Record<string, string | number> = { page, pageSize };
    for (const [key, value] of Object.entries(filter)) {
      if (typeof value === 'string' && value.trim()) params[key] = value.trim();
    }
    return this.http.get<PageResult<AuditEntry>>(this.base, { params });
  }
}
