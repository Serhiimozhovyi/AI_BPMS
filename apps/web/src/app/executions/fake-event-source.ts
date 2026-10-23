import { EventSourceLike } from './executions-api.service';

/** Фейковий `EventSource` для тестів: тест сам «надсилає» події SSE. */
export class FakeEventSource implements EventSourceLike {
  static last: FakeEventSource | null = null;
  readonly listeners = new Map<string, ((e: MessageEvent<string>) => void)[]>();
  onerror: ((e: Event) => void) | null = null;
  readyState = 1;
  closed = false;

  constructor(readonly url: string) {
    FakeEventSource.last = this;
  }

  addEventListener(type: string, listener: (e: MessageEvent<string>) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  emit(type: string, data: unknown): void {
    for (const l of this.listeners.get(type) ?? []) l(new MessageEvent(type, { data: JSON.stringify(data) }));
  }

  fail(readyState: number): void {
    this.readyState = readyState;
    this.onerror?.(new Event('error'));
  }

  close(): void {
    this.closed = true;
    this.readyState = 2;
  }
}
