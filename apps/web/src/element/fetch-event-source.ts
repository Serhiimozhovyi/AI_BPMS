import { EventSourceLike } from '../app/executions/executions-api.service';

/**
 * Мінімальний SSE-клієнт на `fetch` (F4-13): як `EventSource`, але з довільними заголовками (`Authorization`).
 * Розбирає `event:`/`data:`/`id:`; обрив або помилка HTTP — `readyState = 2` і `onerror` (без перепідключення:
 * редактор показує помилку, а стан виконання видно в журналі).
 */
export class FetchEventSource implements EventSourceLike {
  onerror: ((e: Event) => void) | null = null;
  readyState = 0;
  private readonly listeners = new Map<string, ((e: MessageEvent<string>) => void)[]>();
  private readonly abort = new AbortController();

  constructor(url: string, headers: Record<string, string>) {
    void this.run(url, headers);
  }

  addEventListener(type: string, listener: (e: MessageEvent<string>) => void): void {
    this.listeners.set(type, [...(this.listeners.get(type) ?? []), listener]);
  }

  close(): void {
    this.readyState = 2;
    this.abort.abort();
  }

  private async run(url: string, headers: Record<string, string>): Promise<void> {
    try {
      const response = await fetch(url, { headers: { Accept: 'text/event-stream', ...headers }, signal: this.abort.signal });
      if (!response.ok || !response.body) throw new Error(`HTTP ${response.status}`);
      this.readyState = 1;
      const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
      let buffer = '';
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += value;
        let end: number;
        while ((end = buffer.search(/\r?\n\r?\n/)) >= 0) {
          this.dispatch(buffer.slice(0, end));
          buffer = buffer.slice(end).replace(/^\r?\n\r?\n/, '');
        }
      }
      throw new Error('stream closed');
    } catch {
      if (this.readyState === 2 && this.abort.signal.aborted) return;
      this.readyState = 2;
      this.onerror?.(new Event('error'));
    }
  }

  private dispatch(block: string): void {
    let type = 'message';
    const data: string[] = [];
    for (const line of block.split(/\r?\n/)) {
      if (line.startsWith(':')) continue;
      const colon = line.indexOf(':');
      const field = colon < 0 ? line : line.slice(0, colon);
      const value = colon < 0 ? '' : line.slice(colon + 1).replace(/^ /, '');
      if (field === 'event') type = value;
      else if (field === 'data') data.push(value);
    }
    if (data.length === 0) return;
    const event = new MessageEvent<string>(type, { data: data.join('\n') });
    for (const listener of this.listeners.get(type) ?? []) listener(event);
  }
}
