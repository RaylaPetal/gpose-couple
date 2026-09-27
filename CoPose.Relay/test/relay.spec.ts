import { env, runDurableObjectAlarm, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import { Close, MAX_MESSAGE_BYTES } from "../src/protocol";
import { ALICE, BOB, CAROL, connect, Kind, kinds, ROOM, settle } from "./helpers";

describe("worker entry", () => {
  it("answers the health check", async () => {
    const response = await SELF.fetch("http://relay/");
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({ service: "copose-relay", protocol: 1 });
  });

  it("rejects a short room id without upgrading", async () => {
    const response = await SELF.fetch(`http://relay/v1/room/abc?p=${ALICE}`, { headers: { Upgrade: "websocket" } });
    expect(response.status).toBe(400);
    expect(response.webSocket).toBeNull();
  });

  it("rejects a bad participant id", async () => {
    const response = await SELF.fetch(`http://relay/v1/room/${ROOM}?p=nope`, { headers: { Upgrade: "websocket" } });
    expect(response.status).toBe(400);
  });

  it("requires a websocket upgrade", async () => {
    const response = await SELF.fetch(`http://relay/v1/room/${ROOM}?p=${ALICE}`);
    expect(response.status).toBe(426);
  });

  it("upgrades a valid request", async () => {
    const alice = await connect(ALICE);
    expect(alice.ws.readyState).toBe(WebSocket.OPEN);
    alice.ws.close();
  });
});

describe("room", () => {
  it("tells two participants about each other", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);

    expect(kinds(await bob.waitFor(1))).toEqual([Kind.PeerJoined]);
    expect(kinds(await alice.waitFor(1))).toEqual([Kind.PeerJoined]);
  });

  it("refuses a third participant and keeps the other two", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);
    await alice.waitFor(1);

    const carol = await connect(CAROL);
    const closed = await carol.closed;
    expect(closed.code).toBe(Close.RoomFull);

    await settle();
    expect(kinds(alice.frames)).toEqual([Kind.PeerJoined]); // no leave announced for the refused one
    bob.send(Kind.Live, 7);
    expect((await alice.waitFor(2))[1]).toEqual(new Uint8Array([Kind.Live, 7]));
  });

  it("replaces a participant's older connection on reconnect", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);
    await alice.waitFor(1);

    const aliceAgain = await connect(ALICE);
    expect((await alice.closed).code).toBe(Close.Replaced);

    await settle();
    expect(kinds(bob.frames)).not.toContain(Kind.PeerLeft);
    bob.send(Kind.Live, 9);
    const frames = await aliceAgain.waitFor(2);
    expect(frames.at(-1)).toEqual(new Uint8Array([Kind.Live, 9]));
  });

  it("forwards to the partner without echo", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);
    await alice.waitFor(1);
    await bob.waitFor(1);

    alice.send(Kind.Live, 1, 2, 3);

    expect((await bob.waitFor(2))[1]).toEqual(new Uint8Array([Kind.Live, 1, 2, 3]));
    await settle();
    expect(alice.frames).toHaveLength(1); // only PeerJoined
  });

  it("keeps a sender connected when the partner is absent", async () => {
    const alice = await connect(ALICE);
    alice.send(Kind.Live, 1);
    await settle();
    expect(alice.ws.readyState).toBe(WebSocket.OPEN);
  });

  it("replays the partner's latest full state on connect, and only the latest", async () => {
    const alice = await connect(ALICE);
    alice.send(Kind.Full, 1);
    alice.send(Kind.Full, 2);
    alice.send(Kind.Live, 99);
    alice.send(Kind.Full, 3);
    await settle();

    const bob = await connect(BOB);
    const frames = await bob.waitFor(2);

    expect(frames).toEqual([new Uint8Array([Kind.PeerJoined]), new Uint8Array([Kind.Full, 3])]);
  });

  it("replays stored state even after the partner left", async () => {
    const alice = await connect(ALICE);
    alice.send(Kind.Full, 5);
    await settle();
    alice.ws.close();
    await settle();

    const bob = await connect(BOB);
    expect(await bob.waitFor(1)).toEqual([new Uint8Array([Kind.Full, 5])]);
  });

  it("announces when the partner leaves", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);
    await alice.waitFor(1);

    bob.ws.close(1000, "bye");

    expect(kinds(await alice.waitFor(2))).toEqual([Kind.PeerJoined, Kind.PeerLeft]);
  });

  it("purges an abandoned room", async () => {
    const alice = await connect(ALICE);
    alice.send(Kind.Full, 5);
    await settle();
    alice.ws.close();
    await settle();

    const stub = env.POSE_ROOM.get(env.POSE_ROOM.idFromName(ROOM));
    expect(await runDurableObjectAlarm(stub)).toBe(true);

    const bob = await connect(BOB);
    await settle();
    expect(bob.frames).toHaveLength(0);
  });

  it("closes a connection that sends an oversized message", async () => {
    const alice = await connect(ALICE);
    const bob = await connect(BOB);
    await bob.waitFor(1);

    alice.ws.send(new Uint8Array(MAX_MESSAGE_BYTES + 1).fill(Kind.Live));

    expect((await alice.closed).code).toBe(Close.TooLarge);
    await settle();
    expect(kinds(bob.frames)).not.toContain(Kind.Live);
  });

  it("closes a flooding connection", async () => {
    const alice = await connect(ALICE);
    for (let i = 0; i < 50; i++) {
      alice.send(Kind.Live, i);
    }

    expect((await alice.closed).code).toBe(Close.RateLimited);
  });
});
