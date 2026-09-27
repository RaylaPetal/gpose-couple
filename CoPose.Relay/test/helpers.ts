import { SELF } from "cloudflare:test";
import { Kind } from "../src/protocol";

export const ROOM = "a".repeat(64);
export const ALICE = "1".repeat(32);
export const BOB = "2".repeat(32);
export const CAROL = "3".repeat(32);

/** A connected test client that records every frame and the close event. */
export interface Client {
  ws: WebSocket;
  frames: Uint8Array[];
  closed: Promise<{ code: number; reason: string }>;
  /** Waits until at least `count` frames arrived (or times out). */
  waitFor(count: number): Promise<Uint8Array[]>;
  send(kind: number, ...body: number[]): void;
}

export async function connect(participant: string, room = ROOM): Promise<Client> {
  const response = await SELF.fetch(`http://relay/v1/room/${room}?p=${participant}`, {
    headers: { Upgrade: "websocket" },
  });
  const ws = response.webSocket;
  if (!ws) {
    throw new Error(`no websocket (status ${response.status})`);
  }
  ws.accept();

  const frames: Uint8Array[] = [];
  const closed = new Promise<{ code: number; reason: string }>((resolve) =>
    ws.addEventListener("close", (e) => resolve({ code: e.code, reason: e.reason })),
  );
  ws.addEventListener("message", (e) => {
    if (e.data instanceof ArrayBuffer) {
      frames.push(new Uint8Array(e.data));
    }
  });

  return {
    ws,
    frames,
    closed,
    async waitFor(count: number) {
      for (let i = 0; i < 100 && frames.length < count; i++) {
        await new Promise((r) => setTimeout(r, 10));
      }
      return frames;
    },
    send(kind: number, ...body: number[]) {
      ws.send(new Uint8Array([kind, ...body]));
    },
  };
}

/** Lets in-flight frames arrive. */
export const settle = () => new Promise((r) => setTimeout(r, 50));

export const kinds = (frames: Uint8Array[]) => frames.map((f) => f[0]);

export { Kind };
