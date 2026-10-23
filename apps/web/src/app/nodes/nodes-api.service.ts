import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API_BASE } from '../connectors/connectors-api.service';

/** Група вузла в палітрі (`NodeGroup`, camelCase). */
export type NodeGroup = 'trigger' | 'logic' | 'transform' | 'integration' | 'ai';

/** Вимога credential'а вузла (`CredentialRequirement`). */
export interface CredentialRequirement {
  name: string;
  type: string;
  parameter: string;
  required: boolean;
}

/** Дескриптор типу вузла (`NodeDescriptor`) — елемент `GET /api/v1/nodes`. */
export interface NodeTypeInfo {
  type: string;
  version: number;
  displayName: string;
  group: NodeGroup;
  /** Назви вхідних портів у порядку індексів; порожній масив — тригер. */
  inputs: string[];
  /** Назви вихідних портів у порядку індексів (без `error` — його додає `continueOnFail`). */
  outputs: string[];
  parametersSchema: Record<string, unknown>;
  credentials?: CredentialRequirement[] | null;
  description?: string | null;
  isTrigger: boolean;
}

/** Клієнт `GET /api/v1/nodes` — дескриптори `core.*`/`ai.*`/`mcp.*` для палітри (docs/architecture.md §10.3). */
@Injectable({ providedIn: 'root' })
export class NodesApiService {
  private readonly http = inject(HttpClient);

  list(): Observable<NodeTypeInfo[]> {
    return this.http.get<NodeTypeInfo[]>(`${API_BASE}/nodes`);
  }
}
