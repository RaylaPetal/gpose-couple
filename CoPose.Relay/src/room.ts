import { DurableObject } from "cloudflare:workers";
import type { Env } from "./env";
import { Close, EMPTY_ROOM_TTL_MS, Kind, MAX_MESSAGE_BYTES, MAX_MESSAGES_PER_SECOND, MAX_PARTICIPANTS } from "./protocol";

/** Per-socket data kept in the WebSocket attachment, so it survives hibernation. */
interface Attachment {
  participant: string;
  windowStart: number;
  count: number;
}

const STATE_PREFIX = "state:";
const OPEN = 1;

/**
 * One CoPose session room: at most two participants. Forwards every frame to the other participant, stores each
 * participant's latest FULL frame and replays it to whoever connects, and announces joins and leaves. Frames are
 * opaque apart from their first byte (see protocol.ts).
 */
export class PoseRoom extends DurableObject<Env> {
  override async fetch(request: Request): Promise<Response> {
    const participant = new URL(request.url).searchParams.get("p") ?? "";
    const { 0: client, 1: server } = new WebSocketPair();

    const others = new Set(this.open().map((ws) => this.attachment(ws).participant).filter((p) => p !== participant));
    this.ctx.acceptWebSocket(server, [participant]);
    server.serializeAttachment({ participant, windowStart: 0, count: 0 } satisfies Attachment);

    if (others.size >= MAX_PARTICIPANTS) {
      this.detach(server); // never counts as a participant, so its close announces nothing
      server.close(Close.RoomFull, "room full");
      return new Response(null, { status: 101, webSocket: client });
    }

    // A reconnect replaces the participant's older socket instead of counting as a third participant.
    for (const old of this.ctx.getWebSockets(participant)) {
      if (old !== server) {
        this.detach(old);
        old.close(Close.Replaced, "replaced");
      }
    }

    await this.ctx.storage.deleteAlarm();

    // Tell both sides the other is here, then catch the newcomer up on stored state.
    for (const ws of this.open()) {
      if (ws !== server && this.attachment(ws).participant !== participant) {
        send(server, [Kind.PeerJoined]);
        send(ws, [Kind.PeerJoined]);
        break;
      }
    }
    const stored = await this.ctx.storage.list<ArrayBuffer>({ prefix: STATE_PREFIX });
    for (const [key, frame] of stored) {
      if (key !== STATE_PREFIX + participant) {
        server.send(frame);
      }
    }

    return new Response(null, { status: 101, webSocket: client });
  }

  override async webSocketMessage(ws: WebSocket, message: string | ArrayBuffer): Promise<void> {
    if (typeof message === "string") {
      ws.close(Close.Unsupported, "binary frames only");
      return;
    }
    if (message.byteLength > MAX_MESSAGE_BYTES) {
      ws.close(Close.TooLarge, "too large");
      return;
    }

    const attachment = this.attachment(ws);
    const now = Date.now();
    if (now - attachment.windowStart >= 1000) {
      attachment.windowStart = now;
      attachment.count = 0;
    }
    attachment.count++;
    ws.serializeAttachment(attachment);
    if (attachment.count > MAX_MESSAGES_PER_SECOND) {
      ws.close(Close.RateLimited, "rate limited");
      return;
    }

    const kind = new Uint8Array(message)[0];
    if (kind === Kind.Full) {
      await this.ctx.storage.put(STATE_PREFIX + attachment.participant, message);
    } else if (kind !== Kind.Live) {
      return; // unknown client frame kinds are ignored
    }

    for (const other of this.open()) {
      if (other !== ws && this.attachment(other).participant !== attachment.participant) {
        other.send(message);
      }
    }
  }

  override async webSocketClose(ws: WebSocket, code: number, reason: string): Promise<void> {
    try {
      ws.close(code, reason);
    } catch {
      // already closed
    }
    await this.left(ws);
  }

  override async webSocketError(ws: WebSocket): Promise<void> {
    await this.left(ws);
  }

  override async alarm(): Promise<void> {
    if (this.open().length === 0) {
      await this.ctx.storage.deleteAll();
    }
  }

  private async left(ws: WebSocket): Promise<void> {
    const attachment = ws.deserializeAttachment() as Attachment | null;
    if (!attachment || attachment.participant === "") {
      return; // detached (replaced) socket: its participant is still connected
    }
    this.detach(ws);

    const stillHere = this.open().some((s) => this.attachment(s).participant === attachment.participant);
    if (!stillHere) {
      for (const other of this.open()) {
        send(other, [Kind.PeerLeft]);
      }
    }

    if (this.open().length === 0) {
      await this.ctx.storage.setAlarm(Date.now() + EMPTY_ROOM_TTL_MS);
    }
  }

  /** Open sockets that still represent a participant. */
  private open(): WebSocket[] {
    return this.ctx.getWebSockets().filter((ws) => ws.readyState === OPEN && this.attachment(ws).participant !== "");
  }

  private attachment(ws: WebSocket): Attachment {
    return (ws.deserializeAttachment() as Attachment | null) ?? { participant: "", windowStart: 0, count: 0 };
  }

  /** Marks a socket as no longer representing its participant (replaced or closing). */
  private detach(ws: WebSocket): void {
    try {
      ws.serializeAttachment({ participant: "", windowStart: 0, count: 0 } satisfies Attachment);
    } catch {
      // socket already gone
    }
  }
}

function send(ws: WebSocket, bytes: number[]): void {
  try {
    ws.send(new Uint8Array(bytes));
  } catch {
    // the peer went away; its close handler takes care of presence
  }
}
