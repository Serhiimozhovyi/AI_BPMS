import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE, PageResult } from '../connectors/connectors-api.service';

/** Тип колонки таблиці даних — як `DataColumnType` в Api. */
export type DataColumnType = 'string' | 'number' | 'boolean' | 'date' | 'json';

export const DATA_COLUMN_TYPES: readonly DataColumnType[] = ['string', 'number', 'boolean', 'date', 'json'];

/** Колонка: назва, тип і чи є ключем (не більше однієї, рядок або число). */
export interface DataColumn {
  name: string;
  type: DataColumnType;
  key?: boolean;
}

/** `DataTableSummary` — таблиця у списку й деталях (F6-11). */
export interface DataTableSummary {
  id: string;
  name: string;
  columns: DataColumn[];
  rowCount: number;
  updatedAt: string;
}

/** `DataRowDto` — рядок таблиці. */
export interface DataRow {
  key: string;
  data: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
}

/** Результат імпорту CSV. */
export interface DataImportResult {
  inserted: number;
  updated: number;
}

/** Назва колонки: латиниця, цифри й `_`, не з цифри — як перевіряє Api (`DataTableRules`). */
export const DATA_COLUMN_NAME_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** Ключова колонка таблиці, якщо є. */
export function keyColumn(table: Pick<DataTableSummary, 'columns'>): DataColumn | undefined {
  return table.columns.find((c) => c.key);
}

/** Значення комірки для показу: json — компактним JSON, порожнє — порожнім рядком. */
export function cellText(value: unknown): string {
  if (value === null || value === undefined) return '';
  return typeof value === 'object' ? JSON.stringify(value) : String(value);
}

/**
 * Текст із поля форми рядка → значення для Api. Порожнє — `null`; json розбирається (помилка — виняток з назвою колонки);
 * число й булеве лишаються рядком, якщо не розпізнані, — їх перевірить Api з кодом `DATA_TABLE_TYPE_MISMATCH`.
 */
export function inputValue(column: DataColumn, text: string): unknown {
  const trimmed = text.trim();
  if (trimmed === '') return null;
  switch (column.type) {
    case 'json':
      try {
        return JSON.parse(trimmed) as unknown;
      } catch {
        throw new Error($localize`:@@tables.row.jsonInvalid:Колонка «${column.name}:name:» — некоректний JSON`);
      }
    case 'number':
      return Number.isFinite(Number(trimmed)) ? Number(trimmed) : trimmed;
    case 'boolean':
      return trimmed === 'true' ? true : trimmed === 'false' ? false : trimmed;
    default:
      return text;
  }
}

/** Клієнт `/api/v1/tables`: читають усі ролі, змінюють editor і admin. */
@Injectable({ providedIn: 'root' })
export class DataTablesApiService {
  private readonly http = inject(HttpClient);
  private readonly base = `${API_BASE}/tables`;

  list(): Observable<DataTableSummary[]> {
    return this.http.get<DataTableSummary[]>(this.base);
  }

  create(name: string, columns: DataColumn[]): Observable<DataTableSummary> {
    return this.http.post<DataTableSummary>(this.base, { name, columns });
  }

  update(id: string, name: string, columns: DataColumn[]): Observable<DataTableSummary> {
    return this.http.put<DataTableSummary>(`${this.base}/${id}`, { name, columns });
  }

  remove(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  rows(id: string, search: string, page = 1, pageSize = 50): Observable<PageResult<DataRow>> {
    const params: Record<string, string | number> = { page, pageSize };
    if (search.trim()) params['search'] = search.trim();
    return this.http.get<PageResult<DataRow>>(`${this.base}/${id}/rows`, { params });
  }

  insertRow(id: string, values: Record<string, unknown>): Observable<DataRow> {
    return this.http.post<DataRow>(`${this.base}/${id}/rows`, { values });
  }

  updateRow(id: string, key: string, values: Record<string, unknown>): Observable<DataRow> {
    return this.http.patch<DataRow>(`${this.base}/${id}/rows/${encodeURIComponent(key)}`, { values });
  }

  removeRow(id: string, key: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}/rows/${encodeURIComponent(key)}`);
  }

  importCsv(id: string, csv: string): Observable<DataImportResult> {
    return this.http.post<DataImportResult>(`${this.base}/${id}/import`, { csv });
  }
}
