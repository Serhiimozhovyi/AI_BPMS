import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';
import { FormPayload, JsonSchema } from './form-fields';

/** Опис форми — `GET /api/v1/forms/{path}` (F6-03). */
export interface FormDefinition {
  /** `trigger` — форма запускає сценарій; `wait` — продовжує виконання, що чекає. */
  kind: 'trigger' | 'wait';
  title: string;
  description?: string | null;
  schema: JsonSchema;
  submitLabel: string;
}

/** Результат `POST /api/v1/forms/{path}`. */
export interface FormSubmitted {
  executionId: string;
  message?: string | null;
  redirectUrl?: string | null;
}

/** Помилка поля з `errors[]` відповіді 400 `FORM_INVALID`. */
export interface FormFieldError {
  field: string;
  message: string;
}

/** Заголовок з токеном посилання форми (`auth: token`). */
export const FORM_TOKEN_HEADER = 'X-Form-Token';

/** Публічні форми: опис і надсилання; шлях — частина адреси після `/form/`. */
@Injectable({ providedIn: 'root' })
export class FormsApiService {
  private readonly http = inject(HttpClient);

  /** Опис форми; токен посилання — у заголовку. */
  definition(path: string, token: string | null): Observable<FormDefinition> {
    return this.http.get<FormDefinition>(this.url(path), { headers: this.headers(token) });
  }

  /** Надсилає форму: без файлів — JSON `{ fields }`, з файлами — multipart (`fields` + файли за назвою поля). */
  submit(path: string, payload: FormPayload, token: string | null): Observable<FormSubmitted> {
    const names = Object.keys(payload.files);
    if (names.length === 0) {
      return this.http.post<FormSubmitted>(this.url(path), { fields: payload.fields }, { headers: this.headers(token) });
    }
    const body = new FormData();
    body.append('fields', JSON.stringify(payload.fields));
    for (const name of names) {
      body.append(name, payload.files[name], payload.files[name].name);
    }
    return this.http.post<FormSubmitted>(this.url(path), body, { headers: this.headers(token) });
  }

  private url(path: string): string {
    return `${API_BASE}/forms/${path.split('/').map(encodeURIComponent).join('/')}`;
  }

  private headers(token: string | null): HttpHeaders {
    return token ? new HttpHeaders({ [FORM_TOKEN_HEADER]: token }) : new HttpHeaders();
  }
}
