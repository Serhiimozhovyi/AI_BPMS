import { ConnectorInput, ConnectorStatus, ConnectorTransport } from './connectors-api.service';

/** Сирі значення форми додавання конектора. */
export interface ConnectorFormValue {
  name: string;
  transport: ConnectorTransport;
  command: string;
  /** Аргументи stdio — по одному на рядок. */
  arguments: string;
  /** Змінні середовища stdio — `KEY=value` по одному на рядок. */
  environment: string;
  workingDirectory: string;
  url: string;
  /** Заголовки http — `Name: value` по одному на рядок. */
  headers: string;
  credentialId: string;
}

/** Непорожні рядки без пробілів по краях. */
export function parseLines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l.length > 0);
}

/**
 * Пари «ключ-значення» з рядків, розділених `separator` (перше входження).
 * Повертає `null`, якщо хоч один рядок без роздільника або з порожнім ключем.
 */
export function parseKeyValues(text: string, separator: '=' | ':'): Record<string, string> | null {
  const result: Record<string, string> = {};
  for (const line of parseLines(text)) {
    const at = line.indexOf(separator);
    if (at <= 0) return null;
    const key = line.slice(0, at).trim();
    if (!key) return null;
    result[key] = line.slice(at + 1).trim();
  }
  return result;
}

/** Тіло `POST /connectors` з форми; поля іншого транспорту не надсилаються. */
export function toConnectorInput(v: ConnectorFormValue): ConnectorInput {
  const credentialId = v.credentialId.trim() || null;
  if (v.transport === 'stdio') {
    const args = parseLines(v.arguments);
    const env = parseKeyValues(v.environment, '=') ?? {};
    return {
      name: v.name.trim(),
      transport: 'stdio',
      credentialId,
      config: {
        command: v.command.trim(),
        arguments: args.length ? args : null,
        environment: Object.keys(env).length ? env : null,
        workingDirectory: v.workingDirectory.trim() || null,
      },
    };
  }
  const headers = parseKeyValues(v.headers, ':') ?? {};
  return {
    name: v.name.trim(),
    transport: 'http',
    credentialId,
    config: {
      url: v.url.trim(),
      headers: Object.keys(headers).length ? headers : null,
    },
  };
}

/**
 * Заготовка аргументів для тестового виклику з `inputSchema`: обов'язкові властивості з
 * `default` або порожнім значенням за типом.
 */
export function argumentsSkeleton(schema: Record<string, unknown> | null | undefined): Record<string, unknown> {
  const properties = (schema?.['properties'] ?? {}) as Record<string, Record<string, unknown>>;
  const required = Array.isArray(schema?.['required']) ? (schema['required'] as string[]) : [];
  const result: Record<string, unknown> = {};
  for (const name of required) {
    const prop = properties[name] ?? {};
    if ('default' in prop) {
      result[name] = prop['default'];
      continue;
    }
    const type = Array.isArray(prop['type']) ? (prop['type'] as string[])[0] : prop['type'];
    switch (type) {
      case 'number':
      case 'integer':
        result[name] = 0;
        break;
      case 'boolean':
        result[name] = false;
        break;
      case 'array':
        result[name] = [];
        break;
      case 'object':
        result[name] = {};
        break;
      default:
        result[name] = '';
    }
  }
  return result;
}

/**
 * Розбирає JSON аргументів виклику: має бути об'єкт. Порожній текст — `{}`.
 * Повертає `null`, якщо текст не JSON-об'єкт.
 */
export function parseArguments(text: string): Record<string, unknown> | null {
  if (!text.trim()) return {};
  try {
    const value: unknown = JSON.parse(text);
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? (value as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

/** Підпис стану з'єднання. */
export function statusLabel(status: ConnectorStatus): string {
  switch (status) {
    case 'ok':
      return $localize`:@@connectors.status.ok:Підключено`;
    case 'error':
      return $localize`:@@connectors.status.error:Помилка`;
    default:
      return $localize`:@@connectors.status.new:Не перевірено`;
  }
}
