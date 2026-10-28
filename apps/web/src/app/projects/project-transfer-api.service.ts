import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Шаблон проєкту (`GET /projects/{id}/export`, docs/projects.md §5); для UI він непрозорий — читається лише назва. */
export interface ProjectBundle {
  formatVersion: number;
  project: { name: string; description?: string; icon?: string };
  [key: string]: unknown;
}

/** Посилання імпорту без відповідника в цьому середовищі (422 `IMPORT_UNMAPPED`). */
export interface UnmappedReference {
  kind: 'connector' | 'credential' | 'workflow';
  ref: string;
  name?: string | null;
  type?: string | null;
  reason: 'not_found' | 'ambiguous' | 'invalid';
}

/** Явні відповідники «ref джерела → id приймача». */
export interface ImportMapping {
  connectors?: Record<string, string>;
  credentials?: Record<string, string>;
  workflows?: Record<string, string>;
}

/** Що зробив імпорт (`POST /projects/import`). */
export interface ProjectImportResult {
  projectId: string;
  name: string;
  projectCreated: boolean;
  tablesCreated: string[];
  tablesExtended: string[];
  rowsInserted: Record<string, number>;
  pagesCreated: string[];
  pagesUpdated: string[];
  workflowsCreated: string[];
  workflowsUpdated: string[];
  missingVariables: string[];
}

/** Тіло імпорту. */
export interface ProjectImportInput {
  bundle: ProjectBundle;
  mapping?: ImportMapping;
  projectId?: string;
  name?: string;
}

/** Експорт та імпорт шаблонів проєктів (F8-06). */
@Injectable({ providedIn: 'root' })
export class ProjectTransferApiService {
  private readonly http = inject(HttpClient);

  /** Шаблон проєкту; `rowsFor` — таблиці, рядки яких додати (чутливі колонки не експортуються). */
  export(projectId: string, rowsFor: string[] = []): Observable<ProjectBundle> {
    let params = new HttpParams();
    if (rowsFor.length > 0) params = params.set('rowsFor', rowsFor.join(','));
    return this.http.get<ProjectBundle>(`${API_BASE}/projects/${encodeURIComponent(projectId)}/export`, { params });
  }

  import(input: ProjectImportInput): Observable<ProjectImportResult> {
    return this.http.post<ProjectImportResult>(`${API_BASE}/projects/import`, input);
  }
}

/** Відповідники з полів, які заповнив користувач: ключ — `kind:ref`, значення — id в цьому середовищі. */
export function mappingFromFields(unmapped: readonly UnmappedReference[], fields: Readonly<Record<string, string>>): ImportMapping {
  const mapping: Required<ImportMapping> = { connectors: {}, credentials: {}, workflows: {} };
  for (const u of unmapped) {
    const target = fields[unmappedKey(u)]?.trim();
    if (!target) continue;
    const group = u.kind === 'connector' ? mapping.connectors : u.kind === 'credential' ? mapping.credentials : mapping.workflows;
    group[u.ref] = target;
  }
  return mapping;
}

export function unmappedKey(u: Pick<UnmappedReference, 'kind' | 'ref'>): string {
  return `${u.kind}:${u.ref}`;
}

/** Ім'я файлу шаблону з назви проєкту. */
export function bundleFileName(name: string): string {
  const safe = name.trim().replace(/[\\/:*?"<>|\s]+/g, '-').replace(/^-+|-+$/g, '');
  return `${safe || 'project'}.openbp-project.json`;
}

/** Зберігає шаблон файлом у браузері. */
export function downloadBundle(bundle: ProjectBundle): void {
  const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2) + '\n'], { type: 'application/json' }));
  const a = document.createElement('a');
  a.href = url;
  a.download = bundleFileName(bundle.project.name);
  a.click();
  URL.revokeObjectURL(url);
}
