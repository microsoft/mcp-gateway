import assert from "node:assert/strict";
import test from "node:test";
import { readMcpBody } from "../src/api/mcpStream.ts";

test("SSE delivers fragmented events before EOF and ignores keepalives", async () => {
  let controller;
  const stream = new ReadableStream({ start(value) { controller = value; } });
  const response = new Response(stream, { headers: { "content-type": "text/event-stream" } });
  let notify;
  const arrived = new Promise(resolve => { notify = resolve; });
  const reading = readMcpBody(response, notify);
  const encoder = new TextEncoder();
  controller.enqueue(encoder.encode(": keepalive\r\ndata: {\"method\":\"notifications/"));
  controller.enqueue(encoder.encode("progress\"}\r\n\r\n"));
  assert.deepEqual(await arrived, { method: "notifications/progress" });
  controller.close();
  assert.deepEqual(await reading, [{ method: "notifications/progress" }]);
});

test("subscription cancellation releases the reader without waiting for EOF", async () => {
  let cancelled = false;
  const stream = new ReadableStream({ cancel() { cancelled = true; } });
  const abort = new AbortController();
  const reading = readMcpBody(new Response(stream, { headers: { "content-type": "text/event-stream" } }), undefined, abort.signal);
  abort.abort();
  await assert.rejects(reading, { name: "AbortError" });
  assert.equal(cancelled, true);
  assert.equal(stream.locked, false);
});

test("event retention is bounded", async () => {
  const text = Array.from({ length: 100 }, (_, index) => `data: {"index":${index}}\n\n`).join("");
  const result = await readMcpBody(new Response(text, { headers: { "content-type": "text/event-stream" } }));
  assert.equal(result.length, 50);
  assert.equal(result[0].index, 50);
  assert.equal(result.at(-1).index, 99);
});