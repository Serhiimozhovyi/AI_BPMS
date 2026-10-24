import { CredentialData, CredentialFieldInfo, CredentialSummary, CredentialTypeInfo } from './credentials-api.service';

/** Пара «назва → значення» у формі (заголовок, змінна середовища, довільний секрет). */
export interface PairValue {
  key: string;
  value: string;
  /** Пара вже є в збереженому credential'і (значення не показується; порожнє — не змінювати). */
  existing: boolean;
  /** Позначено до видалення. */
  removed: boolean;
}

/** Значення форми: прості поля — рядки, мапи — пари. Для `generic` пари лежать під ключем `''`. */
export interface CredentialFormValue {
  fields: Record<string, string>;
  maps: Record<string, PairValue[]>;
}

/** Поля, які показує форма: для `generic` — одна мапа без назви. */
export function formFields(type: CredentialTypeInfo): CredentialFieldInfo[] {
  return type.fields.length > 0
    ? type.fields.filter((f) => f.kind !== 'managed')
    : [{ name: '', title: '', kind: 'secretMap', required: true }];
}

/** Порожнє значення форми; для редагування — наявні ключі мап зі збереженого credential'а (без значень). */
export function initialValue(type: CredentialTypeInfo, existing?: CredentialSummary | null): CredentialFormValue {
  const value: CredentialFormValue = { fields: {}, maps: {} };
  for (const f of formFields(type)) {
    if (f.kind === 'secretMap') {
      const keys = (existing?.fields ?? [])
        .map((path) => mapKey(f.name, path))
        .filter((k): k is string => k !== null);
      value.maps[f.name] = keys.map((key) => ({ key, value: '', existing: true, removed: false }));
      if (value.maps[f.name].length === 0) value.maps[f.name].push(emptyPair());
    } else {
      value.fields[f.name] = '';
    }
  }
  return value;
}

export function emptyPair(): PairValue {
  return { key: '', value: '', existing: false, removed: false };
}

/** Чи заповнене поле у збереженому credential'і. */
export function isFilled(existing: CredentialSummary | null | undefined, field: string): boolean {
  return !!existing?.fields.includes(field);
}

/**
 * Дані для `POST` (`existing` порожній) або часткової заміни для `PATCH`: при редагуванні порожнє поле не
 * змінюється, вилучена пара — `null`. Помилки повертаються списком повідомлень (порожній — усе гаразд);
 * `data` — `null`, якщо при редагуванні змін у даних немає.
 */
export function buildData(
  type: CredentialTypeInfo,
  value: CredentialFormValue,
  existing?: CredentialSummary | null,
): { data: CredentialData | null; errors: string[] } {
  const editing = !!existing;
  const data: CredentialData = {};
  const errors: string[] = [];
  for (const f of formFields(type)) {
    const label = f.title || $localize`:@@credentials.form.pairs:Пари`;
    if (f.kind === 'secretMap') {
      const pairs = value.maps[f.name] ?? [];
      const map: Record<string, string | null> = {};
      const seen = new Set<string>();
      for (const p of pairs) {
        const key = p.key.trim();
        if (p.removed) {
          if (p.existing) map[key] = null;
          continue;
        }
        if (!key && !p.value) continue;
        if (!key) {
          errors.push($localize`:@@credentials.form.pairKeyRequired:${label}:label:: у кожної пари має бути назва.`);
          continue;
        }
        if (seen.has(key)) {
          errors.push($localize`:@@credentials.form.pairDuplicate:${label}:label:: назва «${key}:key:» повторюється.`);
          continue;
        }
        seen.add(key);
        if (p.existing && !p.value) continue;
        if (!p.value) {
          errors.push($localize`:@@credentials.form.pairValueRequired:${label}:label:: «${key}:key:» без значення.`);
          continue;
        }
        map[key] = p.value;
      }
      const remaining = pairs.filter((p) => !p.removed && p.key.trim() && (p.existing || p.value)).length;
      if (f.required && remaining === 0) {
        errors.push($localize`:@@credentials.form.pairsRequired:${label}:label:: потрібна хоча б одна пара.`);
      }
      if (Object.keys(map).length === 0) continue;
      if (f.name === '') {
        Object.assign(data, map);
      } else {
        data[f.name] = map;
      }
    } else {
      const v = (value.fields[f.name] ?? '').trim();
      if (!v) {
        if (f.required && !(editing && isFilled(existing, f.name))) {
          errors.push($localize`:@@credentials.form.fieldRequired:«${f.title}:label:» — обов'язкове поле.`);
        }
        continue;
      }
      if (f.kind === 'url' && !/^https?:\/\/\S+$/i.test(v)) {
        errors.push($localize`:@@credentials.form.urlInvalid:«${f.title}:label:» — потрібна адреса http(s)://…`);
        continue;
      }
      data[f.name] = v;
    }
  }
  return { data: editing && Object.keys(data).length === 0 ? null : data, errors };
}

/** Ключ мапи `map` у шляху `map.key` (або будь-який шлях для мапи без назви). */
function mapKey(map: string, path: string): string | null {
  if (map === '') return path;
  return path.startsWith(`${map}.`) ? path.slice(map.length + 1) : null;
}

/** Підпис стану OAuth2-підключення. */
export function oauthLabel(status: string): string {
  switch (status) {
    case 'connected':
      return $localize`:@@credentials.oauth.connected:підключено`;
    case 'expired':
      return $localize`:@@credentials.oauth.expired:токен сплив — підключіть знову`;
    case 'error':
      return $localize`:@@credentials.oauth.error:помилка оновлення токена`;
    default:
      return $localize`:@@credentials.oauth.notConnected:не підключено`;
  }
}

/** Опис використання для таблиці: «2 конектори, 1 сценарій» або «—». */
export function usageLabel(c: CredentialSummary): string {
  const parts = [
    ...c.usedBy.connectors.map((r) => $localize`:@@credentials.usage.connector:конектор «${r.name}:name:»`),
    ...c.usedBy.workflows.map((r) => $localize`:@@credentials.usage.workflow:сценарій «${r.name}:name:»`),
  ];
  return parts.length > 0 ? parts.join(', ') : '—';
}
