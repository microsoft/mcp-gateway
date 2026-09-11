import { createParser } from "eventsource-parser";

export async function readMcpBody(response: Response, onEvent?: (event: unknown) => void, signal?: AbortSignal): Promise<unknown> {
  if (!response.headers.get("content-type")?.includes("text/event-stream")) {
    const text = await response.text();
    if (!text) return null;
    try { return JSON.parse(text); } catch { return text; }
  }
  const reader = response.body?.getReader();
  if (!reader) return [];
  const decoder = new TextDecoder();
  const events: Array<{ value: unknown; size: number }> = [];
  let retainedSize = 0;
  const parser = createParser({
    maxBufferSize: 1024 * 1024,
    onEvent(event) {
      let value: unknown;
      try { value = JSON.parse(event.data); } catch { value = event.data; }
      events.push({ value, size: event.data.length });
      retainedSize += event.data.length;
      while (events.length > 50 || retainedSize > 2 * 1024 * 1024) {
        retainedSize -= events.shift()!.size;
      }
      onEvent?.(value);
    },
    onError(error) {
      if (error.type === "max-buffer-size-exceeded") throw error;
    },
  });
  const abort = () => { void reader.cancel(signal?.reason).catch(() => {}); };
  signal?.addEventListener("abort", abort, { once: true });
  try {
    signal?.throwIfAborted();
    while (true) {
      const { done, value } = await reader.read();
      signal?.throwIfAborted();
      if (done) break;
      parser.feed(decoder.decode(value, { stream: true }));
    }
    parser.feed(decoder.decode());
    return events.map(event => event.value);
  } finally {
    signal?.removeEventListener("abort", abort);
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}